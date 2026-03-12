using System.Text;
using CallGraph.Contracts;

namespace CallGraph.Core.Output;

public static class ToolTextFormatter
{
    public static string FormatSearchFiles(SearchFileToolResponse response)
    {
        if (response.Matches.Count == 0)
            return string.Empty;

        return string.Join(Environment.NewLine, response.Matches.Select(static m => m.FilePath));
    }

    public static string FormatSearchMethods(SearchMethodToolResponse response)
    {
        if (response.Matches.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < response.Matches.Count; i++)
        {
            var match = response.Matches[i];
            var location = BuildLocation(match.FilePath, match.StartLine);
            var containingType = string.IsNullOrWhiteSpace(match.ContainingType) ? "-" : match.ContainingType;
            var signature = string.IsNullOrWhiteSpace(match.Signature) ? "-" : match.Signature;
            sb.Append(location)
                .Append('\t')
                .Append(containingType)
                .Append('\t')
                .Append(match.MethodName)
                .Append('\t')
                .Append(signature);

            if (i < response.Matches.Count - 1)
                sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildLocation(string? filePath, int? startLine)
    {
        var path = string.IsNullOrWhiteSpace(filePath) ? "-" : filePath;
        return startLine.HasValue ? $"{path}:{startLine.Value}" : path;
    }
}
