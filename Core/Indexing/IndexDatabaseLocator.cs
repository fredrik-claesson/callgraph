namespace CallGraph.Core.Indexing;

public static class IndexDatabaseLocator
{
    public static string Resolve(string? configuredPath)
        => string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CallGraph",
                "index.db")
            : configuredPath;
}
