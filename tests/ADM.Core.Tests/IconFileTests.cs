namespace ADM.Core.Tests;

/// <summary>Which of an icon file's images the tray asks Windows to draw.</summary>
public sealed class IconFileTests
{
    /// <summary>An icon file with an image of each (width, bits), each image's bytes its own index repeated.</summary>
    static byte[] Icon(params (int Width, int Bits)[] images)
    {
        var file = new List<byte> { 0, 0, 1, 0, (byte)images.Length, 0 };
        int offset = 6 + images.Length * 16;
        foreach (var (width, bits) in images)
        {
            file.AddRange(new byte[] { (byte)(width == 256 ? 0 : width), (byte)(width == 256 ? 0 : width), 0, 0, 1, 0, (byte)bits, 0 });
            file.AddRange(BitConverter.GetBytes(10));
            file.AddRange(BitConverter.GetBytes(offset));
            offset += 10;
        }
        for (int i = 0; i < images.Length; i++) file.AddRange(Enumerable.Repeat((byte)i, 10));
        return file.ToArray();
    }

    /// <summary>Which image <see cref="IconFile.Pick"/> chose, by its place in the directory.</summary>
    static int? Picked(byte[] file, int size) => IconFile.Pick(file, size) is (var offset, _) ? file[offset] : null;

    [Fact]
    public void The_size_asked_for_is_taken_when_the_file_has_it()
    {
        var file = Icon((32, 32), (16, 32), (24, 32));
        Assert.Equal(1, Picked(file, 16));
        Assert.Equal(2, Picked(file, 24));
        Assert.Equal(0, Picked(file, 32));
    }

    [Fact]
    public void Without_it_the_nearest_larger_one_then_the_largest()
    {
        var file = Icon((48, 32), (16, 32), (24, 32));
        Assert.Equal(2, Picked(file, 20));
        Assert.Equal(0, Picked(file, 40));
        // Nothing as large: the largest, stretched least.
        Assert.Equal(0, Picked(file, 64));
    }

    [Fact]
    public void A_width_of_0_means_256()
    {
        var file = Icon((64, 32), (256, 32));
        Assert.Equal(1, Picked(file, 256));
        Assert.Equal(1, Picked(file, 128));
        Assert.Equal(0, Picked(file, 48));
    }

    [Fact]
    public void Of_one_size_the_one_with_the_most_colours()
    {
        Assert.Equal(1, Picked(Icon((16, 8), (16, 32), (16, 4)), 16));
        Assert.Equal(0, Picked(Icon((16, 32), (16, 8)), 16));
    }

    [Fact]
    public void An_image_outside_the_file_is_passed_over()
    {
        var file = Icon((16, 32), (24, 32));
        // The 16's image starts past the end.
        BitConverter.GetBytes(file.Length).CopyTo(file, 6 + 12);
        Assert.Equal(1, Picked(file, 16));
        // Its length runs past the end.
        BitConverter.GetBytes(6 + 32).CopyTo(file, 6 + 12);
        BitConverter.GetBytes(file.Length).CopyTo(file, 6 + 8);
        Assert.Equal(1, Picked(file, 16));
        // Empty.
        BitConverter.GetBytes(0).CopyTo(file, 6 + 8);
        Assert.Equal(1, Picked(file, 16));
    }

    [Fact]
    public void A_file_that_is_not_an_icon_gives_nothing()
    {
        Assert.Null(IconFile.Pick(Array.Empty<byte>(), 16));
        Assert.Null(IconFile.Pick(new byte[] { 0, 0, 1 }, 16));
        // A cursor file, type 2.
        var cursor = Icon((16, 32));
        cursor[2] = 2;
        Assert.Null(IconFile.Pick(cursor, 16));
        // The first field, which an icon file keeps 0.
        var other = Icon((16, 32));
        other[0] = 1;
        Assert.Null(IconFile.Pick(other, 16));
        // A directory that claims more entries than it holds.
        var cut = Icon((16, 32))[..10];
        Assert.Null(IconFile.Pick(cut, 16));
        // No image at all.
        Assert.Null(IconFile.Pick(new byte[] { 0, 0, 1, 0, 0, 0 }, 16));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void The_app_icon_has_the_tray_size_at_each_scale(int size)
    {
        // The notification area's icon is 16 at 100%, 20 at 125%, 24 at 150% and 32 at 200%.
        var file = File.ReadAllBytes(Path.Combine(AppIconTests.AppFolder, "Assets", "ADM.ico"));
        var (offset, length) = IconFile.Pick(file, size)!.Value;
        // Each image is a PNG, which Windows reads at its own size.
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, file[offset..(offset + 4)]);
        Assert.Equal(size, Png.Width(file.AsSpan(offset, length)));
    }

    static class Png
    {
        // The IHDR chunk's width, big-endian, right after the 8-byte signature and the chunk's length and type.
        public static int Width(ReadOnlySpan<byte> png) => png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
    }
}
