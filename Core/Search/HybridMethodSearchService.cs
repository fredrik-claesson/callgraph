using System.Text;
using System.Text.RegularExpressions;
using CallGraph.Contracts;
using CallGraph.Core.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallGraph.Core.Search;

public interface IHybridMethodSearchService
{
    Task<IReadOnlyList<SearchMethodMatch>> SearchAsync(
        string pattern,
        bool useRegex,
        string? solutionPath,
        string? solutionId,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken);
}

public sealed class HybridMethodSearchService : IHybridMethodSearchService
{
    private static readonly Regex SignInRegex = new(@"\bsign[\s_-]*in\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LogInRegex = new(@"\blog[\s_-]*in\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SignOnRegex = new(@"\bsign[\s_-]*on\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string[]> RelatedTerms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth"] =
            [
                "authentication", "authenticate", "authenticated", "authn", "login", "logon", "signin", "signon",
                "sso", "oidc", "oauth", "identity"
            ],
            ["authentication"] =
            [
                "auth", "authenticate", "authenticated", "authn", "login", "logon", "signin", "signon", "identity"
            ],
            ["login"] =
            [
                "auth", "authentication", "authenticate", "signin", "signon", "logon", "identity"
            ],
            ["signin"] =
            [
                "auth", "authentication", "login", "identity"
            ],
            ["signon"] =
            [
                "auth", "authentication", "login", "identity"
            ],
            ["identity"] =
            [
                "auth", "authentication", "login", "signin", "signon"
            ],
            ["tenant"] =
            [
                "organization", "organisation", "org"
            ],
            ["organization"] =
            [
                "tenant", "organisation", "org"
            ],
            ["organisation"] =
            [
                "tenant", "organization", "org"
            ]
        };

