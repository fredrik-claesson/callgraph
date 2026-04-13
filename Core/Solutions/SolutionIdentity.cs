using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CallGraph.Core.Solutions;

public static class SolutionIdentity
{
    public static string FromPath(string solutionPath)
        => FromPath(solutionPath, slnOnly: true, Environment.CurrentDirectory);

    public static string FromPath(string solutionPath, bool slnOnly)
        => FromPath(solutionPath, slnOnly, Environment.CurrentDirectory);

    public static string FromPath(string solutionPath, string basePath)
        => FromPath(solutionPath, slnOnly: true, basePath);

    public static string FromPath(string solutionPath, bool slnOnly, string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        var normalizedBasePath = Path.GetFullPath(basePath);
        var normalizedSolutionPath = Path.IsPathRooted(solutionPath)
            ? Path.GetFullPath(solutionPath)
            : Path.GetFullPath(solutionPath, normalizedBasePath);

        var identityMaterial = BuildIdentityMaterial(normalizedSolutionPath, slnOnly);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identityMaterial));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildIdentityMaterial(string normalizedSolutionPath, bool slnOnly)
    {
        if (TryResolveGitContext(normalizedSolutionPath, out var repositoryRoot, out var commonDirectory))
        {
            var normalizedRoot = TryResolveRealPath(repositoryRoot) ?? repositoryRoot;
            var normalizedCommon = TryResolveRealPath(commonDirectory) ?? commonDirectory;
            var normalizedSolution = TryResolveRealPath(normalizedSolutionPath) ?? normalizedSolutionPath;
            var relativeSolutionPath = Path.GetRelativePath(normalizedRoot, normalizedSolution)
                .Replace('\\', '/');

            return $"git:{normalizedCommon}|{relativeSolutionPath}|slnOnly:{(slnOnly ? "1" : "0")}";
        }

        var workingDirectory = Directory.Exists(normalizedSolutionPath)
            ? normalizedSolutionPath
            : Path.GetDirectoryName(normalizedSolutionPath) ?? Environment.CurrentDirectory;

        repositoryRoot = TryRunGit(workingDirectory, "rev-parse", "--path-format=absolute", "--show-toplevel")
                         ?? TryRunGit(workingDirectory, "rev-parse", "--show-toplevel");
        commonDirectory = TryRunGit(workingDirectory, "rev-parse", "--path-format=absolute", "--git-common-dir")
                          ?? TryRunGit(workingDirectory, "rev-parse", "--git-common-dir");

        if (!string.IsNullOrWhiteSpace(repositoryRoot) && !string.IsNullOrWhiteSpace(commonDirectory))
        {
            var normalizedRoot = TryResolveRealPath(NormalizeGitPath(repositoryRoot, workingDirectory))
                                 ?? NormalizeGitPath(repositoryRoot, workingDirectory);
            var normalizedCommon = TryResolveRealPath(NormalizeGitPath(commonDirectory, workingDirectory))
                                   ?? NormalizeGitPath(commonDirectory, workingDirectory);
            var normalizedSolution = TryResolveRealPath(normalizedSolutionPath) ?? normalizedSolutionPath;
            var relativeSolutionPath = Path.GetRelativePath(normalizedRoot, normalizedSolution)
                .Replace('\\', '/');

            return $"git:{normalizedCommon}|{relativeSolutionPath}|slnOnly:{(slnOnly ? "1" : "0")}";
        }

        return $"path:{normalizedSolutionPath.Replace('\\', '/')}|slnOnly:{(slnOnly ? "1" : "0")}";
    }

    private static bool TryResolveGitContext(
        string normalizedSolutionPath,
        out string repositoryRoot,
        out string commonDirectory)
    {
        repositoryRoot = string.Empty;
        commonDirectory = string.Empty;

        var current = Directory.Exists(normalizedSolutionPath)
            ? normalizedSolutionPath
            : Path.GetDirectoryName(normalizedSolutionPath);

        while (!string.IsNullOrWhiteSpace(current))
        {
            var markerPath = Path.Combine(current, ".git");
            if (Directory.Exists(markerPath))
            {
                repositoryRoot = Path.GetFullPath(current);
                commonDirectory = Path.GetFullPath(markerPath);
                return true;
            }

            if (File.Exists(markerPath))
            {
                string raw;
                try
                {
                    raw = File.ReadAllText(markerPath).Trim();
                }
                catch
                {
                    raw = string.Empty;
                }

                if (raw.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    var gitDirToken = raw["gitdir:".Length..].Trim();
                    var gitDir = NormalizeGitPath(gitDirToken, current);
                    repositoryRoot = Path.GetFullPath(current);
                    commonDirectory = DeriveCommonDirectoryFromGitDir(gitDir);
                    return true;
                }
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static string DeriveCommonDirectoryFromGitDir(string gitDir)
    {
        var normalized = gitDir.Replace('\\', '/');
        var marker = "/worktrees/";
        var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index > 0)
        {
            var common = normalized[..index];
            return Path.GetFullPath(common.Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.GetFullPath(gitDir);
    }

    private static string? TryRunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeGitPath(string rawPath, string workingDirectory)
    {
        var trimmed = rawPath.Trim();
        if (Path.IsPathRooted(trimmed))
            return Path.GetFullPath(trimmed);

        return Path.GetFullPath(trimmed, workingDirectory);
    }

    private static string? TryResolveRealPath(string path)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var startInfo = new ProcessStartInfo
        {
            FileName = "realpath",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            return output.Trim();
        }
        catch
        {
            return null;
        }
    }
}
