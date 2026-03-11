namespace CallGraph.Cli;

internal static class CliInputHelpers
{
    public static string? TryGetString(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public static bool HasFlag(IReadOnlyDictionary<string, string?> options, string key)
    {
        if (!options.TryGetValue(key, out var value))
            return false;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        return bool.TryParse(value, out var parsed) && parsed;
    }

    public static int? TryGetInt(IReadOnlyDictionary<string, string?> options, string key, out string? error)
    {
        error = null;
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        if (int.TryParse(value, out var parsed))
            return parsed;

        error = $"Invalid integer value for --{key}: {value}";
        return null;
    }

    public static bool TryGetBool(
        IReadOnlyDictionary<string, string?> options,
        string key,
        bool defaultValue,
        out string? error)
    {
        error = null;
        if (!options.TryGetValue(key, out var value))
            return defaultValue;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        error = $"Invalid boolean value for --{key}: {value} (expected true/false)";
        return defaultValue;
    }

    public static (string? Path, string? Error) NormalizeSolutionPath(string path, string optionName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (null, $"{optionName} requires a path to a .sln file.");

        var normalized = Path.GetFullPath(path);
        if (!normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return (null, $"{optionName} must point to a .sln file.");

        if (!File.Exists(normalized))
            return (null, $"{optionName} path does not exist: {normalized}");

        return (normalized, null);
    }

    public static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
