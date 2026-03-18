using CallGraph.Cli;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Diagnostics;
using CallGraph.Core.Extraction;
using CallGraph.Core.Indexing;
using CallGraph.Core.Search;
using CallGraph.Core.Solutions;
using Microsoft.Extensions.DependencyInjection;

namespace CallGraph.Tests;

/// <summary>
/// Verifies that search/list commands return an error when results would span
/// multiple indexed solutions (cross-solution leakage prevention).
/// </summary>
public sealed class ToolCommandExecutorSolutionScopeTests
{
    [Fact]
    public async Task SearchFile_WithoutSolutionScope_ErrorsWhenResultsSpanMultipleSolutions()
    {
        var store = new ScopeStubIndexStore(
            fileMatches:
            [
                new SearchFileMatch("sol-1", "/repo1/One.sln", "/repo1/Src/Foo.cs"),
                new SearchFileMatch("sol-2", "/repo2/Two.sln", "/repo2/Src/Foo.cs")
            ]);
        var executor = CreateExecutor(store);
        var command = new ToolCommand("search-file", new Dictionary<string, string?> { ["pattern"] = "*Foo.cs" });

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("2 indexed solutions", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--solutionPath", result.Stderr);
    }

    [Fact]
    public async Task SearchFile_WithoutSolutionScope_SucceedsWhenAllResultsFromOneSolution()
    {
        var store = new ScopeStubIndexStore(
            fileMatches:
            [
                new SearchFileMatch("sol-1", "/repo1/One.sln", "/repo1/Src/Alpha.cs"),
                new SearchFileMatch("sol-1", "/repo1/One.sln", "/repo1/Src/Beta.cs")
            ]);
        var executor = CreateExecutor(store);
        var command = new ToolCommand("search-file", new Dictionary<string, string?> { ["pattern"] = "*.cs" });

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task SearchFile_WithSolutionPathProvided_BypassesLeakageCheck()
    {
        // When solutionPath is supplied the check is skipped entirely, so even a stub that
        // "accidentally" returns cross-solution data won't cause a spurious error.
        var store = new ScopeStubIndexStore(
            fileMatches:
            [
                new SearchFileMatch("sol-1", "/repo1/One.sln", "/repo1/Src/Foo.cs"),
                new SearchFileMatch("sol-2", "/repo2/Two.sln", "/repo2/Src/Foo.cs")
            ]);
        var executor = CreateExecutor(store);
        var command = new ToolCommand("search-file", new Dictionary<string, string?>
        {
            ["pattern"] = "*Foo.cs",
            ["solutionPath"] = "/repo1/One.sln"
        });

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task SearchMethod_WithoutSolutionScope_ErrorsWhenResultsSpanMultipleSolutions()
    {
        var methodMatches = new[]
        {
            MakeMethodMatch("sol-1", "/repo1/One.sln", "Asm:Repo1.FooService.Run()"),
            MakeMethodMatch("sol-2", "/repo2/Two.sln", "Asm:Repo2.FooService.Run()")
        };
        var store = new ScopeStubIndexStore(methodMatches: methodMatches);
        var executor = CreateExecutor(store);
        var command = new ToolCommand("search-method", new Dictionary<string, string?> { ["keywords"] = "FooService Run" });

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("2 indexed solutions", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--solutionPath", result.Stderr);
    }

    [Fact]
    public async Task SearchMethod_WithoutSolutionScope_SucceedsWhenAllResultsFromOneSolution()
    {
        var methodMatches = new[]
        {
            MakeMethodMatch("sol-1", "/repo1/One.sln", "Asm:Repo1.FooService.Run()"),
            MakeMethodMatch("sol-1", "/repo1/One.sln", "Asm:Repo1.BarService.Run()")
        };
        var store = new ScopeStubIndexStore(methodMatches: methodMatches);
        var executor = CreateExecutor(store);
        var command = new ToolCommand("search-method", new Dictionary<string, string?> { ["keywords"] = "Run" });

        var result = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ListMethods_WithoutSolutionScope_ErrorsWhenDiscoverySpansMultipleSolutions()
    {
        // Without --filePath, list-methods uses the index store for discovery then live-parses each file.
        // Real files are required because BuildLiveListMethodMatchesAsync skips non-existent paths.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cg-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "sol1"));
        Directory.CreateDirectory(Path.Combine(tempDir, "sol2"));
        var file1 = Path.Combine(tempDir, "sol1", "FooService.cs");
        var file2 = Path.Combine(tempDir, "sol2", "FooService.cs");
        await File.WriteAllTextAsync(file1, "public class FooService { public void Run() {} }");
        await File.WriteAllTextAsync(file2, "public class FooService { public void Run() {} }");
        try
        {
            var methodMatches = new[]
            {
                MakeMethodMatch("sol-1", "/repo1/One.sln", "Asm:Repo1.FooService.Run()", file1),
                MakeMethodMatch("sol-2", "/repo2/Two.sln", "Asm:Repo2.FooService.Run()", file2)
            };
            var store = new ScopeStubIndexStore(methodMatches: methodMatches);
            var executor = CreateExecutor(store);
            var command = new ToolCommand("list-methods", new Dictionary<string, string?>
            {
                ["visibility"] = "external",
                ["folderPath"] = tempDir
            });

            var result = await executor.ExecuteAsync(command, CancellationToken.None);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("2 indexed solutions", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--solutionPath", result.Stderr);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ListMethods_WithoutSolutionScope_SucceedsWhenAllResultsFromOneSolution()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cg-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var file1 = Path.Combine(tempDir, "FooService.cs");
        var file2 = Path.Combine(tempDir, "BarService.cs");
        await File.WriteAllTextAsync(file1, "public class FooService { public void Run() {} }");
        await File.WriteAllTextAsync(file2, "public class BarService { public void Handle() {} }");
        try
        {
            var methodMatches = new[]
            {
                MakeMethodMatch("sol-1", "/repo1/One.sln", "Asm:Repo1.FooService.Run()", file1),
                MakeMethodMatch("sol-1", "/repo1/One.sln", "Asm:Repo1.BarService.Handle()", file2)
            };
            var store = new ScopeStubIndexStore(methodMatches: methodMatches);
            var executor = CreateExecutor(store);
            var command = new ToolCommand("list-methods", new Dictionary<string, string?>
            {
                ["visibility"] = "external",
                ["folderPath"] = tempDir
            });

            var result = await executor.ExecuteAsync(command, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static ToolCommandExecutor CreateExecutor(ScopeStubIndexStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIndexStore>(store);
        services.AddSingleton<IHybridMethodSearchService>(new ScopeStubHybridSearch(store));
        services.AddSingleton<IGraphAnalyzer>(new NullGraphAnalyzer());
        services.AddSingleton<IDiagnosticCollector>(new NullDiagnosticCollector());
        services.AddSingleton<IMethodSourceExtractor>(new NullMethodSourceExtractor());
        services.AddSingleton<ISolutionLoader>(new NullSolutionLoader());
        services.AddSingleton<ISolutionContextCache>(new NullSolutionContextCache());
        var provider = services.BuildServiceProvider();
        return new ToolCommandExecutor(provider, store);
    }

    private static SearchMethodMatch MakeMethodMatch(
        string solutionId,
        string solutionPath,
        string methodId,
        string? filePath = null)
        => new(
            solutionId,
            solutionPath,
            new Node
            {
                Id = methodId,
                Kind = "method",
                Display = methodId,
                ContainingType = "Demo",
                FilePath = filePath ?? $"/{solutionId}/Demo.cs",
                Accessibility = "public",
                StartLine = 1
            });

    private sealed class ScopeStubIndexStore : IIndexStore
    {
        private readonly IReadOnlyList<SearchFileMatch> _fileMatches;
        private readonly IReadOnlyList<SearchMethodMatch> _methodMatches;

        public ScopeStubIndexStore(
            IReadOnlyList<SearchFileMatch>? fileMatches = null,
            IReadOnlyList<SearchMethodMatch>? methodMatches = null)
        {
            _fileMatches = fileMatches ?? Array.Empty<SearchFileMatch>();
            _methodMatches = methodMatches ?? Array.Empty<SearchMethodMatch>();
        }

        public Task<IReadOnlyList<SearchFileMatch>> SearchFilesAsync(
            string pattern, bool useRegex, string? solutionPath, string? solutionId,
            string? folderPath, string? filePath, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(solutionPath))
                return Task.FromResult<IReadOnlyList<SearchFileMatch>>(
                    _fileMatches.Where(m => string.Equals(m.SolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase)).ToList());
            if (!string.IsNullOrWhiteSpace(solutionId))
                return Task.FromResult<IReadOnlyList<SearchFileMatch>>(
                    _fileMatches.Where(m => string.Equals(m.SolutionId, solutionId, StringComparison.OrdinalIgnoreCase)).ToList());
            return Task.FromResult(_fileMatches);
        }

        public Task<IReadOnlyList<SearchMethodMatch>> SearchMethodsAsync(
            string pattern, bool useRegex, string? solutionPath, string? solutionId,
            string? folderPath, string? filePath, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(solutionPath))
                return Task.FromResult<IReadOnlyList<SearchMethodMatch>>(
                    _methodMatches.Where(m => string.Equals(m.SolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase)).ToList());
            if (!string.IsNullOrWhiteSpace(solutionId))
                return Task.FromResult<IReadOnlyList<SearchMethodMatch>>(
                    _methodMatches.Where(m => string.Equals(m.SolutionId, solutionId, StringComparison.OrdinalIgnoreCase)).ToList());
            return Task.FromResult(_methodMatches);
        }

        public Task<IReadOnlyList<SearchMethodMatch>> ListMethodsAsync(
            string visibility, string? solutionPath, string? solutionId,
            string? folderPath, string? filePath, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(solutionPath))
                return Task.FromResult<IReadOnlyList<SearchMethodMatch>>(
                    _methodMatches.Where(m => string.Equals(m.SolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase)).ToList());
            if (!string.IsNullOrWhiteSpace(solutionId))
                return Task.FromResult<IReadOnlyList<SearchMethodMatch>>(
                    _methodMatches.Where(m => string.Equals(m.SolutionId, solutionId, StringComparison.OrdinalIgnoreCase)).ToList());
            return Task.FromResult(_methodMatches);
        }

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(SolutionIndex index, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SolutionIndex?> LoadAsync(string solutionPath, CancellationToken cancellationToken) => Task.FromResult<SolutionIndex?>(null);
        public Task<IReadOnlyList<SolutionInfo>> ListSolutionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SolutionInfo>>(Array.Empty<SolutionInfo>());
        public Task<SolutionInfo?> GetSolutionByPathAsync(string solutionPath, CancellationToken cancellationToken) => Task.FromResult<SolutionInfo?>(null);
        public Task<DateTime?> GetIndexedAtUtcAsync(string solutionPath, CancellationToken cancellationToken) => Task.FromResult<DateTime?>(null);
        public Task<SolutionInfo?> GetSolutionByIdAsync(string solutionId, CancellationToken cancellationToken) => Task.FromResult<SolutionInfo?>(null);
        public Task<IReadOnlyList<IndexedFileInfo>> ListFilesAsync(string solutionPath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IndexedFileInfo>>(Array.Empty<IndexedFileInfo>());
        public Task<IReadOnlyList<string>> ListProjectPathsAsync(string solutionPath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<SolutionInfo>> FindSolutionsByFilePathAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SolutionInfo>>(Array.Empty<SolutionInfo>());
        public Task<IReadOnlyList<SolutionFileMatch>> FindSolutionsByFilePathSuffixAsync(string relativeFilePath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SolutionFileMatch>>(Array.Empty<SolutionFileMatch>());
        public Task<IReadOnlyList<SolutionProjectMatch>> FindProjectsByPathSuffixAsync(string relativeProjectPath, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SolutionProjectMatch>>(Array.Empty<SolutionProjectMatch>());
        public Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken) => Task.FromResult<Node?>(null);
        public Task<IReadOnlyList<Edge>> GetEdgesAsync(string solutionPath, string methodKey, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Edge>>(Array.Empty<Edge>());
        public Task UpdateFileAsync(string solutionPath, FileIndex update, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveFileAsync(string solutionPath, string filePath, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScopeStubHybridSearch : IHybridMethodSearchService
    {
        private readonly IIndexStore _store;

        public ScopeStubHybridSearch(IIndexStore store) => _store = store;

        public Task<IReadOnlyList<SearchMethodMatch>> SearchAsync(
            string pattern, bool useRegex, string? solutionPath, string? solutionId,
            string? folderPath, string? filePath, CancellationToken cancellationToken)
            => _store.SearchMethodsAsync(pattern, useRegex, solutionPath, solutionId, folderPath, filePath, cancellationToken);
    }

    private sealed class NullGraphAnalyzer : IGraphAnalyzer
    {
        public Task<AnalyzeResult> AnalyzeAsync(AnalyzeRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NullDiagnosticCollector : IDiagnosticCollector
    {
        public Task<IReadOnlyList<Contracts.Diagnostic>> CollectUnusedDiagnosticsAsync(
            IReadOnlyList<Microsoft.CodeAnalysis.Project> projects, string? folderPath, string? filePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<Contracts.Diagnostic>> CollectWarningDiagnosticsAsync(
            IReadOnlyList<Microsoft.CodeAnalysis.Project> projects, string? folderPath, string? filePath, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NullMethodSourceExtractor : IMethodSourceExtractor
    {
        public Task<MethodSourceExtractionResult> ExtractAsync(
            MethodSourceExtractionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NullSolutionLoader : ISolutionLoader
    {
        public Task<SolutionLoadContext> LoadAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SolutionLoadContext> LoadProjectAsync(string projectPath, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NullSolutionContextCache : ISolutionContextCache
    {
        public Task<SolutionLoadContext> GetOrLoadAsync(string solutionPath, bool slnOnly, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
