using System.Net;
using System.Net.Http.Json;
using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Solutions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CallGraph.Tests;

public sealed class IndexEndpointTests
{
    [Fact]
    public async Task IndexReturnsAcceptedWithJobId()
    {
        var jobStore = new StubJobStore();
        await using var factory = new ApiFactory(new StubIndexer(), jobStore);
        using var client = factory.CreateClient();
        var solutionPath = CreateTempSolution();

        var response = await client.PostAsJsonAsync("/index", new { solutionPath, slnOnly = true });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/index-jobs/{StubIndexer.JobId}", response.Headers.Location?.OriginalString);
        var body = await response.Content.ReadFromJsonAsync<IndexJobResponse>();
        Assert.NotNull(body);
        Assert.Equal(StubIndexer.JobId, body!.JobId);
    }

    [Fact]
    public async Task ReindexReturnsAcceptedWithJobId()
    {
        var jobStore = new StubJobStore();
        await using var factory = new ApiFactory(new StubIndexer(), jobStore);
        using var client = factory.CreateClient();
        var solutionPath = CreateTempSolution();

        var response = await client.PostAsJsonAsync("/reindex", new { solutionPath, slnOnly = true });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task IndexJobStatusReturnsOkWhenKnown()
    {
        var jobStore = new StubJobStore();
        jobStore.Add(new IndexJobStatusResponse("job-1", "solution-1", "Running"));
        await using var factory = new ApiFactory(new StubIndexer(), jobStore);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/index-jobs/job-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task IndexJobStatusReturnsNotFoundWhenMissing()
    {
        var jobStore = new StubJobStore();
        await using var factory = new ApiFactory(new StubIndexer(), jobStore);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/index-jobs/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string CreateTempSolution()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sln");
        File.WriteAllText(path, "Microsoft Visual Studio Solution File, Format Version 12.00");
        return path;
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly ISolutionIndexer _indexer;
        private readonly IIndexJobStore _jobStore;

        public ApiFactory(ISolutionIndexer indexer, IIndexJobStore jobStore)
        {
            _indexer = indexer;
            _jobStore = jobStore;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                ReplaceService<ISolutionIndexer>(services, _indexer);
                ReplaceService<IIndexJobStore>(services, _jobStore);

                RemoveHostedServices(services);
            });

            builder.ConfigureLogging(logging => logging.ClearProviders());
        }

        private static void ReplaceService<TService>(IServiceCollection services, TService instance)
            where TService : class
        {
            var descriptors = services
                .Where(d => d.ServiceType == typeof(TService))
                .ToList();
            foreach (var descriptor in descriptors)
                services.Remove(descriptor);

            services.AddSingleton(instance);
        }
    }

    private static void RemoveHostedServices(IServiceCollection services)
    {
        var hosted = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .ToList();
        foreach (var descriptor in hosted)
            services.Remove(descriptor);
    }

    private sealed class StubIndexer : ISolutionIndexer
    {
        public const string JobId = "job-1";
        public const string SolutionId = "solution-1";

        public Task<IndexJobResponse> EnqueueIndexAsync(IndexRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new IndexJobResponse(JobId, SolutionId));

        public Task<IndexJobResponse> EnqueueReindexAsync(ReindexRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new IndexJobResponse(JobId, SolutionId));
    }

    private sealed class StubJobStore : IIndexJobStore
    {
        private readonly Dictionary<string, IndexJobStatusResponse> _jobs = new(StringComparer.OrdinalIgnoreCase);

        public IndexJobStatusResponse CreateJob(string solutionId, string status, string? message = null)
            => throw new NotSupportedException("Not used in tests.");

        public bool TryGetJob(string jobId, out IndexJobStatusResponse job)
            => _jobs.TryGetValue(jobId, out job!);

        public void UpdateJob(IndexJobStatusResponse job)
            => _jobs[job.JobId] = job;

        public void Add(IndexJobStatusResponse job)
            => _jobs[job.JobId] = job;
    }
}
