using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CallGraph.Contracts;
using CallGraph.Core.Solutions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CallGraph.Core.Indexing;

public sealed class SqliteIndexStore : IIndexStore
{
    private const string DateFormat = "O";
    private readonly string _dbPath;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteIndexStore(IOptions<IndexStoreOptions> options)
    {
        var configuredPath = options.Value.DatabasePath;
        _dbPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CallGraph", "index.db")
            : configuredPath;
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = conn.BeginTransaction();

        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE FROM Edges;
            DELETE FROM Methods;
            DELETE FROM Files;
            DELETE FROM Projects;
            DELETE FROM Solutions;
            """;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task SaveAsync(SolutionIndex index, CancellationToken cancellationToken)
    {
        if (index is null)
            throw new ArgumentNullException(nameof(index));

        var indexedAt = index.IndexedAtUtc == default ? DateTime.UtcNow : index.IndexedAtUtc;
        var normalizedPath = Path.GetFullPath(index.SolutionPath);
        var solutionId = index.SolutionId;

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = conn.BeginTransaction();

        await UpsertSolutionAsync(conn, transaction, solutionId, normalizedPath, indexedAt, index.SlnOnly, cancellationToken)
            .ConfigureAwait(false);

        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Methods WHERE SolutionId = $id", cancellationToken,
            ("$id", solutionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Edges WHERE SolutionId = $id", cancellationToken,
            ("$id", solutionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Files WHERE SolutionId = $id", cancellationToken,
            ("$id", solutionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Projects WHERE SolutionId = $id", cancellationToken,
            ("$id", solutionId)).ConfigureAwait(false);

        await InsertMethodsAsync(conn, transaction, solutionId, index.Nodes, cancellationToken).ConfigureAwait(false);
        await InsertEdgesAsync(conn, transaction, solutionId, index.Edges, cancellationToken).ConfigureAwait(false);
        await InsertFilesAsync(conn, transaction, solutionId, index.Nodes, indexedAt, cancellationToken).ConfigureAwait(false);
        await InsertProjectsAsync(conn, transaction, solutionId, index.ProjectPaths, cancellationToken).ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task<SolutionIndex?> LoadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var solution = await LoadSolutionAsync(conn, normalizedPath, cancellationToken).ConfigureAwait(false);
        if (solution is null)
            return null;

        var nodes = await LoadNodesAsync(conn, solution.SolutionId, cancellationToken).ConfigureAwait(false);
        var edges = await LoadEdgesAsync(conn, solution.SolutionId, cancellationToken).ConfigureAwait(false);

        return new SolutionIndex
        {
            SolutionId = solution.SolutionId,
            SolutionPath = solution.SolutionPath,
            IndexedAtUtc = solution.IndexedAtUtc,
            SlnOnly = solution.SlnOnly,
            Nodes = nodes,
            Edges = edges
        };
    }

    public async Task<IReadOnlyList<SolutionInfo>> ListSolutionsAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var solutions = new List<SolutionInfo>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, SlnOnly FROM Solutions";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var path = reader.GetString(1);
            var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
            solutions.Add(new SolutionInfo(id, path, slnOnly));
        }

        return solutions;
    }

    public async Task<SolutionInfo?> GetSolutionByPathAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, SlnOnly FROM Solutions WHERE Path = $path";
        cmd.Parameters.AddWithValue("$path", normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var id = reader.GetString(0);
        var path = reader.GetString(1);
        var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
        return new SolutionInfo(id, path, slnOnly);
    }

    public async Task<DateTime?> GetIndexedAtUtcAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT IndexedAtUtc FROM Solutions WHERE Path = $path";
        cmd.Parameters.AddWithValue("$path", normalizedPath);

        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
            return null;

        return DateTime.Parse(result.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public async Task<SolutionInfo?> GetSolutionByIdAsync(string solutionId, CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, SlnOnly FROM Solutions WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", solutionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var id = reader.GetString(0);
        var path = reader.GetString(1);
        var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
        return new SolutionInfo(id, path, slnOnly);
    }

    public async Task<IReadOnlyList<IndexedFileInfo>> ListFilesAsync(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var solutionId = await ResolveSolutionIdAsync(conn, normalizedPath, cancellationToken).ConfigureAwait(false);
        if (solutionId is null)
            return Array.Empty<IndexedFileInfo>();

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Path, UpdatedAtUtc
            FROM Files
            WHERE SolutionId = $id;
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);

        var files = new List<IndexedFileInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var filePath = reader.GetString(0);
            var updatedAtUtc = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            files.Add(new IndexedFileInfo(filePath, updatedAtUtc));
        }

        return files;
    }

    public async Task<IReadOnlyList<string>> ListProjectPathsAsync(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var solutionId = await ResolveSolutionIdAsync(conn, normalizedPath, cancellationToken).ConfigureAwait(false);
        if (solutionId is null)
            return Array.Empty<string>();

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Path
            FROM Projects
            WHERE SolutionId = $id;
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);

        var projectPaths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projectPaths.Add(reader.GetString(0));
        }

        return projectPaths;
    }

    public async Task<IReadOnlyList<SolutionInfo>> FindSolutionsByFilePathAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.Id, s.Path, s.SlnOnly
            FROM Solutions s
            INNER JOIN Files f ON f.SolutionId = s.Id
            WHERE f.Path = $path COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$path", normalizedPath);

        var matches = new List<SolutionInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var path = reader.GetString(1);
            var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
            matches.Add(new SolutionInfo(id, path, slnOnly));
        }

        return matches;
    }

    public async Task<IReadOnlyList<SolutionFileMatch>> FindSolutionsByFilePathSuffixAsync(
        string relativeFilePath,
        CancellationToken cancellationToken)
    {
        var suffix = EscapeLikePattern(ReversePathValue(relativeFilePath));

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.Id, s.Path, s.SlnOnly, f.Path
            FROM Solutions s
            INNER JOIN Files f ON f.SolutionId = s.Id
            WHERE f.ReversePath LIKE $suffix || '%' ESCAPE '\' COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$suffix", suffix);

        var matches = new List<SolutionFileMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var path = reader.GetString(1);
            var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
            var filePath = reader.GetString(3);
            matches.Add(new SolutionFileMatch(new SolutionInfo(id, path, slnOnly), filePath));
        }

        return matches;
    }

    public async Task<IReadOnlyList<SolutionProjectMatch>> FindProjectsByPathSuffixAsync(
        string relativeProjectPath,
        CancellationToken cancellationToken)
    {
        var suffix = EscapeLikePattern(ReversePathValue(relativeProjectPath));

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.Id, s.Path, s.SlnOnly, p.Path
            FROM Solutions s
            INNER JOIN Projects p ON p.SolutionId = s.Id
            WHERE p.ReversePath LIKE $suffix || '%' ESCAPE '\' COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$suffix", suffix);

        var matches = new List<SolutionProjectMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var path = reader.GetString(1);
            var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
            var projectPath = reader.GetString(3);
            matches.Add(new SolutionProjectMatch(new SolutionInfo(id, path, slnOnly), projectPath));
        }

        return matches;
    }

    public async Task<IReadOnlyList<SearchFileMatch>> SearchFilesAsync(
        string pattern,
        bool useRegex,
        string? solutionPath,
        string? solutionId,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(solutionPath) ? null : Path.GetFullPath(solutionPath);
        var normalizedFolderPath = string.IsNullOrWhiteSpace(folderPath) ? null : Path.GetFullPath(folderPath);
        var normalizedFilePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(solutionId))
        {
            conditions.Add("s.Id = $id");
            cmd.Parameters.AddWithValue("$id", solutionId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            conditions.Add("s.Path = $path COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$path", normalizedPath);
        }

        if (!string.IsNullOrWhiteSpace(normalizedFolderPath))
        {
            conditions.Add("f.Path LIKE $folderPath ESCAPE '\\' COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$folderPath", EscapeLikePattern(normalizedFolderPath) + "%");
        }

        if (!string.IsNullOrWhiteSpace(normalizedFilePath))
        {
            conditions.Add("f.Path = $filePath COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$filePath", normalizedFilePath);
        }

        if (useRegex)
        {
            conditions.Add("f.Path REGEXP $pattern");
            cmd.Parameters.AddWithValue("$pattern", pattern);
        }
        else if (TryGetSuffixOnlyPattern(pattern, out var suffix))
        {
            // Optimize suffix-only wildcard queries (e.g. "*Foo.cs") by using
            // the indexed reverse path instead of a leading-wildcard scan on Path.
            var suffixReverse = EscapeLikePattern(ReversePathValue(suffix));
            conditions.Add("f.ReversePath LIKE $suffixReverse || '%' ESCAPE '\\' COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$suffixReverse", suffixReverse);
        }
        else
        {
            conditions.Add("f.Path LIKE $pattern ESCAPE '\\' COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$pattern", ConvertWildcardToLike(pattern));
        }

        cmd.CommandText = $"""
            SELECT s.Id, s.Path, f.Path
            FROM Solutions s
            INNER JOIN Files f ON f.SolutionId = s.Id
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY s.Path, f.Path;
            """;

        var matches = new List<SearchFileMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            matches.Add(new SearchFileMatch(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return matches;
    }

    public async Task<IReadOnlyList<SearchMethodMatch>> SearchMethodsAsync(
        string pattern,
        bool useRegex,
        string? solutionPath,
        string? solutionId,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(solutionPath) ? null : Path.GetFullPath(solutionPath);
        var normalizedFolderPath = string.IsNullOrWhiteSpace(folderPath) ? null : Path.GetFullPath(folderPath);
        var normalizedFilePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(solutionId))
        {
            conditions.Add("s.Id = $id");
            cmd.Parameters.AddWithValue("$id", solutionId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            conditions.Add("s.Path = $path COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$path", normalizedPath);
        }

        if (!string.IsNullOrWhiteSpace(normalizedFolderPath))
        {
            conditions.Add("m.FilePath LIKE $folderPath ESCAPE '\\' COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$folderPath", EscapeLikePattern(normalizedFolderPath) + "%");
        }

        if (!string.IsNullOrWhiteSpace(normalizedFilePath))
        {
            conditions.Add("m.FilePath = $filePath COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$filePath", normalizedFilePath);
        }

        if (useRegex)
        {
            conditions.Add("(m.Key REGEXP $pattern OR m.Display REGEXP $pattern)");
            cmd.Parameters.AddWithValue("$pattern", pattern);
        }
        else
        {
            conditions.Add("(m.Key LIKE $pattern ESCAPE '\\' COLLATE NOCASE OR m.Display LIKE $pattern ESCAPE '\\' COLLATE NOCASE)");
            cmd.Parameters.AddWithValue("$pattern", ConvertWildcardToLike(pattern));
        }

        cmd.CommandText = $"""
            SELECT s.Id, s.Path, m.Key, m.FilePath, m.Kind, m.Display, m.ContainingType, m.StartLine, m.Accessibility
            FROM Solutions s
            INNER JOIN Methods m ON m.SolutionId = s.Id
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY s.Path, m.Key;
            """;

        var matches = new List<SearchMethodMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            matches.Add(ReadSearchMethodMatch(reader));
        }

        return matches;
    }

    public async Task<IReadOnlyList<SearchMethodMatch>> ListMethodsAsync(
        string visibility,
        string? solutionPath,
        string? solutionId,
        string? folderPath,
        string? filePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(solutionPath) ? null : Path.GetFullPath(solutionPath);
        var normalizedFolderPath = string.IsNullOrWhiteSpace(folderPath) ? null : Path.GetFullPath(folderPath);
        var normalizedFilePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        var includeInternal = string.Equals(visibility, "internal", StringComparison.OrdinalIgnoreCase);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(solutionId))
        {
            conditions.Add("s.Id = $id");
            cmd.Parameters.AddWithValue("$id", solutionId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            conditions.Add("s.Path = $path COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$path", normalizedPath);
        }

        if (!string.IsNullOrWhiteSpace(normalizedFolderPath))
        {
            conditions.Add("m.FilePath LIKE $folderPath ESCAPE '\\' COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$folderPath", EscapeLikePattern(normalizedFolderPath) + "%");
        }

        if (!string.IsNullOrWhiteSpace(normalizedFilePath))
        {
            conditions.Add("m.FilePath = $filePath COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$filePath", normalizedFilePath);
        }

        if (!includeInternal)
        {
            conditions.Add("""
                (
                    m.Accessibility = 'public' COLLATE NOCASE OR
                    m.Accessibility = 'protected' COLLATE NOCASE OR
                    m.Accessibility = 'protected internal' COLLATE NOCASE
                )
                """);
        }

        cmd.CommandText = $"""
            SELECT s.Id, s.Path, m.Key, m.FilePath, m.Kind, m.Display, m.ContainingType, m.StartLine, m.Accessibility
            FROM Solutions s
            INNER JOIN Methods m ON m.SolutionId = s.Id
            {(conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty)}
            ORDER BY s.Path, m.Key;
            """;

        var matches = new List<SearchMethodMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            matches.Add(ReadSearchMethodMatch(reader));
        }

        return matches;
    }

    public async Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var solutionId = await ResolveSolutionIdAsync(conn, normalizedPath, cancellationToken).ConfigureAwait(false);
        if (solutionId is null)
            return null;

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key, FilePath, Kind, Display, ContainingType, StartLine, Accessibility
            FROM Methods
            WHERE SolutionId = $id AND Key = $key;
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);
        cmd.Parameters.AddWithValue("$key", methodKey);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new Node
        {
            Id = reader.GetString(0),
            FilePath = reader.IsDBNull(1) ? null : reader.GetString(1),
            Kind = reader.GetString(2),
            Display = reader.IsDBNull(3) ? null : reader.GetString(3),
            ContainingType = reader.IsDBNull(4) ? null : reader.GetString(4),
            StartLine = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Accessibility = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }

    public async Task<IReadOnlyList<Edge>> GetEdgesAsync(
        string solutionPath,
        string methodKey,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var solutionId = await ResolveSolutionIdAsync(conn, normalizedPath, cancellationToken).ConfigureAwait(false);
        if (solutionId is null)
            return Array.Empty<Edge>();

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FromKey, ToKey, Direction, Kind
            FROM Edges
            WHERE SolutionId = $id AND (FromKey = $key OR ToKey = $key);
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);
        cmd.Parameters.AddWithValue("$key", methodKey);

        var edges = new List<Edge>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(new Edge
            {
                From = reader.GetString(0),
                To = reader.GetString(1),
                Direction = reader.GetString(2),
                Kind = reader.GetString(3)
            });
        }

        return edges;
    }

    public async Task UpdateFileAsync(string solutionPath, FileIndex update, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);
        var normalizedFilePath = Path.GetFullPath(update.FilePath);
        var fileUpdatedAtUtc = TryGetFileLastWriteUtc(normalizedFilePath, DateTime.UtcNow);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = conn.BeginTransaction();

        var solutionId = await ResolveSolutionIdAsync(conn, normalizedPath, cancellationToken).ConfigureAwait(false);
        if (solutionId is null)
            return;

        var existingKeys = await LoadMethodKeysForFileAsync(conn, solutionId, normalizedFilePath, cancellationToken)
            .ConfigureAwait(false);
        var newKeys = update.Nodes
            .Select(node => node.Id)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var keysToRefresh = existingKeys
            .Concat(newKeys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var removedKeys = existingKeys
            .Where(existingKey => !newKeys.Contains(existingKey))
            .ToList();

        await DeleteOutboundEdgesForKeysAsync(conn, transaction, solutionId, keysToRefresh, cancellationToken).ConfigureAwait(false);
        await DeleteEdgesForKeysAsync(conn, transaction, solutionId, removedKeys, cancellationToken).ConfigureAwait(false);
        await DeleteMethodsForKeysAsync(conn, transaction, solutionId, newKeys.ToList(), cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Methods WHERE SolutionId = $id AND FilePath = $file",
            cancellationToken,
            ("$id", solutionId),
            ("$file", normalizedFilePath)).ConfigureAwait(false);

        await InsertMethodsAsync(conn, transaction, solutionId, update.Nodes, cancellationToken).ConfigureAwait(false);
        await InsertEdgesAsync(conn, transaction, solutionId, update.Edges, cancellationToken).ConfigureAwait(false);
        await UpsertFileAsync(conn, transaction, solutionId, normalizedFilePath, fileUpdatedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        await UpdateSolutionTimestampAsync(conn, transaction, solutionId, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task RemoveFileAsync(string solutionPath, string filePath, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(solutionPath);
        var normalizedFilePath = Path.GetFullPath(filePath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = conn.BeginTransaction();

        var solutionId = await ResolveSolutionIdAsync(conn, normalizedPath, cancellationToken).ConfigureAwait(false);
        if (solutionId is null)
            return;

        var existingKeys = await LoadMethodKeysForFileAsync(conn, solutionId, normalizedFilePath, cancellationToken)
            .ConfigureAwait(false);

        await DeleteEdgesForKeysAsync(conn, transaction, solutionId, existingKeys, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Methods WHERE SolutionId = $id AND FilePath = $file",
            cancellationToken,
            ("$id", solutionId),
            ("$file", normalizedFilePath)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Files WHERE SolutionId = $id AND Path = $file",
            cancellationToken,
            ("$id", solutionId),
            ("$file", normalizedFilePath)).ConfigureAwait(false);
        await UpdateSolutionTimestampAsync(conn, transaction, solutionId, DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        transaction.Commit();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);
        RegisterRegexFunction(conn);
        return conn;
    }

    private async Task EnsureInitializedAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Solutions (
                  Id TEXT PRIMARY KEY,
                  Path TEXT NOT NULL UNIQUE,
                  IndexedAtUtc TEXT NOT NULL,
                  SlnOnly INTEGER NOT NULL DEFAULT 1
                );
                CREATE TABLE IF NOT EXISTS Projects (
                  SolutionId TEXT NOT NULL,
                  Path TEXT NOT NULL,
                  ReversePath TEXT,
                  PRIMARY KEY (SolutionId, Path),
                  FOREIGN KEY (SolutionId) REFERENCES Solutions(Id)
                );
                CREATE TABLE IF NOT EXISTS Files (
                  SolutionId TEXT NOT NULL,
                  Path TEXT NOT NULL,
                  ReversePath TEXT,
                  UpdatedAtUtc TEXT NOT NULL,
                  PRIMARY KEY (SolutionId, Path),
                  FOREIGN KEY (SolutionId) REFERENCES Solutions(Id)
                );
                CREATE TABLE IF NOT EXISTS Methods (
                  Key TEXT NOT NULL,
                  SolutionId TEXT NOT NULL,
                  FilePath TEXT,
                  Kind TEXT NOT NULL,
                  Display TEXT,
                  ContainingType TEXT,
                                    StartLine INTEGER,
                                    Accessibility TEXT,
                  PRIMARY KEY (SolutionId, Key),
                  FOREIGN KEY (SolutionId) REFERENCES Solutions(Id)
                );
                CREATE TABLE IF NOT EXISTS Edges (
                  FromKey TEXT NOT NULL,
                  ToKey TEXT NOT NULL,
                  Direction TEXT NOT NULL,
                  Kind TEXT NOT NULL,
                  SolutionId TEXT NOT NULL,
                  FOREIGN KEY (SolutionId) REFERENCES Solutions(Id)
                );
                CREATE INDEX IF NOT EXISTS IX_Solutions_Path ON Solutions(Path);
                CREATE INDEX IF NOT EXISTS IX_Projects_Path ON Projects(Path);
                CREATE INDEX IF NOT EXISTS IX_Projects_ReversePath ON Projects(ReversePath);
                CREATE INDEX IF NOT EXISTS IX_Files_Path ON Files(Path);
                CREATE INDEX IF NOT EXISTS IX_Methods_FilePath ON Methods(FilePath);
                CREATE INDEX IF NOT EXISTS IX_Edges_FromKey ON Edges(FromKey);
                CREATE INDEX IF NOT EXISTS IX_Edges_ToKey ON Edges(ToKey);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await EnsureColumnAsync(conn, "Solutions", "SlnOnly", "INTEGER NOT NULL DEFAULT 1", cancellationToken)
                .ConfigureAwait(false);
            await EnsureColumnAsync(conn, "Files", "ReversePath", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(conn, "Methods", "Accessibility", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureIndexAsync(conn, "IX_Files_ReversePath", "Files", "ReversePath", cancellationToken)
                .ConfigureAwait(false);
            await BackfillReversePathsAsync(conn, cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureIndexAsync(
        SqliteConnection conn,
        string indexName,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName}({columnName});";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSolutionAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        string solutionPath,
        DateTime indexedAtUtc,
        bool slnOnly,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Solutions (Id, Path, IndexedAtUtc, SlnOnly)
            VALUES ($id, $path, $indexedAt, $slnOnly)
            ON CONFLICT(Path)
            DO UPDATE SET Id = $id, IndexedAtUtc = $indexedAt, SlnOnly = $slnOnly;
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);
        cmd.Parameters.AddWithValue("$path", solutionPath);
        cmd.Parameters.AddWithValue("$indexedAt", indexedAtUtc.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$slnOnly", slnOnly ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertMethodsAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        IEnumerable<Node> nodes,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Methods (Key, SolutionId, FilePath, Kind, Display, ContainingType, StartLine, Accessibility)
            VALUES ($key, $solutionId, $filePath, $kind, $display, $containingType, $startLine, $accessibility);
            """;

        var keyParam = cmd.Parameters.Add("$key", SqliteType.Text);
        var solutionParam = cmd.Parameters.Add("$solutionId", SqliteType.Text);
        var fileParam = cmd.Parameters.Add("$filePath", SqliteType.Text);
        var kindParam = cmd.Parameters.Add("$kind", SqliteType.Text);
        var displayParam = cmd.Parameters.Add("$display", SqliteType.Text);
        var containingParam = cmd.Parameters.Add("$containingType", SqliteType.Text);
        var lineParam = cmd.Parameters.Add("$startLine", SqliteType.Integer);
        var accessParam = cmd.Parameters.Add("$accessibility", SqliteType.Text);

        cmd.Prepare();

        foreach (var node in nodes.DistinctBy(n => n.Id))
        {
            keyParam.Value = node.Id;
            solutionParam.Value = solutionId;
            fileParam.Value = (object?)node.FilePath ?? DBNull.Value;
            kindParam.Value = node.Kind;
            displayParam.Value = (object?)node.Display ?? DBNull.Value;
            containingParam.Value = (object?)node.ContainingType ?? DBNull.Value;
            lineParam.Value = (object?)node.StartLine ?? DBNull.Value;
            accessParam.Value = (object?)node.Accessibility ?? DBNull.Value;

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertEdgesAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        IEnumerable<Edge> edges,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Edges (FromKey, ToKey, Direction, Kind, SolutionId)
            VALUES ($fromKey, $toKey, $direction, $kind, $solutionId);
            """;

        var fromParam = cmd.Parameters.Add("$fromKey", SqliteType.Text);
        var toParam = cmd.Parameters.Add("$toKey", SqliteType.Text);
        var directionParam = cmd.Parameters.Add("$direction", SqliteType.Text);
        var kindParam = cmd.Parameters.Add("$kind", SqliteType.Text);
        var solutionParam = cmd.Parameters.Add("$solutionId", SqliteType.Text);

        cmd.Prepare();

        foreach (var edge in edges)
        {
            fromParam.Value = edge.From;
            toParam.Value = edge.To;
            directionParam.Value = edge.Direction;
            kindParam.Value = edge.Kind;
            solutionParam.Value = solutionId;

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertFilesAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        IEnumerable<Node> nodes,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var filePaths = nodes
            .Select(n => n.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Files (SolutionId, Path, ReversePath, UpdatedAtUtc)
            VALUES ($solutionId, $path, $reversePath, $updatedAt)
            ON CONFLICT(SolutionId, Path)
            DO UPDATE SET ReversePath = $reversePath, UpdatedAtUtc = $updatedAt;
            """;

        var solutionParam = cmd.Parameters.Add("$solutionId", SqliteType.Text);
        var pathParam = cmd.Parameters.Add("$path", SqliteType.Text);
        var reverseParam = cmd.Parameters.Add("$reversePath", SqliteType.Text);
        var updatedParam = cmd.Parameters.Add("$updatedAt", SqliteType.Text);

        cmd.Prepare();

        foreach (var filePath in filePaths)
        {
            solutionParam.Value = solutionId;
            pathParam.Value = filePath!;
            reverseParam.Value = ReversePathValue(filePath!);
            updatedParam.Value = TryGetFileLastWriteUtc(filePath!, updatedAtUtc).ToString(DateFormat, CultureInfo.InvariantCulture);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertProjectsAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        IEnumerable<string> projectPaths,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Projects (SolutionId, Path, ReversePath)
            VALUES ($solutionId, $path, $reversePath)
            ON CONFLICT(SolutionId, Path)
            DO UPDATE SET ReversePath = $reversePath;
            """;

        var solutionParam = cmd.Parameters.Add("$solutionId", SqliteType.Text);
        var pathParam = cmd.Parameters.Add("$path", SqliteType.Text);
        var reverseParam = cmd.Parameters.Add("$reversePath", SqliteType.Text);

        cmd.Prepare();

        foreach (var projectPath in projectPaths)
        {
            solutionParam.Value = solutionId;
            pathParam.Value = projectPath;
            reverseParam.Value = ReversePathValue(projectPath);

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpsertFileAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        string filePath,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Files (SolutionId, Path, ReversePath, UpdatedAtUtc)
            VALUES ($solutionId, $path, $reversePath, $updatedAt)
            ON CONFLICT(SolutionId, Path)
            DO UPDATE SET ReversePath = $reversePath, UpdatedAtUtc = $updatedAt;
            """;
        cmd.Parameters.AddWithValue("$solutionId", solutionId);
        cmd.Parameters.AddWithValue("$path", filePath);
        cmd.Parameters.AddWithValue("$reversePath", ReversePathValue(filePath));
        cmd.Parameters.AddWithValue("$updatedAt", updatedAtUtc.ToString(DateFormat, CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateSolutionTimestampAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(conn, transaction,
            "UPDATE Solutions SET IndexedAtUtc = $indexedAt WHERE Id = $id",
            cancellationToken,
            ("$indexedAt", updatedAtUtc.ToString(DateFormat, CultureInfo.InvariantCulture)),
            ("$id", solutionId)).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string name, object value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackfillReversePathsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        var select = conn.CreateCommand();
        select.CommandText = "SELECT Path FROM Files WHERE ReversePath IS NULL OR ReversePath = ''";

        var paths = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                paths.Add(reader.GetString(0));
            }
        }

        if (paths.Count == 0)
            return;

        await using var transaction = conn.BeginTransaction();
        var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE Files SET ReversePath = $reverse WHERE Path = $path";
        var reverseParam = update.Parameters.Add("$reverse", SqliteType.Text);
        var pathParam = update.Parameters.Add("$path", SqliteType.Text);

        foreach (var path in paths)
        {
            reverseParam.Value = ReversePathValue(path);
            pathParam.Value = path;
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    private static DateTime TryGetFileLastWriteUtc(string filePath, DateTime fallbackUtc)
    {
        try
        {
            return File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : fallbackUtc;
        }
        catch
        {
            return fallbackUtc;
        }
    }

    private static string ConvertWildcardToLike(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);
        foreach (var ch in pattern)
        {
            builder.Append(ch switch
            {
                '*' => '%',
                '?' => '_',
                '%' => "\\%",
                '_' => "\\_",
                '\\' => "\\\\",
                _ => ch.ToString()
            });
        }
        return builder.ToString();
    }

    private static bool TryGetSuffixOnlyPattern(string pattern, out string suffix)
    {
        suffix = string.Empty;

        if (string.IsNullOrEmpty(pattern) || pattern.Length < 2 || pattern[0] != '*')
            return false;

        var remainder = pattern[1..];
        if (remainder.IndexOf('*') >= 0 || remainder.IndexOf('?') >= 0)
            return false;

        suffix = remainder;
        return true;
    }

    private static string ReversePathValue(string value)
    {
        var chars = value.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static void RegisterRegexFunction(SqliteConnection conn)
    {
        conn.CreateFunction<string?, string?, bool>(
            "regexp",
            (pattern, input) =>
            {
                if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(input))
                    return false;

                try
                {
                    return Regex.IsMatch(
                        input,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch (ArgumentException)
                {
                    return false;
                }
            });
    }

    private static async Task<SolutionRow?> LoadSolutionAsync(
        SqliteConnection conn,
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, IndexedAtUtc, SlnOnly FROM Solutions WHERE Path = $path";
        cmd.Parameters.AddWithValue("$path", solutionPath);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var id = reader.GetString(0);
        var path = reader.GetString(1);
        var indexedAtRaw = reader.GetString(2);
        var indexedAt = DateTime.Parse(indexedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var slnOnly = !reader.IsDBNull(3) && reader.GetInt32(3) != 0;

        return new SolutionRow(id, path, indexedAt, slnOnly);
    }

    private static async Task<List<Node>> LoadNodesAsync(
        SqliteConnection conn,
        string solutionId,
        CancellationToken cancellationToken)
    {
        var nodes = new List<Node>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key, FilePath, Kind, Display, ContainingType, StartLine, Accessibility
            FROM Methods
            WHERE SolutionId = $id;
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(new Node
            {
                Id = reader.GetString(0),
                FilePath = reader.IsDBNull(1) ? null : reader.GetString(1),
                Kind = reader.GetString(2),
                Display = reader.IsDBNull(3) ? null : reader.GetString(3),
                ContainingType = reader.IsDBNull(4) ? null : reader.GetString(4),
                StartLine = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Accessibility = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }

        return nodes;
    }

    private static SearchMethodMatch ReadSearchMethodMatch(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            new Node
            {
                Id = reader.GetString(2),
                FilePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                Kind = reader.GetString(4),
                Display = reader.IsDBNull(5) ? null : reader.GetString(5),
                ContainingType = reader.IsDBNull(6) ? null : reader.GetString(6),
                StartLine = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Accessibility = reader.IsDBNull(8) ? null : reader.GetString(8)
            });

    private static async Task<List<Edge>> LoadEdgesAsync(
        SqliteConnection conn,
        string solutionId,
        CancellationToken cancellationToken)
    {
        var edges = new List<Edge>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FromKey, ToKey, Direction, Kind
            FROM Edges
            WHERE SolutionId = $id;
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(new Edge
            {
                From = reader.GetString(0),
                To = reader.GetString(1),
                Direction = reader.GetString(2),
                Kind = reader.GetString(3)
            });
        }

        return edges;
    }

    private static async Task<string?> ResolveSolutionIdAsync(
        SqliteConnection conn,
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Solutions WHERE Path = $path";
        cmd.Parameters.AddWithValue("$path", solutionPath);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    private static async Task<List<string>> LoadMethodKeysForFileAsync(
        SqliteConnection conn,
        string solutionId,
        string filePath,
        CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key
            FROM Methods
            WHERE SolutionId = $id AND FilePath = $file;
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);
        cmd.Parameters.AddWithValue("$file", filePath);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private static async Task DeleteEdgesForKeysAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
            return;

        var placeholders = string.Join(", ", keys.Select((_, i) => $"$k{i}"));
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            DELETE FROM Edges
            WHERE SolutionId = $id AND (FromKey IN ({placeholders}) OR ToKey IN ({placeholders}));
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);
        for (var i = 0; i < keys.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$k{i}", keys[i]);
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteOutboundEdgesForKeysAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
            return;

        var placeholders = string.Join(", ", keys.Select((_, i) => $"$k{i}"));
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            DELETE FROM Edges
            WHERE SolutionId = $id AND FromKey IN ({placeholders});
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);
        for (var i = 0; i < keys.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$k{i}", keys[i]);
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteMethodsForKeysAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
            return;

        var placeholders = string.Join(", ", keys.Select((_, i) => $"$k{i}"));
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            DELETE FROM Methods
            WHERE SolutionId = $id AND Key IN ({placeholders});
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);
        for (var i = 0; i < keys.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$k{i}", keys[i]);
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record SolutionRow(string SolutionId, string SolutionPath, DateTime IndexedAtUtc, bool SlnOnly);
}
