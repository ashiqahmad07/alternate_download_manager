namespace ADM.Core;

/// <summary>Reads an .ico file's directory, to find the image Windows should draw at a given size.</summary>
public static class IconFile
{
    const int HeaderSize = 6;
    const int EntrySize = 16;

    /// <summary>
    /// Where the image for <paramref name="size"/> pixels square lies in the file: the one of that size, else the
    /// smallest larger one, else the largest; between images of one size, the one with the most colours.
    /// Null when the file isn't an icon or none of its images lies inside it.
    /// </summary>
    public static (int Offset, int Length)? Pick(ReadOnlySpan<byte> file, int size)
    {
        if (file.Length < HeaderSize || U16(file, 0) != 0 || U16(file, 2) != 1) return null;
        int count = U16(file, 4);
        (int Offset, int Length)? best = null;
        int bestSize = 0, bestBits = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = HeaderSize + i * EntrySize;
            if (entry + EntrySize > file.Length) break;
            // In the width's one byte, 0 means 256.
            int width = file[entry] == 0 ? 256 : file[entry];
            int bits = U16(file, entry + 6);
            long length = U32(file, entry + 8), offset = U32(file, entry + 12);
            if (length == 0 || offset + length > file.Length) continue;
            if (best == null || Better(width, bits, bestSize, bestBits, size))
                (best, bestSize, bestBits) = (((int)offset, (int)length), width, bits);
        }
        return best;
    }

    static bool Better(int width, int bits, int bestWidth, int bestBits, int size)
    {
        int rank = Rank(width, size), bestRank = Rank(bestWidth, size);
        if (rank != bestRank) return rank < bestRank;
        // Larger ones: the nearest is shrunk least. Smaller ones: the nearest is stretched least.
        if (width != bestWidth) return rank == 1 ? width < bestWidth : width > bestWidth;
        return bits > bestBits;
    }

    // The size itself first, then larger ones, which shrink cleanly, then smaller ones.
    static int Rank(int width, int size) => width == size ? 0 : width > size ? 1 : 2;

    static int U16(ReadOnlySpan<byte> bytes, int at) => bytes[at] | bytes[at + 1] << 8;

    static long U32(ReadOnlySpan<byte> bytes, int at) => (uint)(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24);
}
