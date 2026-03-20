namespace CallGraph.Core.Indexing;

internal sealed record DispatchMaps(
    Dictionary<string, List<string>> InterfaceMethodImplementations,
    Dictionary<string, List<string>> MessageHandlers);
