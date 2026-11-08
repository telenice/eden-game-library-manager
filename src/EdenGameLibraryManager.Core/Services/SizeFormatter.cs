using System.Globalization;

namespace EdenGameLibraryManager.Core.Services;

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "-" : Format((long)bytesPerSecond) + "/s";

    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is not { } t || t < TimeSpan.Zero || t.TotalDays > 30) return "-";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{Math.Max(1, t.Seconds)}s";
    }
}
