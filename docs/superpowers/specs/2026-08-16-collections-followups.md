# Auto-updating collections — follow-ups and first-run checks

Companion to [the design spec](2026-08-16-catalog-collections-design.md). Written 2026-08-16, at the
point the feature was complete and reviewed but had **never been executed against a running Jellyfin
server**. Everything below is the output of review, not observation.

---

## 1. Check these first on a real server

In priority order. The first one gates everything else.

1. **Does `[Authorize(Policy = Policies.RequiresElevation)]` resolve for a *plugin* controller?**
   The constant compiles and Jellyfin registers the policy, but a plugin controller participating in it
   is unproven. Failure mode is a 500 or a blanket 403 on every collections endpoint, so it will be
   obvious — check it before anything else.

2. **Does a row rename reach the BoxSet title?**
   `GetOrCreateBoxSetAsync` renames the instance from `GetItemList` and persists it; `ReconcileAsync`
   then swaps to the `GetItemById` instance. If those are distinct objects the repository row updates
   but a cached instance may show the old name for a while. Rename a row, sync, watch the web UI.

3. **Does the log ever say removals are not taking effect?**
   `ReconcileAsync` re-reads membership after removals and warns if the count did not drop. If that
   warning appears, BoxSet removal has never worked and collections only ever grow. This is the one
   open question no amount of static review could close — see §3.

4. **How many BoxSets does an Auto row produce on your library?**
   A row's cap bounds how many *films* it collects, not how many *collections* it creates. Run it
   against a real library and count before enabling it on a large one.

5. **Deliberately trip the safety net.**
   Blank the TMDB key (or set a bad one) and sync a populated row. Expect: an ERROR line, the BoxSet
   keeps its members, and `LastSyncedUtc` does **not** advance. This is the guard against the worst
   failure mode the feature had, so it is worth proving rather than assuming.

Also outstanding: the two spikes recorded in the design spec §9 — whether Jellyfin's own TMDB provider
populates items with `gelato://stub/...` paths, and whether TMDB episode numbering resolves streams.
The second decides whether series can use TMDB at all in a later phase.

---

## 2. Known, accepted, not fixed

| Item | Why it was left |
|---|---|
| **A backfill still reaches the shared hardcoded TMDB key ~2× per title.** `InsertMeta` → `EnrichMetaAsync` → `GetTmdbApiKey()` uses the community key at `GelatoStremioProvider.cs:205`, and the queued refresh adds one AIOStreams `/meta` call per title. A 5,800-title row costs roughly 11,600 shared-key requests. | **No longer blocked.** The spike that gated it is answered: Jellyfin's TMDb provider *does* enrich these items, so the queued refresh is redundant for metadata and can be dropped. Still unfixed — **do not enable a large row until it is** — but the fix is now clear rather than speculative. |
| **`MaxItems > 0` on an Auto row truncates mid-franchise.** The cap applies to the flat title list before grouping, so the boundary franchise reconciles against a partial member list and franchises past the cap are left stale. | Requires a non-default setting; `MaxItems` defaults to 0 (unlimited). The row-wide cap is the recorded design decision. |
| **A row rename overrides a manual BoxSet rename** on every sync. | Inherent to "the row's name is authoritative". |
| **`UpsertRow` accepts a client-supplied row `Id`.** An id containing a `.` could collide with the group-key separator and make two rows share a BoxSet. | Elevated-only and requires deliberate action. A `Guid.TryParseExact` check would close it. |
| **A permanent TMDB 404 on a Picked row is indistinguishable from a transient failure** — both throw, so a wrong `SourceId` logs an error on every run forever. | Safe direction, just noisy. |
| **`PurgeGelatoTask` sweeps BoxSets by any `Stremio` provider id**, so collection BoxSets are in its blast radius. | Pre-existing; self-healing, since the next sync recreates them. |
| **`CountFeatureItems` counts every non-stream Gelato movie**, not only items this feature created, so the global ceiling is conservative. Spec §6.1 still describes the intent rather than the behaviour. | Direction is safe. The code comment records the divergence. |
| **`RowGates` semaphores are never pruned or disposed.** | Bounded by the config file. |
| ~~**Auto mode is blind to library items with no TMDB provider id**~~ — **measured and largely false.** 99% of a live 1,626-movie Gelato library carry one, because Jellyfin's TMDb provider enriches them. | Corrected in the source and spec. Only affects libraries with that provider disabled. See [spike findings](../spikes/2026-08-16-tmdb-provider-and-numbering.md). |

---

## 3. The one thing static review could not settle

BoxSet membership is `LinkedChildren`. Jellyfin's removal path matches entries by `ItemId` or `Path`,
but the decorator persists both as null — so removal works only because `GetLinkedChildren()` populates
`ItemId` in memory, on whichever `BoxSet` instance calls it.

`ReconcileAsync` re-resolves the BoxSet through `GetItemById` immediately before reading membership,
so the population happens on the same instance the removal will use. That is correct **if**
`GetItemById` returns a shared cached instance. Neither I nor two reviewers could verify that without a
server, because the implementing assembly is not a NuGet dependency.

If the assumption fails: additions work, removals silently no-op, and the log fills with `-N` lines
while nothing changes. Hence check #3 above.

---

## 4. Deferred smaller items

Recorded during execution, none blocking:

- `Catalogs` is missing from `UserConfig.ApplyOverrides` — confirmed real, same bug class as the known
  `EnableJavaScriptInjection` / `LazyImages` omissions. Collection sync deliberately reads
  `.Configuration` directly to avoid it; **that constraint must hold** — `ApplyOverrides` copies
  `CollectionRows` as a list reference and `CollectionRow` is mutable.
- Upstream's `CatalogImportService` reads BoxSet membership by parent query rather than
  `GetLinkedChildren()`, so **its removal step has always been a no-op**. This makes
  `MISSING-FEATURES.md` #21 worse than documented.
- Auto mode does not log a failed franchise fetch the way Picked mode does.
- Stream rows are excluded by LINQ post-filter rather than the codebase's `ExcludeTags` query idiom.
- `syncCollectionRow` / `deleteCollectionRow` still show generic error strings; only save and toggle
  surface the server's message.
- A title in two TMDB collections lands only in whichever franchise was discovered first.
- Orphaned `.tmp` files if a cache write succeeds but the move fails.
