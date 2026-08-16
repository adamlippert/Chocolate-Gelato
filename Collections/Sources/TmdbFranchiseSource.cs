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
/// Each discovered franchise is tagged with its own <see cref="TitleRef.GroupKey"/> so the
/// sync service gives it its own BoxSet — one row named "Franchises" yields "The Matrix
/// Collection", "Alien Collection" and so on, not one box of everything.
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

        // A failed fetch is not an empty collection. TmdbClient returns null for every
        // failure it cannot retry past — no key, 401 on a rotated key, exhausted retries —
        // and swallowing that here would hand the sync service an empty desired list, which
        // it would faithfully reconcile by emptying the BoxSet. Throw instead: SyncAllAsync
        // catches per row, logs, and moves on without advancing LastSyncedUtc.
        var collection =
            await tmdb.GetCollectionAsync(collectionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"TMDB collection {collectionId} could not be fetched"
            );

        if (collection.Parts is null)
        {
            log.LogWarning("TMDB collection {Id} returned no parts", collectionId);
            yield break;
        }

        foreach (var part in collection.Parts)
        {
            ct.ThrowIfCancellationRequested();

            // The collection response carries no imdb_id, so fetch the detail. This is
            // the expensive call in a backfill and the reason TmdbDetailCache exists.
            //
            // A null detail must NOT drop the part. The part is in the collection either
            // way; all a failed detail lookup costs us is the IMDb id, and dropping the
            // title instead would silently remove an existing member of the BoxSet.
            // Downstream, FindExisting handles a null ImdbId by matching on TMDB alone.
            var detail = await tmdb.GetMovieAsync(part.Id, ct).ConfigureAwait(false);
            if (detail is null)
            {
                log.LogDebug(
                    "No TMDB detail for {TmdbId}; keeping it as a member without an IMDb id",
                    part.Id
                );
            }

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

            // A seed movie is a discovery input, not a member: if its detail cannot be
            // fetched we simply do not learn about whatever franchise it belongs to. Skip
            // quietly — failing the whole row over one unreadable seed would be worse.
            var detail = await tmdb.GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
            if (detail is null)
            {
                log.LogDebug(
                    "No TMDB detail for library movie {TmdbId}; skipping as a seed",
                    tmdbId
                );
                continue;
            }

            if (detail.BelongsToCollection is not { } belongs)
                continue;

            if (!seenCollections.Add(belongs.Id))
                continue;

            // Unlike Picked mode this does not throw. One unreachable franchise out of
            // dozens should not fail the row: its BoxSet is simply not reconciled this run
            // and keeps whatever members it already has.
            var collection = await tmdb.GetCollectionAsync(belongs.Id, ct).ConfigureAwait(false);
            if (collection is null)
            {
                log.LogWarning(
                    "TMDB collection {Id} could not be fetched; leaving that franchise untouched this run",
                    belongs.Id
                );
                continue;
            }

            if (collection.Parts is null)
                continue;

            // Each discovered franchise becomes its own BoxSet. Without a usable name there
            // is nothing sensible to call it, so skip rather than invent one.
            var groupName = CollectionGrouping.ResolveGroupName(belongs.Name, collection.Name);
            if (groupName is null)
            {
                log.LogWarning(
                    "TMDB collection {Id} has no usable name; skipping that franchise",
                    belongs.Id
                );
                continue;
            }

            var groupKey = CollectionGrouping.GroupKeyFor(belongs.Id);

            foreach (var part in collection.Parts)
            {
                ct.ThrowIfCancellationRequested();

                if (!emitted.Add(part.Id))
                    continue;

                // As in Picked mode, a null detail costs the IMDb id and nothing else.
                var partDetail = await tmdb.GetMovieAsync(part.Id, ct).ConfigureAwait(false);
                yield return new TitleRef(
                    part.Id,
                    partDetail?.ImdbId,
                    TitleMediaType.Movie,
                    groupKey,
                    groupName
                );
            }
        }
    }
}
