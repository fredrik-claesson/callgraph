namespace CallGraph.Core.Search;

public interface ISemanticEmbedder
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<float>> ScoreAsync(
        string queryText,
        IReadOnlyList<string> candidateTexts,
        CancellationToken cancellationToken);
}

public sealed class LocalBgeOptions
{
    public bool Enabled { get; set; } = true;
    public string ModelDirectory { get; set; } = "models/bge-small-en-v1.5";
    public int MaxSequenceLength { get; set; } = 128;
}
