namespace ADM.Core;

/// <summary>The CRC-32 that ZIP uses: start from <see cref="Initial"/>, <see cref="Update"/> with each piece, then take <see cref="Final"/>.</summary>
internal static class Crc32
{
    public const uint Initial = 0xFFFFFFFF;

    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Final(uint crc) => ~crc;
}
