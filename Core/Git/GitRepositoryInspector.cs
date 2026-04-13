using System.Diagnostics;

namespace CallGraph.Core.Git;

public sealed class GitRepositoryInspector : IGitRepositoryInspector
{
    public async Task<GitRepositoryInfo?> TryGetRepositoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        var workingDirectory = ResolveWorkingDirectory(path);

        var inWorkTree = await ExecuteGitAsync(workingDirectory, ["rev-parse", "--is-inside-work-tree"], cancellationToken)
            .ConfigureAwait(false);
        if (!inWorkTree.Success || !string.Equals(inWorkTree.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            return null;

        var rootResult = await ExecuteGitAsync(workingDirectory,
                ["rev-parse", "--path-format=absolute", "--show-toplevel"],
                cancellationToken)
            .ConfigureAwait(false);
        if (!rootResult.Success || string.IsNullOrWhiteSpace(rootResult.Stdout))
            return null;

        var repositoryRoot = Path.GetFullPath(rootResult.Stdout.Trim());

        var commonDirResult = await ExecuteGitAsync(workingDirectory,
                ["rev-parse", "--path-format=absolute", "--git-common-dir"],
                cancellationToken)
            .ConfigureAwait(false);

        var gitCommonDirectory = commonDirResult.Success && !string.IsNullOrWhiteSpace(commonDirResult.Stdout)
            ? Path.GetFullPath(commonDirResult.Stdout.Trim())
            : Path.Combine(repositoryRoot, ".git");

        var headResult = await ExecuteGitAsync(workingDirectory, ["rev-parse", "HEAD"], cancellationToken)
            .ConfigureAwait(false);

        var headCommit = headResult.Success && !string.IsNullOrWhiteSpace(headResult.Stdout)
            ? headResult.Stdout.Trim()
            : null;

        return new GitRepositoryInfo(repositoryRoot, gitCommonDirectory, headCommit);
    }

    public async Task<IReadOnlyList<GitPathChange>> GetCommitChangesAsync(
        string repositoryRoot,
        string fromCommit,
        string toCommit,
        CancellationToken cancellationToken)
    {
        if (string.Equals(fromCommit, toCommit, StringComparison.Ordinal))
            return Array.Empty<GitPathChange>();

        var diffResult = await ExecuteGitAsync(
                repositoryRoot,
                ["diff", "--name-status", "--find-renames", $"{fromCommit}..{toCommit}"],
                cancellationToken)
            .ConfigureAwait(false);

        if (!diffResult.Success)
            return Array.Empty<GitPathChange>();

        return ParseNameStatus(diffResult.Stdout);
    }

    public async Task<IReadOnlyList<GitPathChange>> GetPendingChangesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var trackedResult = await ExecuteGitAsync(
                repositoryRoot,
                ["diff", "--name-status", "--find-renames", "HEAD"],
                cancellationToken)
            .ConfigureAwait(false);

        var changes = trackedResult.Success
            ? ParseNameStatus(trackedResult.Stdout)
            : new List<GitPathChange>();

        var untrackedResult = await ExecuteGitAsync(
                repositoryRoot,
                ["ls-files", "--others", "--exclude-standard"],
                cancellationToken)
            .ConfigureAwait(false);

        if (untrackedResult.Success)
        {
            foreach (var line in SplitLines(untrackedResult.Stdout))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                changes.Add(new GitPathChange(line.Trim(), GitPathChangeKind.Untracked));
            }
        }

        return changes;
    }

    private static string ResolveWorkingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            return fullPath;

        var directory = Path.GetDirectoryName(fullPath);
        return string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;
    }

    private static List<GitPathChange> ParseNameStatus(string output)
    {
        var parsed = new List<GitPathChange>();

        foreach (var line in SplitLines(output))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var columns = line.Split('\t');
            if (columns.Length < 2)
                continue;

            var status = columns[0];
            var statusCode = status[0];

            if ((statusCode == 'R' || statusCode == 'C') && columns.Length >= 3)
            {
                var kind = statusCode == 'R' ? GitPathChangeKind.Renamed : GitPathChangeKind.Copied;
                parsed.Add(new GitPathChange(columns[2], kind, columns[1]));
                continue;
            }

            var kindForStatus = statusCode switch
            {
                'A' => GitPathChangeKind.Added,
                'M' => GitPathChangeKind.Modified,
                'D' => GitPathChangeKind.Deleted,
                'T' => GitPathChangeKind.TypeChanged,
                'U' => GitPathChangeKind.Unmerged,
                'X' => GitPathChangeKind.Unknown,
                'B' => GitPathChangeKind.Unknown,
                _ => GitPathChangeKind.Unknown
            };

            parsed.Add(new GitPathChange(columns[1], kindForStatus));
        }

        return parsed;
    }

    private static IEnumerable<string> SplitLines(string value)
        => value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<GitCommandResult> ExecuteGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
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

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch
        {
            return GitCommandResult.Failed;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new GitCommandResult(process.ExitCode == 0, stdout, stderr);
    }

    private sealed record GitCommandResult(bool Success, string Stdout, string Stderr)
    {
        public static readonly GitCommandResult Failed = new(false, string.Empty, string.Empty);
    }
}
