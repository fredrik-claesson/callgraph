using System.Text;
using CallGraph.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CallGraph.Cli;

internal static class QueryCommandExecutor
{
    public static async Task<ToolExecutionResult> ExecuteAsync(
        string sql,
        string? configuredDbPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return ToolExecutionResult.FromError("query requires a SQL statement.");

        var dbPath = IndexDatabaseLocator.Resolve(configuredDbPath);
        if (!File.Exists(dbPath))
            return ToolExecutionResult.FromError($"Index database not found at {dbPath}. Run --index first.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        try
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder();
            if (reader.FieldCount > 0)
            {
                sb.Append(reader.GetName(0));
                for (var i = 1; i < reader.FieldCount; i++)
                    sb.Append('\t').Append(reader.GetName(i));
                sb.Append('\n');
            }

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (i > 0) sb.Append('\t');
                    if (!reader.IsDBNull(i)) sb.Append(reader.GetValue(i));
                }
                sb.Append('\n');
            }

            return ToolExecutionResult.FromText(sb.ToString().TrimEnd('\n'));
        }
        catch (SqliteException ex)
        {
            return ToolExecutionResult.FromError(ex.Message);
        }
    }
}
