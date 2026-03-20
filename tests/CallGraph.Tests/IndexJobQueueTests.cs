using CallGraph.Core.Indexing;

namespace CallGraph.Tests;

public sealed class IndexJobQueueTests
{
    [Fact]
    public async Task EnqueueAsync_DuplicateRequest_ReturnsActiveJob()
    {
        var queue = new InMemoryIndexJobQueue();
        var solutionPath = Path.Combine(Path.GetTempPath(), $"callgraph-queue-{Guid.NewGuid():N}.sln");
        var firstRequest = new IndexJobRequest("job-1", "solution-1", solutionPath, SlnOnly: true, IsReindex: false);
        var duplicateRequest = new IndexJobRequest("job-2", "solution-1", solutionPath, SlnOnly: true, IsReindex: false);

        var first = await queue.EnqueueAsync(firstRequest, CancellationToken.None);
        var duplicate = await queue.EnqueueAsync(duplicateRequest, CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.Equal("job-1", first.ActiveJobId);
        Assert.False(duplicate.Accepted);
        Assert.Equal("job-1", duplicate.ActiveJobId);
    }

    [Fact]
    public async Task MarkCompleted_AllowsFutureEnqueueForSameSolution()
    {
        var queue = new InMemoryIndexJobQueue();
        var solutionPath = Path.Combine(Path.GetTempPath(), $"callgraph-queue-{Guid.NewGuid():N}.sln");
        var firstRequest = new IndexJobRequest("job-1", "solution-1", solutionPath, SlnOnly: true, IsReindex: true);
        var nextRequest = new IndexJobRequest("job-2", "solution-1", solutionPath, SlnOnly: true, IsReindex: true);

        var first = await queue.EnqueueAsync(firstRequest, CancellationToken.None);
        Assert.True(first.Accepted);

        var dequeued = await queue.DequeueAsync(CancellationToken.None);
        Assert.Equal(firstRequest, dequeued);
        queue.MarkCompleted(dequeued);

        var next = await queue.EnqueueAsync(nextRequest, CancellationToken.None);
        Assert.True(next.Accepted);
        Assert.Equal("job-2", next.ActiveJobId);
    }

    [Fact]
    public async Task EnqueueAsync_IndexAndReindex_AreTrackedSeparately()
    {
        var queue = new InMemoryIndexJobQueue();
        var solutionPath = Path.Combine(Path.GetTempPath(), $"callgraph-queue-{Guid.NewGuid():N}.sln");
        var indexRequest = new IndexJobRequest("job-1", "solution-1", solutionPath, SlnOnly: true, IsReindex: false);
        var reindexRequest = new IndexJobRequest("job-2", "solution-1", solutionPath, SlnOnly: true, IsReindex: true);

        var index = await queue.EnqueueAsync(indexRequest, CancellationToken.None);
        var reindex = await queue.EnqueueAsync(reindexRequest, CancellationToken.None);

        Assert.True(index.Accepted);
        Assert.True(reindex.Accepted);
    }
}
