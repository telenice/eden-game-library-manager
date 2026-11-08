using System.Globalization;
using System.Text.Json;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Persistence;

/// <summary>
/// SQLite manifest of local games and unfinished downloads. If the database is unreadable it is
/// renamed aside and recreated; game files are never touched, and the next scan rediscovers them.
/// </summary>
public sealed class ManifestService(string databasePath, ILogger<ManifestService> logger) : IManifestService
{
    private const int SchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false,
        DefaultTimeout = 30,
    }.ToString();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Paths are case-insensitive on Windows/macOS by default but case-sensitive on Linux.
    private static string PathCollation => OperatingSystem.IsLinux() ? "BINARY" : "NOCASE";

    /// <summary>Set when the previous database was corrupt and had to be replaced.</summary>
    public string? RecoveredFromCorruptFile { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        try
        {
            await CreateSchemaAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException)
        {
            logger.LogError(ex, "Manifest database is corrupt; recreating it (game files are not affected)");
            var backup = $"{databasePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            SqliteConnection.ClearAllPools();
            File.Move(databasePath, backup, overwrite: true);
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                if (File.Exists(databasePath + suffix)) File.Delete(databasePath + suffix);
            RecoveredFromCorruptFile = backup;
            await CreateSchemaAsync(cancellationToken);
        }
    }

    private async Task CreateSchemaAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            var result = (string?)await check.ExecuteScalarAsync(ct);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite integrity check failed: {result}");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS library_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                local_path TEXT NOT NULL UNIQUE COLLATE {PathCollation},
                server_key TEXT,
                remote_file_id INTEGER,
                remote_url TEXT,
                title_id TEXT,
                app_id TEXT,
                name TEXT,
                content_type INTEGER NOT NULL DEFAULT 0,
                version INTEGER,
                original_filename TEXT,
                expected_size INTEGER,
                actual_size INTEGER NOT NULL DEFAULT 0,
                status INTEGER NOT NULL DEFAULT 0,
                downloaded_at TEXT,
                last_accessed_at TEXT,
                first_seen_at TEXT NOT NULL,
                pinned INTEGER NOT NULL DEFAULT 0,
                is_managed INTEGER NOT NULL DEFAULT 0,
                verification TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_library_app ON library_items(app_id);
            CREATE TABLE IF NOT EXISTS downloads (
                id TEXT PRIMARY KEY,
                server_key TEXT NOT NULL,
                item_json TEXT NOT NULL,
                partial_path TEXT NOT NULL,
                total_size INTEGER NOT NULL DEFAULT 0,
                downloaded_bytes INTEGER NOT NULL DEFAULT 0,
                etag TEXT,
                last_modified TEXT,
                status INTEGER NOT NULL,
                error TEXT,
                created_at TEXT NOT NULL
            );
            INSERT INTO schema_info(version) SELECT {SchemaVersion} WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<LocalGame>> GetLibraryItemsAsync(CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<LocalGame>>(async (c, ct) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT * FROM library_items ORDER BY id;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<LocalGame>();
            while (await reader.ReadAsync(ct)) list.Add(ReadGame(reader));
            return list;
        }, cancellationToken);

    public Task<LocalGame> UpsertLibraryItemAsync(LocalGame game, CancellationToken cancellationToken) =>
        WithConnectionAsync(async (c, ct) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO library_items (local_path, server_key, remote_file_id, remote_url, title_id, app_id, name, content_type,
                    version, original_filename, expected_size, actual_size, status, downloaded_at, last_accessed_at, first_seen_at,
                    pinned, is_managed, verification)
                VALUES ($path, $server, $fileId, $url, $titleId, $appId, $name, $type, $version, $orig, $expected, $actual, $status,
                    $downloaded, $accessed, $firstSeen, $pinned, $managed, $verification)
                ON CONFLICT(local_path) DO UPDATE SET
                    server_key = excluded.server_key, remote_file_id = excluded.remote_file_id, remote_url = excluded.remote_url,
                    title_id = excluded.title_id, app_id = excluded.app_id, name = excluded.name, content_type = excluded.content_type,
                    version = excluded.version, original_filename = excluded.original_filename, expected_size = excluded.expected_size,
                    actual_size = excluded.actual_size, status = excluded.status, downloaded_at = excluded.downloaded_at,
                    is_managed = excluded.is_managed, verification = excluded.verification;
                """;
            BindGame(cmd, game);
            await cmd.ExecuteNonQueryAsync(ct);
            return await SelectByPathAsync(c, game.LocalPath, ct) ?? game;
        }, cancellationToken);

    public Task<LocalGame> InsertLibraryItemIfMissingAsync(LocalGame game, CancellationToken cancellationToken) =>
        WithConnectionAsync(async (c, ct) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO library_items (local_path, server_key, remote_file_id, remote_url, title_id, app_id, name,
                    content_type, version, original_filename, expected_size, actual_size, status, downloaded_at, last_accessed_at,
                    first_seen_at, pinned, is_managed, verification)
                VALUES ($path, $server, $fileId, $url, $titleId, $appId, $name, $type, $version, $orig, $expected, $actual, $status,
                    $downloaded, $accessed, $firstSeen, $pinned, $managed, $verification);
                """;
            BindGame(cmd, game);
            await cmd.ExecuteNonQueryAsync(ct);
            return await SelectByPathAsync(c, game.LocalPath, ct) ?? game;
        }, cancellationToken);

    public Task UpdateLibraryItemStateAsync(long id, long actualSize, LocalGameStatus status, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE library_items SET actual_size = $a, status = $s WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$a", actualSize);
                cmd.Parameters.AddWithValue("$s", (int)status);
                cmd.Parameters.AddWithValue("$id", id);
            }, cancellationToken);

    public Task SetPinnedAsync(long id, bool pinned, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE library_items SET pinned = $p WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            }, cancellationToken);

    public Task SetLastAccessedAsync(long id, DateTimeOffset when, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE library_items SET last_accessed_at = $t WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", FormatDate(when));
                cmd.Parameters.AddWithValue("$id", id);
            }, cancellationToken);

    public Task UpdateLibraryItemPathAsync(long id, string localPath, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE library_items SET local_path = $p WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$p", localPath);
                cmd.Parameters.AddWithValue("$id", id);
            }, cancellationToken);

    public Task DeleteLibraryItemAsync(long id, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM library_items WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id), cancellationToken);

    public Task<IReadOnlyList<DownloadRecord>> GetDownloadsAsync(CancellationToken cancellationToken) =>
        WithConnectionAsync<IReadOnlyList<DownloadRecord>>(async (c, ct) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT * FROM downloads ORDER BY created_at;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<DownloadRecord>();
            while (await reader.ReadAsync(ct))
            {
                RemoteGameItem? item;
                try
                {
                    item = JsonSerializer.Deserialize<RemoteGameItem>(reader.GetString(reader.GetOrdinal("item_json")), JsonOptions);
                }
                catch (JsonException)
                {
                    item = null;
                }
                if (item is null) continue;

                list.Add(new DownloadRecord
                {
                    Id = reader.GetString(reader.GetOrdinal("id")),
                    ServerKey = reader.GetString(reader.GetOrdinal("server_key")),
                    Item = item,
                    PartialPath = reader.GetString(reader.GetOrdinal("partial_path")),
                    TotalSize = reader.GetInt64(reader.GetOrdinal("total_size")),
                    DownloadedBytes = reader.GetInt64(reader.GetOrdinal("downloaded_bytes")),
                    ETag = GetNullableString(reader, "etag"),
                    LastModified = GetNullableString(reader, "last_modified"),
                    Status = (DownloadStatus)reader.GetInt32(reader.GetOrdinal("status")),
                    Error = GetNullableString(reader, "error"),
                    CreatedAt = ParseDate(GetNullableString(reader, "created_at")) ?? DateTimeOffset.UtcNow,
                });
            }
            return list;
        }, cancellationToken);

    public Task SaveDownloadAsync(DownloadRecord record, CancellationToken cancellationToken) =>
        ExecuteAsync("""
            INSERT INTO downloads (id, server_key, item_json, partial_path, total_size, downloaded_bytes, etag, last_modified, status, error, created_at)
            VALUES ($id, $server, $item, $partial, $total, $bytes, $etag, $lm, $status, $error, $created)
            ON CONFLICT(id) DO UPDATE SET total_size = excluded.total_size, downloaded_bytes = excluded.downloaded_bytes,
                etag = excluded.etag, last_modified = excluded.last_modified, status = excluded.status, error = excluded.error;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", record.Id);
                cmd.Parameters.AddWithValue("$server", record.ServerKey);
                cmd.Parameters.AddWithValue("$item", JsonSerializer.Serialize(record.Item, JsonOptions));
                cmd.Parameters.AddWithValue("$partial", record.PartialPath);
                cmd.Parameters.AddWithValue("$total", record.TotalSize);
                cmd.Parameters.AddWithValue("$bytes", record.DownloadedBytes);
                cmd.Parameters.AddWithValue("$etag", (object?)record.ETag ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lm", (object?)record.LastModified ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$status", (int)record.Status);
                cmd.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$created", FormatDate(record.CreatedAt));
            }, cancellationToken);

    public Task DeleteDownloadAsync(string id, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM downloads WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id), cancellationToken);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task<T> WithConnectionAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            return await action(connection, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task ExecuteAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct) =>
        WithConnectionAsync(async (c, token) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            return await cmd.ExecuteNonQueryAsync(token);
        }, ct);

    private static async Task<LocalGame?> SelectByPathAsync(SqliteConnection c, string path, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM library_items WHERE local_path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadGame(reader) : null;
    }

    private static void BindGame(SqliteCommand cmd, LocalGame g)
    {
        cmd.Parameters.AddWithValue("$path", g.LocalPath);
        cmd.Parameters.AddWithValue("$server", (object?)g.ServerKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fileId", (object?)g.RemoteFileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)g.RemoteUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$titleId", (object?)g.TitleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$appId", (object?)g.AppId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)g.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", (int)g.ContentType);
        cmd.Parameters.AddWithValue("$version", (object?)g.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$orig", (object?)g.OriginalFileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expected", (object?)g.ExpectedSize ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$actual", g.ActualSize);
        cmd.Parameters.AddWithValue("$status", (int)g.Status);
        cmd.Parameters.AddWithValue("$downloaded", (object?)FormatDate(g.DownloadedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$accessed", (object?)FormatDate(g.LastAccessedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$firstSeen", FormatDate(g.FirstSeenAt));
        cmd.Parameters.AddWithValue("$pinned", g.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$managed", g.IsManaged ? 1 : 0);
        cmd.Parameters.AddWithValue("$verification", (object?)g.Verification ?? DBNull.Value);
    }

    private static LocalGame ReadGame(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        LocalPath = r.GetString(r.GetOrdinal("local_path")),
        ServerKey = GetNullableString(r, "server_key"),
        RemoteFileId = GetNullableLong(r, "remote_file_id"),
        RemoteUrl = GetNullableString(r, "remote_url"),
        TitleId = GetNullableString(r, "title_id"),
        AppId = GetNullableString(r, "app_id"),
        Name = GetNullableString(r, "name"),
        ContentType = (ContentType)r.GetInt32(r.GetOrdinal("content_type")),
        Version = GetNullableLong(r, "version"),
        OriginalFileName = GetNullableString(r, "original_filename"),
        ExpectedSize = GetNullableLong(r, "expected_size"),
        ActualSize = r.GetInt64(r.GetOrdinal("actual_size")),
        Status = (LocalGameStatus)r.GetInt32(r.GetOrdinal("status")),
        DownloadedAt = ParseDate(GetNullableString(r, "downloaded_at")),
        LastAccessedAt = ParseDate(GetNullableString(r, "last_accessed_at")),
        FirstSeenAt = ParseDate(GetNullableString(r, "first_seen_at")) ?? DateTimeOffset.UtcNow,
        IsPinned = r.GetInt64(r.GetOrdinal("pinned")) != 0,
        IsManaged = r.GetInt64(r.GetOrdinal("is_managed")) != 0,
        Verification = GetNullableString(r, "verification"),
    };

    private static string? GetNullableString(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long? GetNullableLong(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    private static string FormatDate(DateTimeOffset d) => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatDate(DateTimeOffset? d) => d is { } v ? FormatDate(v) : null;

    private static DateTimeOffset? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
