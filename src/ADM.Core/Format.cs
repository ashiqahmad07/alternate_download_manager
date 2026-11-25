namespace ADM.Core;

public static class Format
{
    static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(double bytes)
    {
        int unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:0} B" : $"{bytes:0.##} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Eta(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{Math.Max(0, t.Seconds)}s";
    }
}
