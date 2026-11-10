using Microsoft.Data.Sqlite;

namespace ForgeLink.Client.Offline;

public static class CommandKinds
{
    public const string ReportProduction = "ReportProduction";
    public const string MoveInventory = "MoveInventory";
    public const string SubmitCycleCount = "SubmitCycleCount";
    public const string RecordQualityCheck = "RecordQualityCheck";
}

public enum QueuedCommandState
{
    Queued,

    /// <summary>The server refused it (4xx) on replay. Kept for the operator/supervisor to review; never retried automatically.</summary>
    Rejected,
}

public sealed record QueuedCommand(
    long Id, string Kind, string? Target, string PayloadJson, string IdempotencyKey, DateTime CreatedUtc,
    int Attempts, string? LastError, QueuedCommandState State);

/// <summary>
/// Durable FIFO of commands captured while the device was offline, in a local SQLite file so it survives
/// app restarts and power loss (WAL + synchronous=FULL). Each command keeps the idempotency key it was
/// created with, so replaying is safe even if the original request actually reached the server.
/// </summary>
public sealed class OfflineCommandQueue
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OfflineCommandQueue(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Cache = SqliteCacheMode.Shared }.ToString();
        using var conn = Open();
        Execute(conn, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS queued_commands (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                target TEXT NULL,
                payload TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                created_utc TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                last_error TEXT NULL,
                state TEXT NOT NULL DEFAULT 'Queued'
            );
            """);
    }

    public event EventHandler? Changed;

    public async Task EnqueueAsync(string kind, string? target, string payloadJson, string idempotencyKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO queued_commands (kind, target, payload, idempotency_key, created_utc)
                VALUES ($kind, $target, $payload, $key, $created)
                ON CONFLICT(idempotency_key) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$target", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$payload", payloadJson);
            cmd.Parameters.AddWithValue("$key", idempotencyKey);
            cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<QueuedCommand>> ListAsync(QueuedCommandState? state = null, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, target, payload, idempotency_key, created_utc, attempts, last_error, state FROM queued_commands"
            + (state is null ? "" : " WHERE state = $state") + " ORDER BY id";
        if (state is not null) cmd.Parameters.AddWithValue("$state", state.ToString());

        var result = new List<QueuedCommand>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new QueuedCommand(
                reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.GetString(4), DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7), Enum.Parse<QueuedCommandState>(reader.GetString(8))));
        }
        return result;
    }

    public async Task<int> CountAsync(QueuedCommandState state = QueuedCommandState.Queued, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM queued_commands WHERE state = $state";
        cmd.Parameters.AddWithValue("$state", state.ToString());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public Task RemoveAsync(long id, CancellationToken ct = default) =>
        WriteAsync("DELETE FROM queued_commands WHERE id = $id", ct, ("$id", id));

    public Task RecordFailureAsync(long id, string error, CancellationToken ct = default) =>
        WriteAsync("UPDATE queued_commands SET attempts = attempts + 1, last_error = $error WHERE id = $id", ct, ("$id", id), ("$error", error));

    public Task MarkRejectedAsync(long id, string error, CancellationToken ct = default) =>
        WriteAsync("UPDATE queued_commands SET attempts = attempts + 1, last_error = $error, state = 'Rejected' WHERE id = $id", ct, ("$id", id), ("$error", error));

    private async Task WriteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
