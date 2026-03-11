namespace CallGraph.Cli;

internal sealed record DaemonRequest(string Command, Dictionary<string, string?> Options);
internal sealed record DaemonResponse(int ExitCode, string? Stdout, string? Stderr);
