using System.Collections.Concurrent;
using System.Diagnostics;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Solutions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallGraph.Core.Indexing;

public sealed class FileIndexer : IFileIndexer
{
    private readonly ISolutionLoader _solutionLoader;
    private readonly ISolutionContextCache? _solutionContextCache;
    private readonly ILogger<FileIndexer> _logger;
    private sealed record DispatchMaps(
        Dictionary<string, List<INamedTypeSymbol>> InterfaceImplementations,
        Dictionary<string, List<IMethodSymbol>> MessageHandlers);

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

            long dispatchMapBuildMs = -1;
            var dispatchMapBuilt = 0;
            var dispatchMaps = new Lazy<Task<DispatchMaps>>(
                async () =>
                {
                    var mapTimer = Stopwatch.StartNew();
                    var interfaceMap = await BuildInterfaceImplementationMapAsync(loadedContext.Projects, cancellationToken)
                        .ConfigureAwait(false);
                    var messageHandlerMap = await BuildMessageHandlerMapAsync(loadedContext.Projects, cancellationToken)
                        .ConfigureAwait(false);
                    mapTimer.Stop();
                    Interlocked.Exchange(ref dispatchMapBuildMs, mapTimer.ElapsedMilliseconds);
                    Interlocked.Exchange(ref dispatchMapBuilt, 1);
                    return new DispatchMaps(interfaceMap, messageHandlerMap);
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
                        () => dispatchMaps.Value,
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
                dispatchMapBuilt == 1,
                dispatchMapBuilt == 1 ? dispatchMapBuildMs : 0,
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
            Kind = callKind switch
            {
                "interface" => "calls-via-interface",
                "message" => "calls-via-message",
                _ => "calls"
            }
        });
    }

    private static IMethodSymbol? ResolveMethodSymbol(SymbolInfo info)
        => info.Symbol as IMethodSymbol
           ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

    private static IMethodSymbol? ResolveInvocationSymbol(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (model.GetOperation(invocation, cancellationToken) is IInvocationOperation operation)
            return operation.TargetMethod;

        return ResolveMethodSymbol(model.GetSymbolInfo(invocation, cancellationToken));
    }

    private static bool IsInterfaceCall(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken,
        out IMethodSymbol? interfaceMethod)
    {
        interfaceMethod = null;

        var resolved = ResolveInvocationSymbol(invocation, model, cancellationToken);
        if (resolved?.ContainingType?.TypeKind == TypeKind.Interface)
        {
            interfaceMethod = resolved;
            return true;
        }

        if (model.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
            return false;

        if (operation.TargetMethod.ContainingType.TypeKind == TypeKind.Interface)
        {
            interfaceMethod = operation.TargetMethod;
            return true;
        }

        if (operation.Instance?.Type is INamedTypeSymbol { TypeKind: TypeKind.Interface } &&
            operation.TargetMethod.ContainingType.TypeKind == TypeKind.Interface)
        {
            interfaceMethod = operation.TargetMethod;
            return true;
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

    private static async Task<Dictionary<string, List<IMethodSymbol>>> BuildMessageHandlerMapAsync(
        IEnumerable<Project> projects,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<IMethodSymbol>>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

                foreach (var methodDeclaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var method = semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) as IMethodSymbol;
                    if (!IsMessageHandlerCandidate(method))
                        continue;

                    foreach (var parameter in method!.Parameters)
                    {
                        if (!IsMessagePayloadType(parameter.Type))
                            continue;

                        var key = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        if (!map.TryGetValue(key, out var handlers))
                        {
                            handlers = new List<IMethodSymbol>();
                            map[key] = handlers;
                        }

                        handlers.Add(method);
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
        Func<Task<DispatchMaps>> getDispatchMaps,
        CancellationToken cancellationToken)
    {
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
            return null;

        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        DispatchMaps? dispatchMaps = null;

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
                var callee = ResolveInvocationSymbol(inv, model, cancellationToken);
                if (callee is null)
                    continue;

                AddEdge(edges, edgeKeys, callerKey, SymbolKeyFormatter.Format(callee), "direct");

                if (IsInterfaceCall(inv, model, cancellationToken, out var interfaceMethod))
                {
                    dispatchMaps ??= await getDispatchMaps().ConfigureAwait(false);
                    AddInterfaceImplementationEdges(
                        edges,
                        edgeKeys,
                        callerKey,
                        interfaceMethod!,
                        dispatchMaps.InterfaceImplementations);
                }

                dispatchMaps ??= await getDispatchMaps().ConfigureAwait(false);
                AddPublishedMessageHandlerEdges(
                    edges,
                    edgeKeys,
                    callerKey,
                    inv,
                    callee,
                    model,
                    dispatchMaps.MessageHandlers,
                    cancellationToken);
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

    private static void AddPublishedMessageHandlerEdges(
        ICollection<Edge> edges,
        HashSet<string> edgeKeys,
        string callerKey,
        InvocationExpressionSyntax invocation,
        IMethodSymbol callee,
        SemanticModel model,
        Dictionary<string, List<IMethodSymbol>> messageHandlers,
        CancellationToken cancellationToken)
    {
        if (!TryGetPublishedMessageType(invocation, callee, model, cancellationToken, out var payloadType))
            return;

        var payloadKey = payloadType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!messageHandlers.TryGetValue(payloadKey, out var handlers))
            return;

        foreach (var handler in handlers)
        {
            var handlerKey = SymbolKeyFormatter.Format(handler);
            if (string.Equals(handlerKey, callerKey, StringComparison.Ordinal))
                continue;

            AddEdge(edges, edgeKeys, callerKey, handlerKey, "message");
        }
    }

    private static bool TryGetPublishedMessageType(
        InvocationExpressionSyntax invocation,
        IMethodSymbol callee,
        SemanticModel model,
        CancellationToken cancellationToken,
        out ITypeSymbol? payloadType)
    {
        payloadType = null;

        if (!LooksLikePublisherCall(callee))
            return false;

        if (callee.IsGenericMethod && callee.TypeArguments.Length > 0)
        {
            var typeArgument = callee.TypeArguments[0];
            if (IsMessagePayloadType(typeArgument))
            {
                payloadType = typeArgument;
                return true;
            }
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            var type = model.GetTypeInfo(argument.Expression, cancellationToken).Type;
            if (!IsMessagePayloadType(type))
                continue;

            payloadType = type;
            return true;
        }

        return false;
    }

    private static bool LooksLikePublisherCall(IMethodSymbol callee)
    {
        if (ContainsPublisherVerb(callee.Name))
            return true;

        var containingType = callee.ContainingType?.Name ?? string.Empty;
        return containingType.Contains("Event", StringComparison.OrdinalIgnoreCase)
               || containingType.Contains("Bus", StringComparison.OrdinalIgnoreCase)
               || containingType.Contains("Mediator", StringComparison.OrdinalIgnoreCase)
               || containingType.Contains("Publisher", StringComparison.OrdinalIgnoreCase)
               || containingType.Contains("Dispatcher", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPublisherVerb(string methodName)
        => methodName.Contains("Publish", StringComparison.OrdinalIgnoreCase)
           || methodName.Contains("Emit", StringComparison.OrdinalIgnoreCase)
           || methodName.Contains("Dispatch", StringComparison.OrdinalIgnoreCase)
           || methodName.Contains("Raise", StringComparison.OrdinalIgnoreCase)
           || methodName.Contains("Send", StringComparison.OrdinalIgnoreCase);

    private static bool IsMessageHandlerCandidate(IMethodSymbol? method)
    {
        if (method is null || method.MethodKind != MethodKind.Ordinary)
            return false;

        if (method.Parameters.Length == 0)
            return false;

        if (method.Name.Contains("Handle", StringComparison.OrdinalIgnoreCase))
            return true;

        var containingTypeName = method.ContainingType?.Name ?? string.Empty;
        return containingTypeName.Contains("Handler", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMessagePayloadType(ITypeSymbol? type)
    {
        if (type is null)
            return false;

        if (type.SpecialType != SpecialType.None)
            return false;

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (string.Equals(fullName, "global::System.String", StringComparison.Ordinal)
            || string.Equals(fullName, "global::System.Threading.CancellationToken", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
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
