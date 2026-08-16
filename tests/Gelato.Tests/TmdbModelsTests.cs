using System.Text.Json;
using Gelato.Tmdb;
using Xunit;

namespace Gelato.Tests;

public class TmdbModelsTests
{
    [Fact]
    public void ParsesACollectionResponse()
    {
        const string json = """
            {
              "id": 2344,
              "name": "The Matrix Collection",
              "parts": [
                { "id": 603, "title": "The Matrix", "release_date": "1999-03-30" },
                { "id": 604, "title": "The Matrix Reloaded", "release_date": "2003-05-15" }
              ]
            }
            """;

        var collection = JsonSerializer.Deserialize<TmdbCollection>(json, TmdbJson.Options);

        Assert.NotNull(collection);
        Assert.Equal(2344, collection!.Id);
        Assert.Equal("The Matrix Collection", collection.Name);
        Assert.Equal(2, collection.Parts!.Count);
        Assert.Equal(603, collection.Parts[0].Id);
        Assert.Equal("1999-03-30", collection.Parts[0].ReleaseDate);
    }

    [Fact]
    public void ParsesAMovieDetailWithImdbIdAndCollection()
    {
        const string json = """
            {
              "id": 603,
              "imdb_id": "tt0133093",
              "title": "The Matrix",
              "belongs_to_collection": { "id": 2344, "name": "The Matrix Collection" }
            }
            """;

        var movie = JsonSerializer.Deserialize<TmdbMovieDetail>(json, TmdbJson.Options);

        Assert.NotNull(movie);
        Assert.Equal("tt0133093", movie!.ImdbId);
        Assert.Equal(2344, movie.BelongsToCollection!.Id);
    }

    [Fact]
    public void HandlesAMovieWithNoCollection()
    {
        const string json = """
            { "id": 27205, "imdb_id": "tt1375666", "belongs_to_collection": null }
            """;

        var movie = JsonSerializer.Deserialize<TmdbMovieDetail>(json, TmdbJson.Options);

        Assert.NotNull(movie);
        Assert.Null(movie!.BelongsToCollection);
    }

    [Fact]
    public void HandlesAMissingImdbId()
    {
        const string json = """{ "id": 999999, "title": "Obscure" }""";

        var movie = JsonSerializer.Deserialize<TmdbMovieDetail>(json, TmdbJson.Options);

        Assert.NotNull(movie);
        Assert.Null(movie!.ImdbId);
    }

    [Fact]
    public void HandlesACollectionWithNoParts()
    {
        const string json = """{ "id": 1, "name": "Empty" }""";

        var collection = JsonSerializer.Deserialize<TmdbCollection>(json, TmdbJson.Options);

        Assert.NotNull(collection);
        Assert.Null(collection!.Parts);
    }
}
