using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CallGraph.Core.Extraction;

public sealed class MethodSourceExtractor : IMethodSourceExtractor
{
    private const string SignatureOnlyMode = "signature_only";
    private const string SignaturePlusBodyMode = "signature_plus_body";
    private const string BodyOnlyMode = "body_only";
    private const string BodyWithoutCommentsMode = "body_without_comments";

    public async Task<MethodSourceExtractionResult> ExtractAsync(MethodSourceExtractionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
            return MethodSourceExtractionResult.Failure("Missing required option: --filePath");

        if (!Path.IsPathRooted(request.FilePath))
            return MethodSourceExtractionResult.Failure("filePath must be an absolute path.");

        if (!request.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return MethodSourceExtractionResult.Failure("filePath must point to a .cs file.");

        if (!File.Exists(request.FilePath))
            return MethodSourceExtractionResult.Failure($"File not found: {request.FilePath}");

        var mode = NormalizeMode(request.Mode, out var modeError);
        if (modeError is not null)
            return MethodSourceExtractionResult.Failure(modeError);

        if (request.MethodName is null && request.Signature is null && request.StartLine is null)
        {
            return MethodSourceExtractionResult.Failure(
                "Provide at least one selector: --methodName, --signature, or --startLine.");
        }

        var source = await File.ReadAllTextAsync(request.FilePath, cancellationToken).ConfigureAwait(false);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: request.FilePath, cancellationToken: cancellationToken);
        var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        var candidates = root.DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .Select(decl => BuildCandidate(decl, source, syntaxTree))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .ToList();

        var filtered = candidates.Where(candidate => Matches(candidate, request)).ToList();

        if (filtered.Count == 0)
        {
            var allCandidates = candidates
                .OrderBy(c => c.StartLine)
                .Select(ToCandidate)
                .Take(25)
                .ToList();

            return MethodSourceExtractionResult.Failure(
                "No method matched the provided selectors.",
                allCandidates);
        }

        if (filtered.Count > 1)
        {
            var ambiguousCandidates = filtered
                .OrderBy(c => c.StartLine)
                .Select(ToCandidate)
                .Take(25)
                .ToList();

            return MethodSourceExtractionResult.Failure(
                "Method selection is ambiguous. Narrow with --containingType, --signature, or --startLine.",
                ambiguousCandidates);
        }

        var selected = filtered[0];
        var content = BuildContent(selected, mode);

        var match = new MethodSourceMatch(
            FilePath: request.FilePath,
            MethodName: selected.MethodName,
            ContainingType: selected.ContainingType,
            Signature: selected.Signature,
            StartLine: selected.StartLine,
            EndLine: selected.EndLine,
            StartByte: GetUtf8ByteOffset(source, selected.MethodSpan.Start),
            EndByte: GetUtf8ByteOffset(source, selected.MethodSpan.End),
            Mode: mode,
            Content: content);

        return MethodSourceExtractionResult.Ok(match);
    }

    private static MethodSourceCandidate ToCandidate(MethodCandidate candidate)
        => new(
            candidate.MethodName,
            candidate.ContainingType,
            candidate.Signature,
            candidate.StartLine,
            candidate.EndLine);

