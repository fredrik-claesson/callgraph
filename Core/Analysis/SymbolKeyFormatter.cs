using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Analysis;

public static class SymbolKeyFormatter
{
    public static string Format(IMethodSymbol method)
        => $"{method.ContainingAssembly?.Name}:{method.ToDisplayString(SymbolFormats.MethodKeyFormat)}";
}
