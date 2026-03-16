using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallGraph.Core.Analysis;

internal static class CallableSyntax
{
    public static IEnumerable<SyntaxNode> EnumerateDeclarations(SyntaxNode root)
    {
        foreach (var declaration in root.DescendantNodes())
        {
            if (declaration is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax)
                yield return declaration;
        }
    }

    public static string ExtractMethodName(SyntaxNode declaration)
    {
        return declaration switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            DestructorDeclarationSyntax destructor => destructor.Identifier.ValueText,
            OperatorDeclarationSyntax @operator => $"operator {@operator.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
            LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
            AccessorDeclarationSyntax accessor => ExtractAccessorName(accessor),
            _ => declaration.GetType().Name
        };
    }

    public static string GetCallableKind(SyntaxNode declaration)
    {
        return declaration switch
        {
            ConstructorDeclarationSyntax constructor => constructor.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword))
                ? "static-constructor"
                : "constructor",
            DestructorDeclarationSyntax => "destructor",
            OperatorDeclarationSyntax => "operator",
            ConversionOperatorDeclarationSyntax => "conversion-operator",
            LocalFunctionStatementSyntax => "local-function",
            AccessorDeclarationSyntax accessor => accessor.Keyword.ValueText switch
            {
                "get" => "property-get",
                "set" => "property-set",
                "add" => "event-add",
                "remove" => "event-remove",
                _ => "method"
            },
            _ => "method"
        };
    }

    public static string? ExtractContainingType(SyntaxNode declaration)
    {
        var typeNames = declaration.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(type => type.Identifier.ValueText)
            .Reverse()
            .ToList();

        if (typeNames.Count == 0)
            return null;

        var namespaceParts = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(namespaceDeclaration => namespaceDeclaration.Name.ToString())
            .Reverse()
            .ToList();

        var typeChain = string.Join('.', typeNames);
        if (namespaceParts.Count == 0)
            return typeChain;

        return $"{string.Join('.', namespaceParts)}.{typeChain}";
    }

    public static string ExtractSignatureText(SyntaxNode declaration, string source)
    {
        var span = declaration.Span;
        var signatureEnd = span.End;

        switch (declaration)
        {
            case BaseMethodDeclarationSyntax method when method.Body is not null:
                signatureEnd = method.Body.Span.Start;
                break;
            case BaseMethodDeclarationSyntax method when method.ExpressionBody is not null:
                signatureEnd = method.ExpressionBody.Span.Start;
                break;
            case LocalFunctionStatementSyntax localFunction when localFunction.Body is not null:
                signatureEnd = localFunction.Body.Span.Start;
                break;
            case LocalFunctionStatementSyntax localFunction when localFunction.ExpressionBody is not null:
                signatureEnd = localFunction.ExpressionBody.Span.Start;
                break;
            case AccessorDeclarationSyntax accessor when accessor.Body is not null:
                signatureEnd = accessor.Body.Span.Start;
                break;
            case AccessorDeclarationSyntax accessor when accessor.ExpressionBody is not null:
                signatureEnd = accessor.ExpressionBody.Span.Start;
                break;
        }

        if (signatureEnd <= span.Start)
            return string.Empty;

        return source.Substring(span.Start, signatureEnd - span.Start).TrimEnd();
    }

    public static string ExtractBodyText(SyntaxNode declaration)
    {
        return declaration switch
        {
            BaseMethodDeclarationSyntax method when method.Body is not null => method.Body.ToFullString(),
            BaseMethodDeclarationSyntax method when method.ExpressionBody is not null => method.ExpressionBody.ToFullString() + method.SemicolonToken.ToFullString(),
            LocalFunctionStatementSyntax localFunction when localFunction.Body is not null => localFunction.Body.ToFullString(),
            LocalFunctionStatementSyntax localFunction when localFunction.ExpressionBody is not null => localFunction.ExpressionBody.ToFullString() + localFunction.SemicolonToken.ToFullString(),
            AccessorDeclarationSyntax accessor when accessor.Body is not null => accessor.Body.ToFullString(),
            AccessorDeclarationSyntax accessor when accessor.ExpressionBody is not null => accessor.ExpressionBody.ToFullString() + accessor.SemicolonToken.ToFullString(),
            _ => string.Empty
        };
    }

    public static string GetAccessibility(SyntaxNode declaration)
    {
        if (declaration.Ancestors().OfType<InterfaceDeclarationSyntax>().Any())
            return "public";

        if (declaration is LocalFunctionStatementSyntax)
            return "private";

        var modifiers = (declaration switch
        {
            BaseMethodDeclarationSyntax method => method.Modifiers.Select(m => m.ValueText),
            AccessorDeclarationSyntax accessor => accessor.Modifiers.Select(m => m.ValueText),
            _ => Enumerable.Empty<string>()
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (modifiers.Contains("public"))
            return "public";
        if (modifiers.Contains("protected") && modifiers.Contains("internal"))
            return "protected internal";
        if (modifiers.Contains("private") && modifiers.Contains("protected"))
            return "private protected";
        if (modifiers.Contains("protected"))
            return "protected";
        if (modifiers.Contains("internal"))
            return "internal";
        if (modifiers.Contains("private"))
            return "private";

        if (declaration is AccessorDeclarationSyntax accessorDeclaration)
        {
            var ownerAccessibility = accessorDeclaration.Parent?.Parent switch
            {
                BasePropertyDeclarationSyntax property => GetAccessibilityFromTokens(property.Modifiers),
                BaseTypeDeclarationSyntax _ => "private",
                _ => "private"
            };
            return ownerAccessibility;
        }

        return "private";
    }

    private static string GetAccessibilityFromTokens(SyntaxTokenList modifiers)
    {
        var set = modifiers.Select(m => m.ValueText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Contains("public"))
            return "public";
        if (set.Contains("protected") && set.Contains("internal"))
            return "protected internal";
        if (set.Contains("private") && set.Contains("protected"))
            return "private protected";
        if (set.Contains("protected"))
            return "protected";
        if (set.Contains("internal"))
            return "internal";
        if (set.Contains("private"))
            return "private";
        return "private";
    }

    private static string ExtractAccessorName(AccessorDeclarationSyntax accessor)
    {
        var ownerName = accessor.Parent?.Parent switch
        {
            EventDeclarationSyntax eventDeclaration => eventDeclaration.Identifier.ValueText,
            BasePropertyDeclarationSyntax property => property switch
            {
                PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.Identifier.ValueText,
                IndexerDeclarationSyntax => "this[]",
                _ => "property"
            },
            _ => "member"
        };

        return $"{accessor.Keyword.ValueText}_{ownerName}";
    }
}
