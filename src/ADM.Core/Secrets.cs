using System.Security.Cryptography;
using System.Text;

namespace ADM.Core;

/// <summary>
/// Encrypts passwords for state.json with Windows DPAPI, so only the same Windows user can read them back.
/// The stored form is "dpapi:" followed by base64.
/// </summary>
public static class Secrets
{
    const string Prefix = "dpapi:";

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        // Never fall back to plain text on a platform without DPAPI.
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved passwords need Windows DPAPI.");
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
    }

    /// <summary>Returns null for a value that is not ours or cannot be decrypted, e.g. one saved by another Windows user.</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) return null;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
