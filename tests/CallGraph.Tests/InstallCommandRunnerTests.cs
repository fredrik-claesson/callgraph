namespace CallGraph.Tests;

public sealed class InstallCommandRunnerTests
{
    [Fact]
    public void ResolveDefaultUnixBinDirectory_SkipsTransientPathEntries()
    {
        var transientDir = CreateTempDir(Path.GetTempPath(), "callgraph-transient");
        var stableBinDir = CreateTempDir(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".callgraph-install-tests"),
            "bin");

        try
        {
            var path = $"{transientDir}:{stableBinDir}";
            var resolved = InstallCommandRunner.ResolveDefaultUnixBinDirectory(
                home: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path: path);

            Assert.Equal(Path.GetFullPath(stableBinDir), Path.GetFullPath(resolved));
        }
        finally
        {
            CleanupDir(transientDir);
            CleanupDir(Path.GetDirectoryName(stableBinDir)!);
        }
    }

    [Fact]
    public void ResolveDefaultUnixBinDirectory_PrefersBinDirectoriesOverWritableNonBinEntries()
    {
        var stableRoot = CreateTempDir(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".callgraph-install-tests"),
            "stable-root");
        var nonBinDir = Path.Combine(stableRoot, "tools");
        var binDir = Path.Combine(stableRoot, "bin");
        Directory.CreateDirectory(nonBinDir);
        Directory.CreateDirectory(binDir);

        try
        {
            var path = $"{nonBinDir}:{binDir}";
            var resolved = InstallCommandRunner.ResolveDefaultUnixBinDirectory(
                home: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path: path);

            Assert.Equal(Path.GetFullPath(binDir), Path.GetFullPath(resolved));
        }
        finally
        {
            CleanupDir(stableRoot);
        }
    }

    [Fact]
    public void ResolveDefaultUnixBinDirectory_FallsBackToHomeLocalBinWhenPathHasNoWritableEntries()
    {
        var home = CreateTempDir(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".callgraph-install-tests"),
            "home-root");

        try
        {
            var resolved = InstallCommandRunner.ResolveDefaultUnixBinDirectory(home, "/does/not/exist:/still/does/not/exist");
            var expected = Path.Combine(home, ".local", "bin");
            Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(resolved));
        }
        finally
        {
            CleanupDir(home);
        }
    }

    private static string CreateTempDir(string parent, string suffix)
    {
        var dir = Path.Combine(parent, $"{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
