using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gelato.Tmdb;

/// <summary>Shared serializer options. TMDB uses snake_case throughout.</summary>
public static class TmdbJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Response shape of <c>/collection/{id}</c>.</summary>
public class TmdbCollection
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public List<TmdbCollectionPart>? Parts { get; set; }
}

public class TmdbCollectionPart
{
    public int Id { get; set; }
    public string? Title { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }
}

/// <summary>Response shape of <c>/movie/{id}</c>, reduced to what this feature needs.</summary>
public class TmdbMovieDetail
{
    public int Id { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    public string? Title { get; set; }
    public string? Overview { get; set; }

    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>Minutes.</summary>
    public int? Runtime { get; set; }

    public List<TmdbGenre>? Genres { get; set; }

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; set; }

    [JsonPropertyName("belongs_to_collection")]
    public TmdbCollectionRef? BelongsToCollection { get; set; }
}

public class TmdbGenre
{
    public int Id { get; set; }
    public string? Name { get; set; }
}

public class TmdbCollectionRef
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
