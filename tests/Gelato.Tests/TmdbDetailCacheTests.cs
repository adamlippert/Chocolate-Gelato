using Gelato.Tmdb;
using Xunit;

namespace Gelato.Tests;

public class TmdbDetailCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "gelato-tests-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void MissOnAnEmptyCache()
    {
        var cache = new TmdbDetailCache(_dir);

        Assert.False(cache.TryGet<TmdbMovieDetail>("movie:603", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void RoundTripsAValue()
    {
        var cache = new TmdbDetailCache(_dir);
        cache.Set("movie:603", new TmdbMovieDetail { Id = 603, ImdbId = "tt0133093" });

        Assert.True(cache.TryGet<TmdbMovieDetail>("movie:603", out var value));
        Assert.Equal("tt0133093", value!.ImdbId);
    }

    [Fact]
    public void SurvivesANewCacheInstanceOverTheSameDirectory()
    {
        new TmdbDetailCache(_dir).Set("movie:603", new TmdbMovieDetail { Id = 603 });

        var reopened = new TmdbDetailCache(_dir);

        Assert.True(reopened.TryGet<TmdbMovieDetail>("movie:603", out var value));
        Assert.Equal(603, value!.Id);
    }

    [Fact]
    public void KeysWithPathSeparatorsDoNotEscapeTheDirectory()
    {
        var cache = new TmdbDetailCache(_dir);
        cache.Set("../../etc/passwd", new TmdbMovieDetail { Id = 1 });

        Assert.True(cache.TryGet<TmdbMovieDetail>("../../etc/passwd", out _));
        Assert.All(Directory.GetFiles(_dir), f => Assert.Equal(_dir, Path.GetDirectoryName(f)));
    }

    [Fact]
    public void CorruptEntriesAreTreatedAsMisses()
    {
        var cache = new TmdbDetailCache(_dir);
        cache.Set("movie:603", new TmdbMovieDetail { Id = 603 });

        File.WriteAllText(Directory.GetFiles(_dir)[0], "{ not json");

        Assert.False(cache.TryGet<TmdbMovieDetail>("movie:603", out _));
    }
}
