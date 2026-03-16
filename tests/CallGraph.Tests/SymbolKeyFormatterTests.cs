using CallGraph.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CallGraph.Tests;

public sealed class SymbolKeyFormatterTests
{
    [Fact]
    public void Format_NormalizesConstructedGenericMethodToDeclarationKey()
    {
        const string source = """
            public static class GenericHolder
            {
                public static T Echo<T>(T value)
                {
                    return value;
                }

                public static void Caller()
                {
                    Echo<int>(42);
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "GenericHolder",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var declaration = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "Echo");
        var invocation = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single();

        var declaredSymbol = (IMethodSymbol)model.GetDeclaredSymbol(declaration)!;
        var invocationSymbol = ((IInvocationOperation)model.GetOperation(invocation)!).TargetMethod;

        Assert.Equal(
            SymbolKeyFormatter.Format(declaredSymbol),
            SymbolKeyFormatter.Format(invocationSymbol));
    }
}
