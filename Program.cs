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
        EnsureWorkingDirectoryIsUsable();
        CallGraphComposition.EnsureMsBuildRegistered();

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (!CliCommandLine.TryParse(args, out var options, out var error))
        {
            CliCommandLine.PrintUsage(error);
            return error is null ? 0 : 1;
        }

        if (options.ToolCommand is not null)
            return await RunToolLocallyAsync(args, options.ToolCommand).ConfigureAwait(false);

        var normalized = LifecycleCommandRunner.NormalizeLifecycleOptions(options);
        if (normalized.Error is not null)
        {
            CliCommandLine.PrintUsage(normalized.Error);
            return 1;
        }

        return await LifecycleCommandRunner.RunLifecycleAsync(args, normalized, CreateHostBuilder).ConfigureAwait(false);
    }

    private static async Task<int> RunToolLocallyAsync(string[] args, ToolCommand tool)
    {
        var builder = CreateHostBuilder(args, includeHostedServices: false);
        using var host = builder.Build();

        var services = host.Services;
        var cancellationToken = CancellationToken.None;
        var executor = new ToolCommandExecutor(services);

        var result = await executor.ExecuteAsync(tool, cancellationToken).ConfigureAwait(false);
        WriteToolExecutionResult(result);
        return result.ExitCode;
    }

    private static void WriteToolExecutionResult(ToolExecutionResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Stdout))
            Console.WriteLine(result.Stdout);

        if (!string.IsNullOrWhiteSpace(result.Stderr))
            Console.Error.WriteLine(result.Stderr);
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

    private static void EnsureWorkingDirectoryIsUsable()
    {
        try
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            if (!Directory.Exists(currentDirectory))
                throw new DirectoryNotFoundException(
                    $"Current working directory does not exist: {currentDirectory}");

            return;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or IOException)
        {
            var fallback = ResolveFallbackWorkingDirectory();
            if (fallback is null)
                return;

            try
            {
                Directory.SetCurrentDirectory(fallback);
                Console.Error.WriteLine(
                    $"Warning: current working directory is unavailable; switched to '{fallback}'.");
            }
            catch
            {
                // If we cannot recover, continue and let downstream initialization report the real failure.
            }
        }
    }

    private static string? ResolveFallbackWorkingDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
            return home;

        var temp = Path.GetTempPath();
        return Directory.Exists(temp) ? temp : null;
    }
}
