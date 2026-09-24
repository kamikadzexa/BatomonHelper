using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace BatomonHelper.Core;

public sealed class SnapshotRepository(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS catalog_snapshots (
                snapshot_id TEXT PRIMARY KEY, retrieved_at TEXT NOT NULL, steam_build TEXT NOT NULL,
                content_hash TEXT NOT NULL, payload_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS run_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT, captured_at TEXT NOT NULL, payload_json TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(CatalogSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO catalog_snapshots VALUES ($id, $at, $build, $hash, $payload)";
        command.Parameters.AddWithValue("$id", snapshot.SnapshotId);
        command.Parameters.AddWithValue("$at", snapshot.RetrievedAt.ToString("O"));
        command.Parameters.AddWithValue("$build", snapshot.DeclaredSteamBuild);
        command.Parameters.AddWithValue("$hash", snapshot.ContentHash);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CatalogSnapshot?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM catalog_snapshots ORDER BY retrieved_at DESC LIMIT 1";
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        return payload is null ? null : JsonSerializer.Deserialize<CatalogSnapshot>(payload);
    }

    public async Task SaveRunAsync(RunState state, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled) return;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_snapshots(captured_at, payload_json) VALUES($at, $payload)";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(state));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
