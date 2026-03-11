namespace CallGraph.Contracts;

/// <summary>
/// MCP error codes following JSON-RPC 2.0 conventions.
/// Standard codes: -32768 to -32000 are reserved by JSON-RPC.
/// Application-specific codes: -32000 to -32099 are available for custom errors.
/// </summary>
public static class McpErrorCodes
{
    // JSON-RPC standard errors
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // CallGraph application-specific errors (-32000 to -32099)

    /// <summary>
    /// Search query returned too many results and must be narrowed down.
    /// Data payload includes: hitCount (int), limit (int), message (string).
    /// </summary>
    public const int TooManyHits = -32000;

    /// <summary>
    /// Solution index not found or not ready for queries.
    /// Data payload includes: solutionPath (string), message (string).
    /// </summary>
    public const int IndexNotReady = -32001;

    /// <summary>
    /// File path matches multiple indexed solutions - disambiguation required.
    /// Data payload includes: filePath (string), solutions (array), message (string).
    /// </summary>
    public const int AmbiguousSolution = -32002;

    /// <summary>
    /// Analysis targets not found in the indexed solution.
    /// Data payload includes: targets (array), message (string).
    /// </summary>
    public const int TargetsNotFound = -32003;

    /// <summary>
    /// Validation failed for mutually exclusive parameters.
    /// Data payload includes: conflictingParams (array), message (string).
    /// </summary>
    public const int ValidationFailed = -32004;
}

/// <summary>
/// Data payload for "too many hits" error.
/// </summary>
public sealed record TooManyHitsErrorData(
    int HitCount,
    int Limit,
    string Message);

/// <summary>
/// Data payload for validation errors.
/// </summary>
public sealed record ValidationErrorData(
    string[] ConflictingParams,
    string Message);

/// <summary>
/// Data payload for index not ready errors.
/// </summary>
public sealed record IndexNotReadyErrorData(
    string? SolutionPath,
    string Message);

/// <summary>
/// Data payload for ambiguous solution errors.
/// </summary>
public sealed record AmbiguousSolutionErrorData(
    string FilePath,
    string[] Solutions,
    string Message);

/// <summary>
/// Data payload for targets not found errors.
/// </summary>
public sealed record TargetsNotFoundErrorData(
    string[] Targets,
    string Message);
