using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Indexing;

internal sealed record DispatchMaps(
    Dictionary<string, List<INamedTypeSymbol>> InterfaceImplementations,
    Dictionary<string, List<IMethodSymbol>> MessageHandlers);
