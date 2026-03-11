using System.Diagnostics;
using CallGraph.Core.Analysis;
using CallGraph.Core.Projects;
using CallGraph.Core.Solutions;
using Microsoft.Extensions.Logging;

namespace CallGraph.Core.Indexing;

public sealed class IndexingPipeline : IIndexingPipeline
{
    private static readonly TimeSpan FileTimestampTolerance = TimeSpan.FromSeconds(1);
    private const int MinIncrementalFallbackThreshold = 50;
    private const int IncrementalFallbackRatioDivisor = 3;

    private readonly ISolutionLoader _solutionLoader;
    private readonly IProjectIndexer _projectIndexer;
    private readonly IFileIndexer _fileIndexer;
    private readonly IGraphBuilder _graphBuilder;
    private readonly IIndexStore _indexStore;
    private readonly ILogger<IndexingPipeline> _logger;

    public IndexingPipeline(
        ISolutionLoader solutionLoader,
        IProjectIndexer projectIndexer,
        IFileIndexer fileIndexer,
        IGraphBuilder graphBuilder,
        IIndexStore indexStore,
        ILogger<IndexingPipeline> logger)
    {
        _solutionLoader = solutionLoader;
        _projectIndexer = projectIndexer;
        _fileIndexer = fileIndexer;
        _graphBuilder = graphBuilder;
        _indexStore = indexStore;
        _logger = logger;
    }

    public async Task RunAsync(IndexJobRequest request, CancellationToken cancellationToken)
    {
        if (request.IsReindex)
        {
            var incremental = await TryRunIncrementalReindexAsync(request, cancellationToken).ConfigureAwait(false);
            if (incremental.Handled)
            {
                _logger.LogInformation(
                    "Incremental reindex completed for {SolutionPath}: updated={UpdatedCount}, removed={RemovedCount}, discoveredNew={DiscoveredNewCount}.",
                    request.SolutionPath,
                    incremental.UpdatedCount,
                    incremental.RemovedCount,
                    incremental.DiscoveredNewCount);
                return;
            }

            _logger.LogInformation(
                "Falling back to full reindex for {SolutionPath}: {Reason}",
                request.SolutionPath,
                incremental.Reason);
        }

        var totalTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();

        await using var context = await _solutionLoader
            .LoadAsync(request.SolutionPath, request.SlnOnly, cancellationToken)
            .ConfigureAwait(false);
        var loadMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var session = await _projectIndexer.IndexAsync(context.Projects, cancellationToken).ConfigureAwait(false);
        var indexMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var index = _graphBuilder.BuildIndex(request.SolutionId, request.SolutionPath, session, request.SlnOnly);
        var buildMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        await _indexStore.SaveAsync(index, cancellationToken).ConfigureAwait(false);
        var saveMs = stageTimer.ElapsedMilliseconds;
        totalTimer.Stop();

        _logger.LogInformation(
            "Indexing timings for {SolutionPath}: load={LoadMs}ms, index={IndexMs}ms, build={BuildMs}ms, save={SaveMs}ms, total={TotalMs}ms.",
            request.SolutionPath,
            loadMs,
            indexMs,
            buildMs,
            saveMs,
            totalTimer.ElapsedMilliseconds);
    }

