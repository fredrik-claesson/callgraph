using System.Collections.Concurrent;
using System.Diagnostics;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Solutions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallGraph.Core.Indexing;

public sealed class FileIndexer : IFileIndexer
{
    private readonly ISolutionLoader _solutionLoader;
    private readonly ISolutionContextCache? _solutionContextCache;
    private readonly ILogger<FileIndexer> _logger;

    public FileIndexer(
        ISolutionLoader solutionLoader,
        ISolutionContextCache? solutionContextCache = null,
        ILogger<FileIndexer>? logger = null)
    {
        _solutionLoader = solutionLoader;
        _solutionContextCache = solutionContextCache;
        _logger = logger ?? NullLogger<FileIndexer>.Instance;
    }

    public async Task<FileIndex?> IndexFileAsync(
        string solutionPath,
        string filePath,
        bool slnOnly,
        CancellationToken cancellationToken)
        => (await IndexFilesAsync(solutionPath, new[] { filePath }, slnOnly, cancellationToken)
            .ConfigureAwait(false))
            .FirstOrDefault();

    public async Task<IReadOnlyList<FileIndex>> IndexFilesAsync(
        string solutionPath,
        IReadOnlyList<string> filePaths,
        bool slnOnly,
        CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0)
            return Array.Empty<FileIndex>();

        var normalizedFilePaths = filePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();

        var loadedContext = _solutionContextCache is not null
            ? await _solutionContextCache.GetOrLoadAsync(solutionPath, slnOnly, cancellationToken).ConfigureAwait(false)
            : await _solutionLoader.LoadAsync(solutionPath, slnOnly, cancellationToken).ConfigureAwait(false);
        try
        {
            var loadSolutionMs = stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();

            var documentLookup = BuildDocumentLookup(loadedContext.Projects);
            var buildLookupMs = stageTimer.ElapsedMilliseconds;
            stageTimer.Restart();

            long interfaceMapBuildMs = -1;
            var interfaceMapBuilt = 0;
            var interfaceMap = new Lazy<Task<Dictionary<string, List<INamedTypeSymbol>>>>(
                async () =>
                {
                    var mapTimer = Stopwatch.StartNew();
                    var map = await BuildInterfaceImplementationMapAsync(loadedContext.Projects, cancellationToken)
                        .ConfigureAwait(false);
                    mapTimer.Stop();
                    Interlocked.Exchange(ref interfaceMapBuildMs, mapTimer.ElapsedMilliseconds);
                    Interlocked.Exchange(ref interfaceMapBuilt, 1);
                    return map;
                });

            var results = new ConcurrentBag<FileIndex>();
            var matchedDocumentCount = 0;
            var missingDocumentCount = 0;
            await Parallel.ForEachAsync(normalizedFilePaths, cancellationToken, async (normalizedFilePath, ct) =>
            {
                if (!documentLookup.TryGetValue(normalizedFilePath, out var doc))
                {
                    Interlocked.Increment(ref missingDocumentCount);
                    return;
                }

                Interlocked.Increment(ref matchedDocumentCount);

                var index = await BuildIndexForDocumentAsync(
                        doc,
                        normalizedFilePath,
                        () => interfaceMap.Value,
                        ct)
                    .ConfigureAwait(false);
                if (index is not null)
                    results.Add(index);
            }).ConfigureAwait(false);

            var indexDocumentsMs = stageTimer.ElapsedMilliseconds;
            totalTimer.Stop();

            _logger.LogInformation(
                "File indexing timings for {SolutionPath}: requested={RequestedCount}, matched={MatchedCount}, missing={MissingCount}, loadSolution={LoadSolutionMs}ms, buildLookup={BuildLookupMs}ms, buildInterfaceMapBuilt={BuildInterfaceMapBuilt}, buildInterfaceMap={BuildInterfaceMapMs}ms, indexDocuments={IndexDocumentsMs}ms, total={TotalMs}ms, indexedResults={IndexedResultCount}.",
                solutionPath,
                normalizedFilePaths.Count,
                matchedDocumentCount,
                missingDocumentCount,
                loadSolutionMs,
                buildLookupMs,
                interfaceMapBuilt == 1,
                interfaceMapBuilt == 1 ? interfaceMapBuildMs : 0,
                indexDocumentsMs,
                totalTimer.ElapsedMilliseconds,
                results.Count);

            return results.ToList();
        }
        finally
        {
            if (_solutionContextCache is null)
                await loadedContext.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void AddEdge(
        ICollection<Edge> edges,
        HashSet<string> edgeKeys,
        string from,
        string to,
        string callKind = "direct")
    {
        var key = $"{from}\u0000{to}\u0000{callKind}";
        if (!edgeKeys.Add(key))
            return;

        edges.Add(new Edge
        {
            From = from,
            To = to,
            Direction = "outbound",
            Kind = callKind == "interface" ? "calls-via-interface" : "calls"
        });
    }

    private static IMethodSymbol? ResolveMethodSymbol(SymbolInfo info)
        => info.Symbol as IMethodSymbol
           ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

    private static bool IsInterfaceCall(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken,
        out IMethodSymbol? interfaceMethod)
    {
        interfaceMethod = null;

        // Get the expression being invoked (the thing before the parentheses)
        var expression = invocation.Expression;
        
        // For member access (obj.Method()), check the type of the object
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            var typeInfo = model.GetTypeInfo(memberAccess.Expression, cancellationToken);
            var type = typeInfo.Type;
            
            if (type is INamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceType)
            {
                var symbolInfo = model.GetSymbolInfo(invocation, cancellationToken);
                interfaceMethod = symbolInfo.Symbol as IMethodSymbol;
                return interfaceMethod?.ContainingType?.TypeKind == TypeKind.Interface;
            }
        }

        return false;
    }

