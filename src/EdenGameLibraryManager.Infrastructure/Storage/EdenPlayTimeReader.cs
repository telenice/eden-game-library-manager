using System.Buffers.Binary;
using System.Globalization;
using EdenGameLibraryManager.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Storage;

/// <summary>
/// Reads Eden's play-time file, <c>&lt;Eden data&gt;/play_time/playtime.bin</c>: a list of 16-byte records,
/// each a little-endian u64 program id followed by a u64 count of seconds played (the format Eden
/// inherited from yuzu). The file is only ever read; Eden's data is never changed.
/// </summary>
public sealed class EdenPlayTimeReader(ILogger<EdenPlayTimeReader> logger) : IPlayTimeService
{
    private const int RecordSize = 16;
    private const long MaxFileBytes = 1024 * 1024;

    private (string Path, DateTime WriteTime, IReadOnlyDictionary<string, TimeSpan> Times)? _cached;

    /// <summary>Overrides the file location (tests). By default Eden's per-user data folder is used.</summary>
    internal string? FilePathOverride { get; init; }

    /// <summary>Eden's default data folder: %APPDATA%\eden, ~/Library/Application Support/eden or $XDG_DATA_HOME/eden.</summary>
    public static string DefaultFilePath
    {
        get
        {
            var root = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(root, "eden", "play_time", "playtime.bin");
        }
    }

    public Task<IReadOnlyDictionary<string, TimeSpan>?> GetPlayTimesAsync(CancellationToken cancellationToken) =>
        Task.Run(Read, cancellationToken);

    private IReadOnlyDictionary<string, TimeSpan>? Read()
    {
        var path = FilePathOverride ?? DefaultFilePath;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (_cached is { } c && c.Path == path && c.WriteTime == info.LastWriteTimeUtc) return c.Times;
            if (info.Length > MaxFileBytes) return null;

            var times = Parse(File.ReadAllBytes(path));
            _cached = (path, info.LastWriteTimeUtc, times);
            return times;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Eden may be writing the file right now; the next refresh will pick it up.
            logger.LogDebug("Eden play time unavailable: {Error}", ex.Message);
            return _cached?.Times;
        }
    }

    /// <summary>Parses the records; a trailing partial record is ignored. Keys are upper-case title ids.</summary>
    internal static IReadOnlyDictionary<string, TimeSpan> Parse(ReadOnlySpan<byte> data)
    {
        var times = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset + RecordSize <= data.Length; offset += RecordSize)
        {
            var programId = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            var seconds = BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 8)..]);
            if (programId == 0 || seconds > (ulong)TimeSpan.MaxValue.TotalSeconds) continue;
            var id = programId.ToString("X16", CultureInfo.InvariantCulture);
            times[id] = (times.TryGetValue(id, out var existing) ? existing : TimeSpan.Zero) + TimeSpan.FromSeconds(seconds);
        }
        return times;
    }
}
