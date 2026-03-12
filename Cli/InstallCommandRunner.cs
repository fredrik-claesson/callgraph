using System.Runtime.InteropServices;
using CallGraph.Cli;

namespace CallGraph;

internal static class InstallCommandRunner
{
    private static readonly string[] AssetDirectories = ["_claude", "_codex", "_cursor"];
    private static readonly string[] SectionTemplateNames = ["AGENTS.md", "CLAUDE.md"];

    public static async Task<ToolExecutionResult> RunAsync(ToolCommand tool, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        var installSkills = !CliInputHelpers.HasFlag(tool.Options, "skip-skills");
        var installShims = !CliInputHelpers.HasFlag(tool.Options, "skip-shim");

        var homeOverride = CliInputHelpers.TryGetString(tool.Options, "home");
        var homeDir = ResolveHomeDirectory(homeOverride);
        if (homeDir is null)
            return ToolExecutionResult.FromError("Unable to resolve user home directory. Provide --home <path>.");

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            return ToolExecutionResult.FromError("Unable to resolve current executable path.");

        var sourceRoot = ResolveSourceRoot();
        if (sourceRoot is null)
        {
            return ToolExecutionResult.FromError(
                "Install assets not found next to executable. Ensure publish output includes _claude/_codex/_cursor.");
        }

        var messages = new List<string>();

        if (installSkills)
        {
            foreach (var sourceName in AssetDirectories)
            {
                var sourcePath = Path.Combine(sourceRoot, sourceName);
                if (!Directory.Exists(sourcePath))
                    continue;

                var targetRootName = sourceName.StartsWith("_", StringComparison.Ordinal)
                    ? $".{sourceName[1..]}"
                    : sourceName;
                var targetPath = Path.Combine(homeDir, targetRootName);

                if (!Directory.Exists(targetPath))
                {
                    messages.Add($"Skipped {sourceName}: target directory does not exist ({targetPath}).");
                    continue;
                }

                CopyDirectory(sourcePath, targetPath);
                messages.Add($"Deployed {sourceName} -> {targetPath}");
                messages.AddRange(GetManualSectionInstructions(sourcePath, targetPath));
            }
        }

        if (installShims)
        {
            if (OperatingSystem.IsWindows())
            {
                var shimResult = InstallWindowsShim(processPath, tool.Options);
                if (shimResult.Error is not null)
                    return ToolExecutionResult.FromError(shimResult.Error);

                messages.AddRange(shimResult.Messages);
            }
            else
            {
                var shimResult = InstallUnixShim(processPath, tool.Options);
                if (shimResult.Error is not null)
                    return ToolExecutionResult.FromError(shimResult.Error);

                messages.AddRange(shimResult.Messages);
            }
        }

        if (messages.Count == 0)
            messages.Add("Nothing to install.");

        return new ToolExecutionResult(0, string.Join(Environment.NewLine, messages), null);
    }

