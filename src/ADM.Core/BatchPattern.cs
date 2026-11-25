using System.Globalization;

namespace ADM.Core;

/// <summary>Expands "http://host/img*.jpg" over a number or letter range into a batch of URLs.</summary>
public static class BatchPattern
{
    public const int MaxFiles = 10_000;

    /// <param name="width">Zero-pads numbers to this many digits (3 gives 001); 0 adds no padding.</param>
    public static List<string> Expand(string pattern, string from, string to, int width)
    {
        int star = pattern.IndexOf('*');
        if (star < 0 || pattern.IndexOf('*', star + 1) >= 0)
            throw new FormatException("The address must contain exactly one asterisk (*).");
        from = from.Trim();
        to = to.Trim();

        List<string> values;
        if (long.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            && long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out var last))
        {
            CheckRange(last - first + 1);
            values = Enumerable.Range(0, (int)(last - first + 1))
                .Select(i => (first + i).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'))
                .ToList();
        }
        else if (from.Length == 1 && to.Length == 1
            && (char.IsAsciiLetterLower(from[0]) && char.IsAsciiLetterLower(to[0])
                || char.IsAsciiLetterUpper(from[0]) && char.IsAsciiLetterUpper(to[0])))
        {
            CheckRange(to[0] - from[0] + 1);
            values = Enumerable.Range(from[0], to[0] - from[0] + 1).Select(c => ((char)c).ToString()).ToList();
        }
        else
        {
            throw new FormatException("Use two numbers, or two single letters of the same case, for the range.");
        }

        return values.Select(v => pattern[..star] + v + pattern[(star + 1)..]).ToList();
    }

    static void CheckRange(long count)
    {
        if (count < 1) throw new FormatException("The range must run from the smaller value to the larger one.");
        if (count > MaxFiles) throw new FormatException($"A batch can hold at most {MaxFiles:N0} files.");
    }
}
