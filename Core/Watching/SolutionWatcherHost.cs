using System.Collections.Concurrent;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace CallGraph.Core.Watching;

public sealed class SolutionWatcherHost : BackgroundService, ISolutionWatcherRegistry, IHostedService
{
    private readonly ILogger<SolutionWatcherHost> _logger;
    private readonly ISolutionLoader _solutionLoader;
    private readonly IIndexStore _indexStore;
    private readonly ISolutionIndexer _solutionIndexer;
    private readonly ConcurrentDictionary<string, ActiveWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sync = new(1, 1);

    public SolutionWatcherHost(
        ILogger<SolutionWatcherHost> logger,
        ISolutionLoader solutionLoader,
        IIndexStore indexStore,
        ISolutionIndexer solutionIndexer)
    {
        _logger = logger;
        _solutionLoader = solutionLoader;
        _indexStore = indexStore;
        _solutionIndexer = solutionIndexer;
    }

    public async Task EnsureWatchingAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_watchers.TryGetValue(normalizedPath, out var existingRegistration)
                && existingRegistration.SlnOnly == slnOnly)
            {
                _logger.LogDebug("Watcher already active for {SolutionPath}.", normalizedPath);
                return;
            }

            if (_watchers.TryRemove(normalizedPath, out var existing))
                existing.Watcher.Dispose();

            var watcher = new SolutionWatcher(
                normalizedPath,
                slnOnly,
                _solutionLoader,
                _indexStore,
                _solutionIndexer,
                _logger);

            try
            {
                await watcher.StartAsync(cancellationToken).ConfigureAwait(false);
                _watchers[normalizedPath] = new ActiveWatcher(watcher, slnOnly);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                watcher.Dispose();
                _logger.LogInformation("Watcher startup canceled for {SolutionPath}.", normalizedPath);
            }
            catch (Exception ex)
            {
                watcher.Dispose();
                _logger.LogError(ex, "Failed to start watcher for {SolutionPath}.", normalizedPath);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task StopWatchingAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_watchers.TryRemove(normalizedPath, out var watcher))
                watcher.Watcher.Dispose();
        }
        finally
        {
            _sync.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Best-effort cleanup on shutdown cancellation.
            acquired = _sync.Wait(0);
        }

        if (!acquired)
            return;

        try
        {
            foreach (var watcher in _watchers.Values)
                watcher.Watcher.Dispose();
            _watchers.Clear();
        }
        finally
        {
            _sync.Release();
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Solution watcher host started.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record ActiveWatcher(SolutionWatcher Watcher, bool SlnOnly);
}