    private static string? ResolveSourceRoot()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            candidates.Add(AppContext.BaseDirectory);

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDir))
                candidates.Add(processDir);
        }

        candidates.Add(Environment.CurrentDirectory);

        foreach (var candidate in candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (candidate is null)
                continue;

            var hasAssets = AssetDirectories.Any(dir => Directory.Exists(Path.Combine(candidate, dir)));
            if (hasAssets)
                return candidate;
        }

        return null;
    }

    private static string? ResolveHomeDirectory(string? homeOverride)
    {
        if (!string.IsNullOrWhiteSpace(homeOverride))
            return Path.GetFullPath(homeOverride);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            return home;

        return null;
    }

    private static (List<string> Messages, string? Error) InstallWindowsShim(
        string processPath,
        IReadOnlyDictionary<string, string?> options)
    {
        var messages = new List<string>();
        var installDir = CliInputHelpers.TryGetString(options, "binDir");
        if (string.IsNullOrWhiteSpace(installDir))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                return (messages, "Unable to resolve LocalApplicationData for shim installation.");

            installDir = Path.Combine(localAppData, "Programs", "callgraph");
        }

        installDir = Path.GetFullPath(installDir);
        Directory.CreateDirectory(installDir);

        var targetExe = Path.Combine(installDir, "callgraph.exe");
        File.Copy(processPath, targetExe, overwrite: true);
        messages.Add($"Installed CLI shim executable: {targetExe}");

        var updatePath = !CliInputHelpers.HasFlag(options, "skip-path");
        if (updatePath)
        {
            var pathResult = EnsureWindowsUserPathContains(installDir);
            if (pathResult.Error is not null)
                return (messages, pathResult.Error);

            messages.AddRange(pathResult.Messages);
        }

        return (messages, null);
    }

    private static (List<string> Messages, string? Error) InstallUnixShim(
        string processPath,
        IReadOnlyDictionary<string, string?> options)
    {
        var messages = new List<string>();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return (messages, "Unable to resolve home directory for shim installation.");

        var binDir = CliInputHelpers.TryGetString(options, "binDir");
        if (string.IsNullOrWhiteSpace(binDir))
            binDir = ResolveDefaultUnixBinDirectory(home);

        if (string.IsNullOrWhiteSpace(binDir))
            return (messages, "Unable to resolve a unix bin directory for shim installation.");

        binDir = Path.GetFullPath(binDir);
        Directory.CreateDirectory(binDir);

        var targetPath = Path.Combine(binDir, "callgraph");
        TryDeleteFile(targetPath);

        try
        {
            File.CreateSymbolicLink(targetPath, processPath);
            messages.Add($"Installed CLI symlink: {targetPath} -> {processPath}");
        }
        catch
        {
            File.Copy(processPath, targetPath, overwrite: true);
            TryEnsureExecutable(targetPath);
            messages.Add($"Installed CLI executable copy: {targetPath}");
        }

        messages.AddRange(CleanupDuplicateUnixShims(targetPath));

        if (!DirectoryOnPath(binDir))
        {
            messages.Add($"Warning: {binDir} is not on PATH in this shell. Add: export PATH=\"{binDir}:$PATH\"");
        }

        return (messages, null);
    }

    private static (List<string> Messages, string? Error) EnsureWindowsUserPathContains(string installDir)
    {
        var messages = new List<string>();
        var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var entries = currentPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (entries.Any(entry => PathsEqual(entry, installDir)))
        {
            messages.Add("User PATH already contains CallGraph install directory.");
            return (messages, null);
        }

        entries.Add(installDir);
        var updatedPath = string.Join(';', entries);

        try
        {
            Environment.SetEnvironmentVariable("PATH", updatedPath, EnvironmentVariableTarget.User);
            messages.Add("Added CallGraph install directory to user PATH (new shells only).");
            return (messages, null);
        }
        catch (Exception ex)
        {
            return (messages, $"Failed to update user PATH: {ex.Message}");
        }
    }

    private static bool DirectoryOnPath(string dir)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        return path
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => PathsEqual(entry, dir));
    }

    private static string ResolveDefaultUnixBinDirectory(string home)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var entry in path
                         .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Path.IsPathRooted(entry))
                    continue;

                if (CanWriteDirectory(entry))
                    return entry;
            }
        }

        return Path.Combine(home, ".local", "bin");
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;

            var probeFile = Path.Combine(directory, $".callgraph-write-probe-{Guid.NewGuid():N}.tmp");
            using (File.Create(probeFile))
            {
            }
            File.Delete(probeFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(sourceFile), ".DS_Store", StringComparison.Ordinal))
                continue;

            if (SectionTemplateNames.Any(name => string.Equals(Path.GetFileName(sourceFile), name, StringComparison.Ordinal)))
                continue;

            var relative = Path.GetRelativePath(source, sourceFile);
            var destinationFile = Path.Combine(target, relative);
            var destinationDir = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDir))
                Directory.CreateDirectory(destinationDir);

            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private static void TryEnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            const UnixFileMode mode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static IEnumerable<string> CleanupDuplicateUnixShims(string installedShimPath)
    {
        var messages = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return messages;

        var installedFullPath = Path.GetFullPath(installedShimPath);
        foreach (var entry in path
                     .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(Path.IsPathRooted)
                     .Distinct(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(entry, "callgraph");
            if (!File.Exists(candidate) && !IsSymbolicLink(candidate))
                continue;

            var candidateFullPath = Path.GetFullPath(candidate);
            if (PathsEqual(candidateFullPath, installedFullPath))
                continue;

            if (!IsSymbolicLink(candidate))
                continue;

            if (!LooksLikeCallGraphShim(candidate))
                continue;

            try
            {
                File.Delete(candidate);
                messages.Add($"Removed duplicate callgraph symlink: {candidate}");
            }
            catch (Exception ex)
            {
                messages.Add($"Warning: failed to remove duplicate symlink {candidate}: {ex.Message}");
            }
        }

        return messages;
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        catch
        {
            // Fallback to ResolveLinkTarget below.
        }

        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeCallGraphShim(string symlinkPath)
    {
        try
        {
            var resolved = File.ResolveLinkTarget(symlinkPath, returnFinalTarget: true);
            var resolvedPath = resolved?.FullName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return true;

            var fileName = Path.GetFileNameWithoutExtension(resolvedPath);
            return string.Equals(fileName, "CallGraph", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileName, "callgraph", StringComparison.OrdinalIgnoreCase) ||
                   resolvedPath.Contains("callgraph", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static IEnumerable<string> GetManualSectionInstructions(string sourceRoot, string targetRoot)
    {
        var messages = new List<string>();

        foreach (var templatePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(templatePath);
            if (!SectionTemplateNames.Any(name => string.Equals(name, fileName, StringComparison.Ordinal)))
                continue;

            var relative = Path.GetRelativePath(sourceRoot, templatePath);
            var targetPath = Path.Combine(targetRoot, relative);

            if (!File.Exists(targetPath))
            {
                messages.Add($"Manual step: create {targetPath} from template {templatePath}.");
                continue;
            }

            if (TemplateContentAlreadyPresent(templatePath, targetPath))
            {
                messages.Add($"Info: {targetPath} already contains template section from {templatePath}.");
                continue;
            }

            messages.Add($"Manual step: append relevant section from {templatePath} into existing {targetPath}.");
        }

        return messages;
    }

    private static bool TemplateContentAlreadyPresent(string templatePath, string targetPath)
    {
        try
        {
            var template = File.ReadAllText(templatePath).Trim();
            var target = File.ReadAllText(targetPath);
            if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(target))
                return false;

            return target.Contains(template, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
