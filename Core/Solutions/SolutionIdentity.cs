using System.Security.Cryptography;
using System.Text;

namespace CallGraph.Core.Solutions;

public static class SolutionIdentity
{
    public static string FromPath(string solutionPath)
        => FromPath(solutionPath, Environment.CurrentDirectory);

    public static string FromPath(string solutionPath, string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        var normalizedBasePath = Path.GetFullPath(basePath);
        var normalized = Path.IsPathRooted(solutionPath)
            ? Path.GetFullPath(solutionPath)
            : Path.GetFullPath(solutionPath, normalizedBasePath);

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
