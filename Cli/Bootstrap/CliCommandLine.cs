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

            Lifecycle usage:
              callgraph --index <solution.sln> [--watch]
              callgraph --reindex [solution.sln] [--watch]
              callgraph --watch [solution.sln]
              callgraph --clear

            Analysis usage:
              callgraph install [--home <path>] [--binDir <path>] [--skip-skills] [--skip-shim] [--skip-path]
              callgraph rewrite --command <shell command>
              callgraph list-solutions [--no-daemon]
              callgraph search-file --pattern <pattern> [--regex] [--includeTests <true|false>] [--solutionPath <path>] [--solutionId <id>] [--folderPath <path>] [--filePath <path>] [--no-daemon]
              callgraph search-method --keywords <keywords> [--regex] [--pattern <pattern>] [--includeTests <true|false>] [--solutionPath <path>] [--solutionId <id>] [--folderPath <path>] [--filePath <path>] [--no-daemon]
              callgraph list-methods [--visibility <external|internal>] [--includeTests <true|false>] [--solutionPath <path>] [--solutionId <id>] [--folderPath <path>] [--filePath <path>] [--fileList <path>] [--no-daemon]
              callgraph analyze --filepath <file.cs> [--method <name>] [--depth <n>] [--direction <inbound|outbound|bi-directional>] [--visibility <external|internal>] [--includeTests <true|false>] [--solutionPath <path>] [--solutionId <id>] [--no-daemon]
              callgraph get-method-source --filePath <file.cs> [--methodName <name>] [--containingType <type>] [--signature <signature>] [--startLine <n>] [--mode <signature_only|signature_plus_body|body_only|body_without_comments>] [--no-daemon]
              callgraph list-unused --projectPath <project.csproj> --filePath <file.cs> [--no-daemon]
              callgraph list-warnings --projectPath <project.csproj> --filePath <file.cs> [--no-daemon]

            Daemon usage:
              callgraph serve [--pipeName <name>] [--idleMinutes <n>] [--no-watch-indexed]
              callgraph status [--pipeName <name>]
              callgraph stop [--pipeName <name>]

            Notes:
              - Analysis commands auto-start and reuse a background daemon by default.
              - install deploys bundled _claude/_codex/_cursor/_copilot/_opencode only when matching ~/.claude ~/.codex ~/.cursor ~/.copilot ~/.config/opencode directories already exist.
              - install overwrites existing skill/agent/command files in those directories with the bundled versions.
              - install never auto-merges AGENTS.md/CLAUDE.md; it prints manual append instructions instead.
              - Copilot CLI hooks are repository-scoped; install prints a manual copy step for .github/hooks.
              - OpenCode hooks are plugin-based; install deploys the local plugin hook to ~/.config/opencode/plugins.
              - install creates callgraph shim in a writable PATH directory on macOS/Linux (fallback ~/.local/bin), or %LocalAppData%\Programs\callgraph on Windows.
              - on macOS/Linux, install removes duplicate callgraph symlinks found on PATH (keeps the newly installed shim).
              - install updates Windows user PATH unless --skip-path is provided.
              - `serve` idle timeout defaults to 600 minutes (10 hours); override with `--idleMinutes`.
              - `serve` watches all indexed solutions by default; disable with `--no-watch-indexed`.
              - Use --no-daemon to run analysis in one-shot mode.
              - search-file outputs plain text (one file path per line).
              - search-method/list-methods output plain text rows: <filePath[:line]>\t<containingType>\t<methodName>\t<signature>.
              - includeTests defaults to true; set --includeTests false to exclude test-project files from search/list/analyze results.
              - analyze output is plain text, line-based rows:
                - methods: M\t<methodId>\t<filePath[:line]>\t<containingType>\t<methodName>
                - calls: C\t<callerMethodId>\t<calleeMethodId>\t<direction>
              - rewrite prints a rewritten command when a safe CallGraph equivalent exists; otherwise exits non-zero.
              - get-method-source output is structured JSON with exact line/byte span and selected method content.
              - list-methods defaults to --visibility external (public/protected/protected internal), and refreshes listed signatures from live source.
              - list-methods supports --fileList <path> (newline-delimited absolute .cs file paths).
              - list-unused/list-warnings require both --projectPath and --filePath.
              - filePath must be an absolute path to a .cs file.
            """);
    }

    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        if (args.Length > 0 && !IsOption(args[0]))
        {
            var commandName = args[0];
            if (!TryParseToolOptions(args, 1, out var commandOptions, out error))
            {
                options = new CliOptions(null, false, null, false, null, false, null);
                return false;
            }

            options = new CliOptions(null, false, null, false, null, false, new ToolCommand(commandName, commandOptions));
            return true;
        }

        string? indexPath = null;
        var reindexEnabled = false;
        string? reindexPath = null;
        var watchEnabled = false;
        string? watchPath = null;
        var clearEnabled = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--index":
                    if (!TryReadValue(args, ref i, out indexPath, out error))
                    {
                        options = new CliOptions(null, false, null, false, null, false, null);
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
                case "--watch":
                    watchEnabled = true;
                    if (TryPeekValue(args, i + 1, out var possiblePath))
                    {
                        watchPath = possiblePath;
                        i++;
                    }
                    break;
                case "--clear":
                    clearEnabled = true;
                    break;
                case "--help":
                case "-h":
                    options = new CliOptions(null, false, null, false, null, false, null);
                    error = null;
                    return false;
                default:
                    options = new CliOptions(null, false, null, false, null, false, null);
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        if (indexPath is not null && reindexEnabled)
        {
            options = new CliOptions(null, false, null, false, null, false, null);
            error = "Use either --index or --reindex, not both.";
            return false;
        }

        options = new CliOptions(indexPath, reindexEnabled, reindexPath, watchEnabled, watchPath, clearEnabled, null);
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
}

internal sealed record CliOptions(
    string? IndexPath,
    bool ReindexEnabled,
    string? ReindexPath,
    bool WatchEnabled,
    string? WatchPath,
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
    string? WatchPath,
    bool WatchEnabled,
    bool ClearEnabled)
{
    public string? Error { get; init; }

    public NormalizedLifecycleOptions(CliAction? action, string? actionPath, string? watchPath, string? error = null)
        : this(action, actionPath, watchPath, false, false)
    {
        Error = error;
    }
}
