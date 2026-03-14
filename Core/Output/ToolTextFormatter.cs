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

    public static string FormatAnalyze(AnalyzeToolResponse response)
    {
        if (response.Methods.Count == 0 && response.Calls.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        for (var i = 0; i < response.Methods.Count; i++)
        {
            var method = response.Methods[i];
            var location = BuildLocation(method.FilePath, method.StartLine);
            var containingType = string.IsNullOrWhiteSpace(method.ContainingType) ? "-" : method.ContainingType;
            var methodName = string.IsNullOrWhiteSpace(method.MethodName) ? "-" : method.MethodName;

            sb.Append("M\t")
                .Append(method.MethodId)
                .Append('\t')
                .Append(location)
                .Append('\t')
                .Append(containingType)
                .Append('\t')
                .Append(methodName);

            sb.AppendLine();
        }

        for (var i = 0; i < response.Calls.Count; i++)
        {
            var call = response.Calls[i];
            sb.Append("C\t")
                .Append(call.CallerMethodId)
                .Append('\t')
                .Append(call.CalleeMethodId)
                .Append('\t')
                .Append(call.Direction);

            if (i < response.Calls.Count - 1)
                sb.AppendLine();
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string BuildLocation(string? filePath, int? startLine)
    {
        var path = string.IsNullOrWhiteSpace(filePath) ? "-" : filePath;
        return startLine.HasValue ? $"{path}:{startLine.Value}" : path;
    }
}
