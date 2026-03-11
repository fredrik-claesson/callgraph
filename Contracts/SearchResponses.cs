namespace CallGraph.Contracts;

public sealed record SearchFileMatch(string SolutionId, string SolutionPath, string FilePath);

public sealed record SearchMethodMatch(string SolutionId, string SolutionPath, Node Method);

public sealed record SearchFileResponse(IReadOnlyList<SearchFileMatch> Matches);

public sealed record SearchMethodResponse(IReadOnlyList<SearchMethodMatch> Matches);
