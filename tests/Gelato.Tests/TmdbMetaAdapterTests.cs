using Gelato;
using Gelato.Tmdb;
using Xunit;

namespace Gelato.Tests;

public class TmdbMetaAdapterTests
{
    private static TmdbMovieDetail Matrix() =>
        new()
        {
            Id = 603,
            ImdbId = "tt0133093",
            Title = "The Matrix",
            Overview = "A hacker learns the truth.",
            ReleaseDate = "1999-03-30",
            PosterPath = "/abc.jpg",
            Runtime = 136,
            VoteAverage = 8.2,
            Genres = [new TmdbGenre { Id = 28, Name = "Action" }],
        };

    [Fact]
    public void AlwaysUsesTmdbPrefixedIdSoBothProviderIdsSurvive()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal("tmdb:603", meta.Id);
        Assert.Equal("tt0133093", meta.ImdbId);
    }

    [Fact]
    public void UsesTmdbPrefixedIdWhenImdbIsMissing()
    {
        var detail = Matrix();
        detail.ImdbId = null;

        var meta = TmdbMetaAdapter.ToStremioMeta(detail);

        Assert.Equal("tmdb:603", meta.Id);
    }

    [Fact]
    public void LeavesImdbIdNullWhenTmdbSuppliesNone()
    {
        var detail = Matrix();
        detail.ImdbId = null;

        var meta = TmdbMetaAdapter.ToStremioMeta(detail);

        Assert.Null(meta.ImdbId);
    }

    [Fact]
    public void MapsCoreFields()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal(StremioMediaType.Movie, meta.Type);
        Assert.Equal("The Matrix", meta.Name);
        Assert.Equal("A hacker learns the truth.", meta.Description);
        Assert.Equal("tt0133093", meta.ImdbId);
        Assert.Equal(new DateTime(1999, 3, 30), meta.Released);
        Assert.Equal(1999, meta.GetYear());
        Assert.Equal(8.2f, meta.ImdbRating);
        Assert.Equal(["Action"], meta.Genres);
    }

    [Fact]
    public void BuildsAnAbsolutePosterUrl()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal("https://image.tmdb.org/t/p/original/abc.jpg", meta.Poster);
    }

    [Fact]
    public void LeavesPosterNullWhenTmdbHasNoImage()
    {
        var detail = Matrix();
        detail.PosterPath = null;

        var meta = TmdbMetaAdapter.ToStremioMeta(detail);

        Assert.Null(meta.Poster);
    }

    [Fact]
    public void FormatsRuntimeAsMinutes()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal("136 min", meta.Runtime);
    }

    [Fact]
    public void RuntimeStringIsReadableByTheOnlyThingThatConsumesIt()
    {
        // The one cross-module string contract in this branch: the adapter emits "136 min"
        // and Utils.ParseToTicks in Common.cs is its sole consumer. Changing either format
        // in isolation silently zeroes every imported item's runtime.
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal(TimeSpan.FromMinutes(136).Ticks, Utils.ParseToTicks(meta.Runtime));
    }

    [Fact]
    public void SurvivesAnAlmostEmptyDetail()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(new TmdbMovieDetail { Id = 1 });

        Assert.Equal("tmdb:1", meta.Id);
        Assert.Null(meta.Released);
        Assert.Null(meta.Runtime);
        Assert.Null(meta.Poster);
    }

    [Fact]
    public void IgnoresAnUnparseableReleaseDate()
    {
        var detail = Matrix();
        detail.ReleaseDate = "";

        var meta = TmdbMetaAdapter.ToStremioMeta(detail);

        Assert.Null(meta.Released);
    }
}
