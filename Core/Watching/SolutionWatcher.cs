using System.Collections.Concurrent;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;
using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallGraph.Core.Watching;

public sealed class SolutionWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    private const int WatcherBufferSizeBytes = 64 * 1024;
    private readonly string _solutionPath;
    private readonly bool _slnOnly;
    private readonly ISolutionLoader _solutionLoader;
    private readonly IIndexStore _indexStore;
    private readonly ISolutionIndexer _solutionIndexer;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTime> _pendingUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _eventCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _timer;
    private DateTime? _reindexRequestedAtUtc;
    private long _totalEventsReceived;
    private long _totalEventsProcessed;
    private SolutionLoadContext? _cachedContext;
    private readonly SemaphoreSlim _contextLock = new(1, 1);

    public SolutionWatcher(
        string solutionPath,
        bool slnOnly,
        ISolutionLoader solutionLoader,
        IIndexStore indexStore,
        ISolutionIndexer solutionIndexer,
        ILogger logger)
    {
        _solutionPath = Path.GetFullPath(solutionPath);
        _slnOnly = slnOnly;
        _solutionLoader = solutionLoader;
        _indexStore = indexStore;
        _solutionIndexer = solutionIndexer;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var directories = await ResolveProjectDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        var solutionDir = Path.GetDirectoryName(_solutionPath);
        if (!string.IsNullOrWhiteSpace(solutionDir))
            directories.Add(solutionDir);

        directories = directories.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

            AddWatcher(directory, "*.cs", includeSubdirectories: true);
            AddWatcher(directory, "*.csproj", includeSubdirectories: true);
        }

        if (!string.IsNullOrWhiteSpace(solutionDir) && Directory.Exists(solutionDir))
            AddWatcher(solutionDir, "*.sln", includeSubdirectories: false);

        _timer = new Timer(_ => _ = ProcessQueueAsync(), null, DebounceDelay, DebounceDelay);
        _logger.LogInformation(
            "Started watcher for {SolutionPath} with {WatcherCount} watchers across {RootCount} roots.",
            _solutionPath,
            _watchers.Count,
            directories.Count);
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

    private async Task<List<string>> ResolveProjectDirectoriesAsync(CancellationToken cancellationToken)
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

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        Interlocked.Increment(ref _totalEventsReceived);

        _logger.LogTrace(
            "Watcher event: Changed {ChangeType} | {FullPath}",
            e.ChangeType,
            e.FullPath);

        if (IsSolutionFile(e.FullPath))
        {
            _logger.LogInformation(
                "Solution/project file changed: {FilePath}. Queueing full reindex.",
                e.FullPath);
            RequestReindex();
            return;
        }

        if (!IsCodeFile(e.FullPath))
        {
            _logger.LogTrace("Ignoring non-code file: {FilePath}", e.FullPath);
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

        _logger.LogTrace(
            "Watcher event: Deleted | {FullPath}",
            e.FullPath);

        if (IsSolutionFile(e.FullPath))
        {
            _logger.LogInformation(
                "Solution/project file deleted: {FilePath}. Queueing full reindex.",
                e.FullPath);
            RequestReindex();
            return;
        }

        if (!IsCodeFile(e.FullPath))
        {
            _logger.LogTrace("Ignoring non-code file deletion: {FilePath}", e.FullPath);
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

        _logger.LogTrace(
            "Watcher event: Renamed | {OldFullPath} -> {FullPath}",
            e.OldFullPath,
            e.FullPath);

        if (IsSolutionFile(e.OldFullPath) || IsSolutionFile(e.FullPath))
        {
            _logger.LogInformation(
                "Solution/project file renamed: {OldPath} -> {NewPath}. Queueing full reindex.",
                e.OldFullPath,
                e.FullPath);
            RequestReindex();
            return;
        }

        // Handle rename as delete old + create new
        if (IsCodeFile(e.OldFullPath))
        {
            _pendingUpdates.TryRemove(e.OldFullPath, out _);
            _pendingDeletes[e.OldFullPath] = DateTime.UtcNow;
            _eventCounts.AddOrUpdate(e.OldFullPath, 1, (_, count) => count + 1);

            _logger.LogTrace(
                "Queued deletion for renamed file (old path): {FilePath} (event count: {EventCount}).",
                e.OldFullPath,
                _eventCounts[e.OldFullPath]);
        }

        if (IsCodeFile(e.FullPath))
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

    private void RequestReindex()
    {
        _reindexRequestedAtUtc = DateTime.UtcNow;

        // Invalidate cached context when reindex is requested
        InvalidateCachedContext();
    }

    private void InvalidateCachedContext()
    {
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

        // Build document lookup from cached context
        var documentLookup = new Dictionary<string, Microsoft.CodeAnalysis.Document>(StringComparer.OrdinalIgnoreCase);
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

        // Build interface implementation map lazily
        var interfaceMap = new Lazy<Task<Dictionary<string, List<Microsoft.CodeAnalysis.INamedTypeSymbol>>>>(
            () => BuildInterfaceImplementationMapAsync(context.Projects, cancellationToken));

        var results = new System.Collections.Concurrent.ConcurrentBag<FileIndex>();
        await Parallel.ForEachAsync(normalizedFilePaths, cancellationToken, async (normalizedFilePath, ct) =>
        {
            if (!documentLookup.TryGetValue(normalizedFilePath, out var doc))
            {
                _logger.LogTrace("File not found in solution: {FilePath}", normalizedFilePath);
                return;
            }

            var index = await BuildIndexForDocumentAsync(
                    doc,
                    normalizedFilePath,
                    () => interfaceMap.Value,
                    ct)
                .ConfigureAwait(false);
            if (index is not null)
                results.Add(index);
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

            // Process deletes
            var deletes = _pendingDeletes
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
            var updates = _pendingUpdates
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

            // Process reindex request
            if (_reindexRequestedAtUtc.HasValue &&
                now - _reindexRequestedAtUtc.Value >= DebounceDelay)
            {
                _reindexRequestedAtUtc = null;

                // Clear pending updates/deletes since full reindex will handle everything
                var clearedUpdates = _pendingUpdates.Count;
                var clearedDeletes = _pendingDeletes.Count;
                _pendingUpdates.Clear();
                _pendingDeletes.Clear();
                _eventCounts.Clear();

                _logger.LogInformation(
                    "Watcher-triggered reindex queued for {SolutionPath}. " +
                    "Cleared {UpdateCount} pending updates and {DeleteCount} pending deletes.",
                    _solutionPath,
                    clearedUpdates,
                    clearedDeletes);

                await _solutionIndexer
                    .EnqueueReindexAsync(new ReindexRequest(_solutionPath, _slnOnly), _cts.Token)
                    .ConfigureAwait(false);
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

    // Helper methods for file indexing (copied from FileIndexer to support cached context)

    private static async Task<Dictionary<string, List<Microsoft.CodeAnalysis.INamedTypeSymbol>>> BuildInterfaceImplementationMapAsync(
        IEnumerable<Microsoft.CodeAnalysis.Project> projects,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<Microsoft.CodeAnalysis.INamedTypeSymbol>>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

                foreach (var typeDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken);
                    if (typeSymbol is Microsoft.CodeAnalysis.INamedTypeSymbol namedType && !namedType.IsAbstract)
                    {
                        // Track all interfaces this type implements
                        foreach (var @interface in namedType.AllInterfaces)
                        {
                            var interfaceKey = @interface.ToDisplayString(Microsoft.CodeAnalysis.SymbolDisplayFormat.FullyQualifiedFormat);
                            if (!map.TryGetValue(interfaceKey, out var implementations))
                            {
                                implementations = new List<Microsoft.CodeAnalysis.INamedTypeSymbol>();
                                map[interfaceKey] = implementations;
                            }
                            implementations.Add(namedType);
                        }
                    }
                }
            }
        }

        return map;
    }

    private static async Task<FileIndex?> BuildIndexForDocumentAsync(
        Microsoft.CodeAnalysis.Document doc,
        string normalizedFilePath,
        Func<Task<Dictionary<string, List<Microsoft.CodeAnalysis.INamedTypeSymbol>>>> getInterfaceImplementations,
        CancellationToken cancellationToken)
    {
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
            return null;

        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, List<Microsoft.CodeAnalysis.INamedTypeSymbol>>? interfaceImplementations = null;

        foreach (var md in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var caller = model.GetDeclaredSymbol(md, cancellationToken) as Microsoft.CodeAnalysis.IMethodSymbol;
            if (caller is null)
                continue;

            var callerKey = CallGraph.Core.Analysis.SymbolKeyFormatter.Format(caller);
            nodes.Add(MakeNode(caller, md.GetLocation()));

            foreach (var inv in md.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                var symbolInfo = model.GetSymbolInfo(inv, cancellationToken);
                var callee = ResolveMethodSymbol(symbolInfo);
                if (callee is null)
                    continue;

                AddEdge(edges, edgeKeys, callerKey, CallGraph.Core.Analysis.SymbolKeyFormatter.Format(callee), "direct");

                if (IsInterfaceCall(inv, model, cancellationToken, out var interfaceMethod))
                {
                    interfaceImplementations ??= await getInterfaceImplementations().ConfigureAwait(false);
                    AddInterfaceImplementationEdges(
                        edges,
                        edgeKeys,
                        callerKey,
                        interfaceMethod!,
                        interfaceImplementations);
                }
            }

            foreach (var obj in md.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>())
            {
                var ctor = ResolveMethodSymbol(model.GetSymbolInfo(obj, cancellationToken));
                if (ctor is null)
                    continue;

                AddEdge(edges, edgeKeys, callerKey, CallGraph.Core.Analysis.SymbolKeyFormatter.Format(ctor), "direct");
            }
        }

        return new FileIndex
        {
            FilePath = normalizedFilePath,
            Nodes = nodes
                .DistinctBy(n => n.Id)
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToList(),
            Edges = edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Direction, StringComparer.Ordinal)
                .ToList()
        };
    }

    private static void AddEdge(
        ICollection<Edge> edges,
        HashSet<string> edgeKeys,
        string from,
        string to,
        string callKind = "direct")
    {
        var key = $"{from}\u0000{to}\u0000{callKind}";
        if (!edgeKeys.Add(key))
            return;

        edges.Add(new Edge
        {
            From = from,
            To = to,
            Direction = "outbound",
            Kind = callKind == "interface" ? "calls-via-interface" : "calls"
        });
    }

    private static Microsoft.CodeAnalysis.IMethodSymbol? ResolveMethodSymbol(Microsoft.CodeAnalysis.SymbolInfo info)
        => info.Symbol as Microsoft.CodeAnalysis.IMethodSymbol
           ?? info.CandidateSymbols.OfType<Microsoft.CodeAnalysis.IMethodSymbol>().FirstOrDefault();

    private static bool IsInterfaceCall(
        Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation,
        Microsoft.CodeAnalysis.SemanticModel model,
        CancellationToken cancellationToken,
        out Microsoft.CodeAnalysis.IMethodSymbol? interfaceMethod)
    {
        interfaceMethod = null;

        var expression = invocation.Expression;

        if (expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax memberAccess)
        {
            var typeInfo = model.GetTypeInfo(memberAccess.Expression, cancellationToken);
            var type = typeInfo.Type;

            if (type is Microsoft.CodeAnalysis.INamedTypeSymbol { TypeKind: Microsoft.CodeAnalysis.TypeKind.Interface })
            {
                var symbolInfo = model.GetSymbolInfo(invocation, cancellationToken);
                interfaceMethod = symbolInfo.Symbol as Microsoft.CodeAnalysis.IMethodSymbol;
                return interfaceMethod?.ContainingType?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface;
            }
        }

        return false;
    }

    private static void AddInterfaceImplementationEdges(
        ICollection<Edge> edges,
        HashSet<string> edgeKeys,
        string callerKey,
        Microsoft.CodeAnalysis.IMethodSymbol interfaceMethod,
        Dictionary<string, List<Microsoft.CodeAnalysis.INamedTypeSymbol>> interfaceImplementations)
    {
        var interfaceType = interfaceMethod.ContainingType;
        if (interfaceType is null)
            return;

        var interfaceKey = interfaceType.ToDisplayString(Microsoft.CodeAnalysis.SymbolDisplayFormat.FullyQualifiedFormat);
        if (!interfaceImplementations.TryGetValue(interfaceKey, out var implementations))
            return;

        foreach (var implementingType in implementations)
        {
            var implementationMethod = MethodSignatureMatcher.FindImplementationMethod(implementingType, interfaceMethod);

            if (implementationMethod is not null)
            {
                var implementationKey = CallGraph.Core.Analysis.SymbolKeyFormatter.Format(implementationMethod);
                AddEdge(edges, edgeKeys, callerKey, implementationKey, "interface");
            }
        }
    }

    private static Node MakeNode(Microsoft.CodeAnalysis.IMethodSymbol method, Microsoft.CodeAnalysis.Location? location)
    {
        var loc = location?.IsInSource == true
            ? location
            : method.Locations.FirstOrDefault(l => l.IsInSource);
        var file = loc?.SourceTree?.FilePath;
        var line = loc is null ? (int?)null : loc.GetLineSpan().StartLinePosition.Line + 1;

        return new Node
        {
            Id = CallGraph.Core.Analysis.SymbolKeyFormatter.Format(method),
            Kind = "method",
            Display = method.ToDisplayString(Microsoft.CodeAnalysis.SymbolDisplayFormat.MinimallyQualifiedFormat),
            ContainingType = method.ContainingType?.ToDisplayString(Microsoft.CodeAnalysis.SymbolDisplayFormat.MinimallyQualifiedFormat),
            FilePath = file is null ? null : Path.GetFullPath(file),
            StartLine = line,
            Accessibility = MapAccessibility(method.DeclaredAccessibility)
        };
    }

    private static string? MapAccessibility(Microsoft.CodeAnalysis.Accessibility accessibility)
        => accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            Microsoft.CodeAnalysis.Accessibility.Private => "private",
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
            _ => null
        };
}
