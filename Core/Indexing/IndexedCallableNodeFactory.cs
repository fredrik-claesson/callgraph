using CallGraph.Contracts;
using CallGraph.Core.Analysis;
using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Indexing;

internal static class IndexedCallableNodeFactory
{
    public static Node Create(IMethodSymbol method, Location? location = null)
    {
        var normalized = SymbolKeyFormatter.Normalize(method);
        var loc = location?.IsInSource == true
            ? location
            : normalized.Locations.FirstOrDefault(l => l.IsInSource);
        var file = loc?.SourceTree?.FilePath;
        var line = loc is null ? (int?)null : loc.GetLineSpan().StartLinePosition.Line + 1;

        return new Node
        {
            Id = SymbolKeyFormatter.Format(normalized),
            Kind = GetNodeKind(normalized),
            Display = normalized.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ContainingType = SymbolKeyFormatter.FormatContainingType(normalized),
            FilePath = file is null ? null : Path.GetFullPath(file),
            StartLine = line,
            Accessibility = MapAccessibility(normalized.DeclaredAccessibility)
        };
    }

    private static string GetNodeKind(IMethodSymbol method)
        => method.MethodKind switch
        {
            MethodKind.Constructor => method.IsStatic ? "static-constructor" : "constructor",
            MethodKind.StaticConstructor => "static-constructor",
            MethodKind.Destructor => "destructor",
            MethodKind.UserDefinedOperator => "operator",
            MethodKind.Conversion => "conversion-operator",
            MethodKind.LocalFunction => "local-function",
            MethodKind.PropertyGet => "property-get",
            MethodKind.PropertySet => "property-set",
            MethodKind.EventAdd => "event-add",
            MethodKind.EventRemove => "event-remove",
            _ => "method"
        };

    internal static string? MapAccessibility(Accessibility accessibility)
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
