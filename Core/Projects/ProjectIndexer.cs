using System.Collections.Concurrent;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CallGraph.Core.Projects;

public sealed class ProjectIndexer : IProjectIndexer
{
    public async Task<IndexSession> IndexAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        var outbound = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var nodes = new ConcurrentDictionary<string, Node>(StringComparer.Ordinal);

        // Extract project paths
        var projectPaths = projects
            .Where(p => p.FilePath is not null)
            .Select(p => Path.GetFullPath(p.FilePath!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var interfaceImplementations = await BuildInterfaceImplementationMapAsync(projects, cancellationToken)
            .ConfigureAwait(false);
        var messageHandlers = await BuildMessageHandlerMapAsync(projects, cancellationToken)
            .ConfigureAwait(false);

        var documents = projects
            .SelectMany(p => p.Documents)
            .Where(d => d.SupportsSyntaxTree && d.FilePath is not null)
            .ToList();

        await Parallel.ForEachAsync(documents, cancellationToken, async (doc, ct) =>
            {
                await IndexDocumentAsync(
                        doc,
                        outbound,
                        nodes,
                        interfaceImplementations,
                        messageHandlers,
                        ct)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        return new IndexSession(nodes, outbound, projectPaths);
    }

    private static async Task IndexDocumentAsync(
        Document doc,
        ConcurrentDictionary<string, HashSet<string>> outbound,
        ConcurrentDictionary<string, Node> nodes,
        Dictionary<string, List<INamedTypeSymbol>> interfaceImplementations,
        Dictionary<string, List<IMethodSymbol>> messageHandlers,
        CancellationToken cancellationToken)
    {
        var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
            return;

        var methods = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>();

        foreach (var md in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var caller = model.GetDeclaredSymbol(md, cancellationToken) as IMethodSymbol;
            if (caller is null)
                continue;

            var callerKey = SymbolKeyFormatter.Format(caller);
            nodes[callerKey] = MakeNode(caller, md.GetLocation());

            var calls = new HashSet<string>(StringComparer.Ordinal);

            foreach (var inv in md.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var callee = ResolveInvocationSymbol(inv, model, cancellationToken);
                if (callee is null)
                    continue;

                var calleeKey = SymbolKeyFormatter.Format(callee);
                calls.Add(calleeKey);
                TryAddSourceNode(nodes, calleeKey, callee);

                if (IsInterfaceCall(inv, model, cancellationToken, out var interfaceMethod))
                {
                    AddInterfaceImplementationCalls(
                        calls,
                        nodes,
                        interfaceMethod!,
                        interfaceImplementations);
                }

                AddPublishedMessageHandlerCalls(
                    calls,
                    nodes,
                    caller,
                    inv,
                    callee,
                    model,
                    messageHandlers,
                    cancellationToken);
            }

            foreach (var obj in md.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var ctor = ResolveMethodSymbol(model.GetSymbolInfo(obj, cancellationToken));
                if (ctor is null)
                    continue;

                var ctorKey = SymbolKeyFormatter.Format(ctor);
                calls.Add(ctorKey);
                TryAddSourceNode(nodes, ctorKey, ctor);
            }

            outbound[callerKey] = calls;
        }
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
        IReadOnlyList<Project> projects,
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
        IReadOnlyList<Project> projects,
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

    private static void AddInterfaceImplementationCalls(
        HashSet<string> calls,
        ConcurrentDictionary<string, Node> nodes,
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

            if (implementationMethod is null)
                continue;

            var implementationKey = SymbolKeyFormatter.Format(implementationMethod);
            calls.Add(implementationKey);
            TryAddSourceNode(nodes, implementationKey, implementationMethod);
        }
    }

    private static void AddPublishedMessageHandlerCalls(
        HashSet<string> calls,
        ConcurrentDictionary<string, Node> nodes,
        IMethodSymbol caller,
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

        var callerKey = SymbolKeyFormatter.Format(caller);
        foreach (var handler in handlers)
        {
            if (SymbolEqualityComparer.Default.Equals(handler, caller))
                continue;

            var handlerKey = SymbolKeyFormatter.Format(handler);
            if (string.Equals(handlerKey, callerKey, StringComparison.Ordinal))
                continue;

            calls.Add(handlerKey);
            TryAddSourceNode(nodes, handlerKey, handler);
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

    private static void TryAddSourceNode(
        ConcurrentDictionary<string, Node> nodes,
        string key,
        IMethodSymbol method)
    {
        var loc = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc?.SourceTree?.FilePath is null)
            return;

        nodes.TryAdd(key, MakeNode(method, loc));
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
