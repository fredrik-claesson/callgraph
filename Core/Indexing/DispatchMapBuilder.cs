using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallGraph.Core.Indexing;

internal static class DispatchMapBuilder
{
    public static async Task<DispatchMaps> BuildAsync(
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
    {
        var interfaceImplementations = new ConcurrentDictionary<string, ConcurrentBag<INamedTypeSymbol>>(StringComparer.Ordinal);
        var messageHandlers = new ConcurrentDictionary<string, ConcurrentBag<IMethodSymbol>>(StringComparer.Ordinal);
        var syntaxTreeParallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8)
        };

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                continue;

            await Parallel.ForEachAsync(compilation.SyntaxTrees, syntaxTreeParallelOptions, async (syntaxTree, ct) =>
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, ct);
                    if (typeSymbol is not INamedTypeSymbol namedType || namedType.IsAbstract)
                        continue;

                    foreach (var @interface in namedType.AllInterfaces)
                    {
                        var interfaceKey = @interface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var implementations = interfaceImplementations.GetOrAdd(interfaceKey, _ => new ConcurrentBag<INamedTypeSymbol>());
                        implementations.Add(namedType);
                    }
                }

                foreach (var methodDeclaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var method = semanticModel.GetDeclaredSymbol(methodDeclaration, ct) as IMethodSymbol;
                    if (!IsMessageHandlerCandidate(method))
                        continue;

                    foreach (var parameter in method!.Parameters)
                    {
                        if (!IsMessagePayloadType(parameter.Type))
                            continue;

                        var key = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                        var handlers = messageHandlers.GetOrAdd(key, _ => new ConcurrentBag<IMethodSymbol>());
                        handlers.Add(method);
                    }
                }
            }).ConfigureAwait(false);
        }

        var normalizedInterfaceImplementations = interfaceImplementations.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                .GroupBy(symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList(),
            StringComparer.Ordinal);
        var normalizedMessageHandlers = messageHandlers.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                .GroupBy(symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList(),
            StringComparer.Ordinal);

        return new DispatchMaps(normalizedInterfaceImplementations, normalizedMessageHandlers);
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
