using CallGraph.Core.Analysis;
using CallGraph.Core.Diagnostics;
using CallGraph.Core.Extraction;
using CallGraph.Core.Git;
using CallGraph.Core.Indexing;
using CallGraph.Core.Projects;
using CallGraph.Core.Search;
using CallGraph.Core.Solutions;
using CallGraph.Core.Watching;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CallGraph.Hosting;

public static class CallGraphComposition
{
    public static void EnsureMsBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch (InvalidOperationException)
            {
                // MSBuild assemblies can be loaded before registration in some hosting/test scenarios.
                // If that happens, attempting to register throws; continuing is typically safe.
            }
        }
    }

    public static IServiceCollection AddCallGraphCore(
        this IServiceCollection services,
        IConfiguration configuration,
        bool includeHostedServices = true)
    {
        services.Configure<IndexStoreOptions>(configuration.GetSection("IndexStore"));
        services.Configure<DiagnosticCollectorOptions>(configuration.GetSection("Diagnostics"));
        services.Configure<HybridMethodSearchOptions>(configuration.GetSection("MethodSearch"));
        services.Configure<LocalBgeOptions>(configuration.GetSection("SemanticSearch:BgeSmallEnV15"));

        services.AddSingleton<IIndexJobStore, InMemoryIndexJobStore>();
        services.AddSingleton<IIndexJobQueue, InMemoryIndexJobQueue>();

        services.AddSingleton<IProjectFilter, ProjectFilter>();
        services.AddSingleton<ISolutionFileParser, SolutionFileParser>();
        services.AddSingleton<ISolutionLoader, SolutionLoader>();
        services.AddSingleton<ISolutionContextCache, SolutionContextCache>();
        services.AddSingleton<IProjectIndexer, ProjectIndexer>();
        services.AddSingleton<IFileIndexer, FileIndexer>();
        services.AddSingleton<IIndexStore, SqliteIndexStore>();
        services.AddSingleton<IGitRepositoryInspector, GitRepositoryInspector>();
        services.AddSingleton<ISemanticEmbedder, BgeSmallEnV15SemanticEmbedder>();
        services.AddSingleton<IHybridMethodSearchService, HybridMethodSearchService>();
        services.AddSingleton<IIndexingPipeline, IndexingPipeline>();
        services.AddSingleton<ISolutionIndexer, QueueingSolutionIndexer>();

        services.AddSingleton<ITargetResolver, TargetResolver>();
        services.AddSingleton<IGraphBuilder, GraphBuilder>();
        services.AddSingleton<IGraphAnalyzer, GraphAnalyzer>();

        services.AddSingleton<IDiagnosticCollector, DiagnosticCollector>();
        services.AddSingleton<IMethodSourceExtractor, MethodSourceExtractor>();

        // Register watcher services in all modes so CLI daemon (`serve`) can opt into watching
        // without requiring hosted-service startup. Hosted registration stays conditional below.
        services.AddSingleton<SolutionWatcherHost>();
        services.AddSingleton<ISolutionWatcherRegistry>(sp => sp.GetRequiredService<SolutionWatcherHost>());

        if (includeHostedServices)
        {
            services.AddHostedService<IndexJobRunner>();
            services.AddHostedService(sp => sp.GetRequiredService<SolutionWatcherHost>());
        }

        return services;
    }
}
