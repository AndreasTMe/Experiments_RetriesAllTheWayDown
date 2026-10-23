namespace OrdersApi.Instrumentation;

internal static class GroundTruthReader
{
    public static async Task<object> ReadAsync(NpgsqlDataSource direct, CancellationToken cancellationToken)
    {
        await using var connection = await direct.OpenConnectionAsync(cancellationToken);

        await using var rowsCommand = new NpgsqlCommand(
            """
            SELECT coalesce(sum(n), 0)::bigint                  AS rows,
                   count(*)::bigint                             AS distinct_messages,
                   count(*) FILTER (WHERE n > 1)::bigint        AS messages_with_duplicates,
                   coalesce(sum(n - 1), 0)::bigint              AS duplicate_rows,
                   coalesce(max(n), 0)::bigint                  AS max_rows_per_message
            FROM (SELECT message_id, count(*) AS n FROM orders GROUP BY message_id) per_message
            """,
            connection);
        await using var reader = await rowsCommand.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var rows = new
        {
            rows                   = reader.GetInt64(0),
            distinctMessages       = reader.GetInt64(1),
            messagesWithDuplicates = reader.GetInt64(2),
            duplicateRows          = reader.GetInt64(3),
            maxRowsPerMessage      = reader.GetInt64(4),
        };
        await reader.CloseAsync();

        // pg_stat_statements counts statements that finished, in the "orders" database only.
        await using var statementsCommand = new NpgsqlCommand(
            """
            SELECT coalesce(sum(s.calls), 0)::bigint, coalesce(sum(s.rows), 0)::bigint
            FROM pg_stat_statements s
            JOIN pg_database d ON d.oid = s.dbid
             WHERE d.datname = 'orders' AND s.query ILIKE 'INSERT INTO orders%'
            """,
            connection);
        await using var statements = await statementsCommand.ExecuteReaderAsync(cancellationToken);
        await statements.ReadAsync(cancellationToken);

        return new
        {
            timestamp = DateTimeOffset.UtcNow,
            rows.rows,
            rows.distinctMessages,
            rows.messagesWithDuplicates,
            rows.duplicateRows,
            rows.maxRowsPerMessage,
            insertCalls = statements.GetInt64(0),
            insertRows  = statements.GetInt64(1),
        };
    }

    // Message id -> rows, for every message that has at least one row.
    public static async Task<Dictionary<string, long>> ReadRowsPerMessageAsync(
        NpgsqlDataSource direct,
        CancellationToken cancellationToken)
    {
        await using var command =
            direct.CreateCommand("SELECT message_id, count(*)::bigint FROM orders GROUP BY message_id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new Dictionary<string, long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetString(0)] = reader.GetInt64(1);
        }

        return rows;
    }

    public static async Task ResetAsync(NpgsqlDataSource direct, CancellationToken cancellationToken)
    {
        await using var command =
            direct.CreateCommand("TRUNCATE orders RESTART IDENTITY; SELECT pg_stat_statements_reset();");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}