    private static readonly IReadOnlyDictionary<string, string> CanonicalTokens =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth"] = "auth",
            ["authentication"] = "auth",
            ["authenticate"] = "auth",
            ["authenticated"] = "auth",
            ["authn"] = "auth",
            ["login"] = "auth",
            ["signin"] = "auth",
            ["signon"] = "auth",
            ["logon"] = "auth",
            ["identity"] = "auth",
            ["organization"] = "tenant",
            ["organisation"] = "tenant",
            ["org"] = "tenant",
            ["tenant"] = "tenant"
        };

    private readonly IIndexStore _indexStore;
    private readonly ISemanticEmbedder _semanticEmbedder;
    private readonly HybridMethodSearchOptions _options;
    private readonly ILogger<HybridMethodSearchService> _logger;

    public HybridMethodSearchService(
        IIndexStore indexStore,
        ISemanticEmbedder semanticEmbedder,
        IOptions<HybridMethodSearchOptions> options,
        ILogger<HybridMethodSearchService> logger)
    {
        _indexStore = indexStore;
        _semanticEmbedder = semanticEmbedder;
        _logger = logger;

        _options = options.Value;
        _options.ResultLimit = Math.Clamp(_options.ResultLimit, 1, 500);
        _options.LexicalTopK = Math.Clamp(_options.LexicalTopK, 1, 500);
        _options.MaxCandidatePool = Math.Clamp(_options.MaxCandidatePool, _options.LexicalTopK, 20000);
        _options.MaxPatternQueries = Math.Clamp(_options.MaxPatternQueries, 1, 25);
        _options.MinQueryTokenLength = Math.Clamp(_options.MinQueryTokenLength, 1, 10);
        _options.SemanticWeight = Math.Clamp(_options.SemanticWeight, 0, 1);
    }

    public async Task<IReadOnlyList<SearchMethodMatch>> SearchAsync(
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
            return await _indexStore
                .SearchMethodsAsync(pattern, useRegex: true, solutionPath, solutionId, folderPath, filePath, cancellationToken)
                .ConfigureAwait(false);
        }

        var query = BuildQuery(pattern);
        var candidatePatterns = BuildCandidatePatterns(query);

        var byMethodKey = new Dictionary<string, SearchMethodMatch>(StringComparer.Ordinal);
        foreach (var candidatePattern in candidatePatterns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var patternMatches = await _indexStore
                .SearchMethodsAsync(candidatePattern, useRegex: false, solutionPath, solutionId, folderPath, filePath, cancellationToken)
                .ConfigureAwait(false);

            foreach (var match in patternMatches)
            {
                var uniqueKey = match.SolutionId + "::" + match.Method.Id;
                byMethodKey.TryAdd(uniqueKey, match);
            }

            if (byMethodKey.Count >= _options.MaxCandidatePool)
                break;
        }

        if (byMethodKey.Count == 0)
            return Array.Empty<SearchMethodMatch>();

        var candidates = byMethodKey.Values.ToList();
        var lexical = BuildRankedCandidates(candidates, query, filterBySpecificTokens: true);
        if (lexical.Count == 0 && query.SpecificRawTokens.Count > 0)
        {
            // Fall back when strict specific-token filtering is too narrow.
            lexical = BuildRankedCandidates(candidates, query, filterBySpecificTokens: false);
        }

        var lexicalOrdered = lexical
            .OrderByDescending(static m => m.LexicalScore)
            .ThenBy(static m => m.Match.SolutionPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static m => m.Match.Method.Id, StringComparer.Ordinal)
            .Take(_options.LexicalTopK)
            .ToList();

        if (!_options.EnableSemanticRerank || lexicalOrdered.Count < 2)
        {
            var lexicalFiltered = FilterLexicalByRelativeScore(lexicalOrdered);
            return lexicalFiltered
                .Take(_options.ResultLimit)
                .Select(static m => m.Match)
                .ToList();
        }

        if (!_semanticEmbedder.IsAvailable)
        {
            _logger.LogDebug("Semantic embedder unavailable; returning lexical-only search ordering.");
            var lexicalFiltered = FilterLexicalByRelativeScore(lexicalOrdered);
            return lexicalFiltered
                .Take(_options.ResultLimit)
                .Select(static m => m.Match)
                .ToList();
        }

        var semanticScores = await _semanticEmbedder
            .ScoreAsync(query.SemanticText, lexicalOrdered.Select(static m => m.SemanticText).ToList(), cancellationToken)
            .ConfigureAwait(false);

        var maxLexical = Math.Max(1d, lexicalOrdered.Max(static m => m.LexicalScore));
        var combined = new List<RankedMethod>(lexicalOrdered.Count);
        for (var i = 0; i < lexicalOrdered.Count; i++)
        {
            var lexicalNormalized = lexicalOrdered[i].LexicalScore / maxLexical;
            var semanticNormalized = (semanticScores[i] + 1f) / 2f;
            var score = (1 - _options.SemanticWeight) * lexicalNormalized + _options.SemanticWeight * semanticNormalized;

            combined.Add(lexicalOrdered[i] with
            {
                SemanticScore = semanticScores[i],
                CombinedScore = score
            });
        }

        var combinedOrdered = combined
            .OrderByDescending(static m => m.CombinedScore)
            .ThenByDescending(static m => m.LexicalScore)
            .ThenBy(static m => m.Match.SolutionPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static m => m.Match.Method.Id, StringComparer.Ordinal)
            .ToList();

        var combinedFiltered = FilterCombinedByRelativeScore(combinedOrdered);
        return combinedFiltered
            .Take(_options.ResultLimit)
            .Select(static m => m.Match)
            .ToList();
    }

    private static IReadOnlyList<RankedMethod> FilterLexicalByRelativeScore(IReadOnlyList<RankedMethod> ranked)
    {
        if (ranked.Count == 0)
            return ranked;

        var best = ranked[0].LexicalScore;
        if (best <= 0)
            return ranked;

        var cutoff = Math.Max(1d, best * 0.45d);
        var filtered = ranked.Where(m => m.LexicalScore >= cutoff).ToList();
        return filtered.Count >= 5 ? filtered : ranked;
    }

    private static IReadOnlyList<RankedMethod> FilterCombinedByRelativeScore(IReadOnlyList<RankedMethod> ranked)
    {
        if (ranked.Count == 0)
            return ranked;

        var best = ranked[0].CombinedScore;
        if (best <= 0)
            return ranked;

        // Keep only methods close to the best combined score to reduce low-signal tail results.
        var cutoff = Math.Max(0.45d, best * 0.85d);
        var filtered = ranked.Where(m => m.CombinedScore >= cutoff).ToList();
        return filtered.Count > 0 ? filtered : ranked.Take(1).ToList();
    }

    private List<string> BuildCandidatePatterns(MethodQuery query)
    {
        if (query.ContainsWildcard && !ShouldAugmentWildcardPattern(query.RawText))
            return new List<string> { query.RawText };

        var patterns = new List<string>();

        if (query.ContainsWildcard && !string.IsNullOrWhiteSpace(query.RawText))
        {
            patterns.Add(query.RawText);
        }

        if (!string.IsNullOrWhiteSpace(query.RawText))
        {
            var phrasePattern = "*" + string.Join("*", query.RawTokens.Where(static token => token.Length > 0)) + "*";
            if (!string.Equals(phrasePattern, "**", StringComparison.Ordinal))
                patterns.Add(phrasePattern);
        }

        if (query.LiteralTokens.Count > 0)
        {
            var literalPhrasePattern = "*" + string.Join("*", query.LiteralTokens.Where(static token => token.Length > 0)) + "*";
            if (!string.Equals(literalPhrasePattern, "**", StringComparison.Ordinal))
                patterns.Add(literalPhrasePattern);
        }

        var expandedTokens = new HashSet<string>(query.RawTokens, StringComparer.OrdinalIgnoreCase);
        foreach (var token in query.LiteralTokens)
        {
            expandedTokens.Add(token);
        }
        if (query.LiteralTokens.Count > 1)
        {
            expandedTokens.Add(string.Concat(query.LiteralTokens));
        }

        foreach (var token in query.RawTokens)
        {
            if (!RelatedTerms.TryGetValue(token, out var related))
                continue;

            foreach (var relatedToken in related)
            {
                expandedTokens.Add(relatedToken);
            }
        }

        foreach (var token in expandedTokens)
        {
            if (token.Length < _options.MinQueryTokenLength)
                continue;

            patterns.Add($"*{token}*");
        }

        // Fall back to broad wildcard query if token extraction produced nothing.
        if (patterns.Count == 0)
            patterns.Add("*" + query.RawText.Trim() + "*");

        return patterns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(_options.MaxPatternQueries)
            .ToList();
    }

    private static bool ShouldAugmentWildcardPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        if (pattern.IndexOfAny(['*', '?']) < 0)
            return false;

        // Path-like/file-like wildcard patterns should remain strict.
        if (pattern.Contains('/', StringComparison.Ordinal) ||
            pattern.Contains('\\', StringComparison.Ordinal) ||
            pattern.Contains(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Keep strict wildcard semantics for simple prefix/suffix-only patterns.
        // Augment only when a wildcard appears in the interior, which signals
        // uncertain token boundaries (e.g. *Hybrid*Search*).
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if ((ch == '*' || ch == '?') && i > 0 && i < pattern.Length - 1)
                return true;
        }

        var hasPrefixWildcard = pattern.Length > 0 && (pattern[0] == '*' || pattern[0] == '?');
        var hasSuffixWildcard = pattern.Length > 0 && (pattern[^1] == '*' || pattern[^1] == '?');
        return hasPrefixWildcard && hasSuffixWildcard;
    }

    private static MethodQuery BuildQuery(string pattern)
    {
        var containsWildcard = pattern.IndexOfAny(['*', '?']) >= 0;
        var normalized = NormalizeQueryText(pattern);
        var literalTokens = SplitLiteralTerms(normalized)
            .Where(static token => token.Length > 0)
            .ToList();
        var rawTokens = containsWildcard
            ? SplitTokens(normalized)
                .Where(static token => token.Length > 0)
                .ToList()
            : literalTokens;

        var canonicalTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in rawTokens.Concat(literalTokens))
        {
            canonicalTokens.Add(CanonicalizeToken(token));

            if (RelatedTerms.TryGetValue(token, out var related))
            {
                foreach (var relatedToken in related)
                {
                    canonicalTokens.Add(CanonicalizeToken(relatedToken));
                }
            }
        }

        var semanticTextTokens = literalTokens
            .Concat(rawTokens)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var semanticText = semanticTextTokens.Count > 0
            ? string.Join(' ', semanticTextTokens)
            : normalized;
        var specificRawTokens = rawTokens
            .Concat(literalTokens)
            .Where(static token => token.Length >= 6)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MethodQuery(
            RawText: pattern,
            LiteralTokens: literalTokens,
            RawTokens: rawTokens,
            CanonicalTokens: canonicalTokens,
            SemanticText: semanticText,
            SpecificRawTokens: specificRawTokens,
            ContainsWildcard: containsWildcard);
    }

    private static MethodDocument BuildDocument(SearchMethodMatch match)
    {
        var methodId = match.Method.Id;
        var containingType = match.Method.ContainingType;

        if (string.IsNullOrWhiteSpace(containingType))
            containingType = ParseContainingTypeFromMethodId(methodId);

        var className = ParseClassName(containingType);
        var namespaceName = ParseNamespace(containingType);
        var methodName = ParseMethodName(match.Method.Display, methodId);
        var signatureText = match.Method.Display ?? methodId;

        var namespaceTokens = SplitAndCanonicalize(namespaceName);
        var classTokens = SplitAndCanonicalize(className);
        var methodTokens = SplitAndCanonicalize(methodName);
        var signatureTokens = SplitAndCanonicalize(signatureText);

        var semanticText =
            $"namespace {namespaceName} class {className} method {methodName} signature {signatureText} id {methodId}";

        return new MethodDocument(
            NamespaceTokens: namespaceTokens,
            ClassTokens: classTokens,
            MethodTokens: methodTokens,
            SignatureTokens: signatureTokens,
            SemanticText: semanticText);
    }

    private static double ComputeLexicalScore(MethodQuery query, MethodDocument document)
    {
        if (query.CanonicalTokens.Count == 0)
            return 0;

        var score = 0d;
        foreach (var token in query.CanonicalTokens)
        {
            if (document.MethodTokens.Contains(token))
            {
                score += 6;
                continue;
            }

            if (document.ClassTokens.Contains(token))
            {
                score += 4;
                continue;
            }

            if (document.NamespaceTokens.Contains(token))
            {
                score += 2;
                continue;
            }

            if (document.SignatureTokens.Contains(token))
            {
                score += 1;
            }
        }

        if (document.SemanticText.Contains(query.RawText, StringComparison.OrdinalIgnoreCase))
            score += 8;

        return score;
    }

    private static List<RankedMethod> BuildRankedCandidates(
        IReadOnlyList<SearchMethodMatch> candidates,
        MethodQuery query,
        bool filterBySpecificTokens)
    {
        var ranked = new List<RankedMethod>(candidates.Count);
        foreach (var match in candidates)
        {
            var doc = BuildDocument(match);
            if (filterBySpecificTokens &&
                query.SpecificRawTokens.Count > 0 &&
                !ContainsAnyQueryToken(doc, query.SpecificRawTokens))
            {
                continue;
            }

            var score = ComputeLexicalScore(query, doc);
            ranked.Add(new RankedMethod(match, doc.SemanticText, score, SemanticScore: 0f, CombinedScore: 0));
        }

        return ranked;
    }

    private static bool ContainsAnyQueryToken(MethodDocument document, IReadOnlyList<string> queryTokens)
    {
        foreach (var token in queryTokens)
        {
            if (document.MethodTokens.Contains(token) ||
                document.ClassTokens.Contains(token) ||
                document.NamespaceTokens.Contains(token) ||
                document.SignatureTokens.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeQueryText(string text)
    {
        var normalized = SignInRegex.Replace(text, "signin");
        normalized = LogInRegex.Replace(normalized, "login");
        normalized = SignOnRegex.Replace(normalized, "signon");
        return normalized;
    }

    private static HashSet<string> SplitAndCanonicalize(string? text)
        => SplitTokens(text)
            .Concat(SplitLiteralTerms(text))
            .Select(CanonicalizeToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitLiteralTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var buffer = new StringBuilder();
        foreach (var current in text)
        {
            if (!char.IsLetterOrDigit(current))
            {
                if (buffer.Length == 0)
                    continue;

                yield return buffer.ToString().ToLowerInvariant();
                buffer.Clear();
                continue;
            }

            buffer.Append(current);
        }

        if (buffer.Length > 0)
            yield return buffer.ToString().ToLowerInvariant();
    }

    private static IEnumerable<string> SplitTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var buffer = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            var previous = i > 0 ? text[i - 1] : '\0';
            var next = i < text.Length - 1 ? text[i + 1] : '\0';

            if (!char.IsLetterOrDigit(current))
            {
                if (buffer.Length > 0)
                {
                    var token = buffer.ToString();
                    buffer.Clear();
                    if (token.Length > 0)
                        yield return token;
                }
                continue;
            }

            var splitBeforeCurrent =
                buffer.Length > 0 &&
                (char.IsDigit(current) != char.IsDigit(previous) ||
                 (char.IsUpper(current) && char.IsLower(previous)) ||
                 (char.IsUpper(current) && char.IsUpper(previous) && char.IsLower(next)));

            if (splitBeforeCurrent && buffer.Length > 0)
            {
                var token = buffer.ToString();
                buffer.Clear();
                if (token.Length > 0)
                    yield return token;
            }

            buffer.Append(char.ToLowerInvariant(current));
        }

        if (buffer.Length > 0)
            yield return buffer.ToString();
    }

    private static string CanonicalizeToken(string token)
        => CanonicalTokens.TryGetValue(token, out var canonical)
            ? canonical
            : token;

    private static string ParseContainingTypeFromMethodId(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
            return string.Empty;

        var colonIndex = methodId.IndexOf(':');
        var withoutAssembly = colonIndex >= 0 ? methodId[(colonIndex + 1)..] : methodId;
        var parametersIndex = withoutAssembly.IndexOf('(');
        var withoutParameters = parametersIndex >= 0 ? withoutAssembly[..parametersIndex] : withoutAssembly;
        var lastDot = withoutParameters.LastIndexOf('.');

        return lastDot > 0
            ? withoutParameters[..lastDot]
            : string.Empty;
    }

    private static string ParseNamespace(string containingType)
    {
        if (string.IsNullOrWhiteSpace(containingType))
            return string.Empty;

        var lastDot = containingType.LastIndexOf('.');
        return lastDot > 0
            ? containingType[..lastDot]
            : string.Empty;
    }

    private static string ParseClassName(string containingType)
    {
        if (string.IsNullOrWhiteSpace(containingType))
            return string.Empty;

        var lastDot = containingType.LastIndexOf('.');
        return lastDot >= 0
            ? containingType[(lastDot + 1)..]
            : containingType;
    }

    private static string ParseMethodName(string? display, string methodId)
    {
        if (!string.IsNullOrWhiteSpace(display))
        {
            var signatureStart = display.IndexOf('(');
            var withoutSignature = signatureStart >= 0 ? display[..signatureStart] : display;
            var lastDot = withoutSignature.LastIndexOf('.');
            return lastDot >= 0
                ? withoutSignature[(lastDot + 1)..]
                : withoutSignature;
        }

        var colonIndex = methodId.IndexOf(':');
        var withoutAssembly = colonIndex >= 0 ? methodId[(colonIndex + 1)..] : methodId;
        var signatureIndex = withoutAssembly.IndexOf('(');
        var withoutSignatureFromId = signatureIndex >= 0 ? withoutAssembly[..signatureIndex] : withoutAssembly;
        var lastDotFromId = withoutSignatureFromId.LastIndexOf('.');

        return lastDotFromId >= 0
            ? withoutSignatureFromId[(lastDotFromId + 1)..]
            : withoutSignatureFromId;
    }

    private sealed record MethodQuery(
        string RawText,
        IReadOnlyList<string> LiteralTokens,
        IReadOnlyList<string> RawTokens,
        IReadOnlySet<string> CanonicalTokens,
        string SemanticText,
        IReadOnlyList<string> SpecificRawTokens,
        bool ContainsWildcard);

    private sealed record MethodDocument(
        IReadOnlySet<string> NamespaceTokens,
        IReadOnlySet<string> ClassTokens,
        IReadOnlySet<string> MethodTokens,
        IReadOnlySet<string> SignatureTokens,
        string SemanticText);

    private sealed record RankedMethod(
        SearchMethodMatch Match,
        string SemanticText,
        double LexicalScore,
        float SemanticScore,
        double CombinedScore);
}
