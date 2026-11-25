namespace ADM.Core;

/// <summary>
/// Counts how often a download taken from the browser was cancelled, so the second cancel of the
/// same address can offer to stop capturing it.
/// </summary>
public sealed class CaptureCancels
{
    const int MaxTracked = 1000;

    readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Records a cancel; true on the second one for this address, which also starts its count again.</summary>
    public bool Cancelled(string url)
    {
        if (_counts.Remove(url)) return true;
        // Addresses cancelled once are only worth remembering for a while.
        if (_counts.Count >= MaxTracked) _counts.Clear();
        _counts[url] = 1;
        return false;
    }
}
