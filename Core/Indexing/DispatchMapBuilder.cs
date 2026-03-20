using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CallGraph.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CallGraph.Core.Indexing;

internal static class DispatchMapBuilder
{
    private const int CacheSchemaVersion = 1;

    private static readonly string CacheDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CallGraph",
        "dispatch-map-cache");

    private static readonly JsonSerializerOptions CacheSerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheLocks =
        new(StringComparer.Ordinal);

    public static async Task<DispatchMaps> BuildAsync(
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken)
    {
        var keyedProjects = projects
            .Where(project => !string.IsNullOrWhiteSpace(project.FilePath))
            .Select(project => new CachedProjectInput(Path.GetFullPath(project.FilePath!), project))
            .GroupBy(input => input.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(input => input.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var transientProjects = projects
            .Where(project => string.IsNullOrWhiteSpace(project.FilePath))
            .ToList();

        if (keyedProjects.Count == 0 && transientProjects.Count == 0)
            return new DispatchMaps(new(StringComparer.Ordinal), new(StringComparer.Ordinal));

        var contributions = new List<ProjectDispatchContribution>(keyedProjects.Count + transientProjects.Count);

        if (keyedProjects.Count > 0)
        {
            var cacheKey = ComputeProjectSetCacheKey(keyedProjects.Select(project => project.ProjectPath));
            var cacheFilePath = GetCacheFilePath(cacheKey);
            var cacheLock = CacheLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));

            await cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cachedContributions = await LoadProjectContributionCacheAsync(cacheFilePath, cancellationToken)
                    .ConfigureAwait(false);
                var cacheUpdated = false;

                foreach (var cachedProject in keyedProjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fingerprint = ComputeProjectFingerprint(cachedProject.Project);
                    if (cachedContributions.TryGetValue(cachedProject.ProjectPath, out var cachedContribution)
                        && string.Equals(cachedContribution.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        contributions.Add(cachedContribution);
                        continue;
                    }

                    var rebuiltContribution = await BuildProjectContributionAsync(
                            cachedProject.Project,
                            cachedProject.ProjectPath,
                            fingerprint,
                            cancellationToken)
                        .ConfigureAwait(false);
                    contributions.Add(rebuiltContribution);
                    cachedContributions[cachedProject.ProjectPath] = rebuiltContribution;
                    cacheUpdated = true;
                }

                var activeProjectPaths = keyedProjects
                    .Select(project => project.ProjectPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var staleProjectPaths = cachedContributions.Keys
                    .Where(path => !activeProjectPaths.Contains(path))
                    .ToList();
                foreach (var staleProjectPath in staleProjectPaths)
                {
                    cachedContributions.Remove(staleProjectPath);
                    cacheUpdated = true;
                }

                if (cacheUpdated)
                {
                    await SaveProjectContributionCacheAsync(cacheFilePath, cachedContributions.Values, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                cacheLock.Release();
            }
        }

        foreach (var transientProject in transientProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contribution = await BuildProjectContributionAsync(
                    transientProject,
                    projectPath: string.Empty,
                    fingerprint: string.Empty,
                    cancellationToken)
                .ConfigureAwait(false);
            contributions.Add(contribution);
        }

        return MergeContributions(contributions);
    }

    private static async Task<ProjectDispatchContribution> BuildProjectContributionAsync(
        Project project,
        string projectPath,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var interfaceMethodImplementations =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        var messageHandlers =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);

        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
        {
            return new ProjectDispatchContribution(
                projectPath,
                fingerprint,
                new Dictionary<string, List<string>>(StringComparer.Ordinal),
                new Dictionary<string, List<string>>(StringComparer.Ordinal));
        }

        var syntaxTreeParallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8)
        };

        await Parallel.ForEachAsync(compilation.SyntaxTrees, syntaxTreeParallelOptions, async (syntaxTree, ct) =>
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync(ct).ConfigureAwait(false);

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl, ct);
                if (typeSymbol is not INamedTypeSymbol namedType || namedType.IsAbstract)
                    continue;

                foreach (var @interface in namedType.AllInterfaces)
                {
                    foreach (var interfaceMember in @interface.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (interfaceMember.MethodKind != MethodKind.Ordinary)
                            continue;

                        if (namedType.FindImplementationForInterfaceMember(interfaceMember) is not IMethodSymbol implementationMethod)
                            continue;

                        var interfaceMethodKey = SymbolKeyFormatter.Format(interfaceMember);
                        var implementationMethodKey = SymbolKeyFormatter.Format(implementationMethod);
                        AddMapValue(interfaceMethodImplementations, interfaceMethodKey, implementationMethodKey);
                    }
                }
            }

            foreach (var methodDeclaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!LooksLikeMessageHandlerCandidate(methodDeclaration))
                    continue;

                if (semanticModel.GetDeclaredSymbol(methodDeclaration, ct) is not IMethodSymbol method)
                    continue;

                if (!IsMessageHandlerCandidate(method))
                    continue;

                var handlerMethodKey = SymbolKeyFormatter.Format(method);
                foreach (var parameter in method.Parameters)
                {
                    if (!IsMessagePayloadType(parameter.Type))
                        continue;

                    var payloadTypeKey = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    AddMapValue(messageHandlers, payloadTypeKey, handlerMethodKey);
                }
            }
        }).ConfigureAwait(false);

        var normalizedInterfaceMethodImplementations = NormalizeConcurrentMap(interfaceMethodImplementations);
        var normalizedMessageHandlers = NormalizeConcurrentMap(messageHandlers);

        return new ProjectDispatchContribution(
            projectPath,
            fingerprint,
            normalizedInterfaceMethodImplementations,
            normalizedMessageHandlers);
    }

    private static DispatchMaps MergeContributions(IEnumerable<ProjectDispatchContribution> contributions)
    {
        var mergedInterfaceMethodImplementations =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var mergedMessageHandlers =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var contribution in contributions)
        {
            foreach (var (interfaceMethodKey, implementationMethodKeys) in contribution.InterfaceMethodImplementations)
            {
                if (!mergedInterfaceMethodImplementations.TryGetValue(interfaceMethodKey, out var mergedKeys))
                {
                    mergedKeys = new HashSet<string>(StringComparer.Ordinal);
                    mergedInterfaceMethodImplementations[interfaceMethodKey] = mergedKeys;
                }

                foreach (var implementationMethodKey in implementationMethodKeys)
                    mergedKeys.Add(implementationMethodKey);
            }

            foreach (var (payloadTypeKey, handlerMethodKeys) in contribution.MessageHandlers)
            {
                if (!mergedMessageHandlers.TryGetValue(payloadTypeKey, out var mergedKeys))
                {
                    mergedKeys = new HashSet<string>(StringComparer.Ordinal);
                    mergedMessageHandlers[payloadTypeKey] = mergedKeys;
                }

                foreach (var handlerMethodKey in handlerMethodKeys)
                    mergedKeys.Add(handlerMethodKey);
            }
        }

        return new DispatchMaps(
            mergedInterfaceMethodImplementations.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal),
            mergedMessageHandlers.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal));
    }

    private static Dictionary<string, List<string>> NormalizeConcurrentMap(
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> source)
        => source.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Keys.OrderBy(value => value, StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);

    private static Dictionary<string, List<string>> NormalizeMap(
        Dictionary<string, List<string>>? source)
    {
        if (source is null || source.Count == 0)
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var normalized = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || values is null || values.Count == 0)
                continue;

            normalized[key] = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
        }

        return normalized;
    }

    private static void AddMapValue(
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> map,
        string key,
        string value)
    {
        var values = map.GetOrAdd(key, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        values.TryAdd(value, 0);
    }

    private static async Task<Dictionary<string, ProjectDispatchContribution>> LoadProjectContributionCacheAsync(
        string cacheFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cacheFilePath))
            return new Dictionary<string, ProjectDispatchContribution>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = await File.ReadAllTextAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
            var cacheDocument = JsonSerializer.Deserialize<DispatchMapCacheDocument>(json, CacheSerializerOptions);
            if (cacheDocument is null || cacheDocument.SchemaVersion != CacheSchemaVersion)
                return new Dictionary<string, ProjectDispatchContribution>(StringComparer.OrdinalIgnoreCase);

            var contributions = new Dictionary<string, ProjectDispatchContribution>(StringComparer.OrdinalIgnoreCase);
            foreach (var projectEntry in cacheDocument.Projects)
            {
                if (string.IsNullOrWhiteSpace(projectEntry.ProjectPath) || string.IsNullOrWhiteSpace(projectEntry.Fingerprint))
                    continue;

                var normalizedProjectPath = Path.GetFullPath(projectEntry.ProjectPath);
                contributions[normalizedProjectPath] = new ProjectDispatchContribution(
                    normalizedProjectPath,
                    projectEntry.Fingerprint,
                    NormalizeMap(projectEntry.InterfaceMethodImplementations),
                    NormalizeMap(projectEntry.MessageHandlers));
            }

            return contributions;
        }
        catch
        {
            return new Dictionary<string, ProjectDispatchContribution>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task SaveProjectContributionCacheAsync(
        string cacheFilePath,
        IEnumerable<ProjectDispatchContribution> contributions,
        CancellationToken cancellationToken)
    {
        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);

            var cacheDocument = new DispatchMapCacheDocument
            {
                SchemaVersion = CacheSchemaVersion,
                Projects = contributions
                    .Where(contribution => !string.IsNullOrWhiteSpace(contribution.ProjectPath))
                    .OrderBy(contribution => contribution.ProjectPath, StringComparer.OrdinalIgnoreCase)
                    .Select(contribution => new ProjectDispatchCacheEntry
                    {
                        ProjectPath = contribution.ProjectPath,
                        Fingerprint = contribution.Fingerprint,
                        InterfaceMethodImplementations = contribution.InterfaceMethodImplementations,
                        MessageHandlers = contribution.MessageHandlers
                    })
                    .ToList()
            };

            var serialized = JsonSerializer.Serialize(cacheDocument, CacheSerializerOptions);
            tempPath = cacheFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tempPath, serialized, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, cacheFilePath, overwrite: true);
        }
        catch
        {
            // Cache writes are best-effort; indexing correctness does not depend on persistence.
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Ignore best-effort cleanup failures.
                }
            }
        }
    }

    private static string ComputeProjectFingerprint(Project project)
    {
        var builder = new StringBuilder(2048);

        var projectPath = project.FilePath is null ? string.Empty : Path.GetFullPath(project.FilePath);
        AppendPathMetadata(builder, projectPath);

        var documentPaths = project.Documents
            .Select(document => document.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var documentPath in documentPaths)
            AppendPathMetadata(builder, documentPath);

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static void AppendPathMetadata(StringBuilder builder, string path)
    {
        builder.Append(path);

        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                builder.Append('|').Append(-1).Append('|').Append(0L).Append('\n');
                return;
            }

            builder.Append('|').Append(fileInfo.Length).Append('|').Append(fileInfo.LastWriteTimeUtc.Ticks).Append('\n');
        }
        catch
        {
            builder.Append('|').Append(-2).Append('|').Append(0L).Append('\n');
        }
    }

    private static string ComputeProjectSetCacheKey(IEnumerable<string> projectPaths)
    {
        var serializedProjectSet = string.Join(
            "\n",
            projectPaths
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        var bytes = Encoding.UTF8.GetBytes(serializedProjectSet);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string GetCacheFilePath(string cacheKey)
        => Path.Combine(CacheDirectoryPath, cacheKey + ".json");

    private static bool IsMessageHandlerCandidate(IMethodSymbol? method)
    {
        if (method is null || method.MethodKind != MethodKind.Ordinary)
            return false;

        if (method.Parameters.Length == 0)
            return false;

        if (method.Name.Contains("Handle", StringComparison.OrdinalIgnoreCase))
            return true;

        var containingTypeName = method.ContainingType?.Name ?? string.Empty;
        return containingTypeName.Contains("Handler", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMessageHandlerCandidate(MethodDeclarationSyntax methodDeclaration)
    {
        if (methodDeclaration.ParameterList.Parameters.Count == 0)
            return false;

        if (methodDeclaration.Identifier.ValueText.Contains("Handle", StringComparison.OrdinalIgnoreCase))
            return true;

        var containingTypeName = methodDeclaration
            .Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault()?
            .Identifier
            .ValueText;

        return containingTypeName?.Contains("Handler", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsMessagePayloadType(ITypeSymbol? type)
    {
        if (type is null)
            return false;

        if (type.SpecialType != SpecialType.None)
            return false;

        var fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (string.Equals(fullName, "global::System.String", StringComparison.Ordinal)
            || string.Equals(fullName, "global::System.Threading.CancellationToken", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private readonly record struct CachedProjectInput(string ProjectPath, Project Project);

    private sealed record ProjectDispatchContribution(
        string ProjectPath,
        string Fingerprint,
        Dictionary<string, List<string>> InterfaceMethodImplementations,
        Dictionary<string, List<string>> MessageHandlers);

    private sealed class DispatchMapCacheDocument
    {
        public int SchemaVersion { get; set; }

        public List<ProjectDispatchCacheEntry> Projects { get; set; } = new();
    }

    private sealed class ProjectDispatchCacheEntry
    {
        public string ProjectPath { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;

        public Dictionary<string, List<string>> InterfaceMethodImplementations { get; set; } = new();

        public Dictionary<string, List<string>> MessageHandlers { get; set; } = new();
    }
}
