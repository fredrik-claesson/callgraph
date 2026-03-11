using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Solutions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CallGraph.Tests;

public sealed class AnalyzeEndpointTests
{
    [Fact]
    public async Task ReturnsGraphWhenAvailable()
    {
        var graph = new Graph
        {
            Version = 1,
            Targets = new List<string> { "t" },
            Nodes = new List<Node> { new() { Id = "t", Kind = "method" } }
        };

        await using var factory = new ApiFactory(_ => new AnalyzeResult(graph, null));
        using var client = factory.CreateClient();
        var filePath = CreateTempFile();

        var response = await client.PostAsJsonAsync("/analyze", new { filepath = filePath });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task AcceptsRelativeFilePath()
    {
        var graph = new Graph
        {
            Version = 1,
            Targets = new List<string> { "t" },
            Nodes = new List<Node> { new() { Id = "t", Kind = "method" } }
        };

        await using var factory = new ApiFactory(_ => new AnalyzeResult(graph, null));
        using var client = factory.CreateClient();
        var filePath = CreateTempFile();
        var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), filePath);

        var response = await client.PostAsJsonAsync("/analyze", new { filepath = relativePath });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsConflictForAmbiguousSolutions()
    {
        var solutions = new List<SolutionInfo>
        {
            new("s1", "C:\\one.sln", true),
            new("s2", "C:\\two.sln", true)
        };

        await using var factory = new ApiFactory(_ => new AnalyzeResult(
            null,
            new AnalyzeError(AnalyzeErrorKind.AmbiguousSolution, "Multiple indexed solutions contain this file.", solutions)));
        using var client = factory.CreateClient();
        var filePath = CreateTempFile();

        var response = await client.PostAsJsonAsync("/analyze", new { filepath = filePath });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.GetProperty("solutions");
        Assert.Equal(2, list.GetArrayLength());
    }

    [Fact]
    public async Task ReturnsNotFoundWhenTargetsMissing()
    {
        await using var factory = new ApiFactory(_ => new AnalyzeResult(
            null,
            new AnalyzeError(AnalyzeErrorKind.TargetsNotFound, "No targets matched the request.")));
        using var client = factory.CreateClient();
        var filePath = CreateTempFile();

        var response = await client.PostAsJsonAsync("/analyze", new { filepath = filePath });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsServiceUnavailableWhenIndexMissing()
    {
        await using var factory = new ApiFactory(_ => new AnalyzeResult(
            null,
            new AnalyzeError(AnalyzeErrorKind.IndexNotReady, "Index missing or in progress.")));
        using var client = factory.CreateClient();
        var filePath = CreateTempFile();

        var response = await client.PostAsJsonAsync("/analyze", new { filepath = filePath });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Contains("10", values);
    }

    private static string CreateTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "namespace Sample; public class C { }");
        return path;
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly Func<AnalyzeRequest, AnalyzeResult> _handler;

        public ApiFactory(Func<AnalyzeRequest, AnalyzeResult> handler)
            => _handler = handler;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IGraphAnalyzer))
                    .ToList();
                foreach (var descriptor in descriptors)
                    services.Remove(descriptor);

                services.AddSingleton<IGraphAnalyzer>(new StubGraphAnalyzer(_handler));

                RemoveHostedServices(services);
            });

            builder.ConfigureLogging(logging => logging.ClearProviders());
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

    private sealed class StubGraphAnalyzer : IGraphAnalyzer
    {
        private readonly Func<AnalyzeRequest, AnalyzeResult> _handler;

        public StubGraphAnalyzer(Func<AnalyzeRequest, AnalyzeResult> handler)
            => _handler = handler;

        public Task<AnalyzeResult> AnalyzeAsync(AnalyzeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
