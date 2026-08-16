using System.Runtime.CompilerServices;
using Gelato.Config;
using Gelato.Tmdb;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Collections.Sources;

/// <summary>
/// TMDB franchise collections.
///
/// <para><b>Picked</b> — one TMDB collection, named by <see cref="CollectionRow.SourceId"/>.</para>
/// <para><b>Auto</b> — every franchise implied by movies already in the library, found via
/// each movie's <c>belongs_to_collection</c>. Bounded by the library rather than by TMDB.
/// Known blind spot: this only sees library movies that carry a TMDB provider id. Items
/// created by earlier Gelato features (catalog import, search) generally carry only an
/// IMDb id and no TMDB id, so on an existing installation Auto mode will be blind to most
/// of the library until those items are backfilled with TMDB ids.</para>
/// <para><b>All</b> is spec phase 2 and throws here.</para>
/// </summary>
public sealed class TmdbFranchiseSource(
    TmdbClient tmdb,
    ILibraryManager libraryManager,
    ILogger<TmdbFranchiseSource> log
) : ICollectionSource
{
    public CollectionKind Kind => CollectionKind.Franchise;

    public async IAsyncEnumerable<TitleRef> EnumerateAsync(
        CollectionRow row,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        switch (row.Mode)
        {
            case CollectionMode.Picked:
                await foreach (var t in EnumeratePickedAsync(row, ct).ConfigureAwait(false))
                    yield return t;
                break;

            case CollectionMode.Auto:
                await foreach (var t in EnumerateAutoAsync(row, ct).ConfigureAwait(false))
                    yield return t;
                break;

            case CollectionMode.All:
                throw new NotSupportedException(
                    "Franchise 'All' mode requires the TMDB daily ID export path (spec phase 2)."
                );

            default:
                throw new ArgumentOutOfRangeException(nameof(row), row.Mode, "Unknown mode");
        }
    }

    private async IAsyncEnumerable<TitleRef> EnumeratePickedAsync(
        CollectionRow row,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        if (!int.TryParse(row.SourceId, out var collectionId))
        {
            log.LogWarning(
                "Collection row {Name} has a non-numeric TMDB collection id {SourceId}",
                row.Name,
                row.SourceId
            );
            yield break;
        }

        var collection = await tmdb.GetCollectionAsync(collectionId, ct).ConfigureAwait(false);
        if (collection?.Parts is null)
        {
            log.LogWarning("TMDB collection {Id} returned no parts", collectionId);
            yield break;
        }

        foreach (var part in collection.Parts)
        {
            ct.ThrowIfCancellationRequested();

            // The collection response carries no imdb_id, so fetch the detail. This is
            // the expensive call in a backfill and the reason TmdbDetailCache exists.
            var detail = await tmdb.GetMovieAsync(part.Id, ct).ConfigureAwait(false);

            yield return new TitleRef(part.Id, detail?.ImdbId, TitleMediaType.Movie);
        }
    }

    private async IAsyncEnumerable<TitleRef> EnumerateAutoAsync(
        CollectionRow row,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        // Every movie in the library that carries a TMDB id, excluding stream rows.
        //
        // Known blind spot: items created by earlier Gelato features (catalog import,
        // search) generally carry only an IMDb id and no TMDB id, since Stremio/AIOStreams
        // metadata is IMDb-keyed. GetProviderId(MetadataProvider.Tmdb) will be empty for
        // most of those, so Auto mode is effectively blind to most of an existing
        // installation's library until those items are backfilled with TMDB ids. This is a
        // real limitation, not a bug — see the class doc.
        var movies = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Video>()
            .Where(v => !v.IsStream())
            .Select(v => v.GetProviderId(MetadataProvider.Tmdb))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => int.TryParse(id, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        log.LogInformation(
            "Franchise auto mode for {Name}: scanning {Count} library movies",
            row.Name,
            movies.Count
        );

        var seenCollections = new HashSet<int>();
        var emitted = new HashSet<int>();

        foreach (var tmdbId in movies)
        {
            ct.ThrowIfCancellationRequested();

            var detail = await tmdb.GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
            if (detail?.BelongsToCollection is not { } belongs)
                continue;

            if (!seenCollections.Add(belongs.Id))
                continue;

            var collection = await tmdb.GetCollectionAsync(belongs.Id, ct).ConfigureAwait(false);
            if (collection?.Parts is null)
                continue;

            foreach (var part in collection.Parts)
            {
                ct.ThrowIfCancellationRequested();

                if (!emitted.Add(part.Id))
                    continue;

                var partDetail = await tmdb.GetMovieAsync(part.Id, ct).ConfigureAwait(false);
                yield return new TitleRef(part.Id, partDetail?.ImdbId, TitleMediaType.Movie);
            }
        }
    }
}
