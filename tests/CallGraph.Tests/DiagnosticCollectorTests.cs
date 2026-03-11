using CallGraph.Core.Diagnostics;
using CallGraph.Core.Projects;
using CallGraph.Core.Solutions;
using CallGraph.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace CallGraph.Tests;

/// <summary>
/// Tests for DiagnosticCollector that verify unused code detection works
/// even on "vanilla" projects without any analyzer configuration.
/// </summary>
public sealed class DiagnosticCollectorTests
{
    /// <summary>
    /// Creates a vanilla project (no analyzers configured) with the given source code.
    /// This simulates a project that has no .editorconfig or analyzer packages.
    /// </summary>
    private static Project CreateVanillaProject(string sourceCode, string fileName = "Test.cs")
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        };

        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            "VanillaProject",
            "VanillaProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: references);

        var project = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(project.Id, fileName, Microsoft.CodeAnalysis.Text.SourceText.From(sourceCode));
        return document.Project;
    }

    [Fact]
    public async Task DetectsCS0169_UnusedField_OnVanillaProject()
    {
        // Arrange - vanilla project with no analyzer configuration
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        private int _unusedField;  // CS0169: Field is never used

        public void PublicMethod() { }
    }
}";
        var project = CreateVanillaProject(sourceCode);
        var diagnosticCollector = new DiagnosticCollector(NullLogger<DiagnosticCollector>.Instance);

        // Act
        var diagnostics = await diagnosticCollector.CollectUnusedDiagnosticsAsync(
            new[] { project },
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        // Assert - CS0169 is a compiler warning, should always be detected
        var cs0169 = diagnostics.FirstOrDefault(d => d.Id == "CS0169");
        Assert.NotNull(cs0169);
        Assert.Contains("_unusedField", cs0169.Message);
    }

    [Fact]
    public async Task DetectsIDE0051_UnusedPrivateMember_OnVanillaProject()
    {
        // Arrange - vanilla project with no analyzer configuration
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        private void UnusedMethod() { }  // IDE0051: Private member is unused

        public void PublicMethod() { }
    }
}";
        var project = CreateVanillaProject(sourceCode);
        var diagnosticCollector = new DiagnosticCollector(NullLogger<DiagnosticCollector>.Instance);

        // Act
        var diagnostics = await diagnosticCollector.CollectUnusedDiagnosticsAsync(
            new[] { project },
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        // Assert - IDE0051 requires IDE analyzers, which we load as fallback
        var ide0051 = diagnostics.FirstOrDefault(d => d.Id == "IDE0051");
        Assert.NotNull(ide0051);
        Assert.Contains("UnusedMethod", ide0051.Message);
    }

    [Fact]
    public async Task DetectsMultipleDiagnosticTypes_OnVanillaProject()
    {
        // Arrange - vanilla project with multiple types of unused code
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        private int _unusedField;           // CS0169: Field is never used
        private void UnusedMethod() { }     // IDE0051: Private member is unused

        public void PublicMethod() { }
    }
}";
        var project = CreateVanillaProject(sourceCode);
        var diagnosticCollector = new DiagnosticCollector(NullLogger<DiagnosticCollector>.Instance);

        // Act
        var diagnostics = await diagnosticCollector.CollectUnusedDiagnosticsAsync(
            new[] { project },
            folderPath: null,
            filePath: null,
            CancellationToken.None);

        // Assert - both diagnostic types should be detected
        Assert.Contains(diagnostics, d => d.Id == "CS0169");   // Compiler warning
        Assert.Contains(diagnostics, d => d.Id == "IDE0051");  // IDE analyzer
    }

    [Fact]
    public async Task FiltersToSpecificFile()
    {
        // Arrange
        CallGraphComposition.EnsureMsBuildRegistered();

        var solutionLoader = new SolutionLoader(
            new ProjectFilter(),
            new SolutionFileParser());

        var diagnosticCollector = new DiagnosticCollector(
            NullLogger<DiagnosticCollector>.Instance);

        var solutionPath = Path.GetFullPath(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "CallGraph.Tests", "TestAssets", "InterfaceCallE2E", "InterfaceCallE2E.sln"));

        var helperFilePath = Path.GetFullPath(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "CallGraph.Tests", "TestAssets", "InterfaceCallE2E", "InterfaceCallE2E", "Services", "Helper.cs"));

        // Act
        await using var loadContext = await solutionLoader.LoadAsync(solutionPath, slnOnly: true, CancellationToken.None);
        var diagnostics = await diagnosticCollector.CollectUnusedDiagnosticsAsync(
            loadContext.Projects,
            folderPath: null,
            filePath: helperFilePath,
            CancellationToken.None);

        // Assert
        // All diagnostics should be from Helper.cs
        Assert.All(diagnostics, d => Assert.Contains("Helper.cs", d.FilePath));
    }
}
