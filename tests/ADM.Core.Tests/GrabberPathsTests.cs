using ADM.Core;

namespace ADM.Core.Tests;

public sealed class GrabberPathsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "site");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch (IOException) { }
    }

    string Place(string address, string? name = null, bool isPage = false, bool keepSubfolders = true, bool overwrite = false)
    {
        var uri = new Uri(address);
        name ??= FileNames.FromResponse(null, address, null);
        return new GrabberPaths(keepSubfolders, overwrite).Place(_root, uri, name, isPage);
    }

    string Under(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    [Theory]
    [InlineData("http://h.test/../../evil.exe")]
    [InlineData("http://h.test/%2e%2e/%2e%2e/evil.exe")]
    [InlineData("http://h.test/%2E%2E%2F%2E%2E%2Fevil.exe")]
    [InlineData("http://h.test/..%2f..%2fevil.exe")]
    [InlineData("http://h.test/..%5c..%5cevil.exe")]
    [InlineData("http://h.test/a/%2e%2e%5c%2e%2e%5c%2e%2e%5cevil.exe")]
    [InlineData("http://h.test/.../....//evil.exe")]
    [InlineData("http://h.test/C:%5cWindows%5cevil.exe")]
    [InlineData("http://h.test/C:/Windows/evil.exe")]
    [InlineData("http://h.test/%5c%5cserver%5cshare%5cevil.exe")]
    [InlineData("http://h.test/file.txt:evil.exe")]
    [InlineData("http://h.test/file.txt%3aZone.Identifier")]
    [InlineData("http://h.test/a%00b.exe")]
    [InlineData("http://h.test/a%0d%0ab.exe")]
    [InlineData("http://h.test/CON")]
    [InlineData("http://h.test/con.txt")]
    [InlineData("http://h.test/NUL.tar.gz")]
    [InlineData("http://h.test/COM1/evil.exe")]
    [InlineData("http://h.test/LPT9.html")]
    [InlineData("http://h.test/AUX%20.txt")]
    [InlineData("http://h.test/AUX%20.d/evil.exe")]
    [InlineData("http://h.test/conin$")]
    [InlineData("http://h.test/COM¹")]
    [InlineData("http://h.test/lpt%C2%B2.txt")]
    [InlineData("http://h.test/COM³/evil.exe")]
    [InlineData("http://h.test/trailing.")]
    [InlineData("http://h.test/trailing%20")]
    [InlineData("http://h.test/dots.../file")]
    [InlineData("http://h.test/%20/file")]
    [InlineData("http://h.test/./././file")]
    [InlineData("http://[::1]:8080/file")]
    [InlineData("http://h.test/x?a=../../../evil")]
    [InlineData("http://h.test/x?a=C:\\Windows\\evil")]
    [InlineData("http://h.test/%2F%2F%2Fetc/passwd")]
    [InlineData("http://h.test/a/b/c/d/e/f/g/h/i/j/k/l/m/n/o/p/q/r/s/t/u/v/w/x/y/z/evil.exe")]
    public void Hostile_paths_stay_in_the_folder(string address)
    {
        foreach (var keep in new[] { true, false })
        {
            foreach (var isPage in new[] { true, false })
            {
                var path = Place(address, null, isPage, keep);
                Assert.Equal(path, Path.GetFullPath(path));
                Assert.StartsWith(_root + Path.DirectorySeparatorChar, path);
                foreach (var part in Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar))
                {
                    Assert.NotEqual("..", part);
                    Assert.NotEmpty(part);
                    Assert.False(part.Contains(':'), part);
                    Assert.False(part.EndsWith('.') || part.EndsWith(' '), part);
                    Assert.DoesNotMatch("(?i)^(con|prn|aux|nul|conin\\$|conout\\$|com[0-9¹²³]|lpt[0-9¹²³])( *)(\\..*)?$", part);
                }
            }
        }
    }

    [Theory]
    [InlineData("../../evil.exe")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("C:\\Windows\\evil.exe")]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("...")]
    [InlineData("")]
    public void Hostile_name_from_the_server_stays_in_the_folder(string name)
    {
        var path = Place("http://h.test/download", name);
        Assert.StartsWith(_root + Path.DirectorySeparatorChar, path);
        Assert.Equal(path, Path.GetFullPath(path));
        Assert.DoesNotContain(":", Path.GetRelativePath(_root, path));
        Assert.Equal(Under("h.test"), Path.GetDirectoryName(path));
        Assert.DoesNotMatch("(?i)^(con|nul)(\\..*)?$", Path.GetFileName(path));
    }

    [Fact]
    public void Very_long_names_are_cut()
    {
        var path = Place("http://h.test/" + new string('f', 150) + "/" + new string('n', 500) + ".zip");
        Assert.All(Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar), part => Assert.True(part.Length <= 180, part));
        Assert.EndsWith(".zip", path);
    }

    [Fact]
    public void A_long_folder_name_is_cut()
    {
        var path = Place("http://h.test/" + new string('f', 300) + "/file.zip");
        Assert.Equal(Under("h.test", new string('f', 100), "file.zip"), path);
    }

    [Fact]
    public void A_cut_folder_name_ends_as_windows_can_read_it()
    {
        var path = Place("http://h.test/" + new string('f', 99) + ".." + new string('g', 50) + "/file.zip");
        Assert.Equal(Under("h.test", new string('f', 99), "file.zip"), path);
    }

    [Fact]
    public void A_folder_with_a_name_of_only_spaces_keeps_its_place()
    {
        Assert.Equal(Under("h.test", "_", "file.zip"), Place("http://h.test/%20/file.zip"));
    }

    [Fact]
    public void Keeps_the_site_folders()
    {
        Assert.Equal(Under("www.h.test", "a", "b", "file.zip"), Place("http://www.h.test/a/b/file.zip"));
    }

    [Fact]
    public void Port_keeps_two_servers_apart()
    {
        Assert.Equal(Under("h.test_8080", "f.zip"), Place("http://h.test:8080/f.zip"));
    }

    [Fact]
    public void Escapes_are_undone_in_folders()
    {
        Assert.Equal(Under("h.test", "my folder", "caf\u00e9.zip"), Place("http://h.test/my%20folder/caf%C3%A9.zip"));
    }

    [Fact]
    public void Without_subfolders_everything_is_in_the_folder()
    {
        Assert.Equal(Under("file.zip"), Place("http://www.h.test/a/b/file.zip", keepSubfolders: false));
    }

    [Theory]
    [InlineData("http://h.test/", "index.html")]
    [InlineData("http://h.test/a/", "index.html")]
    public void Folder_address_becomes_index(string address, string name)
    {
        var path = Place(address, name, isPage: true);
        Assert.Equal("index.html", Path.GetFileName(path));
    }

    [Fact]
    public void Folder_address_keeps_its_folder()
    {
        Assert.Equal(Under("h.test", "a", "index.html"), Place("http://h.test/a/", "index.html", isPage: true));
    }

    [Theory]
    [InlineData("http://h.test/page.php?id=1", "page.php", "page.php_id=1.html")]
    [InlineData("http://h.test/page.html?id=1", "page.html", "page_id=1.html")]
    public void Query_goes_in_a_page_name(string address, string name, string expected)
    {
        Assert.Equal(expected, Path.GetFileName(Place(address, name, isPage: true)));
    }

    [Fact]
    public void Query_goes_before_a_file_extension()
    {
        Assert.Equal("a_v=2.png", Path.GetFileName(Place("http://h.test/a.png?v=2")));
    }

    [Fact]
    public void Long_query_is_hashed()
    {
        var first = Path.GetFileName(Place("http://h.test/a.png?x=" + new string('1', 200)));
        var second = Path.GetFileName(Place("http://h.test/a.png?x=" + new string('2', 200)));
        Assert.True(first.Length < 40, first);
        Assert.NotEqual(first, second);
        Assert.EndsWith(".png", first);
    }

    [Fact]
    public void Query_with_a_path_in_it_stays_one_name()
    {
        Assert.Equal(Under("h.test", "a_p=_x_y.png"), Place("http://h.test/a.png?p=/x/y"));
    }

    [Theory]
    [InlineData("about", "about.html")]
    [InlineData("about.html", "about.html")]
    [InlineData("about.htm", "about.htm")]
    [InlineData("about.php", "about.php.html")]
    public void Html_gets_an_extension(string name, string expected)
    {
        Assert.Equal(expected, Path.GetFileName(Place("http://h.test/" + name, name, isPage: true)));
    }

    [Fact]
    public void A_file_keeps_its_name_without_html()
    {
        Assert.Equal("about", Path.GetFileName(Place("http://h.test/about", "about")));
    }

    [Fact]
    public void Two_addresses_never_share_a_path()
    {
        var paths = new GrabberPaths(keepSubfolders: true, overwrite: false);
        var one = paths.Place(_root, new Uri("http://h.test/File.zip"), "File.zip", false);
        var two = paths.Place(_root, new Uri("http://h.test/file.zip"), "file.zip", false);
        var three = paths.Place(_root, new Uri("http://h.test/FILE.ZIP"), "FILE.ZIP", false);
        Assert.Equal(Under("h.test", "File.zip"), one);
        Assert.Equal(Under("h.test", "file_2.zip"), two);
        Assert.Equal(Under("h.test", "FILE_3.ZIP"), three);
    }

    [Fact]
    public void Two_addresses_share_no_path_without_subfolders()
    {
        var paths = new GrabberPaths(keepSubfolders: false, overwrite: true);
        Assert.Equal(Under("a.zip"), paths.Place(_root, new Uri("http://h.test/1/a.zip"), "a.zip", false));
        Assert.Equal(Under("a_2.zip"), paths.Place(_root, new Uri("http://h.test/2/a.zip"), "a.zip", false));
    }

    [Fact]
    public void File_and_folder_of_one_name()
    {
        var paths = new GrabberPaths(keepSubfolders: true, overwrite: false);
        var file = paths.Place(_root, new Uri("http://h.test/a"), "a", false);
        var inside = paths.Place(_root, new Uri("http://h.test/a/b.zip"), "b.zip", false);
        Assert.Equal(Under("h.test", "a"), file);
        Assert.Equal(Under("h.test", "a_", "b.zip"), inside);
    }

    [Fact]
    public void Folder_and_file_of_one_name()
    {
        var paths = new GrabberPaths(keepSubfolders: true, overwrite: false);
        var inside = paths.Place(_root, new Uri("http://h.test/a/b.zip"), "b.zip", false);
        var file = paths.Place(_root, new Uri("http://h.test/a"), "a", false);
        Assert.Equal(Under("h.test", "a", "b.zip"), inside);
        Assert.Equal(Under("h.test", "a_2"), file);
    }

    [Fact]
    public void File_and_folder_of_one_name_whatever_the_case()
    {
        var paths = new GrabberPaths(keepSubfolders: true, overwrite: false);
        paths.Place(_root, new Uri("http://h.test/Dir/b.zip"), "b.zip", false);
        Assert.Equal(Under("h.test", "dir_2"), paths.Place(_root, new Uri("http://h.test/dir"), "dir", false));
    }

    [Fact]
    public void Existing_file_gets_a_number()
    {
        Directory.CreateDirectory(Under("h.test"));
        File.WriteAllText(Under("h.test", "a.zip"), "mine");
        Assert.Equal(Under("h.test", "a_2.zip"), Place("http://h.test/a.zip"));
    }

    [Fact]
    public void Existing_file_is_kept_as_the_target_with_overwrite()
    {
        Directory.CreateDirectory(Under("h.test"));
        File.WriteAllText(Under("h.test", "a.zip"), "mine");
        Assert.Equal(Under("h.test", "a.zip"), Place("http://h.test/a.zip", overwrite: true));
    }

    [Fact]
    public void Existing_folder_is_never_a_target()
    {
        Directory.CreateDirectory(Under("h.test", "a"));
        Assert.Equal(Under("h.test", "a_2"), Place("http://h.test/a", overwrite: true));
    }

    [Fact]
    public void Existing_file_where_a_folder_goes()
    {
        Directory.CreateDirectory(Under("h.test"));
        File.WriteAllText(Under("h.test", "a"), "mine");
        Assert.Equal(Under("h.test", "a_", "b.zip"), Place("http://h.test/a/b.zip", overwrite: true));
    }

    [Fact]
    public void The_folder_of_a_path_taken_before_is_no_file()
    {
        var paths = new GrabberPaths(keepSubfolders: true, overwrite: false);
        paths.Taken(Under("h.test", "dir", "a.zip"));
        Assert.Equal(Under("h.test", "dir_2"), paths.Place(_root, new Uri("http://h.test/dir"), "dir", false));
    }

    [Fact]
    public void Paths_taken_before_are_not_given_again()
    {
        var paths = new GrabberPaths(keepSubfolders: true, overwrite: true);
        paths.Taken(Under("h.test", "a.zip"));
        Assert.Equal(Under("h.test", "a_2.zip"), paths.Place(_root, new Uri("http://h.test/b/../a.zip"), "a.zip", false));
    }
}
