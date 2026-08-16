using System.Globalization;

namespace Gelato.Tmdb;

/// <summary>
/// Maps a TMDB movie detail onto the <see cref="StremioMeta"/> shape that
/// <c>GelatoManager.IntoBaseItem</c> and <c>InsertMeta</c> already consume.
///
/// This is how spec §4.3's inversion is achieved without reimplementing item creation:
/// TMDB is the source of truth, and the existing creation path is reused unchanged.
/// No AIOStreams <c>/meta</c> call is involved, so a title the user's meta addon does
/// not know is still created.
/// </summary>
public static class TmdbMetaAdapter
{
    private const string ImageBase = "https://image.tmdb.org/t/p/original";

    public static StremioMeta ToStremioMeta(TmdbMovieDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var id = string.IsNullOrWhiteSpace(detail.ImdbId)
            ? $"tmdb:{detail.Id.ToString(CultureInfo.InvariantCulture)}"
            : detail.ImdbId!;

        DateTime? released = DateTime.TryParse(
            detail.ReleaseDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed
        )
            ? parsed
            : null;

        return new StremioMeta
        {
            Id = id,
            Type = StremioMediaType.Movie,
            Name = detail.Title,
            Title = detail.Title,
            Description = detail.Overview,
            ImdbId = string.IsNullOrWhiteSpace(detail.ImdbId) ? null : detail.ImdbId,
            Released = released,
            Year = released?.Year,
            Poster = string.IsNullOrWhiteSpace(detail.PosterPath)
                ? null
                : ImageBase + detail.PosterPath,
            Background = string.IsNullOrWhiteSpace(detail.BackdropPath)
                ? null
                : ImageBase + detail.BackdropPath,
            Runtime = detail.Runtime is { } m
                ? $"{m.ToString(CultureInfo.InvariantCulture)} min"
                : null,
            ImdbRating = detail.VoteAverage is { } v ? (float)v : null,
            Genres = detail
                .Genres?.Select(g => g.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .ToList(),
        };
    }
}
