using System.Text.RegularExpressions;
using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using CallGraph.Core.Search;
using CallGraph.Core.Solutions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CallGraph.Tests;

public sealed class HybridMethodSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_UsesNamespaceClassMethodLexicalContext()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Mews.Security.Authentication.AuthenticationComponent.LoginUser(System.String)",
                containingType: "Mews.Security.Authentication.AuthenticationComponent",
                display: "AuthenticationComponent.LoginUser(string username)"),
            CreateMatch(
                methodId: "Asm:Mews.Reporting.Export.CsvExporter.GenerateCsv()",
                containingType: "Mews.Reporting.Export.CsvExporter",
                display: "CsvExporter.GenerateCsv()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new StaticSemanticEmbedder(new[] { 0.1f, 0.0f }),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 0,
                EnableSemanticRerank = false
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "login authentication component",
            useRegex: false,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Equal(methods[0].Method.Id, result[0].Method.Id);
    }

    [Fact]
    public async Task SearchAsync_AppliesSemanticRerankOverLexicalTopK()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Company.Security.AuthTokenService.ValidateAuthToken()",
                containingType: "Company.Security.AuthTokenService",
                display: "AuthTokenService.ValidateAuthToken()"),
            CreateMatch(
                methodId: "Asm:Company.Security.Authentication.AuthenticationComponent.LoginUser()",
                containingType: "Company.Security.Authentication.AuthenticationComponent",
                display: "AuthenticationComponent.LoginUser()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new ContentAwareSemanticEmbedder(
                highScoreMarker: "LoginUser",
                highScore: 0.95f,
                lowScore: 0.05f),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 1,
                EnableSemanticRerank = true
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "login",
            useRegex: false,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Equal(methods[1].Method.Id, result[0].Method.Id);
    }

    [Fact]
    public async Task SearchAsync_RegexBypassesHybridPipeline()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Company.Security.Authentication.AuthenticationComponent.LoginUser()",
                containingType: "Company.Security.Authentication.AuthenticationComponent",
                display: "AuthenticationComponent.LoginUser()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new StaticSemanticEmbedder(new[] { 0.0f }),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 1,
                EnableSemanticRerank = true
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "LoginUser",
            useRegex: true,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, store.RegexSearchCalls);
    }

    [Fact]
    public async Task SearchAsync_SemanticFinalPhase_FiltersLowScoringTailAggressively()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Company.Security.Authentication.AuthenticationComponent.LoginPrimary()",
                containingType: "Company.Security.Authentication.AuthenticationComponent",
                display: "AuthenticationComponent.LoginPrimary()"),
            CreateMatch(
                methodId: "Asm:Company.Security.Authentication.AuthenticationComponent.LoginSecondary()",
                containingType: "Company.Security.Authentication.AuthenticationComponent",
                display: "AuthenticationComponent.LoginSecondary()"),
            CreateMatch(
                methodId: "Asm:Company.Security.Authentication.AuthenticationComponent.LoginFallback()",
                containingType: "Company.Security.Authentication.AuthenticationComponent",
                display: "AuthenticationComponent.LoginFallback()"),
            CreateMatch(
                methodId: "Asm:Company.Security.Authentication.AuthenticationComponent.LoginLegacy()",
                containingType: "Company.Security.Authentication.AuthenticationComponent",
                display: "AuthenticationComponent.LoginLegacy()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new MarkerSemanticEmbedder(new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["LoginPrimary"] = 0.9f,
                ["LoginSecondary"] = 0.6f,
                ["LoginFallback"] = 0.0f,
                ["LoginLegacy"] = -0.6f
            }),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 1,
                EnableSemanticRerank = true
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "login",
            useRegex: false,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(methods[0].Method.Id, result[0].Method.Id);
    }

    [Fact]
    public async Task SearchAsync_NonWildcardPattern_UsesWholeTokensForCandidatePatterns()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Mews.Visits.Reservations.ReservationComponent.CheckSkipCleaning()",
                containingType: "Mews.Visits.Reservations.ReservationComponent",
                display: "ReservationComponent.CheckSkipCleaning()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new StaticSemanticEmbedder(new[] { 0.0f }),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 0,
                EnableSemanticRerank = false
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "CheckSkipCleaning ReservationComponent",
            useRegex: false,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Contains("*checkskipcleaning*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("*checkskipcleaning*reservationcomponent*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("*check*skip*cleaning*reservation*component*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("*check*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("*reservation*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_WildcardIdentifierPattern_AddsCollapsedTokenVariant()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Mews.Core.Search.HybridSearchService.Execute()",
                containingType: "Mews.Core.Search.HybridSearchService",
                display: "HybridSearchService.Execute()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new StaticSemanticEmbedder(new[] { 0.0f }),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 0,
                EnableSemanticRerank = false
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "*Hybrid*Search*",
            useRegex: false,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Contains("*Hybrid*Search*", store.NonRegexPatterns, StringComparer.Ordinal);
        Assert.Contains("*hybridsearch*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("*hybrid*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("*search*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_WildcardPrefixOnly_RemainsStrictPattern()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Mews.Core.Search.HybridSearchService.Execute()",
                containingType: "Mews.Core.Search.HybridSearchService",
                display: "HybridSearchService.Execute()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new StaticSemanticEmbedder(new[] { 0.0f }),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 0,
                EnableSemanticRerank = false
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "*HybridSearch",
            useRegex: false,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.Empty(result);
        Assert.Single(store.NonRegexPatterns);
        Assert.Equal("*HybridSearch", store.NonRegexPatterns[0]);
    }

    [Fact]
    public async Task SearchAsync_WildcardSuffixOnly_RemainsStrictPattern()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Mews.Core.Search.HybridSearchService.Execute()",
                containingType: "Mews.Core.Search.HybridSearchService",
                display: "HybridSearchService.Execute()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new StaticSemanticEmbedder(new[] { 0.0f }),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 0,
                EnableSemanticRerank = false
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "HybridSearch*",
            useRegex: false,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Single(store.NonRegexPatterns);
        Assert.Equal("HybridSearch*", store.NonRegexPatterns[0]);
    }

    [Fact]
    public async Task SearchAsync_WildcardPrefixAndSuffix_AugmentsLexicalPatterns()
    {
        var methods = new[]
        {
            CreateMatch(
                methodId: "Asm:Mews.Core.Search.HybridSearchService.Execute()",
                containingType: "Mews.Core.Search.HybridSearchService",
                display: "HybridSearchService.Execute()")
        };

        var store = new FilteringIndexStore(methods);
        var service = new HybridMethodSearchService(
            store,
            new StaticSemanticEmbedder(new[] { 0.0f }),
            Options.Create(new HybridMethodSearchOptions
            {
                ResultLimit = 10,
                LexicalTopK = 10,
                SemanticWeight = 0,
                EnableSemanticRerank = false
            }),
            NullLogger<HybridMethodSearchService>.Instance);

        var result = await service.SearchAsync(
            pattern: "*HybridSearch*",
            useRegex: false,
            solutionPath: null,
            solutionId: null,
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.Contains("*HybridSearch*", store.NonRegexPatterns, StringComparer.Ordinal);
        Assert.Contains("*hybridsearch*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("*hybrid*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("*search*", store.NonRegexPatterns, StringComparer.OrdinalIgnoreCase);
    }

    private static SearchMethodMatch CreateMatch(string methodId, string containingType, string display)
        => new(
            "solution-1",
            "/tmp/solution.sln",
            new Node
            {
                Id = methodId,
                Kind = "method",
                ContainingType = containingType,
                Display = display,
                FilePath = "/tmp/File.cs",
                Accessibility = "public",
                StartLine = 1
            });

    private sealed class StaticSemanticEmbedder : ISemanticEmbedder
    {
        private readonly IReadOnlyList<float> _scores;

        public StaticSemanticEmbedder(IReadOnlyList<float> scores)
        {
            _scores = scores;
        }

        public bool IsAvailable => true;

        public Task<IReadOnlyList<float>> ScoreAsync(
            string queryText,
            IReadOnlyList<string> candidateTexts,
            CancellationToken cancellationToken)
        {
            var scores = new List<float>(candidateTexts.Count);
            for (var i = 0; i < candidateTexts.Count; i++)
            {
                scores.Add(i < _scores.Count ? _scores[i] : 0f);
            }

            return Task.FromResult<IReadOnlyList<float>>(scores);
        }
    }

    private sealed class ContentAwareSemanticEmbedder : ISemanticEmbedder
    {
        private readonly string _highScoreMarker;
        private readonly float _highScore;
        private readonly float _lowScore;

        public ContentAwareSemanticEmbedder(string highScoreMarker, float highScore, float lowScore)
        {
            _highScoreMarker = highScoreMarker;
            _highScore = highScore;
            _lowScore = lowScore;
        }

        public bool IsAvailable => true;

        public Task<IReadOnlyList<float>> ScoreAsync(
            string queryText,
            IReadOnlyList<string> candidateTexts,
            CancellationToken cancellationToken)
        {
            var scores = candidateTexts
                .Select(text => text.Contains(_highScoreMarker, StringComparison.OrdinalIgnoreCase) ? _highScore : _lowScore)
                .ToList();

            return Task.FromResult<IReadOnlyList<float>>(scores);
        }
    }

    private sealed class MarkerSemanticEmbedder : ISemanticEmbedder
    {
        private readonly IReadOnlyDictionary<string, float> _scoresByMarker;

        public MarkerSemanticEmbedder(IReadOnlyDictionary<string, float> scoresByMarker)
        {
            _scoresByMarker = scoresByMarker;
        }

        public bool IsAvailable => true;

        public Task<IReadOnlyList<float>> ScoreAsync(
            string queryText,
            IReadOnlyList<string> candidateTexts,
            CancellationToken cancellationToken)
        {
            var scores = new List<float>(candidateTexts.Count);
            foreach (var text in candidateTexts)
            {
                var score = 0f;
                foreach (var (marker, value) in _scoresByMarker)
                {
                    if (!text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        continue;

                    score = value;
                    break;
                }

                scores.Add(score);
            }

            return Task.FromResult<IReadOnlyList<float>>(scores);
        }
    }

    private sealed class FilteringIndexStore : IIndexStore
    {
        private readonly IReadOnlyList<SearchMethodMatch> _matches;

        public FilteringIndexStore(IReadOnlyList<SearchMethodMatch> matches)
        {
            _matches = matches;
        }

        public int RegexSearchCalls { get; private set; }
        public List<string> NonRegexPatterns { get; } = [];

        public Task<IReadOnlyList<SearchMethodMatch>> SearchMethodsAsync(
            string pattern,
            bool useRegex,
            string? solutionPath,
            string? solutionId,
            string? folderPath,
            string? filePath,
            CancellationToken cancellationToken)
        {
            if (useRegex)
            {
                RegexSearchCalls++;
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                return Task.FromResult<IReadOnlyList<SearchMethodMatch>>(
                    _matches.Where(match =>
                        regex.IsMatch(match.Method.Id) ||
                        regex.IsMatch(match.Method.Display ?? string.Empty)).ToList());
            }

            NonRegexPatterns.Add(pattern);
            var wildcardRegex = WildcardToRegex(pattern);
            return Task.FromResult<IReadOnlyList<SearchMethodMatch>>(
                _matches.Where(match =>
                    wildcardRegex.IsMatch(match.Method.Id) ||
                    wildcardRegex.IsMatch(match.Method.Display ?? string.Empty)).ToList());
        }

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveAsync(SolutionIndex index, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SolutionIndex?> LoadAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<SolutionIndex?>(null);

        public Task<IReadOnlyList<SolutionInfo>> ListSolutionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionInfo>>(Array.Empty<SolutionInfo>());

        public Task<SolutionInfo?> GetSolutionByPathAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<SolutionInfo?>(null);

        public Task<DateTime?> GetIndexedAtUtcAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<DateTime?>(null);

        public Task<SolutionInfo?> GetSolutionByIdAsync(string solutionId, CancellationToken cancellationToken)
            => Task.FromResult<SolutionInfo?>(null);

        public Task<IReadOnlyList<IndexedFileInfo>> ListFilesAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<IndexedFileInfo>>(Array.Empty<IndexedFileInfo>());

        public Task<IReadOnlyList<string>> ListProjectPathsAsync(string solutionPath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<SolutionInfo>> FindSolutionsByFilePathAsync(string filePath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionInfo>>(Array.Empty<SolutionInfo>());

        public Task<IReadOnlyList<SolutionFileMatch>> FindSolutionsByFilePathSuffixAsync(
            string relativeFilePath,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionFileMatch>>(Array.Empty<SolutionFileMatch>());

        public Task<IReadOnlyList<SolutionProjectMatch>> FindProjectsByPathSuffixAsync(
            string relativeProjectPath,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SolutionProjectMatch>>(Array.Empty<SolutionProjectMatch>());

        public Task<IReadOnlyList<SearchFileMatch>> SearchFilesAsync(
            string pattern,
            bool useRegex,
            string? solutionPath,
            string? solutionId,
            string? folderPath,
            string? filePath,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SearchFileMatch>>(Array.Empty<SearchFileMatch>());

        public Task<IReadOnlyList<SearchMethodMatch>> ListMethodsAsync(
            string visibility,
            string? solutionPath,
            string? solutionId,
            string? folderPath,
            string? filePath,
            CancellationToken cancellationToken)
            => Task.FromResult(_matches);

        public Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken)
            => Task.FromResult<Node?>(null);

        public Task<IReadOnlyList<Edge>> GetEdgesAsync(string solutionPath, string methodKey, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Edge>>(Array.Empty<Edge>());

        public Task UpdateFileAsync(string solutionPath, FileIndex update, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemoveFileAsync(string solutionPath, string filePath, CancellationToken cancellationToken)
            => Task.CompletedTask;

        private static Regex WildcardToRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".");

            return new Regex($"^{escaped}$", RegexOptions.IgnoreCase);
        }
    }
}
