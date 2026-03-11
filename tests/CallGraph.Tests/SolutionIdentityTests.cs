
using CallGraph.Core.Solutions;

namespace CallGraph.Tests;

public sealed class SolutionIdentityTests
{
    [Fact]
    public void GeneratesStableHashForEquivalentPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        var absolutePath = Path.Combine(root, "sample.sln");
        var relativePath = Path.GetRelativePath(root, absolutePath);

        try
        {
            var relativeId = SolutionIdentity.FromPath(relativePath, root);
            var absoluteId = SolutionIdentity.FromPath(absolutePath, root);

            Assert.Equal(absoluteId, relativeId);
            Assert.Matches("^[0-9a-f]{64}$", absoluteId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
