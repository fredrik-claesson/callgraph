using System.Text;
using CallGraph.Cli;
using CallGraph.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace CallGraph;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CallGraphComposition.EnsureMsBuildRegistered();

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (!CliCommandLine.TryParse(args, out var options, out var error))
        {
            CliCommandLine.PrintUsage(error);
            return error is null ? 0 : 1;
        }

        if (options.ToolCommand is not null)
        {
            var tool = options.ToolCommand;
            if (DaemonCommandRunner.IsStatusCommand(tool))
                return await DaemonCommandRunner.HandleStatusCommandAsync(tool).ConfigureAwait(false);

            if (DaemonCommandRunner.IsStopCommand(tool))
                return await DaemonCommandRunner.HandleStopCommandAsync(tool).ConfigureAwait(false);

            if (DaemonCommandRunner.IsServeCommand(tool))
                return await DaemonCommandRunner.RunServeCommandAsync(args, tool, CreateHostBuilder).ConfigureAwait(false);

            if (DaemonCommandRunner.ShouldUseDaemon(tool))
            {
                var pipeName = DaemonCommandRunner.GetDefaultPipeName();
                var daemonResult = await DaemonCommandRunner.ExecuteViaDaemonWithAutoStartAsync(tool, pipeName)
                    .ConfigureAwait(false);
                if (daemonResult is not null)
                {
                    if (DaemonCommandRunner.ShouldFallbackToLocalExecution(daemonResult, tool))
                        return await RunToolLocallyAsync(args, tool).ConfigureAwait(false);

                    DaemonCommandRunner.WriteToolExecutionResult(daemonResult);
                    return daemonResult.ExitCode;
                }
            }

            return await RunToolLocallyAsync(args, tool).ConfigureAwait(false);
        }

        var normalized = LifecycleCommandRunner.NormalizeLifecycleOptions(options);
        if (normalized.Error is not null)
        {
            CliCommandLine.PrintUsage(normalized.Error);
            return 1;
        }

        if (normalized.Action is CliAction.Reindex && !normalized.WatchEnabled)
        {
            var daemonExitCode = await DaemonCommandRunner.TryRunReindexViaDaemonAsync(args, normalized, CreateHostBuilder)
                .ConfigureAwait(false);
            if (daemonExitCode.HasValue)
                return daemonExitCode.Value;
        }

        return await LifecycleCommandRunner.RunLifecycleAsync(args, normalized, CreateHostBuilder).ConfigureAwait(false);
    }

    private static async Task<int> RunToolLocallyAsync(string[] args, ToolCommand tool)
    {
        var builder = CreateHostBuilder(args, includeHostedServices: false);
        using var host = builder.Build();

        var services = host.Services;
        var indexStore = services.GetRequiredService<CallGraph.Core.Indexing.IIndexStore>();
        var cancellationToken = CancellationToken.None;
        var executor = new ToolCommandExecutor(services, indexStore);

        var result = await executor.ExecuteAsync(tool, cancellationToken).ConfigureAwait(false);
        DaemonCommandRunner.WriteToolExecutionResult(result);
        return result.ExitCode;
    }

    private static HostApplicationBuilder CreateHostBuilder(string[] args, bool includeHostedServices)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o =>
        {
            o.FormatterName = ConsoleFormatterNames.Simple;
            o.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services.Configure<SimpleConsoleFormatterOptions>(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
            o.ColorBehavior = LoggerColorBehavior.Disabled;
        });

        builder.Services.AddCallGraphCore(builder.Configuration, includeHostedServices);
        return builder;
    }
}
