namespace CallGraph.Core.Extraction;

public sealed record MethodSourceExtractionRequest(
    string FilePath,
    string? MethodName,
    string? ContainingType,
    string? Signature,
    int? StartLine,
    string Mode);

public sealed record MethodSourceExtractionResult(
    bool Success,
    string? Error,
    MethodSourceMatch? Match,
    IReadOnlyList<MethodSourceCandidate>? Candidates)
{
    public static MethodSourceExtractionResult Failure(string error, IReadOnlyList<MethodSourceCandidate>? candidates = null)
        => new(false, error, null, candidates);

    public static MethodSourceExtractionResult Ok(MethodSourceMatch match)
        => new(true, null, match, null);
}

public sealed record MethodSourceCandidate(
    string MethodName,
    string? ContainingType,
    string Signature,
    int StartLine,
    int EndLine);

public sealed record MethodSourceMatch(
    string FilePath,
    string MethodName,
    string? ContainingType,
    string Signature,
    int StartLine,
    int EndLine,
    int StartByte,
    int EndByte,
    string Mode,
    string Content);
