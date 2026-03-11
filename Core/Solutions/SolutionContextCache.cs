using Microsoft.Extensions.Logging;

namespace CallGraph.Core.Solutions;

public interface ISolutionContextCache
{
    Task<SolutionLoadContext> GetOrLoadAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken);
}

public sealed class SolutionContextCache : ISolutionContextCache, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(10);

    private readonly ISolutionLoader _solutionLoader;
    private readonly ILogger<SolutionContextCache> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private SolutionLoadContext? _cachedContext;
    private string? _cachedSolutionPath;
    private bool _cachedSlnOnly;
    private DateTime _cachedSolutionLastWriteUtc;
    private DateTime _cachedLastUsedUtc;
    private bool _disposed;
    private int _disposeStarted;

    public SolutionContextCache(ISolutionLoader solutionLoader, ILogger<SolutionContextCache> logger)
    {
        _solutionLoader = solutionLoader;
        _logger = logger;
    }

    public async Task<SolutionLoadContext> GetOrLoadAsync(
        string solutionPath,
        bool slnOnly,
        CancellationToken cancellationToken)
    {
        var normalizedSolutionPath = Path.GetFullPath(solutionPath);
        var nowUtc = DateTime.UtcNow;
        var lastWriteUtc = File.GetLastWriteTimeUtc(normalizedSolutionPath);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SolutionContextCache));

            var hasCached = _cachedContext is not null;
            var differentSolution = !hasCached ||
                                   !string.Equals(_cachedSolutionPath, normalizedSolutionPath, StringComparison.OrdinalIgnoreCase) ||
                                   _cachedSlnOnly != slnOnly;
            var slnChanged = hasCached && _cachedSolutionLastWriteUtc != lastWriteUtc;
            var expired = hasCached && (nowUtc - _cachedLastUsedUtc) > DefaultTtl;

            if (differentSolution || slnChanged || expired)
            {
                if (_cachedContext is not null)
                {
                    _logger.LogTrace("Disposing cached solution context (invalidate/refresh).");
                    await _cachedContext.DisposeAsync().ConfigureAwait(false);
                    _cachedContext = null;
                }

                _logger.LogTrace(
                    "Loading solution context for {SolutionPath} (slnOnly: {SlnOnly}).",
                    normalizedSolutionPath,
                    slnOnly);

                _cachedContext = await _solutionLoader
                    .LoadAsync(normalizedSolutionPath, slnOnly, cancellationToken)
                    .ConfigureAwait(false);

                _cachedSolutionPath = normalizedSolutionPath;
                _cachedSlnOnly = slnOnly;
                _cachedSolutionLastWriteUtc = lastWriteUtc;
            }

            _cachedLastUsedUtc = nowUtc;
            return _cachedContext!;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
            return;

        try
        {
            await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_cachedContext is not null)
            {
                await _cachedContext.DisposeAsync().ConfigureAwait(false);
                _cachedContext = null;
            }

            _cachedSolutionPath = null;
            _cachedSlnOnly = default;
            _cachedSolutionLastWriteUtc = default;
            _cachedLastUsedUtc = default;
        }
        finally
        {
            _lock.Release();
        }
    }
}
