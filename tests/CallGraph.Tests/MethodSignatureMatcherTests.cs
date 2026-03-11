using CallGraph.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CallGraph.Tests;

public sealed class MethodSignatureMatcherTests
{
    [Fact]
    public void FindImplementationMethod_MatchesAcrossCompilations()
    {
        var leftCompilation = CreateCompilation("LeftAssembly");
        var rightCompilation = CreateCompilation("RightAssembly");

        var interfaceMethod = GetInterfaceMethod(leftCompilation);
        var implementationType = GetTypeSymbol(rightCompilation, "Demo.AdyenBalanceCommunicationComponent");

        var matched = MethodSignatureMatcher.FindImplementationMethod(implementationType, interfaceMethod);

        Assert.NotNull(matched);
        Assert.Equal("GetBalanceAccountAsync", matched!.Name);
    }

    [Fact]
    public void FindImplementationMethod_MatchesExplicitImplementationAcrossCompilations()
    {
        var leftCompilation = CreateCompilation("LeftAssembly");
        var rightCompilation = CreateCompilation("RightAssembly");

        var interfaceMethod = GetInterfaceMethod(leftCompilation);
        var implementationType = GetTypeSymbol(rightCompilation, "Demo.ExplicitAdyenBalanceCommunicationComponent");

        var matched = MethodSignatureMatcher.FindImplementationMethod(implementationType, interfaceMethod);

        Assert.NotNull(matched);
        Assert.Single(matched!.ExplicitInterfaceImplementations);
    }

    [Fact]
    public void IsCompatibleInterfaceImplementation_ReturnsFalseForDifferentSignature()
    {
        var compilation = CreateCompilation("SingleAssembly");
        var interfaceMethod = GetInterfaceMethod(compilation);
        var mismatchedType = GetTypeSymbol(compilation, "Demo.NotAnImplementation");
        var mismatchedMethod = mismatchedType.GetMembers("GetBalanceAccountAsync")
            .OfType<IMethodSymbol>()
            .Single();

        var result = MethodSignatureMatcher.IsCompatibleInterfaceImplementation(mismatchedMethod, interfaceMethod);

        Assert.False(result);
    }

    private static IMethodSymbol GetInterfaceMethod(Compilation compilation)
    {
        var interfaceType = GetTypeSymbol(compilation, "Demo.IAdyenBalanceCommunicator");
        return interfaceType.GetMembers("GetBalanceAccountAsync")
            .OfType<IMethodSymbol>()
            .Single();
    }

    private static INamedTypeSymbol GetTypeSymbol(Compilation compilation, string metadataName)
    {
        var type = compilation.GetTypeByMetadataName(metadataName);
        Assert.NotNull(type);
        return type!;
    }

    private static Compilation CreateCompilation(string assemblyName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            """
            namespace Demo;

            public readonly struct Token;
            public sealed class Wrapper<T>;
            public sealed class BalanceAccount;

            public interface IAdyenBalanceCommunicator
            {
                Wrapper<BalanceAccount> GetBalanceAccountAsync(string balanceAccountId, Token cancellationToken);
            }

            public sealed class AdyenBalanceCommunicationComponent : IAdyenBalanceCommunicator
            {
                public Wrapper<BalanceAccount> GetBalanceAccountAsync(string balanceAccountId, Token cancellationToken) => new();
            }

            public sealed class ExplicitAdyenBalanceCommunicationComponent : IAdyenBalanceCommunicator
            {
                Wrapper<BalanceAccount> IAdyenBalanceCommunicator.GetBalanceAccountAsync(string balanceAccountId, Token cancellationToken) => new();
            }

            public sealed class NotAnImplementation
            {
                public Wrapper<object> GetBalanceAccountAsync(string balanceAccountId, Token cancellationToken) => new();
            }
            """);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);

        return compilation;
    }
}