    private static async Task<Dictionary<string, List<INamedTypeSymbol>>> BuildInterfaceImplementationMapAsync(
        IEnumerable<Project> projects,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                
                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken);
                    if (typeSymbol is INamedTypeSymbol namedType && !namedType.IsAbstract)
                    {
                        // Track all interfaces this type implements
                        foreach (var @interface in namedType.AllInterfaces)
                        {
                            var interfaceKey = @interface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            if (!map.TryGetValue(interfaceKey, out var implementations))
                            {
                                implementations = new List<INamedTypeSymbol>();
                                map[interfaceKey] = implementations;
                            }
                            implementations.Add(namedType);
                        }
                    }
                }
            }
        }

        return map;
    }

    private static Dictionary<string, Document> BuildDocumentLookup(IEnumerable<Project> projects)
    {
        var lookup = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            foreach (var doc in project.Documents)
            {
                if (!doc.SupportsSyntaxTree || doc.FilePath is null)
                    continue;

                var normalized = Path.GetFullPath(doc.FilePath);
                lookup[normalized] = doc;
            }
        }

        return lookup;
    }

    private static async Task<FileIndex?> BuildIndexForDocumentAsync(
        Document doc,
        string normalizedFilePath,
        Func<Task<Dictionary<string, List<INamedTypeSymbol>>>> getInterfaceImplementations,
        CancellationToken cancellationToken)
    {
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
            return null;

        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, List<INamedTypeSymbol>>? interfaceImplementations = null;

        foreach (var md in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var caller = model.GetDeclaredSymbol(md, cancellationToken) as IMethodSymbol;
            if (caller is null)
                continue;

            var callerKey = SymbolKeyFormatter.Format(caller);
            nodes.Add(MakeNode(caller, md.GetLocation()));

            foreach (var inv in md.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var symbolInfo = model.GetSymbolInfo(inv, cancellationToken);
                var callee = ResolveMethodSymbol(symbolInfo);
                if (callee is null)
                    continue;

                AddEdge(edges, edgeKeys, callerKey, SymbolKeyFormatter.Format(callee), "direct");

                if (IsInterfaceCall(inv, model, cancellationToken, out var interfaceMethod))
                {
                    interfaceImplementations ??= await getInterfaceImplementations().ConfigureAwait(false);
                    AddInterfaceImplementationEdges(
                        edges,
                        edgeKeys,
                        callerKey,
                        interfaceMethod!,
                        interfaceImplementations);
                }
            }

            foreach (var obj in md.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var ctor = ResolveMethodSymbol(model.GetSymbolInfo(obj, cancellationToken));
                if (ctor is null)
                    continue;

                AddEdge(edges, edgeKeys, callerKey, SymbolKeyFormatter.Format(ctor), "direct");
            }
        }

        return new FileIndex
        {
            FilePath = normalizedFilePath,
            Nodes = nodes
                .DistinctBy(n => n.Id)
                .OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToList(),
            Edges = edges
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ThenBy(e => e.Direction, StringComparer.Ordinal)
                .ToList()
        };
    }

    private static void AddInterfaceImplementationEdges(
        ICollection<Edge> edges,
        HashSet<string> edgeKeys,
        string callerKey,
        IMethodSymbol interfaceMethod,
        Dictionary<string, List<INamedTypeSymbol>> interfaceImplementations)
    {
        var interfaceType = interfaceMethod.ContainingType;
        if (interfaceType is null)
            return;

        var interfaceKey = interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!interfaceImplementations.TryGetValue(interfaceKey, out var implementations))
            return;

        foreach (var implementingType in implementations)
        {
            var implementationMethod = MethodSignatureMatcher.FindImplementationMethod(implementingType, interfaceMethod);

            if (implementationMethod is not null)
            {
                var implementationKey = SymbolKeyFormatter.Format(implementationMethod);
                AddEdge(edges, edgeKeys, callerKey, implementationKey, "interface");
            }
        }
    }

    private static Node MakeNode(IMethodSymbol method, Location? location)
    {
        var loc = location?.IsInSource == true
            ? location
            : method.Locations.FirstOrDefault(l => l.IsInSource);
        var file = loc?.SourceTree?.FilePath;
        var line = loc is null ? (int?)null : loc.GetLineSpan().StartLinePosition.Line + 1;

        return new Node
        {
            Id = SymbolKeyFormatter.Format(method),
            Kind = "method",
            Display = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ContainingType = method.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            FilePath = file is null ? null : Path.GetFullPath(file),
            StartLine = line,
            Accessibility = MapAccessibility(method.DeclaredAccessibility)
        };
    }

    private static string? MapAccessibility(Accessibility accessibility)
        => accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.Private => "private",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            _ => null
        };
}
