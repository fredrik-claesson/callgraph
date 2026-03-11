using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CallGraph.Cli;
using CallGraph.Core.Indexing;
using CallGraph.Core.Watching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CallGraph;

internal static class DaemonCommandRunner
{
    private static readonly TimeSpan DefaultDaemonIdleTimeout = TimeSpan.FromHours(10);

    private static readonly JsonSerializerOptions JsonTransportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task<int> RunServeCommandAsync(
        string[] args,
        ToolCommand tool,
        Func<string[], bool, HostApplicationBuilder> createHostBuilder)
    {
        var pipeName = CliInputHelpers.TryGetString(tool.Options, "pipeName") ?? GetDefaultPipeName();
        var idleMinutes = CliInputHelpers.TryGetInt(tool.Options, "idleMinutes", out var idleError);
        if (idleError is not null)
            return PrintToolError(idleError);
        var autoWatchIndexed = !CliInputHelpers.HasFlag(tool.Options, "no-watch-indexed");

        var idleTimeout = idleMinutes.HasValue
            ? TimeSpan.FromMinutes(idleMinutes.Value)
            : DefaultDaemonIdleTimeout;
        if (idleTimeout < TimeSpan.FromMinutes(1))
            idleTimeout = TimeSpan.FromMinutes(1);

        var builder = createHostBuilder(args, false);
        using var host = builder.Build();

        var services = host.Services;
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var indexStore = services.GetRequiredService<IIndexStore>();
        var watcherRegistry = services.GetRequiredService<ISolutionWatcherRegistry>();

        if (autoWatchIndexed)
        {
            await LifecycleCommandRunner
                .EnsureWatchingAllIndexedSolutionsAsync(indexStore, watcherRegistry, CancellationToken.None)
                .ConfigureAwait(false);
        }

        var exitCode = await RunServeLoopAsync(pipeName, idleTimeout, services, indexStore, lifetime, CancellationToken.None)
            .ConfigureAwait(false);
        return exitCode;
    }

    public static async Task<ToolExecutionResult?> ExecuteViaDaemonWithAutoStartAsync(
        ToolCommand tool,
        string pipeName)
    {
        var firstTry = await TryExecuteViaDaemonAsync(pipeName, tool).ConfigureAwait(false);
        if (firstTry.Success)
            return firstTry.Result;

        if (!StartDaemonProcess(pipeName))
            return null;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(200).ConfigureAwait(false);
            var retry = await TryExecuteViaDaemonAsync(pipeName, tool).ConfigureAwait(false);
            if (retry.Success)
                return retry.Result;
        }

