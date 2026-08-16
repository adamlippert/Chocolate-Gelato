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
        var root = Path.Combine(Path.GetTempPath(), "gelato-root-" + Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(root, "cache");
        try
        {
            var cache = new TmdbDetailCache(cacheDir);
            cache.Set("../../etc/passwd", new TmdbMovieDetail { Id = 1 });

            Assert.True(cache.TryGet<TmdbMovieDetail>("../../etc/passwd", out _));

            // Recursive from the PARENT: an escaped write would land here and be seen.
            var all = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(all);
            Assert.All(all, f => Assert.Equal(cacheDir, Path.GetDirectoryName(f)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptEntriesAreTreatedAsMisses()
    {
        var cache = new TmdbDetailCache(_dir);
        cache.Set("movie:603", new TmdbMovieDetail { Id = 603 });

        // Filter to *.json: a leaked .tmp file must not make this corrupt the wrong entry.
        File.WriteAllText(Directory.GetFiles(_dir, "*.json")[0], "{ not json");

        Assert.False(cache.TryGet<TmdbMovieDetail>("movie:603", out _));
    }

    [Fact]
    public void AnEntryWithinMaxAgeIsAHit()
    {
        var cache = new TmdbDetailCache(_dir);
        cache.Set("collection:2344", new TmdbMovieDetail { Id = 2344 });

        Assert.True(
            cache.TryGet<TmdbMovieDetail>("collection:2344", out var value, TimeSpan.FromDays(1))
        );
        Assert.Equal(2344, value!.Id);
    }

    [Fact]
    public void AnEntryOlderThanMaxAgeIsAMiss()
    {
        // The bug this guards: a franchise collection's `parts` array grows when a sequel
        // ships, so an indefinitely cached collection would never gain the new film.
        var cache = new TmdbDetailCache(_dir);
        cache.Set("collection:2344", new TmdbMovieDetail { Id = 2344 });

        Age(TimeSpan.FromDays(2));

        Assert.False(
            cache.TryGet<TmdbMovieDetail>("collection:2344", out var value, TimeSpan.FromDays(1))
        );
        Assert.Null(value);
    }

    [Fact]
    public void NullMaxAgeReturnsAnOldEntry()
    {
        // Movie details are read this way: their imdb_id never changes.
        var cache = new TmdbDetailCache(_dir);
        cache.Set("movie:603", new TmdbMovieDetail { Id = 603, ImdbId = "tt0133093" });

        Age(TimeSpan.FromDays(400));

        Assert.True(cache.TryGet<TmdbMovieDetail>("movie:603", out var value, maxAge: null));
        Assert.Equal("tt0133093", value!.ImdbId);
    }

    /// <summary>
    /// Backdates the one cached entry. Each test that calls this has written exactly one,
    /// and the on-disk name is an MD5 of the key that the cache keeps private.
    /// </summary>
    private void Age(TimeSpan by) =>
        File.SetLastWriteTimeUtc(Directory.GetFiles(_dir, "*.json").Single(), DateTime.UtcNow - by);
}
