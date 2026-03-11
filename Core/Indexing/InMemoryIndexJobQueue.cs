using System.Threading.Channels;

namespace CallGraph.Core.Indexing;

public sealed class InMemoryIndexJobQueue : IIndexJobQueue
{
    private readonly Channel<IndexJobRequest> _channel = Channel.CreateUnbounded<IndexJobRequest>();

    public ValueTask EnqueueAsync(IndexJobRequest request, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(request, cancellationToken);

    public ValueTask<IndexJobRequest> DequeueAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAsync(cancellationToken);
}