        return null;
    }

    public static async Task<int> HandleStatusCommandAsync(ToolCommand tool)
    {
        var pipeName = CliInputHelpers.TryGetString(tool.Options, "pipeName") ?? GetDefaultPipeName();
        var statusRequest = new ToolCommand("__status", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var status = await TryExecuteViaDaemonAsync(pipeName, statusRequest).ConfigureAwait(false);

        if (status.Success)
        {
            Console.WriteLine("running");
            return 0;
        }

        Console.WriteLine("stopped");
        return 1;
    }

    public static async Task<int> HandleStopCommandAsync(ToolCommand tool)
    {
        var pipeName = CliInputHelpers.TryGetString(tool.Options, "pipeName") ?? GetDefaultPipeName();
        var stopRequest = new ToolCommand("__stop", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var stop = await TryExecuteViaDaemonAsync(pipeName, stopRequest).ConfigureAwait(false);

        if (stop.Success)
            return 0;

        Console.Error.WriteLine(stop.Error ?? "Daemon is not running.");
        return 1;
    }

    public static async Task<int?> TryRunReindexViaDaemonAsync(
        string[] args,
        NormalizedLifecycleOptions normalized,
        Func<string[], bool, HostApplicationBuilder> createHostBuilder)
    {
        var solutionPath = normalized.ActionPath;
        if (solutionPath is null)
        {
            var builder = createHostBuilder(args, false);
            using var host = builder.Build();
            var indexStore = host.Services.GetRequiredService<IIndexStore>();
            solutionPath = await LifecycleCommandRunner
                .ResolveIndexedSolutionAsync(indexStore, "--reindex", CancellationToken.None)
                .ConfigureAwait(false);
            if (solutionPath is null)
                return 1;
        }

        var tool = new ToolCommand(
            "reindex",
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["solutionPath"] = solutionPath,
                ["slnOnly"] = "true"
            });

        var pipeName = GetDefaultPipeName();
        var daemonResult = await ExecuteViaDaemonWithAutoStartAsync(tool, pipeName).ConfigureAwait(false);
        if (daemonResult is null)
            return null;

        if (ShouldFallbackToLocalExecution(daemonResult, tool))
            return null;

        WriteToolExecutionResult(daemonResult);
        return daemonResult.ExitCode;
    }

    public static bool IsServeCommand(ToolCommand tool)
        => string.Equals(tool.Name, "serve", StringComparison.OrdinalIgnoreCase);

    public static bool IsStatusCommand(ToolCommand tool)
        => string.Equals(tool.Name, "status", StringComparison.OrdinalIgnoreCase);

    public static bool IsStopCommand(ToolCommand tool)
        => string.Equals(tool.Name, "stop", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldUseDaemon(ToolCommand tool)
    {
        if (!ToolCommandExecutor.ShouldUseDaemonByDefault(tool.Name))
            return false;

        if (CliInputHelpers.HasFlag(tool.Options, "no-daemon"))
            return false;

        return true;
    }

    public static string GetDefaultPipeName()
    {
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(workspace));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return $"callgraph-{hash[..16]}";
    }

    public static bool ShouldFallbackToLocalExecution(ToolExecutionResult daemonResult, ToolCommand tool)
    {
        if (daemonResult.ExitCode == 0)
            return false;

        if (string.IsNullOrWhiteSpace(daemonResult.Stderr))
            return false;

        return daemonResult.Stderr.Contains($"Unknown command: {tool.Name}", StringComparison.OrdinalIgnoreCase);
    }

    public static void WriteToolExecutionResult(ToolExecutionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            Console.WriteLine(result.Stdout);

        if (!string.IsNullOrWhiteSpace(result.Stderr))
            Console.Error.WriteLine(result.Stderr);
    }

    private static int PrintToolError(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static async Task<(bool Success, ToolExecutionResult Result, string? Error)> TryExecuteViaDaemonAsync(
        string pipeName,
        ToolCommand tool)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(750).ConfigureAwait(false);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var request = new DaemonRequest(tool.Name, tool.Options.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase));
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonTransportOptions)).ConfigureAwait(false);

            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return (false, new ToolExecutionResult(1, null, "Empty response from daemon."), "Empty daemon response");

            var response = JsonSerializer.Deserialize<DaemonResponse>(line, JsonTransportOptions);
            if (response is null)
                return (false, new ToolExecutionResult(1, null, "Invalid daemon response."), "Invalid daemon response");

            return (true, new ToolExecutionResult(response.ExitCode, response.Stdout, response.Stderr), null);
        }
        catch (Exception ex)
        {
            return (false, new ToolExecutionResult(1, null, ex.Message), ex.Message);
        }
    }

    private static bool StartDaemonProcess(string pipeName)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                return false;

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false
            };

            var dotnetHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
            if (dotnetHost)
            {
                var commandLine = Environment.GetCommandLineArgs();
                if (commandLine.Length < 2 || string.IsNullOrWhiteSpace(commandLine[1]))
                    return false;

                var entryAssemblyPath = commandLine[1];
                startInfo.FileName = processPath;
                startInfo.ArgumentList.Add(entryAssemblyPath);
                startInfo.ArgumentList.Add("serve");
            }
            else
            {
                startInfo.FileName = processPath;
                startInfo.ArgumentList.Add("serve");
            }

            startInfo.ArgumentList.Add("--pipeName");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--daemonChild");
            // Auto-started daemon is query-focused; avoid watcher registration startup cost.
            startInfo.ArgumentList.Add("--no-watch-indexed");

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunServeLoopAsync(
        string pipeName,
        TimeSpan idleTimeout,
        IServiceProvider services,
        IIndexStore indexStore,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        var executor = new ToolCommandExecutor(services, indexStore);

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            var waitForConnection = server.WaitForConnectionAsync(cancellationToken);
            var idleDelay = Task.Delay(idleTimeout, cancellationToken);
            var completed = await Task.WhenAny(waitForConnection, idleDelay).ConfigureAwait(false);
            if (completed == idleDelay)
                return 0;

            await waitForConnection.ConfigureAwait(false);

            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            DaemonRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<DaemonRequest>(line, JsonTransportOptions);
            }
            catch (Exception ex)
            {
                var badRequest = new DaemonResponse(1, null, $"Invalid request: {ex.Message}");
                await writer.WriteLineAsync(JsonSerializer.Serialize(badRequest, JsonTransportOptions)).ConfigureAwait(false);
                continue;
            }

            if (request is null || string.IsNullOrWhiteSpace(request.Command))
            {
                var badRequest = new DaemonResponse(1, null, "Invalid request payload.");
                await writer.WriteLineAsync(JsonSerializer.Serialize(badRequest, JsonTransportOptions)).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(request.Command, "__status", StringComparison.Ordinal))
            {
                var status = new DaemonResponse(0, "running", null);
                await writer.WriteLineAsync(JsonSerializer.Serialize(status, JsonTransportOptions)).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(request.Command, "__stop", StringComparison.Ordinal))
            {
                var stopped = new DaemonResponse(0, "stopping", null);
                await writer.WriteLineAsync(JsonSerializer.Serialize(stopped, JsonTransportOptions)).ConfigureAwait(false);
                lifetime.StopApplication();
                return 0;
            }

            var tool = new ToolCommand(
                request.Command,
                request.Options is null
                    ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string?>(request.Options, StringComparer.OrdinalIgnoreCase));
            var result = await executor.ExecuteAsync(tool, cancellationToken).ConfigureAwait(false);
            var response = new DaemonResponse(result.ExitCode, result.Stdout, result.Stderr);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonTransportOptions)).ConfigureAwait(false);
        }

        return 0;
    }
}
