using CallGraph.Cli;
using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;
using CallGraph.Core.Watching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CallGraph;

internal static class LifecycleCommandRunner
{
    public static async Task<int> RunLifecycleAsync(
        string[] args,
        NormalizedLifecycleOptions normalized,
        Func<string[], bool, HostApplicationBuilder> createHostBuilder)
    {
        var builder = createHostBuilder(args, true);
        using var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);

        var services = host.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CallGraph.Cli");
        var cancellationToken = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

        var indexer = services.GetRequiredService<ISolutionIndexer>();
        var jobStore = services.GetRequiredService<IIndexJobStore>();
        var watcherRegistry = services.GetRequiredService<ISolutionWatcherRegistry>();
        var indexStore = services.GetRequiredService<IIndexStore>();

        var exit = 0;

        if (normalized.Action is CliAction.Index)
        {
            normalized = normalized with
            {
                ActionPath = normalized.ActionPath ?? normalized.WatchPath
            };

            if (normalized.ActionPath is null)
            {
                await host.StopAsync().ConfigureAwait(false);
                return 1;
            }

            var response = await indexer.EnqueueIndexAsync(new IndexRequest(normalized.ActionPath, true), cancellationToken)
                .ConfigureAwait(false);
            exit = await WaitForJobAsync(jobStore, response.JobId, logger, cancellationToken).ConfigureAwait(false);
        }
        else if (normalized.Action is CliAction.Reindex)
        {
            normalized = normalized with
            {
                ActionPath = normalized.ActionPath ??
                             normalized.WatchPath ??
                             await ResolveIndexedSolutionAsync(indexStore, "--reindex", cancellationToken).ConfigureAwait(false)
            };

            if (normalized.ActionPath is null)
            {
                await host.StopAsync().ConfigureAwait(false);
                return 1;
            }

            var response = await indexer.EnqueueReindexAsync(new ReindexRequest(normalized.ActionPath, true), cancellationToken)
                .ConfigureAwait(false);
            exit = await WaitForJobAsync(jobStore, response.JobId, logger, cancellationToken).ConfigureAwait(false);
        }
        else if (normalized.Action is CliAction.Clear)
        {
            await indexStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Index database cleared.");
            await host.StopAsync().ConfigureAwait(false);
            return 0;
        }

        if (exit != 0)
        {
            await host.StopAsync().ConfigureAwait(false);
            return exit;
        }

        if (normalized.WatchEnabled)
        {
            if (normalized.WatchPath is null)
            {
                await host.StopAsync().ConfigureAwait(false);
                return 1;
            }

            await watcherRegistry.EnsureWatchingAsync(normalized.WatchPath, slnOnly: true, cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation("Watching {SolutionPath}. Press Ctrl+C to stop.", normalized.WatchPath);
            await host.WaitForShutdownAsync().ConfigureAwait(false);
        }

        await host.StopAsync().ConfigureAwait(false);
        return exit;
    }

    public static NormalizedLifecycleOptions NormalizeLifecycleOptions(CliOptions options)
    {
        if (options.ClearEnabled)
            return new NormalizedLifecycleOptions(CliAction.Clear, null, null, false, true);

        var actionPath = options.IndexPath ?? options.ReindexPath;
        var action = options.IndexPath is not null
            ? CliAction.Index
            : options.ReindexEnabled
                ? CliAction.Reindex
                : CliAction.None;

        if (action is CliAction.None)
            return new NormalizedLifecycleOptions(null, null, null, "Specify --index, --reindex, --clear, or a subcommand.");

        if (actionPath is not null)
        {
            var optionName = action is CliAction.Reindex
                ? "--reindex"
                : "--index";

            var normalizedPath = CliInputHelpers.NormalizeSolutionPath(actionPath, optionName);
            if (normalizedPath.Error is not null)
                return new NormalizedLifecycleOptions(null, null, null, normalizedPath.Error);

            actionPath = normalizedPath.Path;
        }

        return new NormalizedLifecycleOptions(action, actionPath, null, false, false);
    }

    public static async Task<string?> ResolveIndexedSolutionAsync(
        IIndexStore indexStore,
        string optionName,
        CancellationToken cancellationToken)
    {
        var solutions = await indexStore.ListSolutionsAsync(cancellationToken).ConfigureAwait(false);
        if (solutions.Count == 0)
        {
            Console.Error.WriteLine($"{optionName} requires an indexed solution. Provide a .sln path to index first.");
            return null;
        }

        if (solutions.Count == 1)
            return solutions[0].SolutionPath;

        Console.WriteLine("Multiple indexed solutions found. Select one:");
        for (var i = 0; i < solutions.Count; i++)
        {
            Console.WriteLine($"  {i + 1}) {solutions[i].SolutionPath}");
        }

        while (true)
        {
            Console.Write("Enter number: ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (int.TryParse(input, out var choice) && choice >= 1 && choice <= solutions.Count)
                return solutions[choice - 1].SolutionPath;

            Console.WriteLine($"Invalid selection. Enter a number between 1 and {solutions.Count}.");
        }
    }

    public static async Task EnsureWatchingAllIndexedSolutionsAsync(
        IIndexStore indexStore,
        ISolutionWatcherRegistry watcherRegistry,
        CancellationToken cancellationToken)
    {
        var solutions = await indexStore.ListSolutionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var solution in solutions)
        {
            await watcherRegistry.EnsureWatchingAsync(solution.SolutionPath, solution.SlnOnly, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<int> WaitForJobAsync(
        IIndexJobStore jobStore,
        string jobId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? lastStatus = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (jobStore.TryGetJob(jobId, out var job))
                {
                    if (!string.Equals(lastStatus, job.Status, StringComparison.OrdinalIgnoreCase))
                    {
                        lastStatus = job.Status;
                        logger.LogInformation("Index job {JobId} status: {Status}.", job.JobId, job.Status);
                    }

                    if (string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                        return 0;

                    if (string.Equals(job.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
                        return 0;

                    if (string.Equals(job.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(job.Message))
                            logger.LogError("Index job {JobId} failed: {Message}", job.JobId, job.Message);
                        else
                            logger.LogError("Index job {JobId} failed.", job.JobId);
                        return 1;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        return 0;
    }
}