    private static bool Matches(MethodCandidate candidate, MethodSourceExtractionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.MethodName) &&
            !string.Equals(candidate.MethodName, request.MethodName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.ContainingType) && !MatchesContainingType(candidate, request.ContainingType!))
            return false;

        if (request.StartLine.HasValue && candidate.StartLine != request.StartLine.Value)
            return false;

        if (!string.IsNullOrWhiteSpace(request.Signature) && !MatchesSignature(candidate.Signature, request.Signature!))
            return false;

        return true;
    }

    private static bool MatchesContainingType(MethodCandidate candidate, string requestedContainingType)
    {
        if (string.IsNullOrWhiteSpace(candidate.ContainingType))
            return false;

        var requested = NormalizeSymbolText(requestedContainingType);
        var actual = NormalizeSymbolText(candidate.ContainingType);

        if (string.Equals(actual, requested, StringComparison.Ordinal))
            return true;

        return actual.EndsWith($".{requested}", StringComparison.Ordinal);
    }

    private static bool MatchesSignature(string signature, string requestedSignature)
    {
        var left = NormalizeWhitespace(signature);
        var right = NormalizeWhitespace(requestedSignature);

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        return left.Contains(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();

    private static string NormalizeSymbolText(string value)
        => value.Replace("global::", string.Empty, StringComparison.Ordinal)
            .Replace('+', '.')
            .Trim();

    private static string BuildContent(MethodCandidate selected, string mode)
    {
        return mode switch
        {
            SignatureOnlyMode => selected.Signature,
            SignaturePlusBodyMode => string.IsNullOrWhiteSpace(selected.Body)
                ? selected.Signature
                : $"{selected.Signature}{Environment.NewLine}{selected.Body}",
            BodyOnlyMode => selected.Body,
            BodyWithoutCommentsMode => RemoveComments(selected.Body),
            _ => selected.Body
        };
    }

    private static string RemoveComments(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        var withoutBlockComments = Regex.Replace(body, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
        return withoutLineComments.TrimEnd();
    }

    private static MethodCandidate? BuildCandidate(BaseMethodDeclarationSyntax declaration, string source, SyntaxTree syntaxTree)
    {
        var signature = ExtractSignatureText(declaration, source);
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var body = ExtractBodyText(declaration);
        var methodName = ExtractMethodName(declaration);
        var containingType = ExtractContainingType(declaration);

        var lineSpan = syntaxTree.GetLineSpan(declaration.Span);
        var startLine = lineSpan.StartLinePosition.Line + 1;
        var endLine = lineSpan.EndLinePosition.Line + 1;

        return new MethodCandidate(
            methodName,
            containingType,
            signature.TrimEnd(),
            body,
            startLine,
            endLine,
            declaration.Span);
    }

    private static string ExtractSignatureText(BaseMethodDeclarationSyntax declaration, string source)
    {
        var signatureEnd = declaration.Span.End;

        if (declaration.Body is not null)
            signatureEnd = declaration.Body.Span.Start;
        else if (declaration.ExpressionBody is not null)
            signatureEnd = declaration.ExpressionBody.Span.Start;

        if (signatureEnd <= declaration.Span.Start)
            return string.Empty;

        return source.Substring(declaration.Span.Start, signatureEnd - declaration.Span.Start).TrimEnd();
    }

    private static string ExtractBodyText(BaseMethodDeclarationSyntax declaration)
    {
        if (declaration.Body is not null)
            return declaration.Body.ToFullString();

        if (declaration.ExpressionBody is not null)
            return declaration.ExpressionBody.ToFullString() + declaration.SemicolonToken.ToFullString();

        return string.Empty;
    }

    private static string ExtractMethodName(BaseMethodDeclarationSyntax declaration)
    {
        return declaration switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            DestructorDeclarationSyntax destructor => destructor.Identifier.ValueText,
            OperatorDeclarationSyntax @operator => $"operator {@operator.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
            _ => declaration.GetType().Name
        };
    }

    private static string? ExtractContainingType(BaseMethodDeclarationSyntax declaration)
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

    private static int GetUtf8ByteOffset(string source, int charOffset)
    {
        if (charOffset <= 0)
            return 0;

        if (charOffset >= source.Length)
            return Encoding.UTF8.GetByteCount(source);

        return Encoding.UTF8.GetByteCount(source.AsSpan(0, charOffset));
    }

    private static string NormalizeMode(string? rawMode, out string? error)
    {
        var normalized = (rawMode ?? SignaturePlusBodyMode).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case SignatureOnlyMode:
            case SignaturePlusBodyMode:
            case BodyOnlyMode:
            case BodyWithoutCommentsMode:
                error = null;
                return normalized;
            default:
                error =
                    "mode must be one of: signature_only, signature_plus_body, body_only, body_without_comments.";
                return SignaturePlusBodyMode;
        }
    }

    private sealed record MethodCandidate(
        string MethodName,
        string? ContainingType,
        string Signature,
        string Body,
        int StartLine,
        int EndLine,
        TextSpan MethodSpan);
}
