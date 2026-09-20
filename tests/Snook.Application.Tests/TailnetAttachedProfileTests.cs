using System.Text;
using Snook.Application;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class TailnetAttachedProfileTests
{
    [Fact]
    public void RoundTripContainsIdentityButNeverCredential()
    {
        const string secret = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var credential = new DaemonPairingCredential(Guid.NewGuid(), secret,
            new Uri("https://snook.example.ts.net/"), "person@example.com", "Pixel test",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

        var profile = TailnetAttachedProfile.FromCredential(credential);
        var encoded = profile.Encode();
        var decoded = TailnetAttachedProfile.Decode(encoded);

        Assert.Equal(profile, decoded);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(encoded), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://snook.example.ts.net/")]
    [InlineData("https://192.0.2.1/")]
    [InlineData("https://snook.example.ts.net/path")]
    [InlineData("https://lookalike-ts.net/")]
    public void RejectsNonServeOrigins(string origin)
    {
        var profile = Valid() with { ServeOrigin = new Uri(origin) };
        Assert.Throws<SnookException>(() => profile.Encode());
    }

    [Fact]
    public void RejectsUnknownPropertiesAndInvalidIdentities()
    {
        var encoded = Encoding.UTF8.GetString(Valid().Encode());
        encoded = encoded[..^1] + ",\"credential\":\"secret\"}";
        Assert.Throws<SnookException>(() => TailnetAttachedProfile.Decode(Encoding.UTF8.GetBytes(encoded)));
        Assert.Throws<SnookException>(() => (Valid() with { TailnetLogin = " person@example.com" }).Encode());
        Assert.Throws<SnookException>(() => (Valid() with { CredentialId = Guid.Empty }).Encode());
    }

    private static TailnetAttachedProfile Valid() => new(
        TailnetAttachedProfile.CurrentVersion,
        new Uri("https://snook.example.ts.net/"),
        "person@example.com",
        "Pixel test",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        DateTimeOffset.UtcNow);
}
