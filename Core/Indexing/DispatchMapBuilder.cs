using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallGraph.Core.Indexing;

internal static class DispatchMapBuilder
{
    public static async Task<DispatchMaps> BuildAsync(
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
    {
        var interfaceImplementations = await BuildInterfaceImplementationMapAsync(projects, cancellationToken)
            .ConfigureAwait(false);
        var messageHandlers = await BuildMessageHandlerMapAsync(projects, cancellationToken)
            .ConfigureAwait(false);

        return new DispatchMaps(interfaceImplementations, messageHandlers);
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
                    if (typeSymbol is not INamedTypeSymbol namedType || namedType.IsAbstract)
                        continue;

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
}
