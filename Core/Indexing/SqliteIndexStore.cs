using System.Globalization;
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
        _dbPath = IndexDatabaseLocator.Resolve(configuredPath);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // A full wipe does not care about delete ordering, and the DB may carry stray tables from
        // older schema versions (e.g. a previously removed feature) that still reference Solutions.
        // Disable foreign-key enforcement so the wipe cannot fail on such schema drift.
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = conn.BeginTransaction();

        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            DELETE FROM Edges;
            DELETE FROM Methods;
            DELETE FROM Files;
            DELETE FROM Projects;
            DELETE FROM SolutionAliases;
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
        var storedSolutionPath = ToStoredPath(normalizedPath);
        var storedNodes = ConvertNodesToStoredPaths(index.Nodes);
        var storedProjectPaths = ConvertPathsToStored(index.ProjectPaths);
        var solutionId = index.SolutionId;

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = conn.BeginTransaction();

        await UpsertSolutionAsync(
                conn,
                transaction,
                solutionId,
                storedSolutionPath,
                indexedAt,
                index.HeadCommit,
                index.SlnOnly,
                cancellationToken)
            .ConfigureAwait(false);
        await UpsertSolutionAliasAsync(conn, transaction, solutionId, normalizedPath, cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Methods WHERE SolutionId = $id", cancellationToken,
            ("$id", solutionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Edges WHERE SolutionId = $id", cancellationToken,
            ("$id", solutionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Files WHERE SolutionId = $id", cancellationToken,
            ("$id", solutionId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(conn, transaction, "DELETE FROM Projects WHERE SolutionId = $id", cancellationToken,
            ("$id", solutionId)).ConfigureAwait(false);

        await InsertMethodsAsync(conn, transaction, solutionId, storedNodes, cancellationToken).ConfigureAwait(false);
        await InsertEdgesAsync(conn, transaction, solutionId, index.Edges, cancellationToken).ConfigureAwait(false);
        await InsertFilesAsync(conn, transaction, solutionId, storedNodes, indexedAt, cancellationToken).ConfigureAwait(false);
        await InsertProjectsAsync(conn, transaction, solutionId, storedProjectPaths, cancellationToken).ConfigureAwait(false);

        transaction.Commit();
    }

    public async Task<SolutionIndex?> LoadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var pathCandidates = GetPathCandidates(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var solutionId = await ResolveSolutionIdAsync(conn, pathCandidates, cancellationToken).ConfigureAwait(false);
        if (solutionId is null)
            return null;

        var solution = await LoadSolutionByIdAsync(conn, solutionId, cancellationToken).ConfigureAwait(false);
        if (solution is null)
            return null;

        var nodes = await LoadNodesAsync(conn, solution.SolutionId, cancellationToken).ConfigureAwait(false);
        var edges = await LoadEdgesAsync(conn, solution.SolutionId, cancellationToken).ConfigureAwait(false);

        return new SolutionIndex
        {
            SolutionId = solution.SolutionId,
            SolutionPath = ToRuntimePath(solution.SolutionPath),
            IndexedAtUtc = solution.IndexedAtUtc,
            HeadCommit = solution.HeadCommit,
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
            solutions.Add(new SolutionInfo(id, ToRuntimePath(path), slnOnly));
        }

        return solutions;
    }

    public async Task<SolutionInfo?> GetSolutionByPathAsync(string solutionPath, CancellationToken cancellationToken)
    {
        var pathCandidates = GetPathCandidates(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var solutionId = await ResolveSolutionIdAsync(conn, pathCandidates, cancellationToken).ConfigureAwait(false);
        if (solutionId is null)
            return null;

        var solution = await LoadSolutionByIdAsync(conn, solutionId, cancellationToken).ConfigureAwait(false);
        if (solution is null)
            return null;

        return new SolutionInfo(solution.SolutionId, ToRuntimePath(solution.SolutionPath), solution.SlnOnly);
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
        return new SolutionInfo(id, ToRuntimePath(path), slnOnly);
    }

    public async Task<IReadOnlyList<SolutionInfo>> FindSolutionsByFilePathAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var pathCandidates = GetPathCandidates(filePath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.Id, s.Path, s.SlnOnly
            FROM Solutions s
            INNER JOIN Files f ON f.SolutionId = s.Id
            WHERE f.Path = $path1 COLLATE NOCASE OR f.Path = $path2 COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$path1", pathCandidates.AbsolutePath);
        cmd.Parameters.AddWithValue("$path2", pathCandidates.StoredPath);

        var matches = new List<SolutionInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var path = reader.GetString(1);
            var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
            matches.Add(new SolutionInfo(id, ToRuntimePath(path), slnOnly));
        }

        return matches;
    }

    public async Task<IReadOnlyList<SolutionFileMatch>> FindSolutionsByFilePathSuffixAsync(
        string relativeFilePath,
        CancellationToken cancellationToken)
    {
        var suffix = EscapeLikePattern(ReversePathValue(relativeFilePath));
        var pathCandidates = GetPathCandidates(relativeFilePath);
        var storedSuffix = EscapeLikePattern(ReversePathValue(pathCandidates.StoredPath));

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.Id, s.Path, s.SlnOnly, f.Path
            FROM Solutions s
            INNER JOIN Files f ON f.SolutionId = s.Id
            WHERE f.ReversePath LIKE $suffix || '%' ESCAPE '\' COLLATE NOCASE
               OR f.ReversePath LIKE $storedSuffix || '%' ESCAPE '\' COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$suffix", suffix);
        cmd.Parameters.AddWithValue("$storedSuffix", storedSuffix);

        var matches = new List<SolutionFileMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var path = reader.GetString(1);
            var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
            var filePath = reader.GetString(3);
            matches.Add(new SolutionFileMatch(new SolutionInfo(id, ToRuntimePath(path), slnOnly), ToRuntimePath(filePath)));
        }

        return matches;
    }

    public async Task<IReadOnlyList<SolutionProjectMatch>> FindProjectsByPathSuffixAsync(
        string relativeProjectPath,
        CancellationToken cancellationToken)
    {
        var suffix = EscapeLikePattern(ReversePathValue(relativeProjectPath));
        var pathCandidates = GetPathCandidates(relativeProjectPath);
        var storedSuffix = EscapeLikePattern(ReversePathValue(pathCandidates.StoredPath));

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.Id, s.Path, s.SlnOnly, p.Path
            FROM Solutions s
            INNER JOIN Projects p ON p.SolutionId = s.Id
            WHERE p.ReversePath LIKE $suffix || '%' ESCAPE '\' COLLATE NOCASE
               OR p.ReversePath LIKE $storedSuffix || '%' ESCAPE '\' COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$suffix", suffix);
        cmd.Parameters.AddWithValue("$storedSuffix", storedSuffix);

        var matches = new List<SolutionProjectMatch>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var path = reader.GetString(1);
            var slnOnly = !reader.IsDBNull(2) && reader.GetInt32(2) != 0;
            var projectPath = reader.GetString(3);
            matches.Add(new SolutionProjectMatch(new SolutionInfo(id, ToRuntimePath(path), slnOnly), ToRuntimePath(projectPath)));
        }

        return matches;
    }

    public async Task<Node?> GetMethodAsync(string solutionPath, string methodKey, CancellationToken cancellationToken)
    {
        var pathCandidates = GetPathCandidates(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var solutionId = await ResolveSolutionIdAsync(conn, pathCandidates, cancellationToken).ConfigureAwait(false);
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
            FilePath = reader.IsDBNull(1) ? null : ToRuntimePath(reader.GetString(1)),
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
        var pathCandidates = GetPathCandidates(solutionPath);

        await using var conn = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var solutionId = await ResolveSolutionIdAsync(conn, pathCandidates, cancellationToken).ConfigureAwait(false);
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

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureInitializedAsync(conn, cancellationToken).ConfigureAwait(false);
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
                DROP TABLE IF EXISTS SolutionSnapshots;
                CREATE TABLE IF NOT EXISTS Solutions (
                  Id TEXT PRIMARY KEY,
                  Path TEXT NOT NULL UNIQUE,
                  IndexedAtUtc TEXT NOT NULL,
                  HeadCommit TEXT,
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
                CREATE TABLE IF NOT EXISTS SolutionAliases (
                  SolutionId TEXT NOT NULL,
                  AliasPath TEXT NOT NULL UNIQUE,
                  PRIMARY KEY (SolutionId, AliasPath),
                  FOREIGN KEY (SolutionId) REFERENCES Solutions(Id)
                );
                CREATE INDEX IF NOT EXISTS IX_Solutions_Path ON Solutions(Path);
                CREATE INDEX IF NOT EXISTS IX_Projects_Path ON Projects(Path);
                CREATE INDEX IF NOT EXISTS IX_Projects_ReversePath ON Projects(ReversePath);
                CREATE INDEX IF NOT EXISTS IX_Files_Path ON Files(Path);
                CREATE INDEX IF NOT EXISTS IX_Methods_FilePath ON Methods(FilePath);
                CREATE INDEX IF NOT EXISTS IX_Edges_FromKey ON Edges(FromKey);
                CREATE INDEX IF NOT EXISTS IX_Edges_ToKey ON Edges(ToKey);
                CREATE INDEX IF NOT EXISTS IX_SolutionAliases_AliasPath ON SolutionAliases(AliasPath);
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await EnsureColumnAsync(conn, "Solutions", "SlnOnly", "INTEGER NOT NULL DEFAULT 1", cancellationToken)
                .ConfigureAwait(false);
            await EnsureColumnAsync(conn, "Solutions", "HeadCommit", "TEXT", cancellationToken).ConfigureAwait(false);
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
        string? headCommit,
        bool slnOnly,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO Solutions (Id, Path, IndexedAtUtc, HeadCommit, SlnOnly)
            VALUES ($id, $path, $indexedAt, $headCommit, $slnOnly)
            ON CONFLICT(Id)
            DO UPDATE SET Path = $path, IndexedAtUtc = $indexedAt, HeadCommit = $headCommit, SlnOnly = $slnOnly;
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);
        cmd.Parameters.AddWithValue("$path", solutionPath);
        cmd.Parameters.AddWithValue("$indexedAt", indexedAtUtc.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$headCommit", (object?)headCommit ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$slnOnly", slnOnly ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSolutionAliasAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string solutionId,
        string aliasPath,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO SolutionAliases (SolutionId, AliasPath)
            VALUES ($solutionId, $aliasPath)
            ON CONFLICT(AliasPath)
            DO UPDATE SET SolutionId = $solutionId;
            """;
        cmd.Parameters.AddWithValue("$solutionId", solutionId);
        cmd.Parameters.AddWithValue("$aliasPath", aliasPath);
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
            updatedParam.Value = TryGetFileLastWriteUtc(ToRuntimePath(filePath!), updatedAtUtc).ToString(DateFormat, CultureInfo.InvariantCulture);

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

    private static PathCandidates GetPathCandidates(string path)
    {
        var absolutePath = Path.GetFullPath(path);
        var storedPath = ToStoredPath(absolutePath);
        return new PathCandidates(absolutePath, storedPath);
    }

    private static string ToStoredPath(string path)
    {
        var absolutePath = Path.GetFullPath(path);
        var cwd = Path.GetFullPath(Environment.CurrentDirectory);
        return Path.GetRelativePath(cwd, absolutePath);
    }

    private static string ToRuntimePath(string path)
    {
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        return Path.GetFullPath(path, Environment.CurrentDirectory);
    }

    private static List<Node> ConvertNodesToStoredPaths(IEnumerable<Node> nodes)
    {
        var converted = new List<Node>();
        foreach (var node in nodes)
        {
            converted.Add(new Node
            {
                Id = node.Id,
                Kind = node.Kind,
                Display = node.Display,
                ContainingType = node.ContainingType,
                FilePath = string.IsNullOrWhiteSpace(node.FilePath) ? null : ToStoredPath(node.FilePath),
                StartLine = node.StartLine,
                Accessibility = node.Accessibility
            });
        }

        return converted;
    }

    private static List<string> ConvertPathsToStored(IEnumerable<string> paths)
        => paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(ToStoredPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    private static string ReversePathValue(string value)
    {
        var chars = value.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static async Task<SolutionRow?> LoadSolutionByIdAsync(
        SqliteConnection conn,
        string solutionId,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Path, IndexedAtUtc, HeadCommit, SlnOnly
            FROM Solutions
            WHERE Id = $id
            """;
        cmd.Parameters.AddWithValue("$id", solutionId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var id = reader.GetString(0);
        var path = reader.GetString(1);
        var indexedAtRaw = reader.GetString(2);
        var headCommit = reader.IsDBNull(3) ? null : reader.GetString(3);
        var indexedAt = DateTime.Parse(indexedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var slnOnly = !reader.IsDBNull(4) && reader.GetInt32(4) != 0;

        return new SolutionRow(id, path, indexedAt, headCommit, slnOnly);
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
                FilePath = reader.IsDBNull(1) ? null : ToRuntimePath(reader.GetString(1)),
                Kind = reader.GetString(2),
                Display = reader.IsDBNull(3) ? null : reader.GetString(3),
                ContainingType = reader.IsDBNull(4) ? null : reader.GetString(4),
                StartLine = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Accessibility = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }

        return nodes;
    }

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
        PathCandidates solutionPath,
        CancellationToken cancellationToken)
    {
        var byPathCmd = conn.CreateCommand();
        byPathCmd.CommandText = "SELECT Id FROM Solutions WHERE Path = $pathAbs OR Path = $pathStored";
        byPathCmd.Parameters.AddWithValue("$pathAbs", solutionPath.AbsolutePath);
        byPathCmd.Parameters.AddWithValue("$pathStored", solutionPath.StoredPath);
        var byPath = await byPathCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (!string.IsNullOrWhiteSpace(byPath))
            return byPath;

        var byAliasCmd = conn.CreateCommand();
        byAliasCmd.CommandText = "SELECT SolutionId FROM SolutionAliases WHERE AliasPath = $aliasPath";
        byAliasCmd.Parameters.AddWithValue("$aliasPath", solutionPath.AbsolutePath);
        var byAlias = await byAliasCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (!string.IsNullOrWhiteSpace(byAlias))
            return byAlias;

        var candidateIds = new[]
        {
            SolutionIdentity.FromPath(solutionPath.AbsolutePath, slnOnly: true),
            SolutionIdentity.FromPath(solutionPath.AbsolutePath, slnOnly: false)
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidateId in candidateIds)
        {
            var byIdCmd = conn.CreateCommand();
            byIdCmd.CommandText = "SELECT Id FROM Solutions WHERE Id = $id";
            byIdCmd.Parameters.AddWithValue("$id", candidateId);
            var byId = await byIdCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.IsNullOrWhiteSpace(byId))
                return byId;
        }

        return null;
    }

    private sealed record PathCandidates(string AbsolutePath, string StoredPath);

    private sealed record SolutionRow(
        string SolutionId,
        string SolutionPath,
        DateTime IndexedAtUtc,
        string? HeadCommit,
        bool SlnOnly);
}
