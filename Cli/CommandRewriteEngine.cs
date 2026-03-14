using System.Text;
using System.Text.RegularExpressions;

namespace CallGraph.Cli;

internal static class CommandRewriteEngine
{
    private static readonly HashSet<string> OptionsWithValue = new(StringComparer.Ordinal)
    {
        "-e", "--regexp",
        "-g", "--glob",
        "--iglob",
        "--include",
        "--exclude",
        "-m", "--max-count",
        "--type", "-t"
    };

    private static readonly char[] RegexHeavyCharacters = ['[', ']', '(', ')', '{', '}', '+', '?', '|', '\\', '^', '$'];

    public static bool TryRewrite(string command, out string rewritten)
    {
        rewritten = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var trimmed = command.Trim();
        if (trimmed.StartsWith("callgraph ", StringComparison.OrdinalIgnoreCase))
            return false;

        if (ContainsHardControlOperators(trimmed))
            return false;

        var pipelineSegments = SplitPipeline(trimmed);
        if (pipelineSegments.Count == 0)
            return false;

        var tokens = Tokenize(pipelineSegments[0]);
        if (tokens.Count == 0)
            return false;

        if (TryRewriteFindPipeToXargsGrep(tokens, pipelineSegments, out rewritten))
            return true;

        if (TryRewriteFind(tokens, out rewritten))
            return true;

        if (TryRewriteLsListing(tokens, pipelineSegments, out rewritten))
            return true;

        if (TryRewriteRgLike(tokens, "rg", out rewritten))
            return true;

        if (TryRewriteRgLike(tokens, "grep", out rewritten))
            return true;

        return false;
    }

    private static bool TryRewriteFindPipeToXargsGrep(
        IReadOnlyList<string> firstSegmentTokens,
        IReadOnlyList<string> pipelineSegments,
        out string rewritten)
    {
        rewritten = string.Empty;
        if (pipelineSegments.Count < 2)
            return false;

        if (!TryParseFind(firstSegmentTokens, out var rootPath, out var patterns))
            return false;

        if (!patterns.Any(LooksLikeCsPattern))
            return false;

        var xargsTokens = Tokenize(pipelineSegments[1]);
        if (xargsTokens.Count < 2 || !string.Equals(xargsTokens[0], "xargs", StringComparison.Ordinal))
            return false;

        var grepTokens = xargsTokens.Skip(1).ToList();
        if (!TryParseSearchPattern(grepTokens, grepTokens[0], out var pattern, out _, out _))
            return false;

        var builder = new StringBuilder("callgraph search-method --regex --pattern ");
        builder.Append(ShellQuote(pattern));

        var absoluteFolder = TryResolveAbsoluteDirectory(rootPath);
        if (!string.IsNullOrWhiteSpace(absoluteFolder))
        {
            builder.Append(" --folderPath ");
            builder.Append(ShellQuote(absoluteFolder));
        }

        rewritten = builder.ToString();
        return true;
    }

    private static bool TryRewriteFind(IReadOnlyList<string> tokens, out string rewritten)
    {
        rewritten = string.Empty;
        if (!TryParseFind(tokens, out var rootPath, out var patterns))
            return false;

        var csPatterns = patterns.Where(LooksLikeCsPattern).ToList();
        if (csPatterns.Count == 0)
            return false;

        var absoluteFolder = TryResolveAbsoluteDirectory(rootPath);
        var commands = new List<string>(csPatterns.Count);
        foreach (var pattern in csPatterns)
        {
            var builder = new StringBuilder("callgraph search-file --pattern ");
            builder.Append(ShellQuote(pattern));

            if (!string.IsNullOrWhiteSpace(absoluteFolder))
            {
                builder.Append(" --folderPath ");
                builder.Append(ShellQuote(absoluteFolder));
            }

            commands.Add(builder.ToString());
        }

        rewritten = string.Join(" && ", commands);
        return true;
    }

