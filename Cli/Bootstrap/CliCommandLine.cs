using CallGraph.Cli;

namespace CallGraph;

internal static class CliCommandLine
{
    public static void PrintUsage(string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            Console.Error.WriteLine(error);

        Console.WriteLine(
            """
            CallGraph CLI

            Usage:
              callgraph --index <solution.sln>
              callgraph --reindex [solution.sln]
              callgraph --clear
              callgraph query "<SQL>"
              callgraph analyze --filepath <file.cs> [--method <name>] [--depth <n>] [--direction <inbound|outbound|bi-directional>] [--visibility <external|internal>] [--solutionPath <path>] [--solutionId <id>]

            Notes:
              - query runs read-only SQL against the indexed SQLite database and prints tab-separated rows.
              - analyze traverses the indexed call graph; filePath must be an absolute .cs path.
            """);
    }

    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        if (args.Length > 0 && !IsOption(args[0]))
        {
            var commandName = NormalizeToolCommandName(args[0]);

            if (string.Equals(commandName, "query", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2 || IsOption(args[1]))
                {
                    options = new CliOptions(null, false, null, false, null);
                    error = "query requires a SQL statement: callgraph query \"<SQL>\"";
                    return false;
                }

                var sqlOptions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["sql"] = args[1] };
                options = new CliOptions(null, false, null, false, new ToolCommand("query", sqlOptions));
                error = null;
                return true;
            }

            if (!TryParseToolOptions(args, 1, out var commandOptions, out error))
            {
                options = new CliOptions(null, false, null, false, null);
                return false;
            }

            options = new CliOptions(null, false, null, false, new ToolCommand(commandName, commandOptions));
            return true;
        }

        string? indexPath = null;
        var reindexEnabled = false;
        string? reindexPath = null;
        var clearEnabled = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--index":
                    if (!TryReadValue(args, ref i, out indexPath, out error))
                    {
                        options = new CliOptions(null, false, null, false, null);
                        return false;
                    }
                    break;
                case "--reindex":
                    reindexEnabled = true;
                    if (TryPeekValue(args, i + 1, out var possibleReindexPath))
                    {
                        reindexPath = possibleReindexPath;
                        i++;
                    }
                    break;
                case "--clear":
                    clearEnabled = true;
                    break;
                case "--help":
                case "-h":
                    options = new CliOptions(null, false, null, false, null);
                    error = null;
                    return false;
                default:
                    options = new CliOptions(null, false, null, false, null);
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        if (indexPath is not null && reindexEnabled)
        {
            options = new CliOptions(null, false, null, false, null);
            error = "Use either --index or --reindex, not both.";
            return false;
        }

        options = new CliOptions(indexPath, reindexEnabled, reindexPath, clearEnabled, null);
        error = null;
        return true;
    }

    private static bool TryParseToolOptions(
        string[] args,
        int startIndex,
        out IReadOnlyDictionary<string, string?> parsed,
        out string? error)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = startIndex; i < args.Length; i++)
        {
            var arg = args[i];
            if (!IsOption(arg))
            {
                parsed = new Dictionary<string, string?>();
                error = $"Unexpected token: {arg}. Use --key value options for subcommands.";
                return false;
            }

            var key = arg[2..];
            if (dict.ContainsKey(key))
            {
                parsed = new Dictionary<string, string?>();
                error = $"Duplicate option: {arg}";
                return false;
            }

            if (TryPeekValue(args, i + 1, out var value))
            {
                dict[key] = value;
                i++;
            }
            else
            {
                dict[key] = null;
            }
        }

        parsed = dict;
        error = null;
        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value, out string? error)
    {
        if (!TryPeekValue(args, index + 1, out value))
        {
            error = $"Missing value for {args[index]}.";
            return false;
        }

        index++;
        error = null;
        return true;
    }

    private static bool TryPeekValue(string[] args, int index, out string value)
    {
        if (index >= args.Length || IsOption(args[index]))
        {
            value = string.Empty;
            return false;
        }

        value = args[index];
        return true;
    }

    private static bool IsOption(string value)
        => value.StartsWith("--", StringComparison.Ordinal);

    private static string NormalizeToolCommandName(string commandName)
    {
        if (string.Equals(commandName, "analyze-callgraph", StringComparison.OrdinalIgnoreCase))
            return "analyze";

        return commandName;
    }
}

internal sealed record CliOptions(
    string? IndexPath,
    bool ReindexEnabled,
    string? ReindexPath,
    bool ClearEnabled,
    ToolCommand? ToolCommand);

internal enum CliAction
{
    None,
    Index,
    Reindex,
    Clear
}

internal sealed record NormalizedLifecycleOptions(
    CliAction? Action,
    string? ActionPath,
    bool ClearEnabled)
{
    public string? Error { get; init; }

    public NormalizedLifecycleOptions(CliAction? action, string? actionPath, string? error = null)
        : this(action, actionPath, false)
    {
        Error = error;
    }
}
