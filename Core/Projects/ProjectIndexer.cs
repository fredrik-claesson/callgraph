using System.Collections.Concurrent;
using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using CallGraph.Core.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
                var callee = ResolveMethodSymbol(model.GetSymbolInfo(inv, cancellationToken));
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

    private static bool IsInterfaceCall(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        CancellationToken cancellationToken,
        out IMethodSymbol? interfaceMethod)
    {
        interfaceMethod = null;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var typeInfo = model.GetTypeInfo(memberAccess.Expression, cancellationToken);
            var type = typeInfo.Type;

            if (type is INamedTypeSymbol { TypeKind: TypeKind.Interface })
            {
                var symbolInfo = model.GetSymbolInfo(invocation, cancellationToken);
                interfaceMethod = symbolInfo.Symbol as IMethodSymbol;
                return interfaceMethod?.ContainingType?.TypeKind == TypeKind.Interface;
            }
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
