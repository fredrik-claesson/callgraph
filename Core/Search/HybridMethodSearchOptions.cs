namespace CallGraph.Core.Search;

public sealed class HybridMethodSearchOptions
{
    public int ResultLimit { get; set; } = 80;
    public int LexicalTopK { get; set; } = 200;
    public int MaxCandidatePool { get; set; } = 2000;
    public int MaxPatternQueries { get; set; } = 8;
    public int MinQueryTokenLength { get; set; } = 3;
    public bool EnableSemanticRerank { get; set; } = true;
    public double SemanticWeight { get; set; } = 0.55;
}
