using ADM.Core;

namespace ADM.Core.Tests;

public class BatchPatternTests
{
    [Fact]
    public void Expands_numbers_with_zero_padding() =>
        Assert.Equal(
            new[] { "http://x.com/img001.jpg", "http://x.com/img002.jpg", "http://x.com/img003.jpg" },
            BatchPattern.Expand("http://x.com/img*.jpg", "1", "3", 3));

    [Fact]
    public void Width_zero_means_no_padding() =>
        Assert.Equal(
            new[] { "http://x.com/9.jpg", "http://x.com/10.jpg" },
            BatchPattern.Expand("http://x.com/*.jpg", "9", "10", 0));

    [Fact]
    public void Expands_letters() =>
        Assert.Equal(
            new[] { "http://x.com/a.txt", "http://x.com/b.txt", "http://x.com/c.txt" },
            BatchPattern.Expand("http://x.com/*.txt", "a", "c", 0));

    [Theory]
    [InlineData("http://x.com/a.jpg", "1", "2")]     // no asterisk
    [InlineData("http://x.com/*/*.jpg", "1", "2")]   // two asterisks
    [InlineData("http://x.com/*.jpg", "5", "1")]     // reversed range
    [InlineData("http://x.com/*.jpg", "a", "9")]     // letter to number
    [InlineData("http://x.com/*.jpg", "aa", "zz")]   // letters are single characters
    [InlineData("http://x.com/*.jpg", "1", "20000")] // too many files
    public void Rejects_invalid_patterns(string pattern, string from, string to) =>
        Assert.Throws<FormatException>(() => BatchPattern.Expand(pattern, from, to, 0));
}
