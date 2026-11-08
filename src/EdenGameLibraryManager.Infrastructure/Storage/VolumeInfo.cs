namespace EdenGameLibraryManager.Infrastructure.Storage;

/// <summary>
/// Cross-platform volume helpers. On Windows the path root ("D:\") identifies the volume; on
/// macOS and Linux every path shares the root "/", so the longest matching mount point is used.
/// </summary>
public static class VolumeInfo
{
    public static string? GetVolumeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
                return Path.GetPathRoot(full);

            string? best = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                var root = drive.RootDirectory.FullName;
                var prefix = root.EndsWith('/') ? root : root + "/";
                if ((full == root || full.StartsWith(prefix, StringComparison.Ordinal)) && (best is null || root.Length > best.Length))
                    best = root;
            }
            return best ?? "/";
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsSameVolume(string a, string b)
    {
        var ra = GetVolumeRoot(a);
        var rb = GetVolumeRoot(b);
        if (ra is null || rb is null) return false; // unknown: treat as different so the safe copy path is used
        return string.Equals(ra, rb, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static (long Free, long Total)? GetSpace(string path)
    {
        try
        {
            var root = GetVolumeRoot(path);
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
