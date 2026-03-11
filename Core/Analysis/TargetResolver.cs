using CallGraph.Core.Solutions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallGraph.Core.Analysis;

public sealed class TargetResolver : ITargetResolver
{
    private readonly ISolutionLoader _solutionLoader;

    public TargetResolver(ISolutionLoader solutionLoader)
        => _solutionLoader = solutionLoader;

    public async Task<HashSet<string>> ResolveTargetsAsync(
        string solutionPath,
        bool slnOnly,
        string filePath,
        string? methodName,
        CancellationToken cancellationToken)
    {
        var normalizedFilePath = Path.GetFullPath(filePath);
        var targets = new HashSet<string>(StringComparer.Ordinal);

        await using var context = await _solutionLoader
            .LoadAsync(solutionPath, slnOnly, cancellationToken)
            .ConfigureAwait(false);

        var document = context.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d =>
                d.FilePath is not null &&
                string.Equals(Path.GetFullPath(d.FilePath), normalizedFilePath, StringComparison.OrdinalIgnoreCase));

        if (document is null || !document.SupportsSyntaxTree)
            return targets;

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
            return targets;

        var decls = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>();
        if (!string.IsNullOrWhiteSpace(methodName))
        {
            decls = decls.Where(d =>
                d is MethodDeclarationSyntax m && m.Identifier.ValueText == methodName);
        }

        foreach (var decl in decls)
        {
            var symbol = model.GetDeclaredSymbol(decl, cancellationToken) as IMethodSymbol;
            if (symbol is null)
                continue;

            targets.Add(SymbolKeyFormatter.Format(symbol));
        }

        return targets;
    }
}
