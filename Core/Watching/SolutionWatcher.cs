using System.Collections.Concurrent;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;
using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Document = Microsoft.CodeAnalysis.Document;

namespace CallGraph.Core.Watching;

public sealed class SolutionWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    private const int WatcherBufferSizeBytes = 64 * 1024;
    private readonly string _solutionPath;
    private readonly bool _slnOnly;
    private readonly ISolutionLoader _solutionLoader;
    private readonly IIndexStore _indexStore;
    private readonly IIndexJobStore _jobStore;
    private readonly ISolutionIndexer _solutionIndexer;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTime> _pendingUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pendingMetadataReindexSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _eventCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _timer;
    private DateTime? _reindexRequestedAtUtc;
    private long _totalEventsReceived;
    private long _totalEventsProcessed;
    private SolutionLoadContext? _cachedContext;
    private Dictionary<string, Document>? _cachedDocumentLookup;
    private Task<DispatchMaps>? _cachedDispatchMaps;
    private readonly SemaphoreSlim _contextLock = new(1, 1);
    private string? _activeReindexJobId;

    public SolutionWatcher(
        string solutionPath,
        bool slnOnly,
        ISolutionLoader solutionLoader,
        IIndexStore indexStore,
        IIndexJobStore jobStore,
        ISolutionIndexer solutionIndexer,
        ILogger logger)
    {
        _solutionPath = Path.GetFullPath(solutionPath);
        _slnOnly = slnOnly;
        _solutionLoader = solutionLoader;
        _indexStore = indexStore;
        _jobStore = jobStore;
        _solutionIndexer = solutionIndexer;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var startupDirectories = await ResolveStartupDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        var directories = startupDirectories.Directories;
        var solutionDir = Path.GetDirectoryName(_solutionPath);
        if (!string.IsNullOrWhiteSpace(solutionDir))
            directories.Add(solutionDir);

        directories = directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var originalRootCount = directories.Count;
        directories = CompactWatcherRoots(directories);

        if (directories.Count != originalRootCount)
        {
            _logger.LogInformation(
                "Compacted watcher roots for {SolutionPath}: {OriginalRootCount} -> {CompactedRootCount}.",
                _solutionPath,
                originalRootCount,
                directories.Count);
        }

        _logger.LogInformation(
            "Starting watcher for {SolutionPath} with {DirectoryCount} project directories.",
            _solutionPath,
            directories.Count);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                _logger.LogWarning("Skipping non-existent directory: {Directory}", directory);
                continue;
            }

            AddWatcher(directory, "*.*", includeSubdirectories: true);
        }

        _timer = new Timer(_ => _ = ProcessQueueAsync(), null, DebounceDelay, DebounceDelay);
        _logger.LogInformation(
            "Started watcher for {SolutionPath} with {WatcherCount} watchers across {RootCount} roots.",
            _solutionPath,
            _watchers.Count,
            directories.Count);

        if (startupDirectories.FromIndex)
            _ = WarmCachedContextAsync();
    }

    private void AddWatcher(string directory, string filter, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = includeSubdirectories,
            Filter = filter,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size,
            InternalBufferSize = WatcherBufferSizeBytes
        };

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
        watcher.EnableRaisingEvents = true;

        _watchers.Add(watcher);

        _logger.LogTrace(
            "Created watcher for directory {Directory} with filter {Filter} (subdirs: {IncludeSubdirectories}).",
            directory,
            filter,
            includeSubdirectories);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Dispose();
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        _watchers.Clear();

        _cachedDocumentLookup = null;
        _cachedDispatchMaps = null;

        // Dispose cached context
        if (_cachedContext is not null)
        {
            try
            {
                _cachedContext.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing cached solution context.");
            }
        }

        _cts.Dispose();
        _processingLock.Dispose();
        _contextLock.Dispose();
    }

    private async Task<(List<string> Directories, bool FromIndex)> ResolveStartupDirectoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var directoriesFromIndex = await ResolveProjectDirectoriesFromIndexAsync(cancellationToken).ConfigureAwait(false);
            if (directoriesFromIndex.Count > 0)
            {
                _logger.LogInformation(
                    "Resolved {DirectoryCount} project directories for watcher from index metadata.",
                    directoriesFromIndex.Count);
                return (directoriesFromIndex, true);
            }

            _logger.LogInformation(
                "No indexed project paths found for {SolutionPath}; resolving watcher roots from solution load.",
                _solutionPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve watcher roots from index metadata for {SolutionPath}. Falling back to solution load.",
                _solutionPath);
        }

        var directoriesFromSolution = await ResolveProjectDirectoriesFromSolutionAsync(cancellationToken).ConfigureAwait(false);
        return (directoriesFromSolution, false);
    }

    private async Task<List<string>> ResolveProjectDirectoriesFromIndexAsync(CancellationToken cancellationToken)
    {
        var projectPaths = await _indexStore
            .ListProjectPathsAsync(_solutionPath, cancellationToken)
            .ConfigureAwait(false);

        return projectPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<string>> ResolveProjectDirectoriesFromSolutionAsync(CancellationToken cancellationToken)
    {
        await using var context = await _solutionLoader
            .LoadAsync(_solutionPath, _slnOnly, cancellationToken)
            .ConfigureAwait(false);

        return context.Projects
            .Select(p => p.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetDirectoryName(path!))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private async Task WarmCachedContextAsync()
    {
        try
        {
            var context = await GetOrLoadContextAsync(_cts.Token).ConfigureAwait(false);
            _ = await GetOrLoadDocumentLookupAsync(context, _cts.Token).ConfigureAwait(false);
            _ = await GetOrLoadDispatchMapsAsync(context, _cts.Token).ConfigureAwait(false);
            _logger.LogTrace("Background context warm-up completed for {SolutionPath}.", _solutionPath);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background context warm-up failed for {SolutionPath}.", _solutionPath);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        Interlocked.Increment(ref _totalEventsReceived);

        if (!IsRelevantFile(e.FullPath))
        {
            _logger.LogTrace("Ignoring non-relevant file change: {FilePath}", e.FullPath);
            return;
        }

        _logger.LogTrace(
            "Watcher event: Changed {ChangeType} | {FullPath}",
            e.ChangeType,
            e.FullPath);

        if (IsIgnoredPath(e.FullPath))
        {
            _logger.LogTrace("Ignoring change in obj/bin path: {FilePath}", e.FullPath);
            return;
        }

        if (IsSolutionFile(e.FullPath))
        {
            QueueMetadataReindex(e.FullPath, "changed");
            return;
        }

        // Check if file still exists (to avoid processing transient temp files)
        if (!File.Exists(e.FullPath))
        {
            _logger.LogTrace("File no longer exists, ignoring change: {FilePath}", e.FullPath);
            return;
        }

        // Remove from deletes if it was previously marked for deletion
        if (_pendingDeletes.TryRemove(e.FullPath, out _))
        {
            _logger.LogTrace("Removing {FilePath} from pending deletes (file changed).", e.FullPath);
        }

        _pendingUpdates[e.FullPath] = DateTime.UtcNow;
        _eventCounts.AddOrUpdate(e.FullPath, 1, (_, count) => count + 1);

        _logger.LogTrace(
            "Queued update for {FilePath} (event count: {EventCount}).",
            e.FullPath,
            _eventCounts[e.FullPath]);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        Interlocked.Increment(ref _totalEventsReceived);

        if (!IsRelevantFile(e.FullPath))
        {
            _logger.LogTrace("Ignoring non-relevant file deletion: {FilePath}", e.FullPath);
            return;
        }

        _logger.LogTrace(
            "Watcher event: Deleted | {FullPath}",
            e.FullPath);

        if (IsIgnoredPath(e.FullPath))
        {
            _logger.LogTrace("Ignoring deletion in obj/bin path: {FilePath}", e.FullPath);
            return;
        }

        if (IsSolutionFile(e.FullPath))
        {
            QueueMetadataReindex(e.FullPath, "deleted");
            return;
        }

        // Remove from updates if it was previously marked for update
        if (_pendingUpdates.TryRemove(e.FullPath, out _))
        {
            _logger.LogTrace("Removing {FilePath} from pending updates (file deleted).", e.FullPath);
        }

        _pendingDeletes[e.FullPath] = DateTime.UtcNow;
        _eventCounts.AddOrUpdate(e.FullPath, 1, (_, count) => count + 1);

        _logger.LogTrace(
            "Queued deletion for {FilePath} (event count: {EventCount}).",
            e.FullPath,
            _eventCounts[e.FullPath]);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Interlocked.Increment(ref _totalEventsReceived);

        var oldRelevant = IsRelevantFile(e.OldFullPath);
        var newRelevant = IsRelevantFile(e.FullPath);

        if (!oldRelevant && !newRelevant)
        {
            _logger.LogTrace(
                "Ignoring non-relevant file rename: {OldPath} -> {NewPath}",
                e.OldFullPath,
                e.FullPath);
            return;
        }

        _logger.LogTrace(
            "Watcher event: Renamed | {OldFullPath} -> {FullPath}",
            e.OldFullPath,
            e.FullPath);

        var oldPathIgnored = IsIgnoredPath(e.OldFullPath);
        var newPathIgnored = IsIgnoredPath(e.FullPath);

        if (oldPathIgnored && newPathIgnored)
        {
            _logger.LogTrace(
                "Ignoring rename in obj/bin path: {OldPath} -> {NewPath}",
                e.OldFullPath,
                e.FullPath);
            return;
        }

        if ((!oldPathIgnored && IsSolutionFile(e.OldFullPath)) || (!newPathIgnored && IsSolutionFile(e.FullPath)))
        {
            QueueMetadataReindex(e.OldFullPath, "renamed-old");
            QueueMetadataReindex(e.FullPath, "renamed-new");
            return;
        }

        // Handle rename as delete old + create new
        if (!oldPathIgnored && oldRelevant && IsCodeFile(e.OldFullPath))
        {
            _pendingUpdates.TryRemove(e.OldFullPath, out _);
            _pendingDeletes[e.OldFullPath] = DateTime.UtcNow;
            _eventCounts.AddOrUpdate(e.OldFullPath, 1, (_, count) => count + 1);

            _logger.LogTrace(
                "Queued deletion for renamed file (old path): {FilePath} (event count: {EventCount}).",
                e.OldFullPath,
                _eventCounts[e.OldFullPath]);
        }

        if (!newPathIgnored && newRelevant && IsCodeFile(e.FullPath))
        {
            // Remove from deletes if it was previously marked for deletion
            if (_pendingDeletes.TryRemove(e.FullPath, out _))
            {
                _logger.LogTrace("Removing {FilePath} from pending deletes (file renamed to this path).", e.FullPath);
            }

            _pendingUpdates[e.FullPath] = DateTime.UtcNow;
            _eventCounts.AddOrUpdate(e.FullPath, 1, (_, count) => count + 1);

            _logger.LogTrace(
                "Queued update for renamed file (new path): {FilePath} (event count: {EventCount}).",
                e.FullPath,
                _eventCounts[e.FullPath]);
        }
    }

    private bool IsIgnoredPath(string path)
    {
        var solutionDir = Path.GetDirectoryName(_solutionPath);
        if (string.IsNullOrWhiteSpace(solutionDir))
            return false;

        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(solutionDir, fullPath);
        if (relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            return false;

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        var exception = e.GetException();

        // Check if this is a buffer overflow error
        if (exception is InternalBufferOverflowException)
        {
            _logger.LogWarning(
                exception,
                "File watcher buffer overflow for {SolutionPath}. Too many changes detected. " +
                "Queueing full reindex as fallback.",
                _solutionPath);
        }
        else
        {
            _logger.LogWarning(
                exception,
                "File watcher error for {SolutionPath}. Queueing full reindex.",
                _solutionPath);
        }

        RequestReindex();
    }

    private bool RequestReindex()
    {
        if (_reindexRequestedAtUtc.HasValue)
            return false;

        _reindexRequestedAtUtc = DateTime.UtcNow;

        // Invalidate cached context when reindex is requested
        InvalidateCachedContext();
        return true;
    }

    private void QueueMetadataReindex(string filePath, string changeKind)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        _pendingMetadataReindexSignals[normalizedPath] = changeKind;

        if (RequestReindex())
        {
            _logger.LogInformation(
                "Solution/project file {ChangeKind}: {FilePath}. Queueing full reindex.",
                changeKind,
                normalizedPath);
        }
        else
        {
            _logger.LogTrace(
                "Additional solution/project file {ChangeKind} while reindex pending: {FilePath}.",
                changeKind,
                normalizedPath);
        }
    }

    private bool RefreshActiveReindexState()
    {
        if (string.IsNullOrWhiteSpace(_activeReindexJobId))
            return false;

        if (!_jobStore.TryGetJob(_activeReindexJobId, out var jobStatus))
        {
            // Best effort: if status cannot be loaded, keep buffering updates to avoid racing
            // watcher incremental writes with an unknown reindex state.
            return true;
        }

        if (!IsTerminalJobStatus(jobStatus.Status))
            return true;

        _logger.LogInformation(
            "Full reindex job {JobId} reached terminal state {Status} for {SolutionPath}. Replaying buffered file changes.",
            jobStatus.JobId,
            jobStatus.Status,
            _solutionPath);
        _activeReindexJobId = null;
        return false;
    }

    private static bool IsTerminalJobStatus(string status)
        => string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "Superseded", StringComparison.OrdinalIgnoreCase);

    private void InvalidateCachedContext()
    {
        _cachedDocumentLookup = null;
        _cachedDispatchMaps = null;

        if (_cachedContext is not null)
        {
            _logger.LogTrace("Invalidating cached solution context.");

            try
            {
                _cachedContext.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing cached solution context during invalidation.");
            }

            _cachedContext = null;
        }
    }

    private async Task<Dictionary<string, Document>> GetOrLoadDocumentLookupAsync(
        SolutionLoadContext context,
        CancellationToken cancellationToken)
    {
        await _contextLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedDocumentLookup is null)
            {
                var documentLookup = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
                foreach (var project in context.Projects)
                {
                    foreach (var doc in project.Documents)
                    {
                        if (!doc.SupportsSyntaxTree || doc.FilePath is null)
                            continue;

                        var normalized = Path.GetFullPath(doc.FilePath);
                        documentLookup[normalized] = doc;
                    }
                }

                _cachedDocumentLookup = documentLookup;
            }

            return _cachedDocumentLookup;
        }
        finally
        {
            _contextLock.Release();
        }
    }

    private async Task<DispatchMaps> GetOrLoadDispatchMapsAsync(
        SolutionLoadContext context,
        CancellationToken cancellationToken)
    {
        Task<DispatchMaps> dispatchMapsTask;

        await _contextLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cachedDispatchMaps ??= DispatchMapBuilder.BuildAsync(context.Projects, _cts.Token);
            dispatchMapsTask = _cachedDispatchMaps;
        }
        finally
        {
            _contextLock.Release();
        }

        return await dispatchMapsTask.ConfigureAwait(false);
    }

    private async Task<SolutionLoadContext> GetOrLoadContextAsync(CancellationToken cancellationToken)
    {
        await _contextLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedContext is null)
            {
                _logger.LogTrace("Loading solution context for {SolutionPath} (cache miss).", _solutionPath);
                _cachedContext = await _solutionLoader
                    .LoadAsync(_solutionPath, _slnOnly, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _logger.LogTrace("Reusing cached solution context for {SolutionPath}.", _solutionPath);
            }

            return _cachedContext;
        }
        finally
        {
            _contextLock.Release();
        }
    }

    private async Task<IReadOnlyList<FileIndex>> IndexFilesWithCachedContextAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0)
            return Array.Empty<FileIndex>();

        var normalizedFilePaths = filePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Get or load cached context
        var context = await GetOrLoadContextAsync(cancellationToken).ConfigureAwait(false);
        var documentLookup = await GetOrLoadDocumentLookupAsync(context, cancellationToken).ConfigureAwait(false);

        var dispatchMaps = new Lazy<Task<DispatchMaps>>(
            () => GetOrLoadDispatchMapsAsync(context, cancellationToken));

        var results = new System.Collections.Concurrent.ConcurrentBag<FileIndex>();
        await Parallel.ForEachAsync(normalizedFilePaths, cancellationToken, async (normalizedFilePath, ct) =>
        {
            if (!documentLookup.TryGetValue(normalizedFilePath, out var doc))
            {
                _logger.LogTrace("File not found in solution: {FilePath}", normalizedFilePath);
                return;
            }

            var graph = await DocumentCallGraphExtractor
                .ExtractAsync(doc, () => dispatchMaps.Value, ct)
                .ConfigureAwait(false);

            results.Add(new FileIndex
            {
                FilePath = normalizedFilePath,
                Nodes = graph.Nodes.ToList(),
                Edges = graph.Edges.ToList()
            });
        }).ConfigureAwait(false);

        return results.ToList();
    }

    private async Task ProcessQueueAsync()
    {
        if (_cts.IsCancellationRequested)
            return;

        if (!await _processingLock.WaitAsync(0).ConfigureAwait(false))
            return;

        var processingStarted = DateTime.UtcNow;

        try
        {
            var now = DateTime.UtcNow;
            var reindexInFlight = RefreshActiveReindexState();

            // Log queue sizes before processing
            var pendingDeleteCount = _pendingDeletes.Count;
            var pendingUpdateCount = _pendingUpdates.Count;

            if (pendingDeleteCount > 0 || pendingUpdateCount > 0 || _reindexRequestedAtUtc.HasValue)
            {
                _logger.LogTrace(
                    "Processing watcher queue: {DeleteCount} pending deletes, {UpdateCount} pending updates, " +
                    "reindex requested: {ReindexRequested}",
                    pendingDeleteCount,
                    pendingUpdateCount,
                    _reindexRequestedAtUtc.HasValue);
            }

            List<string> deletes = [];
            List<string> updates = [];
            if (!reindexInFlight)
            {
                // Process deletes
                deletes = _pendingDeletes
                    .Where(kvp => now - kvp.Value >= DebounceDelay)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (deletes.Count > 0)
                {
                    _logger.LogInformation(
                        "Processing {DeleteCount} file deletions for {SolutionPath}.",
                        deletes.Count,
                        _solutionPath);

                    foreach (var file in deletes)
                    {
                        _pendingDeletes.TryRemove(file, out _);
                        _eventCounts.TryRemove(file, out _);

                        try
                        {
                            await _indexStore.RemoveFileAsync(_solutionPath, file, _cts.Token).ConfigureAwait(false);
                            _logger.LogTrace("Removed file from index: {FilePath}", file);
                            Interlocked.Increment(ref _totalEventsProcessed);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to remove file from index: {FilePath}", file);
                        }
                    }
                }

                // Process updates
                updates = _pendingUpdates
                    .Where(kvp => now - kvp.Value >= DebounceDelay)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (updates.Count > 0)
                {
                    _logger.LogInformation(
                        "Processing {UpdateCount} file updates for {SolutionPath}.",
                        updates.Count,
                        _solutionPath);

                    foreach (var file in updates)
                    {
                        _pendingUpdates.TryRemove(file, out _);
                        _eventCounts.TryRemove(file, out _);
                    }

                    try
                    {
                        var indexingStarted = DateTime.UtcNow;

                        // Use cached context for incremental indexing to avoid reloading solution
                        var indexed = await IndexFilesWithCachedContextAsync(updates, _cts.Token)
                            .ConfigureAwait(false);

                        var indexingDuration = DateTime.UtcNow - indexingStarted;

                        _logger.LogInformation(
                            "Indexed {IndexedCount}/{RequestedCount} files in {Duration}ms (using cached context).",
                            indexed.Count,
                            updates.Count,
                            indexingDuration.TotalMilliseconds);

                        foreach (var update in indexed)
                        {
                            try
                            {
                                await _indexStore.UpdateFileAsync(_solutionPath, update, _cts.Token).ConfigureAwait(false);
                                _logger.LogTrace("Updated file in index: {FilePath}", update.FilePath);
                                Interlocked.Increment(ref _totalEventsProcessed);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to update file in index: {FilePath}", update.FilePath);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to index files for {SolutionPath}. Invalidating cache.", _solutionPath);

                        // Invalidate cache on error and fallback to full reindex
                        InvalidateCachedContext();
                        RequestReindex();
                    }
                }
            }
            else if (pendingDeleteCount > 0 || pendingUpdateCount > 0)
            {
                _logger.LogDebug(
                    "Buffering {DeleteCount} deletes and {UpdateCount} updates while full reindex job {JobId} is active for {SolutionPath}.",
                    pendingDeleteCount,
                    pendingUpdateCount,
                    _activeReindexJobId,
                    _solutionPath);
            }

            // Process reindex request
            if (_reindexRequestedAtUtc.HasValue &&
                now - _reindexRequestedAtUtc.Value >= DebounceDelay)
            {
                _reindexRequestedAtUtc = null;
                var metadataSignals = _pendingMetadataReindexSignals.ToArray();
                _pendingMetadataReindexSignals.Clear();

                var bufferedUpdates = _pendingUpdates.Count;
                var bufferedDeletes = _pendingDeletes.Count;

                _logger.LogInformation(
                    "Watcher-triggered reindex queued for {SolutionPath}. " +
                    "Metadata changes={MetadataSignalCount}. Buffered updates={UpdateCount}, buffered deletes={DeleteCount}.",
                    _solutionPath,
                    metadataSignals.Length,
                    bufferedUpdates,
                    bufferedDeletes);

                if (metadataSignals.Length > 0)
                {
                    var metadataPreview = string.Join(
                        ", ",
                        metadataSignals
                            .Take(5)
                            .Select(signal => $"{signal.Key} ({signal.Value})"));
                    _logger.LogDebug(
                        "Metadata reindex signal preview for {SolutionPath}: {MetadataPreview}",
                        _solutionPath,
                        metadataPreview);
                }

                var reindexResponse = await _solutionIndexer
                    .EnqueueReindexAsync(new ReindexRequest(_solutionPath, _slnOnly), _cts.Token)
                    .ConfigureAwait(false);
                _activeReindexJobId = reindexResponse.JobId;
                _logger.LogInformation(
                    "Tracking full reindex job {JobId} for {SolutionPath}. Buffered file changes will be replayed after completion.",
                    reindexResponse.JobId,
                    _solutionPath);
            }

            var processingDuration = DateTime.UtcNow - processingStarted;

            if (deletes.Count > 0 || updates.Count > 0)
            {
                _logger.LogTrace(
                    "Watcher queue processing completed in {Duration}ms. " +
                    "Total events received: {ReceivedCount}, processed: {ProcessedCount}.",
                    processingDuration.TotalMilliseconds,
                    _totalEventsReceived,
                    _totalEventsProcessed);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogTrace("Watcher queue processing cancelled for {SolutionPath}.", _solutionPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watcher processing failed for {SolutionPath}.", _solutionPath);
        }
        finally
        {
            _processingLock.Release();
        }
    }

    private static bool IsCodeFile(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSolutionFile(string path)
        => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelevantFile(string path)
        => IsCodeFile(path) || IsSolutionFile(path);

    private static List<string> CompactWatcherRoots(IReadOnlyList<string> directories)
    {
        if (directories.Count <= 1)
            return directories.Select(Path.GetFullPath).ToList();

        var normalizedDirectories = directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();

        var compacted = new List<string>(normalizedDirectories.Count);
        foreach (var directory in normalizedDirectories)
        {
            if (compacted.Any(root => IsSameOrSubdirectory(directory, root)))
                continue;

            compacted.Add(directory);
        }

        return compacted;
    }

    private static bool IsSameOrSubdirectory(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        var rootLength = root.Length;
        if (path.Length <= rootLength)
            return false;

        var separator = path[rootLength];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

}
