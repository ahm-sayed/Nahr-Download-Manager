using System.Globalization;

namespace DownloadManager.Presentation.Formatting;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{Math.Max(0, bytes)} B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "—" : Bytes((long)bytesPerSecond) + "/s";

    public static string Eta(TimeSpan? eta)
    {
        if (eta is not { } t || t < TimeSpan.Zero) return "—";
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours:00}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))}s";
    }

    public static string Percent(double percent) => Math.Clamp(percent, 0, 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string DetailedBytes(long bytes)
    {
        if (bytes < 1024) return $"{Math.Max(0, bytes)} B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("0.00", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string DetailedPercent(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return clamped >= 99.995
            ? "100 %"
            : clamped.ToString("0.00", CultureInfo.InvariantCulture) + " %";
    }

    public static string DetailedSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1) return "—";
        if (bytesPerSecond < 1024) return $"{(long)bytesPerSecond} B/sec";
        double value = bytesPerSecond;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var decimals = unit >= 2 ? "0.000" : "0.0";
        return value.ToString(decimals, CultureInfo.InvariantCulture) + " " + Units[unit] + "/sec";
    }

    public static string DetailedEta(TimeSpan? eta)
    {
        if (eta is not { } t || t < TimeSpan.Zero) return "—";
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} hr {t.Minutes} min";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} hr {t.Minutes} min {t.Seconds} sec";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min {t.Seconds} sec";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} sec";
    }
}
