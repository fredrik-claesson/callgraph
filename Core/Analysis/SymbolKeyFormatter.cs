using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Analysis;

public static class SymbolKeyFormatter
{
    public static IMethodSymbol Normalize(IMethodSymbol method)
    {
        var normalized = method.MethodKind == MethodKind.ReducedExtension && method.ReducedFrom is not null
            ? method.ReducedFrom
            : method;

        normalized = normalized.OriginalDefinition;
        return normalized;
    }

    public static string Format(IMethodSymbol method)
    {
        var normalized = Normalize(method);
        var formatted = normalized.ToDisplayString(SymbolFormats.MethodKeyFormat);
        if (normalized.MethodKind == MethodKind.LocalFunction)
        {
            var sourceLocation = normalized.Locations.FirstOrDefault(location => location.IsInSource);
            if (sourceLocation is not null)
            {
                var startLine = sourceLocation.GetLineSpan().StartLinePosition.Line + 1;
                formatted = $"{formatted}@L{startLine}";
            }
        }

        return $"{normalized.ContainingAssembly?.Name}:{formatted}";
    }

    public static string? FormatContainingType(IMethodSymbol method)
    {
        var normalized = Normalize(method);
        if (normalized.ContainingType is null)
            return null;

        return normalized.ContainingType
            .ToDisplayString(SymbolFormats.FullyQualifiedTypeFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);
    }
}
