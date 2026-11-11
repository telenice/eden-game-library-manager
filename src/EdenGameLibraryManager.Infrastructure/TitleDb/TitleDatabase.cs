using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.TitleDb;

/// <summary>
/// Local copy of titledb (github.com/blawar/titledb). The region file is about 90 MB of JSON (about
/// 26 MB gzipped), so it is streamed entry by entry into a small SQLite database that keeps only the
/// fields the app shows, then refreshed weekly with If-None-Match. The JSON is never held in memory
/// as a whole. Every request goes to a public URL without credentials.
/// </summary>
public sealed class TitleDatabase(HttpClient http, ISettingsService settings, AppPaths paths, ILogger<TitleDatabase> logger) : ITitleDatabase
{
    public const string SourceUrlFormat = "https://raw.githubusercontent.com/blawar/titledb/master/{0}.json";

    public static readonly IReadOnlyList<string> Regions =
    [
        "US.en", "GB.en", "AU.en", "CA.en", "NZ.en", "ZA.en", "CA.fr", "FR.fr", "DE.de", "ES.es", "MX.es",
        "IT.it", "NL.nl", "BR.pt", "RU.ru", "JP.ja", "KR.ko", "HK.zh",
    ];

    private const long MaxJsonBytes = 1024L * 1024 * 1024;
    private const int MaxEntryBytes = 16 * 1024 * 1024;
    private const int LookupChunk = 400;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    // Held while reading the database and while a refresh swaps the file in, since Windows can't
    // replace a file that is open.
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private volatile TitleDbStatus? _status;
    private volatile string? _lastError;

    internal TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public event EventHandler? Updated;