    private async Task<IncrementalReindexResult> TryRunIncrementalReindexAsync(
        IndexJobRequest request,
        CancellationToken cancellationToken)
    {
        var totalTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();

        long loadIndexedAtMs = 0;
        long checkSolutionMs = 0;
        long loadProjectPathsMs = 0;
        long checkProjectFilesMs = 0;
        long loadIndexedFilesMs = 0;
        long detectChangesMs = 0;
        long discoverNewFilesMs = 0;
        long removeDeletedMs = 0;
        long indexUpdatedFilesMs = 0;
        long applyUpdatedFilesMs = 0;
        long removeStaleFilesMs = 0;

        var normalizedSolutionPath = Path.GetFullPath(request.SolutionPath);

        var indexedAtUtc = await _indexStore
            .GetIndexedAtUtcAsync(normalizedSolutionPath, cancellationToken)
            .ConfigureAwait(false);
        loadIndexedAtMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();
        if (indexedAtUtc is null)
            return IncrementalReindexResult.NotHandled("solution is not indexed yet");

        var solutionChanged = FileWasModifiedAfter(normalizedSolutionPath, indexedAtUtc.Value);
        checkSolutionMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();
        if (solutionChanged)
            return IncrementalReindexResult.NotHandled("solution file changed");

        var projectPaths = await _indexStore
            .ListProjectPathsAsync(normalizedSolutionPath, cancellationToken)
            .ConfigureAwait(false);
        loadProjectPathsMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FileWasModifiedAfter(projectPath, indexedAtUtc.Value))
                return IncrementalReindexResult.NotHandled($"project file changed: {projectPath}");
        }
        checkProjectFilesMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var indexedFiles = await _indexStore.ListFilesAsync(normalizedSolutionPath, cancellationToken).ConfigureAwait(false);
        loadIndexedFilesMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();
        if (indexedFiles.Count == 0)
            return IncrementalReindexResult.NotHandled("no indexed files found");

        var indexedFilePaths = indexedFiles
            .Select(file => Path.GetFullPath(file.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var updates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var indexedFile in indexedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = Path.GetFullPath(indexedFile.FilePath);
            if (!File.Exists(filePath))
            {
                deletes.Add(filePath);
                continue;
            }

            DateTime currentWriteUtc;
            try
            {
                currentWriteUtc = File.GetLastWriteTimeUtc(filePath);
            }
            catch
            {
                return IncrementalReindexResult.NotHandled($"failed to read file timestamp: {filePath}");
            }

            if (currentWriteUtc > indexedFile.UpdatedAtUtc.Add(FileTimestampTolerance))
                updates.Add(filePath);
        }
        detectChangesMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var discoveredNewFiles = DiscoverPotentialNewCodeFiles(projectPaths, indexedFilePaths, indexedAtUtc.Value);
        foreach (var discoveredFile in discoveredNewFiles)
            updates.Add(discoveredFile);
        discoverNewFilesMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var totalChanges = updates.Count + deletes.Count;
        if (totalChanges == 0)
        {
            totalTimer.Stop();
            LogIncrementalReindexTimings(
                request.SolutionPath,
                indexedFiles.Count,
                updates.Count,
                deletes.Count,
                discoveredNewFiles.Count,
                loadIndexedAtMs,
                checkSolutionMs,
                loadProjectPathsMs,
                checkProjectFilesMs,
                loadIndexedFilesMs,
                detectChangesMs,
                discoverNewFilesMs,
                removeDeletedMs,
                indexUpdatedFilesMs,
                applyUpdatedFilesMs,
                removeStaleFilesMs,
                totalTimer.ElapsedMilliseconds);
            return IncrementalReindexResult.Succeeded(0, 0, discoveredNewFiles.Count);
        }

        var fullReindexThreshold = Math.Max(
            MinIncrementalFallbackThreshold,
            indexedFiles.Count / IncrementalFallbackRatioDivisor);
        if (totalChanges > fullReindexThreshold)
        {
            return IncrementalReindexResult.NotHandled(
                $"change set too large ({totalChanges}/{indexedFiles.Count})");
        }

        var removedCount = 0;
        foreach (var deletedFile in deletes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _indexStore.RemoveFileAsync(normalizedSolutionPath, deletedFile, cancellationToken).ConfigureAwait(false);
            removedCount++;
        }
        removeDeletedMs = stageTimer.ElapsedMilliseconds;
        stageTimer.Restart();

        var updatedCount = 0;
        if (updates.Count > 0)
        {
            var updatePaths = updates.ToList();
            var fileUpdates = await _fileIndexer
                .IndexFilesAsync(normalizedSolutionPath, updatePaths, request.SlnOnly, cancellationToken)
                .ConfigureAwait(false);
            indexUpdatedFilesMs = stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();

            foreach (var fileUpdate in fileUpdates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _indexStore.UpdateFileAsync(normalizedSolutionPath, fileUpdate, cancellationToken).ConfigureAwait(false);
                updatedCount++;
            }
            applyUpdatedFilesMs = stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();

            // If a previously indexed file no longer belongs to the loaded project set, remove stale rows.
            var updatedPaths = fileUpdates
                .Select(update => Path.GetFullPath(update.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var staleIndexedFiles = updatePaths
                .Where(path => indexedFilePaths.Contains(path) && !updatedPaths.Contains(path))
                .ToList();

            foreach (var staleFile in staleIndexedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _indexStore.RemoveFileAsync(normalizedSolutionPath, staleFile, cancellationToken).ConfigureAwait(false);
                removedCount++;
            }
            removeStaleFilesMs = stageTimer.ElapsedMilliseconds;
        }

        totalTimer.Stop();
        LogIncrementalReindexTimings(
            request.SolutionPath,
            indexedFiles.Count,
            updates.Count,
            deletes.Count,
            discoveredNewFiles.Count,
            loadIndexedAtMs,
            checkSolutionMs,
            loadProjectPathsMs,
            checkProjectFilesMs,
            loadIndexedFilesMs,
            detectChangesMs,
            discoverNewFilesMs,
            removeDeletedMs,
            indexUpdatedFilesMs,
            applyUpdatedFilesMs,
            removeStaleFilesMs,
            totalTimer.ElapsedMilliseconds);

        return IncrementalReindexResult.Succeeded(updatedCount, removedCount, discoveredNewFiles.Count);
    }

    private void LogIncrementalReindexTimings(
        string solutionPath,
        int indexedFileCount,
        int updateCandidateCount,
        int deleteCandidateCount,
        int discoveredNewCount,
        long loadIndexedAtMs,
        long checkSolutionMs,
        long loadProjectPathsMs,
        long checkProjectFilesMs,
        long loadIndexedFilesMs,
        long detectChangesMs,
        long discoverNewFilesMs,
        long removeDeletedMs,
        long indexUpdatedFilesMs,
        long applyUpdatedFilesMs,
        long removeStaleFilesMs,
        long totalMs)
    {
        _logger.LogInformation(
            "Incremental reindex timings for {SolutionPath}: indexedFiles={IndexedFiles}, updateCandidates={UpdateCandidates}, deleteCandidates={DeleteCandidates}, discoveredNew={DiscoveredNew}, loadIndexedAt={LoadIndexedAtMs}ms, checkSolution={CheckSolutionMs}ms, loadProjectPaths={LoadProjectPathsMs}ms, checkProjectFiles={CheckProjectFilesMs}ms, loadIndexedFiles={LoadIndexedFilesMs}ms, detectChanges={DetectChangesMs}ms, discoverNewFiles={DiscoverNewFilesMs}ms, removeDeleted={RemoveDeletedMs}ms, indexUpdatedFiles={IndexUpdatedFilesMs}ms, applyUpdatedFiles={ApplyUpdatedFilesMs}ms, removeStale={RemoveStaleMs}ms, total={TotalMs}ms.",
            solutionPath,
            indexedFileCount,
            updateCandidateCount,
            deleteCandidateCount,
            discoveredNewCount,
            loadIndexedAtMs,
            checkSolutionMs,
            loadProjectPathsMs,
            checkProjectFilesMs,
            loadIndexedFilesMs,
            detectChangesMs,
            discoverNewFilesMs,
            removeDeletedMs,
            indexUpdatedFilesMs,
            applyUpdatedFilesMs,
            removeStaleFilesMs,
            totalMs);
    }

    private static bool FileWasModifiedAfter(string filePath, DateTime utcThreshold)
    {
        try
        {
            if (!File.Exists(filePath))
                return true;

            return File.GetLastWriteTimeUtc(filePath) > utcThreshold.Add(FileTimestampTolerance);
        }
        catch
        {
            return true;
        }
    }

    private IReadOnlyList<string> DiscoverPotentialNewCodeFiles(
        IEnumerable<string> projectPaths,
        HashSet<string> indexedFilePaths,
        DateTime indexedAtUtc)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in projectPaths)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
                continue;

            IEnumerable<string> candidateFiles;
            try
            {
                candidateFiles = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to scan project directory for new code files: {ProjectDirectory}", projectDirectory);
                continue;
            }

            foreach (var candidateFile in candidateFiles)
            {
                if (ShouldIgnorePath(candidateFile))
                    continue;

                var normalizedCandidate = Path.GetFullPath(candidateFile);
                if (indexedFilePaths.Contains(normalizedCandidate))
                    continue;

                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(normalizedCandidate);
                }
                catch
                {
                    // If we cannot read timestamp, skip discovery and let later full fallback paths handle it.
                    continue;
                }

                // Only treat not-yet-indexed files as changes when they are newer than the current index snapshot.
                if (lastWriteUtc > indexedAtUtc.Add(FileTimestampTolerance))
                    discovered.Add(normalizedCandidate);
            }
        }

        return discovered.ToList();
    }

    private static bool ShouldIgnorePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record IncrementalReindexResult(
        bool Handled,
        string Reason,
        int UpdatedCount,
        int RemovedCount,
        int DiscoveredNewCount)
    {
        public static IncrementalReindexResult NotHandled(string reason)
            => new(false, reason, 0, 0, 0);

        public static IncrementalReindexResult Succeeded(int updatedCount, int removedCount, int discoveredNewCount)
            => new(true, string.Empty, updatedCount, removedCount, discoveredNewCount);
    }
}
