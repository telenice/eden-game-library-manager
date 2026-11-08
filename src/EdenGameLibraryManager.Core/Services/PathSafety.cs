using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Core.Services;

/// <summary>Filename and path rules that keep downloads and deletions inside the managed directories.</summary>
public static class PathSafety
{
    public static readonly IReadOnlySet<string> GameExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nsp", "nsz", "xci", "xcz" };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Windows rules are applied on every OS so a library folder can be shared between machines.
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private const int MaxFileNameLength = 180;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsGameFile(string path) =>
        GameExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary>
    /// Turns a server-supplied file name into a safe local file name. Names containing path
    /// separators or traversal segments are rejected outright rather than silently rewritten.
    /// </summary>
    public static string SanitizeGameFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new UnsafePathException("The server did not provide a file name for this item.");

        var trimmed = name.Trim();
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains(':') || trimmed is "." or "..")
            throw new UnsafePathException($"Unsafe file name from server: '{name}'.");

        var chars = trimmed.Select(c => c < 32 || InvalidChars.Contains(c) ? '_' : c).ToArray();
        var clean = new string(chars).TrimEnd('.', ' ').TrimStart(' ');

        var ext = Path.GetExtension(clean).TrimStart('.');
        if (!GameExtensions.Contains(ext))
            throw new UnsafePathException($"'{name}' is not a supported game file (.nsp, .nsz, .xci, .xcz).");

        var stem = Path.GetFileNameWithoutExtension(clean).TrimEnd('.', ' ');
        if (stem.Length == 0 || stem.Trim('.').Length == 0)
            throw new UnsafePathException($"Unsafe file name from server: '{name}'.");

        var firstSegment = stem.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(firstSegment))
            stem = "_" + stem;

        if (stem.Length + ext.Length + 1 > MaxFileNameLength)
            stem = stem[..(MaxFileNameLength - ext.Length - 1)].TrimEnd('.', ' ');

        return $"{stem}.{ext.ToLowerInvariant()}";
    }

    /// <summary>
    /// Turns a game name into a single safe folder name: separators and invalid characters become
    /// '_', so the result can never contain path segments; reserved device names are prefixed.
    /// </summary>
    public static string SanitizeFolderName(string? name, int maxLength = 100)
    {
        var chars = (name ?? string.Empty).Trim().Select(c => c < 32 || InvalidChars.Contains(c) ? '_' : c).ToArray();
        // Leading dots would make a hidden folder on macOS/Linux; trailing dots/spaces are invalid on Windows.
        var clean = new string(chars).Trim().Trim('.', ' ');
        if (clean.Length > maxLength) clean = clean[..maxLength].TrimEnd('.', ' ');
        if (clean.Trim('.', '_', ' ').Length == 0) return "Unknown game";
        if (ReservedNames.Contains(clean.Split('.')[0].TrimEnd(' '))) clean = "_" + clean;
        return clean;
    }

    public static string NormalizeDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    public static bool IsWithinDirectory(string root, string candidate)
    {
        var rootFull = NormalizeDirectory(root);
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(rootFull, PathComparison) && full.Length > rootFull.Length;
    }

    public static string EnsureWithinDirectory(string root, string candidate)
    {
        if (!IsWithinDirectory(root, candidate))
            throw new UnsafePathException($"'{candidate}' is outside the managed directory '{root}'.");
        return Path.GetFullPath(candidate);
    }

    /// <summary>
    /// True when the file or any directory between it and <paramref name="root"/> is a symbolic
    /// link, junction or other reparse point (which could redirect writes or deletes elsewhere).
    /// </summary>
    public static bool HasReparsePoint(string root, string path)
    {
        var rootFull = NormalizeDirectory(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(path);
        while (current.Length > rootFull.Length && current.StartsWith(rootFull, PathComparison))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
        return false;
    }

    /// <summary>Returns a path in <paramref name="directory"/> that does not overwrite an existing file.</summary>
    public static string GetAvailablePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new UnsafePathException($"Could not find a free file name for '{fileName}'.");
    }
}
