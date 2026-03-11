using Microsoft.CodeAnalysis;

namespace CallGraph.Core.Analysis;

public static class MethodSignatureMatcher
{
    private static readonly SymbolDisplayFormat TypeSignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static IMethodSymbol? FindImplementationMethod(INamedTypeSymbol implementingType, IMethodSymbol interfaceMethod)
    {
        var byName = implementingType
            .GetMembers(interfaceMethod.Name)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(candidate => IsCompatibleInterfaceImplementation(candidate, interfaceMethod));
        if (byName is not null)
            return byName;

        return implementingType
            .GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(candidate => candidate.ExplicitInterfaceImplementations
                .Any(explicitMember => IsCompatibleInterfaceImplementation(explicitMember, interfaceMethod)));
    }

    public static bool IsCompatibleInterfaceImplementation(IMethodSymbol candidate, IMethodSymbol interfaceMethod)
    {
        if (!string.Equals(candidate.Name, interfaceMethod.Name, StringComparison.Ordinal))
            return false;

        if (candidate.Parameters.Length != interfaceMethod.Parameters.Length)
            return false;

        if (candidate.TypeParameters.Length != interfaceMethod.TypeParameters.Length)
            return false;

        if (!string.Equals(GetTypeKey(candidate.ReturnType), GetTypeKey(interfaceMethod.ReturnType), StringComparison.Ordinal))
            return false;

        for (var i = 0; i < candidate.Parameters.Length; i++)
        {
            var candidateParameter = candidate.Parameters[i];
            var interfaceParameter = interfaceMethod.Parameters[i];

            if (candidateParameter.RefKind != interfaceParameter.RefKind)
                return false;

            if (candidateParameter.IsParams != interfaceParameter.IsParams)
                return false;

            if (!string.Equals(
                    GetTypeKey(candidateParameter.Type),
                    GetTypeKey(interfaceParameter.Type),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetTypeKey(ITypeSymbol type)
        => type.ToDisplayString(TypeSignatureFormat);
}
