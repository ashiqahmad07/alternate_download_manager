using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>A category made, and a folder remembered for one, from the dialog that asks where a download goes.</summary>
public sealed class CategoriesTests
{
    static AppState NewState() => new()
    {
        Categories = Categories.Defaults(),
        Settings = new Settings { DownloadRoot = @"C:\Downloads\ADM" },
    };

    [Fact]
    public void A_new_category_goes_before_general_with_a_folder_of_its_name()
    {
        var state = NewState();

        var made = Categories.Add("Books", state);

        Assert.Equal("Books", made.Name);
        Assert.Equal("Books", made.Folder);
        Assert.Empty(made.Extensions);
        Assert.Equal(new[] { "Compressed", "Documents", "Music", "Programs", "Video", "Books", "General" }, state.Categories.Select(c => c.Name));
        Assert.Equal(@"C:\Downloads\ADM\Books", Categories.FolderFor("Books", state));
    }

    [Fact]
    public void A_name_already_there_gives_that_category_and_makes_none()
    {
        var state = NewState();

        var found = Categories.Add("  video ", state);

        Assert.Equal("Video", found.Name);
        Assert.Equal(6, state.Categories.Count);
    }

    [Fact]
    public void A_name_that_cant_be_a_folder_gets_a_folder_that_can()
    {
        var state = NewState();

        var made = Categories.Add("Work/2026: drafts", state);

        Assert.Equal("Work/2026: drafts", made.Name);
        Assert.Equal("Work_2026_ drafts", made.Folder);
    }

    [Fact]
    public void The_list_is_replaced_rather_than_changed_where_it_is_read()
    {
        var state = NewState();
        var before = state.Categories;

        Categories.Add("Books", state);

        Assert.NotSame(before, state.Categories);
        Assert.Equal(6, before.Count);
    }

    [Fact]
    public void A_folder_under_the_download_root_is_kept_as_one_under_it()
    {
        var state = NewState();

        Categories.SetFolder("Compressed", @"C:\Downloads\ADM\Archives\Zip", state);

        Assert.Equal(@"Archives\Zip", state.Categories.Single(c => c.Name == "Compressed").Folder);
        // So it moves with the root.
        state.Settings.DownloadRoot = @"D:\Files";
        Assert.Equal(@"D:\Files\Archives\Zip", Categories.FolderFor("Compressed", state));
    }

    [Fact]
    public void The_download_root_itself_is_kept_as_no_folder()
    {
        var state = NewState();

        Categories.SetFolder("Music", @"C:\Downloads\ADM\", state);

        Assert.Equal("", state.Categories.Single(c => c.Name == "Music").Folder);
        Assert.Equal(@"C:\Downloads\ADM", Categories.FolderFor("Music", state));
    }

    [Fact]
    public void A_folder_under_the_root_whose_name_starts_with_dots_is_still_under_it()
    {
        var state = NewState();

        Categories.SetFolder("Music", @"C:\Downloads\ADM\..mixes", state);

        Assert.Equal("..mixes", state.Categories.Single(c => c.Name == "Music").Folder);
    }

    [Theory]
    [InlineData(@"D:\Archives")]
    [InlineData(@"C:\Downloads\Other")]
    [InlineData(@"C:\Downloads\ADM Old")]
    public void A_folder_elsewhere_is_kept_whole(string folder)
    {
        var state = NewState();

        Categories.SetFolder("Compressed", folder, state);

        Assert.Equal(folder, state.Categories.Single(c => c.Name == "Compressed").Folder);
        Assert.Equal(folder, Categories.FolderFor("Compressed", state));
    }

    [Fact]
    public void A_folder_for_a_category_not_there_changes_nothing()
    {
        var state = NewState();

        Categories.SetFolder("Gone", @"D:\Archives", state);

        Assert.Equal(Categories.Defaults().Select(c => c.Folder), state.Categories.Select(c => c.Folder));
    }
}