    private static bool TryRewriteLsListing(
        IReadOnlyList<string> firstSegmentTokens,
        IReadOnlyList<string> pipelineSegments,
        out string rewritten)
    {
        rewritten = string.Empty;

        if (firstSegmentTokens.Count == 0 || !string.Equals(firstSegmentTokens[0], "ls", StringComparison.Ordinal))
            return false;

        string? directory = null;
        for (var i = 1; i < firstSegmentTokens.Count; i++)
        {
            var token = firstSegmentTokens[i];
            if (token.StartsWith("-", StringComparison.Ordinal))
                continue;

            directory ??= token;
        }

        if (string.IsNullOrWhiteSpace(directory))
            return false;

        var absoluteFolder = TryResolveAbsoluteDirectory(directory);
        if (string.IsNullOrWhiteSpace(absoluteFolder) && LooksLikeCsPattern(directory))
        {
            var parent = Path.GetDirectoryName(directory);
            absoluteFolder = TryResolveAbsoluteDirectory(parent);
        }
        if (string.IsNullOrWhiteSpace(absoluteFolder))
            return false;

        var explicitCsIntent = firstSegmentTokens.Any(LooksLikeCsPattern);
        var hasGrepFilter = TryParsePipeGrepFilter(pipelineSegments, out var grepPattern, out var ignoreCase);
        var grepHasCsIntent = hasGrepFilter && LooksLikeCsPattern(grepPattern);

        if (!explicitCsIntent && !grepHasCsIntent)
            return false;

        if (hasGrepFilter && !string.IsNullOrWhiteSpace(grepPattern))
        {
            var escaped = Regex.Escape(grepPattern);
            var regexPattern = ignoreCase
                ? $"(?i).*{escaped}.*\\.cs$"
                : $".*{escaped}.*\\.cs$";
            rewritten = $"callgraph search-file --regex --pattern {ShellQuote(regexPattern)} --folderPath {ShellQuote(absoluteFolder)}";
            return true;
        }

        rewritten = $"callgraph search-file --pattern '*.cs' --folderPath {ShellQuote(absoluteFolder)}";
        return true;
    }

    private static bool TryParseFind(
        IReadOnlyList<string> tokens,
        out string? rootPath,
        out List<string> patterns)
    {
        rootPath = null;
        patterns = [];

        if (tokens.Count == 0 || !string.Equals(tokens[0], "find", StringComparison.Ordinal))
            return false;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (string.Equals(token, "-name", StringComparison.Ordinal) ||
                string.Equals(token, "-iname", StringComparison.Ordinal))
            {
                if (i + 1 < tokens.Count)
                {
                    patterns.Add(tokens[i + 1]);
                    i++;
                }
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
                continue;

            rootPath ??= token;
        }

        return true;
    }

    private static bool TryRewriteRgLike(IReadOnlyList<string> tokens, string commandName, out string rewritten)
    {
        rewritten = string.Empty;
        if (!TryParseSearchPattern(tokens, commandName, out var pattern, out var explicitScopes, out var sawCsScopeHint))
            return false;

        if (!sawCsScopeHint && !explicitScopes.Any(LooksLikeCsPattern))
            return false;

        var isRegexPattern = pattern.IndexOfAny(RegexHeavyCharacters) >= 0;

        if (LooksLikeCsPattern(pattern))
        {
            rewritten = isRegexPattern
                ? $"callgraph search-file --regex --pattern {ShellQuote(pattern)}"
                : $"callgraph search-file --pattern {ShellQuote(pattern)}";
            return true;
        }

        var builder = new StringBuilder(
            isRegexPattern
                ? "callgraph search-method --regex --pattern "
                : "callgraph search-method --keywords ");
        builder.Append(ShellQuote(pattern));

        var absoluteCsFile = explicitScopes
            .Select(TryResolveAbsoluteCsFile)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(absoluteCsFile))
        {
            builder.Append(" --filePath ");
            builder.Append(ShellQuote(absoluteCsFile));
            rewritten = builder.ToString();
            return true;
        }

