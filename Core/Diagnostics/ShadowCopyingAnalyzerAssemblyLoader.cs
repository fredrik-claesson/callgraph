namespace CallGraph.Core.Diagnostics;

// NOTE: Roslyn 5.0 (Microsoft.CodeAnalysis 5.x) doesn't expose the public analyzer assembly
// loader hooks that would allow safe shadow-copy loading of analyzer DLLs.
// We keep this file as a placeholder in case we upgrade Roslyn and reintroduce shadow-copy.
internal static class ShadowCopyingAnalyzerAssemblyLoader
{
}
