using ADM.Core;

namespace ADM.Core.Tests;

public class SpecialKeysTests
{
    [Fact]
    public void Parses_names_in_any_case_order_and_spacing()
    {
        Assert.Equal(new[] { "ctrl", "alt" }, SpecialKeys.Parse(" alt + CTRL "));
        Assert.Equal(new[] { "insert" }, SpecialKeys.Parse("Insert+insert"));
        Assert.Empty(SpecialKeys.Parse("")!);
        Assert.Empty(SpecialKeys.Parse(null)!);
    }

    [Fact]
    public void Unknown_key_names_are_refused()
    {
        Assert.Null(SpecialKeys.Parse("Ctrl+Win"));
        Assert.Null(SpecialKeys.Parse("Alt+F4"));
    }

    [Fact]
    public void Formats_keys_for_display() => Assert.Equal("Ctrl+Alt", SpecialKeys.Format(SpecialKeys.Parse("alt+ctrl")!));

    [Fact]
    public void A_combination_applies_only_when_every_key_is_held()
    {
        Assert.True(SpecialKeys.AllHeld(new[] { "ctrl", "alt" }, new[] { "alt", "ctrl", "shift" }));
        Assert.False(SpecialKeys.AllHeld(new[] { "ctrl", "alt" }, new[] { "alt" }));
        Assert.False(SpecialKeys.AllHeld(Array.Empty<string>(), new[] { "alt" }));
    }

    [Fact]
    public void Prevent_keys_inside_force_keys_conflict()
    {
        // Prevent wins when both are held, so a prevent set inside the force set would make forcing impossible.
        Assert.True(SpecialKeys.Conflict("Alt", "Ctrl+Alt"));
        Assert.True(SpecialKeys.Conflict("Alt", "Alt"));
        Assert.False(SpecialKeys.Conflict("Alt", "Insert"));
        Assert.False(SpecialKeys.Conflict("Ctrl+Alt", "Alt"));
        Assert.False(SpecialKeys.Conflict("", "Alt"));
    }
}
