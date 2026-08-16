using System.Collections.Concurrent;
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

    /// <summary>
    /// One gate per row id, so two overlapping runs of the same row cannot interleave.
    ///
    /// <para>The scheduled task and the settings page's "Sync now" button reach the same rows
    /// by different paths, and manual runs bypass the refresh floor entirely. Two concurrent
    /// runs of one row would each snapshot the global item budget independently — so between
    /// them they can create twice the ceiling — and both would write
    /// <c>LastSyncedUtc</c>.</para>
    ///
    /// <para>Static because the service is resolved per scope; keyed by row id, never pruned.
    /// Rows are a handful of configuration entries with stable ids, so the dictionary is
    /// bounded by the config file, not by traffic.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RowGates = new(
        StringComparer.Ordinal
    );

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

        var gate = RowGates.GetOrAdd(row.Id ?? string.Empty, _ => new SemaphoreSlim(1, 1));

        // Zero timeout: a second run of the same row is skipped, not queued. Queuing would
        // only guarantee that the duplicate work happens eventually, which is the thing worth
        // avoiding — the first run's result is the one the user asked for.
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            log.LogWarning(
                "Collection {Name} is already syncing; skipping this run to avoid a concurrent pass",
                row.Name
            );
            return false;
        }

        try
        {
            return await SyncRowCoreAsync(row, ct, manual).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> SyncRowCoreAsync(CollectionRow row, CancellationToken ct, bool manual)
    {
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

        // Spec §6.2: without a TMDB key the feature stays disabled. Enforced here and not
        // merely on the settings page, because every source shipped today is TMDB-backed and
        // a keyless TmdbClient returns null from every call — which, left unchecked, reads
        // downstream as "this collection is empty now" and empties the BoxSet. Returning
        // early leaves LastSyncedUtc untouched, so the row runs the moment a key appears.
        if (!tmdb.IsEnabled)
        {
            log.LogWarning(
                "Collection {Name} skipped: no TMDB API key is configured, so the source cannot be read",
                row.Name
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

        // One entry per resolved title, carrying the group it belongs to. Grouping is what
        // turns a franchise Auto row into one BoxSet per franchise rather than a single box
        // of every film it discovered.
        var resolved = new List<(Guid ItemId, string? GroupKey, string? GroupName)>();
        var skippedForBudget = 0;

        await foreach (var titleRef in source.EnumerateAsync(row, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            // The row cap applies across the row as a whole, not per group: it exists to
            // bound how much work one config entry can cause, and the group count is not
            // something the user chose.
            if (resolved.Count >= rowLimit)
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

            resolved.Add((item.Id, titleRef.GroupKey, titleRef.GroupName));
        }

        if (skippedForBudget > 0)
        {
            log.LogWarning(
                "Collection {Name}: {Count} titles not created — the global item ceiling {Ceiling} "
                    + "was reached, measured against every non-stream movie under the Gelato movie "
                    + "folder (a deliberate over-count; see CountFeatureItems)",
                row.Name,
                skippedForBudget,
                cfg.GlobalItemCeiling
            );
        }

        // Safety net. Sources are hardened to throw rather than truncate, but a source that
        // yields nothing at all while the row's BoxSets still hold members is overwhelmingly
        // more likely to be a failure than a franchise that genuinely lost every film. The
        // destructive reading is unrecoverable in practice — the run would empty the BoxSet
        // and then advance the refresh floor, keeping it empty for a week — so refuse it and
        // leave the row due. A user who really did mean to empty it can delete the row.
        if (resolved.Count == 0)
        {
            var existingMembers = CountRowBoxSetMembers(row);

            if (CollectionSafety.ShouldSkipEmptyReconcile(0, existingMembers))
            {
                log.LogError(
                    "Collection {Name}: the source returned no titles while {Count} members are already "
                        + "in place. Treating this as a source failure and leaving the collection "
                        + "untouched rather than emptying it. Delete the row if it should really be empty.",
                    row.Name,
                    existingMembers
                );
                return false;
            }
        }

        var totalMembers = 0;

        foreach (
            var group in resolved.GroupBy(r => r.GroupKey ?? string.Empty, StringComparer.Ordinal)
        )
        {
            ct.ThrowIfCancellationRequested();

            var groupKey = group.Key.Length == 0 ? null : group.Key;
            var groupName = group.Select(g => g.GroupName).FirstOrDefault(n => n is not null);

            var members = group.Select(g => g.ItemId).ToList();
            totalMembers += members.Count;

            await ReconcileAsync(
                    row,
                    CollectionGrouping.ProviderId(row.Id, groupKey),
                    CollectionGrouping.BoxSetName(row.Name, groupKey, groupName),
                    members,
                    ct
                )
                .ConfigureAwait(false);
        }

        // Only a completed run advances the clock. A cancelled or failed run throws before
        // reaching here, and the safety net above returns before reaching here, so the row
        // stays due. Nothing below may be moved above this point.
        row.LastSyncedUtc = DateTime.UtcNow;

        log.LogInformation("Collection {Name} synced: {Count} members", row.Name, totalMembers);

        return true;
    }

    /// <summary>
    /// How many members the BoxSets owned by this row currently hold, across the row's own
    /// BoxSet and any per-group BoxSets it has created. Used only by the empty-source safety
    /// net, so it runs at most once per row per sync.
    /// </summary>
    private int CountRowBoxSetMembers(CollectionRow row) =>
        libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.BoxSet],
                    CollapseBoxSetItems = false,
                    Recursive = true,
                    IsDeadPerson = true, // skip filter marker
                }
            )
            .OfType<BoxSet>()
            .Where(b => CollectionGrouping.OwnedByRow(b.GetProviderId("Stremio"), row.Id))
            .Sum(b => b.GetLinkedChildren().Count);

    /// <summary>
    /// Every non-stream movie under the Gelato movie folder.
    ///
    /// <para>This is <em>not</em> a count of items this feature created, which is what the
    /// global ceiling nominally bounds (spec §6.1). Items imported by catalog import or added
    /// by search are indistinguishable from collection members at this level — nothing marks
    /// provenance — so they are counted too and the ceiling is reached sooner than it strictly
    /// should be. That over-count is deliberate: the ceiling exists to bound how large this
    /// feature can grow the database, and erring toward stopping early is the safe direction.
    /// Per-stream rows are excluded because <c>SyncStreams</c> creates them on playback, one
    /// per available stream, which would exhaust the ceiling for an unrelated reason.</para>
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

        // InsertMeta (below) calls EnrichMetaAsync, whose early-out is
        // `meta.App_Extras?.ReleaseDates is not null`. Left unpopulated, EnrichMetaAsync would
        // fetch release dates itself via GelatoStremioProvider.EnrichDigitalReleaseDateAsync,
        // which uses the hardcoded community TMDB API key at GelatoStremioProvider.cs:205 —
        // shared by every Gelato installation and sized for occasional lookups, not a backfill
        // of thousands of titles. Fetch on our own keyed, cached TmdbClient instead so that
        // shared key is never reached for these items, and so EnrichMetaAsync short-circuits.
        //
        // Do not skip this by stubbing App_Extras.ReleaseDates with an empty object instead —
        // that would short-circuit EnrichMetaAsync too, but IntoBaseItem derives a movie's
        // EndDate from GetDigitalReleaseDate(), and a missing digital date there falls back to
        // the 9999 sentinel the unreleased filter reads as "not yet released", which could hide
        // a newly added film. A failed fetch must not fail the item, so on null we simply leave
        // ReleaseDates unset and let that existing sentinel behaviour stand.
        var releaseDates = await tmdb.GetReleaseDatesAsync(titleRef.TmdbId, ct)
            .ConfigureAwait(false);
        if (releaseDates is not null)
        {
            meta.App_Extras ??= new StremioAppExtras();
            meta.App_Extras.ReleaseDates = releaseDates;
        }

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
    private async Task ReconcileAsync(
        CollectionRow row,
        string providerId,
        string boxSetName,
        List<Guid> desired,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        var boxSet = await GetOrCreateBoxSetAsync(providerId, boxSetName).ConfigureAwait(false);
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

            // Removal depends on GetItemById and GetItemList agreeing on which instance holds
            // LinkedChildren (see the note above). Whether they do cannot be settled without a
            // running server, so verify the outcome instead of assuming it: if the membership
            // did not shrink, every removal silently no-opped and will be retried forever.
            var after = boxSet.GetLinkedChildren().Count;
            var expected = current.Count - delta.ToRemove.Count;

            if (after > expected)
            {
                log.LogWarning(
                    "Collection {BoxSet}: asked to remove {Removed} members but membership went "
                        + "from {Before} to {After} (expected {Expected}). Removals are not taking "
                        + "effect — the BoxSet instance being read is probably not the one being written.",
                    boxSetName,
                    delta.ToRemove.Count,
                    current.Count,
                    after,
                    expected
                );
            }
        }

        if (delta.ToAdd.Count > 0)
        {
            await collectionManager
                .AddToCollectionAsync(boxSet.Id, delta.ToAdd)
                .ConfigureAwait(false);
        }

        log.LogInformation(
            "Collection {Name} / {BoxSet}: +{Added} -{Removed}",
            row.Name,
            boxSetName,
            delta.ToAdd.Count,
            delta.ToRemove.Count
        );
    }

    /// <summary>
    /// The BoxSet is keyed by a stable provider id derived from the row id (and, for a
    /// grouped row, the group key), not by its name, so renaming a row or a franchise does
    /// not orphan its collection.
    /// </summary>
    private async Task<BoxSet?> GetOrCreateBoxSetAsync(string providerId, string name)
    {
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
        {
            // Identity is the provider id, so a rename has to be pushed onto the BoxSet
            // explicitly — otherwise a row renamed in the settings page keeps its old
            // display name forever, and the user has no way to tell the two apart.
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                log.LogInformation("Renaming collection {Old} to {New}", existing.Name, name);

                existing.Name = name;
                await existing
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return existing;
        }

        var created = await collectionManager
            .CreateCollectionAsync(
                new CollectionCreationOptions
                {
                    Name = name,
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