    public static string NormalizeRegion(string? region) =>
        Regions.FirstOrDefault(r => string.Equals(r, region?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Regions[0];

    public TitleDbStatus GetStatus()
    {
        var status = _status ??= ReadStatus();
        return status with { LastError = _lastError };
    }

    public async Task<IReadOnlyDictionary<string, TitleInfo>> LookupAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, TitleInfo>(StringComparer.OrdinalIgnoreCase);
        if (!settings.Current.UseTitleDb) return result;

        var wanted = ids.Select(TitleIdHelper.Normalize).Where(TitleIdHelper.IsValidId).Distinct().ToList();
        if (wanted.Count == 0) return result;

        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.TitleDbFile)) return result;
            await using var connection = Open(paths.TitleDbFile, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(cancellationToken);
            foreach (var chunk in wanted.Chunk(LookupChunk))
            {
                await using var command = connection.CreateCommand();
                var names = chunk.Select((id, i) =>
                {
                    command.Parameters.AddWithValue($"$p{i}", id);
                    return $"$p{i}";
                });
                command.CommandText =
                    "SELECT id, name, publisher, description, icon_url, screenshots, release_date, category " +
                    $"FROM titles WHERE id IN ({string.Join(',', names)});";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var info = ReadTitle(reader);
                    result[info.Id] = info;
                }
            }
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "titledb lookup failed");
        }
        finally
        {
            _fileGate.Release();
        }
        return result;
    }

    public async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        var current = settings.Current;
        if (!current.UseTitleDb) return;
        var region = NormalizeRegion(current.TitleDbRegion);

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var status = GetStatus();
            var sameRegion = status.IsAvailable && status.Region == region;
            if (!force && sameRegion && status.UpdatedAt is { } updated && DateTimeOffset.UtcNow - updated < MaxAge)
                return;

            var url = string.Format(CultureInfo.InvariantCulture, SourceUrlFormat, region);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            var etag = sameRegion ? ReadMeta("etag") : null;
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            logger.LogInformation("Downloading titledb {Region}", region);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (HttpRequestException ex)
            {
                throw AeroFoilResponses.FromHttpException(ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    await WriteMetaAsync("updated_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
                    logger.LogInformation("titledb {Region} is up to date", region);
                    _lastError = null;
                    return;
                }
                if (!response.IsSuccessStatusCode)
                    throw new AeroFoilException(AeroFoilErrorKind.ServerError, $"GitHub answered HTTP {(int)response.StatusCode} for {region}.json.");

                var temp = paths.TitleDbFile + ".tmp";
                TryDelete(temp);
                int count;
                await using (var raw = await response.Content.ReadAsStreamAsync(timeout.Token))
                await using (var json = response.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase)
                                 ? new GZipStream(raw, CompressionMode.Decompress)
                                 : raw)
                {
                    count = await BuildDatabaseAsync(json, temp, region, response.Headers.ETag?.ToString(), timeout.Token);
                }
                if (count == 0)
                {
                    TryDelete(temp);
                    throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, $"{region}.json contained no titles.");
                }

                await _fileGate.WaitAsync(cancellationToken);
                try
                {
                    File.Move(temp, paths.TitleDbFile, overwrite: true);
                }
                finally
                {
                    _fileGate.Release();
                }
                logger.LogInformation("titledb {Region} saved: {Count} titles", region, count);
            }

            _status = null;
            _lastError = null;
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastError = ex is OperationCanceledException ? "Downloading titledb took too long." : ex.Message;
            logger.LogWarning("titledb refresh failed: {Error}", ex.Message);
        }
        finally
        {
            TryDelete(paths.TitleDbFile + ".tmp");
            _refreshGate.Release();
        }
    }

    private async Task<int> BuildDatabaseAsync(Stream json, string file, string region, string? etag, CancellationToken ct)
    {
        await using var connection = Open(file, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(ct);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                PRAGMA journal_mode = OFF;
                PRAGMA synchronous = OFF;
                CREATE TABLE titles (
                    id TEXT PRIMARY KEY, name TEXT, publisher TEXT, description TEXT, icon_url TEXT,
                    screenshots TEXT, release_date TEXT, category TEXT);
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
                """;
            await schema.ExecuteNonQueryAsync(ct);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        // titledb lists some ids more than once (bundles, regional duplicates); keep the entry with artwork.
        insert.CommandText = """
            INSERT INTO titles (id, name, publisher, description, icon_url, screenshots, release_date, category)
            VALUES ($id, $name, $publisher, $description, $icon, $screenshots, $release, $category)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, publisher = excluded.publisher,
                description = excluded.description, icon_url = excluded.icon_url,
                screenshots = excluded.screenshots, release_date = excluded.release_date, category = excluded.category
            WHERE titles.icon_url IS NULL AND excluded.icon_url IS NOT NULL;
            """;
        var parameters = new[] { "$id", "$name", "$publisher", "$description", "$icon", "$screenshots", "$release", "$category" }
            .Select(n => insert.Parameters.Add(n, SqliteType.Text)).ToArray();
        insert.Prepare();

        var count = 0;
        await ParseAsync(json, info =>
        {
            object?[] values =
            [
                info.Id, info.Name, info.Publisher, info.Description, info.IconUrl,
                info.ScreenshotUrls.Count == 0 ? null : JsonSerializer.Serialize(info.ScreenshotUrls),
                info.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), info.Category,
            ];
            for (var i = 0; i < values.Length; i++) parameters[i].Value = values[i] ?? DBNull.Value;
            insert.ExecuteNonQuery();
            count++;
        }, ct);

        await using (var meta = connection.CreateCommand())
        {
            meta.Transaction = transaction;
            meta.CommandText = "INSERT INTO meta (key, value) VALUES ('region', $region), ('etag', $etag), ('updated_at', $updated);";
            meta.Parameters.AddWithValue("$region", region);
            meta.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
            meta.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await meta.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return count;
    }

    /// <summary>
    /// Streams a titledb region file (one JSON object keyed by eShop id) and reports each entry that
    /// has a valid application id. Only one entry is buffered at a time.
    /// </summary>
    internal static async Task ParseAsync(Stream stream, Action<TitleInfo> onEntry, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        var length = 0;
        var state = new JsonReaderState();
        var final = false;
        var finished = false;
        long total = 0;

        while (true)
        {
            if (!final)
            {
                if (length == buffer.Length)
                {
                    if (buffer.Length >= MaxEntryBytes)
                        throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "titledb contains an entry that is too large.");
                    Array.Resize(ref buffer, buffer.Length * 2);
                }
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (read == 0) final = true;
                length += read;
                total += read;
                if (total > MaxJsonBytes)
                    throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "titledb is larger than expected.");
            }

            var consumed = ParseChunk(buffer.AsSpan(0, length), final, ref state, ref finished, onEntry);
            Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
            length -= consumed;

            if (final)
            {
                if (!finished) throw new JsonException("titledb ended unexpectedly.");
                return;
            }
        }
    }

    private static int ParseChunk(ReadOnlySpan<byte> data, bool final, ref JsonReaderState state, ref bool finished, Action<TitleInfo> onEntry)
    {
        var reader = new Utf8JsonReader(data, final, state);
        while (true)
        {
            var checkpoint = reader;
            if (!reader.Read()) break;

            if (reader.CurrentDepth == 0)
            {
                if (reader.TokenType == JsonTokenType.EndObject) finished = true;
                else if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("titledb is not a JSON object.");
                continue;
            }

            if (reader.CurrentDepth != 1 || reader.TokenType == JsonTokenType.PropertyName) continue;
            if (reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) continue;

            // Only parse an entry once all of it is in the buffer; otherwise rewind and read more.
            var probe = reader;
            if (!probe.TrySkip())
            {
                reader = checkpoint;
                break;
            }
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                reader.TrySkip(); // Succeeds: the probe above already skipped the same bytes.
                continue;
            }
            using var entry = JsonDocument.ParseValue(ref reader);
            if (ReadEntry(entry.RootElement) is { } info) onEntry(info);
        }
        state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    internal static TitleInfo? ReadEntry(JsonElement e)
    {
        var id = TitleIdHelper.Normalize(String(e, "id"));
        if (!TitleIdHelper.IsValidId(id)) return null;

        var screenshots = e.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array
            ? shots.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String && UrlPolicy.IsPublicArtworkUrl(s.GetString()))
                .Select(s => s.GetString()!)
                .ToList()
            : [];

        DateOnly? release = null;
        if (e.TryGetProperty("releaseDate", out var rd) && rd.ValueKind == JsonValueKind.Number && rd.TryGetInt32(out var ymd) &&
            DateOnly.TryParseExact(ymd.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            release = date;

        string? category = null;
        if (e.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Array)
        {
            var names = cat.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).Distinct().Take(3).ToList();
            if (names.Count > 0) category = string.Join(", ", names);
        }

        return new TitleInfo
        {
            Id = id!,
            Name = String(e, "name")?.Trim(),
            Publisher = String(e, "publisher"),
            Description = String(e, "description")?.Trim(),
            IconUrl = PublicUrl(String(e, "iconUrl")),
            ScreenshotUrls = screenshots,
            ReleaseDate = release,
            Category = category,
        };
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static string? PublicUrl(string? url) => UrlPolicy.IsPublicArtworkUrl(url) ? url : null;

    private static TitleInfo ReadTitle(SqliteDataReader r)
    {
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var screenshots = Text(5) is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
        return new TitleInfo
        {
            Id = r.GetString(0),
            Name = Text(1),
            Publisher = Text(2),
            Description = Text(3),
            IconUrl = Text(4),
            ScreenshotUrls = screenshots,
            ReleaseDate = DateOnly.TryParseExact(Text(6), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            Category = Text(7),
        };
    }

    private TitleDbStatus ReadStatus()
    {
        if (!File.Exists(paths.TitleDbFile)) return new TitleDbStatus(false, null, 0, null, null);
        _fileGate.Wait();
        try
        {
            using var connection = Open(paths.TitleDbFile, SqliteOpenMode.ReadOnly);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT (SELECT value FROM meta WHERE key = 'region'), (SELECT value FROM meta WHERE key = 'updated_at'),
                       (SELECT COUNT(*) FROM titles);
                """;
            using var reader = command.ExecuteReader();
            reader.Read();
            var region = reader.IsDBNull(0) ? null : reader.GetString(0);
            DateTimeOffset? updated = !reader.IsDBNull(1) &&
                DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var u) ? u : null;
            return new TitleDbStatus(true, region, reader.GetInt32(2), updated, null);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "titledb copy is unreadable; it will be downloaded again");
            return new TitleDbStatus(false, null, 0, null, null);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private string? ReadMeta(string key)
    {
        _fileGate.Wait();
        try
        {
            using var connection = Open(paths.TitleDbFile, SqliteOpenMode.ReadOnly);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private async Task WriteMetaAsync(string key, string value, CancellationToken ct)
    {
        await _fileGate.WaitAsync(ct);
        try
        {
            await using var connection = Open(paths.TitleDbFile, SqliteOpenMode.ReadWrite);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _fileGate.Release();
        }
        _status = null;
    }

    private static SqliteConnection Open(string file, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = file,
        Mode = mode,
        Pooling = false,
    }.ToString());

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
