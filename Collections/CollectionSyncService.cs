using System.Globalization;
using Gelato.Config;
using Gelato.Tmdb;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Collections;

/// <summary>
/// Brings each tracked collection in line with its source.
///
/// Invariants (spec §3): the library is an archive and this service never deletes item
/// rows; a collection is a mirror of its source after caps; identity is TMDB + IMDb so a
/// title already present is reused; nothing here touches stream resolution.
/// </summary>
public sealed class CollectionSyncService(
    IEnumerable<ICollectionSource> sources,
    GelatoManager manager,
    ILibraryManager libraryManager,
    ICollectionManager collectionManager,
    TmdbClient tmdb,
    ILogger<CollectionSyncService> log
)
{
    /// <summary>
    /// The plugin configuration, read directly and never through
    /// <c>PluginConfiguration.GetEffectiveConfig</c>.
    ///
    /// <para><b>Do not switch this to the per-user effective config.</b>
    /// <c>UserConfig.ApplyOverrides</c> builds a new <see cref="PluginConfiguration"/> and copies
    /// <c>CollectionRows</c> as a <em>list reference</em>. <c>CollectionRow</c> is a mutable class
    /// and this service writes to it (<c>row.LastSyncedUtc</c>), so rows obtained through an
    /// effective config would be the same live objects reached through an alias — mutated, but
    /// persisted only by luck. Collection rows are a server-wide setting; there is no per-user
    /// variant of them.</para>
    /// </summary>
    private static PluginConfiguration Config => GelatoPlugin.Instance!.Configuration;

    public async Task SyncAllAsync(
        CancellationToken ct,
        IProgress<double>? progress = null,
        bool manual = false
    )
    {
        var rows = Config.CollectionRows.Where(r => r.Enabled).ToList();

        if (rows.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await SyncRowAsync(rows[i], ct, manual).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Genuine cancellation must stop the whole task. TmdbClient is hardened so
                // that only real cancellation surfaces as OperationCanceledException — a
                // request timeout does not — so rethrowing here cannot swallow a bad row.
                throw;
            }
            catch (Exception ex)
            {
                // One bad row must not stop the rest.
                log.LogError(ex, "Collection sync failed for {Name}", rows[i].Name);
            }

            progress?.Report((i + 1) / (double)rows.Count * 100.0);
        }

        // Persists the LastSyncedUtc values advanced by the rows that completed.
        GelatoPlugin.Instance!.SaveConfiguration();
        progress?.Report(100);
    }

    /// <summary>Returns true when the row ran to completion; false when it was skipped.</summary>
    public async Task<bool> SyncRowAsync(CollectionRow row, CancellationToken ct, bool manual)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The floor suppresses syncs; it never causes one.
        if (!SyncSchedule.IsDue(row.LastSyncedUtc, row.MinIntervalDays, DateTime.UtcNow, manual))
        {
            log.LogDebug(
                "Collection {Name} not due (last synced {Last}, floor {Days}d)",
                row.Name,
                row.LastSyncedUtc,
                row.MinIntervalDays
            );
            return false;
        }

        var source =
            sources.FirstOrDefault(s => s.Kind == row.Kind)
            ?? throw new InvalidOperationException($"No source registered for kind {row.Kind}");

        var cfg = Config;

        // Both caps use int.MaxValue for "unlimited", so they may only be compared against
        // or decremented — never added to.
        var rowLimit = CapPolicy.RowLimit(row.MaxItems);
        var budget = CapPolicy.RemainingBudget(cfg.GlobalItemCeiling, CountFeatureItems());

        var desired = new List<Guid>();
        var skippedForBudget = 0;

        await foreach (var titleRef in source.EnumerateAsync(row, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (desired.Count >= rowLimit)
                break;

            var item = FindExisting(titleRef);

            if (item is null)
            {
                if (budget <= 0)
                {
                    skippedForBudget++;
                    continue;
                }

                item = await CreateAsync(titleRef, ct).ConfigureAwait(false);
                if (item is null)
                    continue;

                budget--;
            }

            desired.Add(item.Id);
        }

        if (skippedForBudget > 0)
        {
            log.LogWarning(
                "Collection {Name}: {Count} titles not created — global item ceiling {Ceiling} reached",
                row.Name,
                skippedForBudget,
                cfg.GlobalItemCeiling
            );
        }

        await ReconcileAsync(row, desired, ct).ConfigureAwait(false);

        // Only a completed run advances the clock. A cancelled or failed run throws before
        // reaching here, so the row stays due. Nothing below may be moved above this point.
        row.LastSyncedUtc = DateTime.UtcNow;

        log.LogInformation("Collection {Name} synced: {Count} members", row.Name, desired.Count);

        return true;
    }

    /// <summary>
    /// How many item rows exist under the Gelato movie folder, excluding per-stream rows.
    /// Stream rows are created by <c>SyncStreams</c> on playback, not by this feature, so
    /// counting them would exhaust the ceiling for the wrong reason.
    /// </summary>
    private int CountFeatureItems()
    {
        var parent = manager.TryGetMovieFolder(Config);
        if (parent is null)
            return 0;

        return libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    ParentId = parent.Id,
                    Recursive = true,
                    IsDeadPerson = true, // skip filter marker
                }
            )
            .OfType<Video>()
            .Count(v => !v.IsStream());
    }

    /// <summary>
    /// Identity is TMDB + IMDb (spec §3). A title already in the library — a local file or an
    /// item an earlier Gelato feature created — is reused rather than duplicated.
    /// </summary>
    private BaseItem? FindExisting(TitleRef titleRef)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(MetadataProvider.Tmdb)] = titleRef.TmdbId.ToString(
                CultureInfo.InvariantCulture
            ),
        };

        if (!string.IsNullOrWhiteSpace(titleRef.ImdbId))
            ids[nameof(MetadataProvider.Imdb)] = titleRef.ImdbId!;

        var probe = new Movie { ProviderIds = ids };
        return manager.FindExistingItem(probe);
    }

    private async Task<BaseItem?> CreateAsync(TitleRef titleRef, CancellationToken ct)
    {
        var parent = manager.TryGetMovieFolder(Config);
        if (parent is null)
        {
            log.LogWarning("No movie folder configured; cannot create collection members");
            return null;
        }

        // TMDB is the source of truth (spec §4.3) — no AIOStreams /meta call here, so a
        // title the user's meta addon does not know is still created. The adapter maps the
        // TMDB detail onto the StremioMeta shape InsertMeta already consumes, which reuses
        // the existing creation path rather than reimplementing it.
        var detail = await tmdb.GetMovieAsync(titleRef.TmdbId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            log.LogDebug("No TMDB detail for {TmdbId}; skipping", titleRef.TmdbId);
            return null;
        }

        var meta = TmdbMetaAdapter.ToStremioMeta(detail);

        // allowRemoteRefresh MUST stay false. It looks like the more thorough option; it is
        // not. InsertMeta's remote-refresh branch fires when meta.ImdbId is null — which is
        // exactly the obscure films TMDB has no imdb_id for — and replaces the meta with an
        // AIOStreams /meta lookup, returning (null, false) when that addon does not know the
        // title. The adapter above has already produced complete metadata from TMDB, so a
        // remote refresh here can only subtract: it would silently drop precisely the titles
        // the TMDB-first approach exists to include (spec §4.3). Do not "fix" this to true.
        var (item, _) = await manager
            .InsertMeta(
                parent,
                meta,
                user: null,
                allowRemoteRefresh: false,
                refreshItem: true,
                // Queued, not fired directly. With queueRefreshItem false, InsertMeta calls
                // RefreshFullItem unawaited once per created item, each running
                // GelatoMovieMetadataProvider and so an AIOStreams /meta call — reintroducing
                // the metadata hop this design removes, at unbounded parallelism, across a
                // backfill of thousands of titles. Queuing bounds the concurrency.
                queueRefreshItem: true,
                ct
            )
            .ConfigureAwait(false);

        return item;
    }

    /// <summary>
    /// Makes membership equal the desired list. A diff, not a wipe-and-refill — at several
    /// thousand members the difference matters. Items dropped from the source lose their
    /// membership only; their library rows survive (spec §3, the library is an archive).
    /// </summary>
    private async Task ReconcileAsync(CollectionRow row, List<Guid> desired, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var boxSet = await GetOrCreateBoxSetAsync(row).ConfigureAwait(false);
        if (boxSet is null)
            return;

        // RemoveFromCollectionAsync re-resolves the BoxSet via GetItemById and matches
        // LinkedChildren by ItemId, which CollectionManagerDecorator never persists — it is
        // populated in memory only, by GetLinkedChildren(), on whichever instance calls it.
        // GetItemList returns repository instances and GetItemById returns cached ones, so
        // read from the same instance the removal will use or the ItemIds land on the wrong
        // object, nothing matches, and every removal silently no-ops and is retried forever.
        // Harmless when the two instances already coincide. The non-generic overload is used
        // because that is what ILibraryManager exposes here and what the rest of the
        // codebase (CollectionManagerDecorator, PlaylistManagerDecorator) calls.
        if (libraryManager.GetItemById(boxSet.Id) is BoxSet resolved)
            boxSet = resolved;

        // BoxSet membership is linked children, not parent-child: collection members keep
        // their real library parent, so an InternalItemsQuery on Parent returns nothing and
        // the diff would degenerate into "add everything, never remove".
        var current = boxSet.GetLinkedChildren().Select(i => i.Id).ToList();

        var delta = CollectionDiff.Compute(current, desired);

        if (delta.ToRemove.Count > 0)
        {
            await collectionManager
                .RemoveFromCollectionAsync(boxSet.Id, delta.ToRemove)
                .ConfigureAwait(false);
        }

        if (delta.ToAdd.Count > 0)
        {
            await collectionManager
                .AddToCollectionAsync(boxSet.Id, delta.ToAdd)
                .ConfigureAwait(false);
        }

        log.LogInformation(
            "Collection {Name}: +{Added} -{Removed}",
            row.Name,
            delta.ToAdd.Count,
            delta.ToRemove.Count
        );
    }

    /// <summary>
    /// The BoxSet is keyed by the row's stable id, not its name, so renaming a row does not
    /// orphan its collection.
    /// </summary>
    private async Task<BoxSet?> GetOrCreateBoxSetAsync(CollectionRow row)
    {
        var providerId = $"gelato-collection.{row.Id}";

        var existing = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet],
                    CollapseBoxSetItems = false,
                    Recursive = true,
                    IsDeadPerson = true, // skip filter marker
                    HasAnyProviderId = new Dictionary<string, string> { { "Stremio", providerId } },
                }
            )
            .OfType<BoxSet>()
            .FirstOrDefault();

        if (existing is not null)
            return existing;

        var created = await collectionManager
            .CreateCollectionAsync(
                new CollectionCreationOptions
                {
                    Name = row.Name,
                    IsLocked = true,
                    ProviderIds = new Dictionary<string, string> { { "Stremio", providerId } },
                }
            )
            .ConfigureAwait(false);

        created.DisplayOrder = "Default";
        await created
            .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
            .ConfigureAwait(false);

        return created;
    }
}
