using CallGraph.Core.Indexing;
using Xunit;

namespace CallGraph.Tests.Indexing;

public sealed class IndexDatabaseLocatorTests
{
    [Fact]
    public void Resolve_ReturnsConfiguredPath_WhenProvided()
    {
        var result = IndexDatabaseLocator.Resolve("/tmp/custom/index.db");
        Assert.Equal("/tmp/custom/index.db", result);
    }

    [Fact]
    public void Resolve_FallsBackToLocalApplicationData_WhenBlank()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CallGraph",
            "index.db");
        Assert.Equal(expected, IndexDatabaseLocator.Resolve(null));
        Assert.Equal(expected, IndexDatabaseLocator.Resolve("   "));
    }
}
