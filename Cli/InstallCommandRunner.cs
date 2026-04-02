using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CallGraph.Cli;

namespace CallGraph;

internal static class InstallCommandRunner
{
    private static readonly string[] AssetDirectories = ["_claude", "_codex", "_cursor", "_copilot", "_opencode"];
    private static readonly string[] SectionTemplateNames = ["AGENTS.md", "CLAUDE.md"];
    private const string ClaudeHookRelativePath = "hooks/callgraph-rewrite.sh";
    private const string CopilotHookScriptRelativePath = "hooks/callgraph-pretooluse.sh";
    private const string CopilotHookTemplateRelativePath = "hooks/callgraph-pretooluse.hooks.json";

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
                "Install assets not found next to executable. Ensure publish output includes _claude/_codex/_cursor/_copilot/_opencode.");
        }

        var messages = new List<string>();

        if (installSkills)
        {
            foreach (var sourceName in AssetDirectories)
            {
                var sourcePath = Path.Combine(sourceRoot, sourceName);
                if (!Directory.Exists(sourcePath))
                    continue;

                var targetPath = GetInstallTargetPath(homeDir, sourceName);

                if (!Directory.Exists(targetPath))
                {
                    messages.Add($"Skipped {sourceName}: target directory does not exist ({targetPath}).");
                    continue;
                }

                CopyDirectory(sourcePath, targetPath);
                messages.Add($"Deployed {sourceName} -> {targetPath}");
                messages.AddRange(GetManualSectionInstructions(sourcePath, targetPath));

                if (string.Equals(sourceName, "_claude", StringComparison.Ordinal))
                    EnsureClaudeRewriteHookConfigured(targetPath, messages);

                if (string.Equals(sourceName, "_copilot", StringComparison.Ordinal))
                    AddCopilotHookSetupGuidance(targetPath, messages);

                if (string.Equals(sourceName, "_opencode", StringComparison.Ordinal))
                    AddOpenCodeSetupGuidance(targetPath, messages);
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

        return new ToolExecutionResult(0, FormatInstallOutput(messages), null);
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

    private static string GetInstallTargetPath(string homeDir, string sourceName)
    {
        if (string.Equals(sourceName, "_opencode", StringComparison.Ordinal))
            return Path.Combine(homeDir, ".config", "opencode");

        var targetRootName = sourceName.StartsWith("_", StringComparison.Ordinal)
            ? $".{sourceName[1..]}"
            : sourceName;

        return Path.Combine(homeDir, targetRootName);
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

        if (OperatingSystem.IsMacOS())
        {
            File.Copy(processPath, targetPath, overwrite: true);
            TryEnsureExecutable(targetPath);
            messages.Add($"Installed CLI executable copy (stable on macOS): {targetPath}");
        }
        else
        {
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
        }

        TryClearMacOsQuarantine(processPath, messages);
        TryClearMacOsQuarantine(targetPath, messages);

        messages.AddRange(CleanupDuplicateUnixShims(targetPath));

        if (!DirectoryOnPath(binDir))
        {
            messages.Add($"Warning: {binDir} is not on PATH in this shell. Add: export PATH=\"{binDir}:$PATH\"");
        }

        return (messages, null);
    }

    private static void TryClearMacOsQuarantine(string path, List<string> messages)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        if (string.IsNullOrWhiteSpace(path))
            return;

        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.GetFullPath(path)
        };

        try
        {
            var linkTarget = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (linkTarget is not null && !string.IsNullOrWhiteSpace(linkTarget.FullName))
                candidates.Add(Path.GetFullPath(linkTarget.FullName));
        }
        catch
        {
            // Ignore; path might not be a symlink.
        }

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                continue;

            try
            {
                var startInfo = new ProcessStartInfo("/usr/bin/xattr")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add("-d");
                startInfo.ArgumentList.Add("com.apple.quarantine");
                startInfo.ArgumentList.Add(candidate);

                using var process = Process.Start(startInfo);
                if (process is null)
                    continue;

                process.WaitForExit();
                var stderr = process.StandardError.ReadToEnd().Trim();
                if (process.ExitCode == 0)
                {
                    messages.Add($"Cleared macOS quarantine attribute: {candidate}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(stderr) &&
                    stderr.Contains("No such xattr", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                messages.Add(
                    $"Warning: failed to clear macOS quarantine on {candidate}. " +
                    $"Run manually: xattr -d com.apple.quarantine \"{candidate}\"");
            }
            catch
            {
                messages.Add(
                    $"Warning: failed to clear macOS quarantine on {candidate}. " +
                    $"Run manually: xattr -d com.apple.quarantine \"{candidate}\"");
            }
        }
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

    internal static string ResolveDefaultUnixBinDirectory(string home)
    {
        return ResolveDefaultUnixBinDirectory(home, Environment.GetEnvironmentVariable("PATH"));
    }

    internal static string ResolveDefaultUnixBinDirectory(string home, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var entries = path
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Path.IsPathRooted)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var entry in entries)
            {
                if (!LooksLikeStableUnixShimDirectory(entry))
                    continue;

                if (!IsLikelyUnixBinDirectory(entry))
                    continue;

                if (CanWriteDirectory(entry))
                    return entry;
            }

            foreach (var entry in path
                         .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Where(Path.IsPathRooted)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!LooksLikeStableUnixShimDirectory(entry))
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

    private static bool IsLikelyUnixBinDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "sbin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.EndsWith("/bin", StringComparison.OrdinalIgnoreCase) ||
               fullPath.EndsWith("/sbin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStableUnixShimDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (fullPath.StartsWith(tempRoot, StringComparison.Ordinal))
            return false;

        if (fullPath.StartsWith("/tmp", StringComparison.Ordinal) ||
            fullPath.StartsWith("/private/tmp", StringComparison.Ordinal) ||
            fullPath.StartsWith("/var/tmp", StringComparison.Ordinal) ||
            fullPath.StartsWith("/private/var/tmp", StringComparison.Ordinal))
        {
            return false;
        }

        if (fullPath.Contains("/.codex/tmp/", StringComparison.Ordinal))
            return false;

        return true;
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
                messages.Add(
                    $"Warning: failed to remove duplicate symlink {candidate}: {ex.Message}. " +
                    $"Manually run: sudo rm {candidate}");
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

            messages.Add(
                $"Manual step: append the full template content from {templatePath} into existing {targetPath}. " +
                $"Suggested command (review before running): printf '\\n\\n' >> \"{targetPath}\" && cat \"{templatePath}\" >> \"{targetPath}\"");
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

    private static void EnsureClaudeRewriteHookConfigured(string claudeRoot, List<string> messages)
    {
        var hookPath = Path.Combine(claudeRoot, ClaudeHookRelativePath);
        if (!File.Exists(hookPath))
        {
            messages.Add($"Warning: Claude hook file not found: {hookPath}");
            return;
        }

        TryEnsureExecutable(hookPath);
        var settingsPath = Path.Combine(claudeRoot, "settings.json");

        try
        {
            JsonObject root;
            if (File.Exists(settingsPath))
            {
                var content = File.ReadAllText(settingsPath);
                if (string.IsNullOrWhiteSpace(content))
                {
                    root = new JsonObject();
                }
                else
                {
                    var parsed = JsonNode.Parse(content);
                    if (parsed is not JsonObject parsedObject)
                    {
                        messages.Add(
                            $"Manual step: {settingsPath} is not a JSON object. Add a PreToolUse hook pointing to {hookPath}.");
                        return;
                    }

                    root = parsedObject;
                }
            }
            else
            {
                root = new JsonObject();
            }

            if (HasClaudeHook(root, hookPath))
            {
                messages.Add($"Claude hook already configured in {settingsPath}");
                return;
            }

            AddClaudeHook(root, hookPath);
            var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, serialized + Environment.NewLine);
            messages.Add($"Configured Claude PreToolUse hook in {settingsPath}");
        }
        catch (Exception ex)
        {
            messages.Add(
                $"Manual step: could not update {settingsPath} ({ex.Message}). Add a PreToolUse hook pointing to {hookPath}.");
        }
    }

    private static void AddCopilotHookSetupGuidance(string copilotRoot, List<string> messages)
    {
        var hookScriptPath = Path.Combine(copilotRoot, CopilotHookScriptRelativePath);
        if (File.Exists(hookScriptPath))
            TryEnsureExecutable(hookScriptPath);

        var hookTemplatePath = Path.Combine(copilotRoot, CopilotHookTemplateRelativePath);
        if (!File.Exists(hookTemplatePath))
        {
            messages.Add($"Warning: Copilot hook template not found: {hookTemplatePath}");
            return;
        }

        messages.Add(
            $"Manual step (Copilot CLI): copy {hookTemplatePath} to <repo>/.github/hooks/callgraph-pretooluse.hooks.json " +
            $"because Copilot CLI loads hooks from the current working directory.");
    }

    private static void AddOpenCodeSetupGuidance(string openCodeRoot, List<string> messages)
    {
        var pluginPath = Path.Combine(openCodeRoot, "plugins", "callgraph-hooks.js");
        if (!File.Exists(pluginPath))
        {
            messages.Add($"Warning: OpenCode plugin hook not found: {pluginPath}");
            return;
        }

        messages.Add($"Info: OpenCode local hook plugin deployed to {pluginPath}.");
        messages.Add($"Info: OpenCode auto-loads local plugins from {Path.Combine(openCodeRoot, "plugins")}.");
    }

    private static string FormatInstallOutput(IReadOnlyList<string> messages)
    {
        var manualSteps = new List<string>();
        var deployed = new List<string>();
        var shim = new List<string>();
        var skipped = new List<string>();
        var warnings = new List<string>();
        var details = new List<string>();

        foreach (var message in messages)
        {
            if (message.StartsWith("Manual step", StringComparison.Ordinal))
            {
                manualSteps.Add(message);
                continue;
            }

            if (message.StartsWith("Deployed ", StringComparison.Ordinal))
            {
                deployed.Add(message);
                continue;
            }

            if (message.StartsWith("Installed ", StringComparison.Ordinal) ||
                message.StartsWith("Removed duplicate callgraph symlink:", StringComparison.Ordinal) ||
                message.StartsWith("Added CallGraph install directory to user PATH", StringComparison.Ordinal) ||
                message.StartsWith("User PATH already contains CallGraph install directory.", StringComparison.Ordinal))
            {
                shim.Add(message);
                continue;
            }

            if (message.StartsWith("Skipped ", StringComparison.Ordinal))
            {
                skipped.Add(message);
                continue;
            }

            if (message.StartsWith("Warning:", StringComparison.Ordinal))
            {
                warnings.Add(message);
                continue;
            }

            details.Add(message);
        }

        var lines = new List<string>
        {
            "=== INSTALL SUMMARY ===",
            $"Assets deployed: {deployed.Count}",
            $"Manual steps: {manualSteps.Count}",
            $"Warnings: {warnings.Count}",
            $"Skipped targets: {skipped.Count}",
            string.Empty
        };

        AddNumberedSection(lines, "MANUAL STEPS (ACTION REQUIRED)", manualSteps);
        AddBulletedSection(lines, "DEPLOYED ASSETS", deployed);
        AddBulletedSection(lines, "INSTALLATION DETAILS", details);
        AddBulletedSection(lines, "SHIM INSTALLATION", shim);
        AddBulletedSection(lines, "SKIPPED", skipped);
        AddBulletedSection(lines, "WARNINGS", warnings);

        if (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddNumberedSection(List<string> lines, string title, IReadOnlyList<string> items)
    {
        lines.Add($"=== {title} ===");
        if (items.Count == 0)
        {
            lines.Add("None.");
        }
        else
        {
            for (var i = 0; i < items.Count; i++)
                lines.Add($"{i + 1}. {items[i]}");
        }

        lines.Add(string.Empty);
    }

    private static void AddBulletedSection(List<string> lines, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return;

        lines.Add($"=== {title} ===");
        foreach (var item in items)
            lines.Add($"- {item}");

        lines.Add(string.Empty);
    }

    private static bool HasClaudeHook(JsonObject root, string hookPath)
    {
        if (root["hooks"] is not JsonObject hooksObject)
            return false;

        if (hooksObject["PreToolUse"] is not JsonArray preToolUseArray)
            return false;

        foreach (var entry in preToolUseArray.OfType<JsonObject>())
        {
            if (entry["hooks"] is not JsonArray hooksArray)
                continue;

            foreach (var hook in hooksArray.OfType<JsonObject>())
            {
                var command = hook["command"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(command) &&
                    PathsEqual(ExpandHomePath(command), hookPath))
                    return true;
            }
        }

        return false;
    }

    private static void AddClaudeHook(JsonObject root, string hookPath)
    {
        if (root["hooks"] is not JsonObject hooksObject)
        {
            hooksObject = new JsonObject();
            root["hooks"] = hooksObject;
        }

        if (hooksObject["PreToolUse"] is not JsonArray preToolUseArray)
        {
            preToolUseArray = new JsonArray();
            hooksObject["PreToolUse"] = preToolUseArray;
        }

        preToolUseArray.Add(new JsonObject
        {
            ["matcher"] = "Bash",
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = hookPath
                }
            }
        });
    }

    private static string ExpandHomePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        if (!path.StartsWith("~", StringComparison.Ordinal))
            return path;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return path;

        if (path.Length == 1)
            return home;

        if (path[1] == '/' || path[1] == '\\')
            return Path.Combine(home, path[2..]);

        return path;
    }
}
