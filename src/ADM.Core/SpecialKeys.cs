namespace ADM.Core;

/// <summary>
/// Key combinations such as "Ctrl+Alt" that, held while clicking a link, prevent or force a
/// browser capture. The extension reports held keys by these lower-case names.
/// </summary>
public static class SpecialKeys
{
    public static readonly IReadOnlyList<string> Names = new[] { "ctrl", "alt", "shift", "insert" };

    /// <summary>The keys of a combination in a fixed order; empty for none, null when a name is unknown.</summary>
    public static string[]? Parse(string? combo)
    {
        var keys = (combo ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLowerInvariant())
            .ToList();
        if (keys.Any(k => !Names.Contains(k))) return null;
        return Names.Where(keys.Contains).ToArray();
    }

    public static string Format(IEnumerable<string> keys) =>
        string.Join("+", keys.Select(k => char.ToUpperInvariant(k[0]) + k[1..]));

    /// <summary>True when every key of a non-empty combination is held.</summary>
    public static bool AllHeld(IReadOnlyCollection<string> combo, IReadOnlyCollection<string> held) =>
        combo.Count > 0 && combo.All(held.Contains);

    /// <summary>
    /// True when the prevent keys are all part of the force keys. Prevent wins when both are held,
    /// so such a force combination could never apply.
    /// </summary>
    public static bool Conflict(string? prevent, string? force)
    {
        var p = Parse(prevent) ?? Array.Empty<string>();
        var f = Parse(force) ?? Array.Empty<string>();
        return p.Length > 0 && p.All(f.Contains);
    }
}
