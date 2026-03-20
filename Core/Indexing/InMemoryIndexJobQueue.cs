using System.Threading.Channels;
using System.Collections.Concurrent;

namespace CallGraph.Core.Indexing;

public sealed class InMemoryIndexJobQueue : IIndexJobQueue
{
    private const int QueueCapacity = 256;
    private readonly Channel<IndexJobRequest> _channel = Channel.CreateBounded<IndexJobRequest>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly ConcurrentDictionary<string, ActiveJob> _activeJobs = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<IndexJobQueueEnqueueResult> EnqueueAsync(IndexJobRequest request, CancellationToken cancellationToken)
    {
        var jobKey = BuildJobKey(request);
        while (true)
        {
            if (_activeJobs.TryGetValue(jobKey, out var existingJob))
                return new IndexJobQueueEnqueueResult(false, existingJob.JobId, existingJob.IsReindex);

            var activeJob = new ActiveJob(request.JobId, request.IsReindex);
            if (!_activeJobs.TryAdd(jobKey, activeJob))
                continue;

            try
            {
                await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                return new IndexJobQueueEnqueueResult(true, request.JobId, request.IsReindex);
            }
            catch
            {
                if (_activeJobs.TryGetValue(jobKey, out var current) &&
                    string.Equals(current.JobId, request.JobId, StringComparison.Ordinal))
                {
                    _activeJobs.TryRemove(jobKey, out _);
                }

                throw;
            }
        }
    }

    public ValueTask<IndexJobRequest> DequeueAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAsync(cancellationToken);

    public void MarkCompleted(IndexJobRequest request)
    {
        var jobKey = BuildJobKey(request);
        if (_activeJobs.TryGetValue(jobKey, out var activeJob) &&
            string.Equals(activeJob.JobId, request.JobId, StringComparison.Ordinal))
        {
            _activeJobs.TryRemove(jobKey, out _);
        }
    }

    private static string BuildJobKey(IndexJobRequest request)
    {
        var normalizedPath = Path.GetFullPath(request.SolutionPath);
        return string.Create(
            normalizedPath.Length + 4,
            (normalizedPath, request.SlnOnly, request.IsReindex),
            static (span, state) =>
            {
                state.normalizedPath.AsSpan().CopyTo(span);
                var cursor = state.normalizedPath.Length;
                span[cursor++] = '\u0000';
                span[cursor++] = state.SlnOnly ? '1' : '0';
                span[cursor++] = '\u0000';
                span[cursor] = state.IsReindex ? '1' : '0';
            });
    }

    private readonly record struct ActiveJob(string JobId, bool IsReindex);
}
