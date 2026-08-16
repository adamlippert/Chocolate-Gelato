using Gelato.Tmdb;
using Xunit;

namespace Gelato.Tests;

public class TmdbKeyResolverTests
{
    [Fact]
    public void ConfiguredKeyWins()
    {
        var key = TmdbKeyResolver.Resolve("mine", () => "jellyfins");

        Assert.Equal("mine", key);
    }

    [Fact]
    public void FallsBackToJellyfinTmdbPluginKey()
    {
        var key = TmdbKeyResolver.Resolve("", () => "jellyfins");

        Assert.Equal("jellyfins", key);
    }

    [Fact]
    public void WhitespaceConfiguredKeyIsIgnored()
    {
        var key = TmdbKeyResolver.Resolve("   ", () => "jellyfins");

        Assert.Equal("jellyfins", key);
    }

    [Fact]
    public void ReturnsNullWhenNoKeyIsAvailable()
    {
        // No hardcoded fallback: without a key the feature must stay disabled rather
        // than borrow the shared key baked into the plugin.
        var key = TmdbKeyResolver.Resolve(null, () => null);

        Assert.Null(key);
    }

    [Fact]
    public void ReturnsNullWhenBothAreWhitespace()
    {
        var key = TmdbKeyResolver.Resolve("  ", () => "  ");

        Assert.Null(key);
    }

    [Fact]
    public void FallbackThrowingIsTreatedAsAbsent()
    {
        // The fallback reads another plugin's config by reflection and may throw.
        var key = TmdbKeyResolver.Resolve(null, () => throw new InvalidOperationException());

        Assert.Null(key);
    }
}
