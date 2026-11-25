using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>Checking a finished file against the SHA-256 a page gives for it.</summary>
public static class FileHash
{
    const int BufferSize = 1024 * 1024;

    /// <summary>
    /// A SHA-256 as lower-case hex, from what people paste: capitals, spaces around it, or a sha256sum line with a
    /// file name after it. Null if it isn't one. MD5 and SHA-1 count as that too, since a match on either says little.
    /// </summary>
    public static string? Normalize(string? text)
    {
        var hash = Regex.Match(text ?? "", @"^\s*([0-9a-fA-F]{64})(?:\s|$)");
        return hash.Success ? hash.Groups[1].Value.ToLowerInvariant() : null;
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }
}