        var absoluteFolder = explicitScopes
            .Select(TryResolveAbsoluteDirectory)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(absoluteFolder))
        {
            builder.Append(" --folderPath ");
            builder.Append(ShellQuote(absoluteFolder));
        }

        rewritten = builder.ToString();
        return true;
    }

    private static bool TryParseSearchPattern(
        IReadOnlyList<string> tokens,
        string commandName,
        out string pattern,
        out List<string> explicitScopes,
        out bool sawCsScopeHint)
    {
        pattern = string.Empty;
        explicitScopes = [];
        sawCsScopeHint = false;

        if (tokens.Count == 0 || !string.Equals(tokens[0], commandName, StringComparison.Ordinal))
            return false;

        string? capturedPattern = null;
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (string.Equals(token, "-e", StringComparison.Ordinal) ||
                string.Equals(token, "--regexp", StringComparison.Ordinal))
            {
                if (i + 1 < tokens.Count)
                {
                    capturedPattern = tokens[i + 1];
                    i++;
                }
                continue;
            }

            if (OptionsWithValue.Contains(token))
            {
                if (i + 1 < tokens.Count)
                {
                    var value = tokens[i + 1];
                    if (LooksLikeCsPattern(value))
                        sawCsScopeHint = true;
                    i++;
                }
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
                continue;

            if (capturedPattern is null)
            {
                capturedPattern = token;
                continue;
            }

            explicitScopes.Add(token);
            if (LooksLikeCsPattern(token))
                sawCsScopeHint = true;
        }

        if (string.IsNullOrWhiteSpace(capturedPattern))
            return false;

        pattern = capturedPattern;
        return true;
    }

    private static bool TryParsePipeGrepFilter(
        IReadOnlyList<string> pipelineSegments,
        out string? pattern,
        out bool ignoreCase)
    {
        pattern = null;
        ignoreCase = false;

        if (pipelineSegments.Count < 2)
            return false;

        var grepTokens = Tokenize(pipelineSegments[1]);
        if (grepTokens.Count == 0 || !string.Equals(grepTokens[0], "grep", StringComparison.Ordinal))
            return false;

        for (var i = 1; i < grepTokens.Count; i++)
        {
            var token = grepTokens[i];
            if (string.Equals(token, "-i", StringComparison.Ordinal) ||
                string.Equals(token, "--ignore-case", StringComparison.Ordinal))
            {
                ignoreCase = true;
                continue;
            }

            if (string.Equals(token, "-e", StringComparison.Ordinal) ||
                string.Equals(token, "--regexp", StringComparison.Ordinal))
            {
                if (i + 1 < grepTokens.Count)
                {
                    pattern = grepTokens[i + 1];
                    return true;
                }

                return false;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
                continue;

            pattern = token;
            return true;
        }

        return false;
    }

    private static bool ContainsHardControlOperators(string command)
    {
        return command.Contains("&&", StringComparison.Ordinal) ||
               command.Contains("||", StringComparison.Ordinal) ||
               command.Contains(';') ||
               command.Contains('`') ||
               command.Contains("$(", StringComparison.Ordinal);
    }

    private static List<string> SplitPipeline(string command)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                current.Append(ch);
                continue;
            }

            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                current.Append(ch);
                continue;
            }

            if (!inSingle && !inDouble && ch == '|' &&
                (i + 1 >= command.Length || command[i + 1] != '|'))
            {
                var segment = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(segment))
                    segments.Add(segment);
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        var tail = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(tail))
            segments.Add(tail);

        return segments;
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (ch == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                continue;
            }

            if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inSingle && !inDouble)
            {
                if (current.Length == 0)
                    continue;

                tokens.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (ch == '\\' && i + 1 < command.Length && !inSingle)
            {
                i++;
                current.Append(command[i]);
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static bool LooksLikeCsPattern(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains(".cs", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("*.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolveAbsoluteCsFile(string? pathCandidate)
    {
        if (string.IsNullOrWhiteSpace(pathCandidate))
            return null;

        var absolutePath = Path.GetFullPath(pathCandidate);
        if (!absolutePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        return absolutePath;
    }

    private static string? TryResolveAbsoluteDirectory(string? pathCandidate)
    {
        if (string.IsNullOrWhiteSpace(pathCandidate))
            return null;

        var absolutePath = Path.GetFullPath(pathCandidate);
        return Directory.Exists(absolutePath) ? absolutePath : null;
    }

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
