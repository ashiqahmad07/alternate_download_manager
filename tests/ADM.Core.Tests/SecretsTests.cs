using ADM.Core;

namespace ADM.Core.Tests;

public sealed class SecretsTests
{
    [Fact]
    public void Round_trips_a_password() => Assert.Equal("p@ss", Secrets.Unprotect(Secrets.Protect("p@ss")));

    [Fact]
    public void Stored_form_hides_the_password()
    {
        var stored = Secrets.Protect("p@ss")!;
        Assert.StartsWith("dpapi:", stored);
        Assert.DoesNotContain("p@ss", stored);
    }

    [Fact]
    public void Empty_passes_through()
    {
        Assert.Null(Secrets.Protect(null));
        Assert.Equal("", Secrets.Protect(""));
    }

    [Fact]
    public void Corrupt_value_unprotects_to_null() => Assert.Null(Secrets.Unprotect("dpapi:not-base64!"));
}
