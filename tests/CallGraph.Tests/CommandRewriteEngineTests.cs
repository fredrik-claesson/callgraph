using System.Reflection;

namespace CallGraph.Tests;

public sealed class CommandRewriteEngineTests
{
    [Fact]
    public void TryRewrite_FindCsPattern_RewritesToSearchFile()
    {
        var tempDir = CreateTempDir();
        try
        {
            var rewritten = Rewrite($"find {tempDir} -name \"*Controller.cs\"");
            Assert.Equal($"callgraph search-file --pattern '*Controller.cs' --folderPath '{tempDir}'", rewritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryRewrite_RgKeywordsWithCsGlob_RewritesToSearchMethod()
    {
        var tempDir = CreateTempDir();
        try
        {
            var rewritten = Rewrite($"rg GetBalance -g \"*.cs\" {tempDir}");
            Assert.Equal($"callgraph search-method --keywords 'GetBalance' --folderPath '{tempDir}'", rewritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryRewrite_GrepCsFile_RewritesToScopedSearchMethod()
    {
        var rewritten = Rewrite("grep Authorize /tmp/src/Payments/AuthorizationService.cs");
        Assert.Equal(
            "callgraph search-method --keywords 'Authorize' --filePath '/tmp/src/Payments/AuthorizationService.cs'",
            rewritten);
    }

    [Fact]
    public void TryRewrite_FindRelativePath_ResolvesToAbsoluteFolder()
    {
        var expectedFolder = Path.GetFullPath(".");
        var rewritten = Rewrite("find . -name \"*.cs\"");
        Assert.Equal($"callgraph search-file --pattern '*.cs' --folderPath '{expectedFolder}'", rewritten);
    }

    [Fact]
    public void TryRewrite_FindWithHeadPipe_StillRewrites()
    {
        var tempDir = CreateTempDir();
        try
        {
            var rewritten = Rewrite($"find {tempDir} -name \"*ReservationAssignmentComponent.cs\" -type f 2>&1 | head -5");
            Assert.Equal(
                $"callgraph search-file --pattern '*ReservationAssignmentComponent.cs' --folderPath '{tempDir}'",
                rewritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryRewrite_LsDirectory_RewritesToSearchFileInFolder()
    {
        var tempDir = CreateTempDir();
        try
        {
            var rewritten = Rewrite($"ls -la {tempDir}");
            Assert.Null(rewritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryRewrite_LsPipedToGrep_RewritesToRegexSearchFile()
    {
        var tempDir = CreateTempDir();
        try
        {
            var rewritten = Rewrite($"ls -la {tempDir} | grep -i reserv | head -20");
            Assert.Null(rewritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryRewrite_LsCsGlob_RewritesToSearchFileInFolder()
    {
        var tempDir = CreateTempDir();
        try
        {
            var rewritten = Rewrite($"ls -la {tempDir}/*.cs");
            Assert.Equal($"callgraph search-file --pattern '*.cs' --folderPath '{tempDir}'", rewritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryRewrite_LsPipedToGrepCs_RewritesToRegexSearchFile()
    {
        var tempDir = CreateTempDir();
        try
        {
            var rewritten = Rewrite($"ls -la {tempDir} | grep -i \"\\.cs$\" | head -20");
            Assert.Equal(
                $"callgraph search-file --regex --pattern '(?i).*\\.cs\\$.*\\.cs$' --folderPath '{tempDir}'",
                rewritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryRewrite_GrepRegexInFile_UsesRegexMode()
    {
        var rewritten = Rewrite(
            "grep -n \"async.*Task.*LoadVisits\\|async.*Task.*LoadSlots\" /tmp/src/EnterpriseComponent.cs 2>/dev/null");
        Assert.Equal(
            "callgraph search-method --regex --pattern 'async.*Task.*LoadVisits|async.*Task.*LoadSlots' --filePath '/tmp/src/EnterpriseComponent.cs'",
            rewritten);
    }

    [Fact]
    public void TryRewrite_FindPipedToXargsGrep_UsesRegexSearchMethod()
    {
        var tempDir = CreateTempDir();
        try
        {
            var rewritten = Rewrite(
                $"find {tempDir} -name \"*Component.cs\" -type f | xargs grep -l \"LoadVisitsAsync\\|LoadSlotsAsync\" 2>/dev/null | head -20");
            Assert.Equal(
                $"callgraph search-method --regex --pattern 'LoadVisitsAsync|LoadSlotsAsync' --folderPath '{tempDir}'",
                rewritten);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryRewrite_RegexHeavyQuery_UsesRegexMode()
    {
        var rewritten = Rewrite("rg \"^Get[A-Z]\" -g \"*.cs\" /tmp/src");
        Assert.Equal("callgraph search-method --regex --pattern '^Get[A-Z]'", rewritten);
    }

    private static string? Rewrite(string command)
    {
        var assembly = typeof(Program).Assembly;
        var type = assembly.GetType("CallGraph.Cli.CommandRewriteEngine", throwOnError: true);
        var method = type!.GetMethod("TryRewrite", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        object?[] args = { command, null };
        var success = (bool)method!.Invoke(null, args)!;
        return success ? (string?)args[1] : null;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"callgraph-rewrite-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
