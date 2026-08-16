# Auto-updating collections — design

**Date:** 2026-08-16
**Status:** approved for planning
**Repo:** Chocolate-Gelato (fork of lostb1t/Gelato)

---

## 1. Goal

Make groupings of titles — film franchises, streaming-platform line-ups, and Stremio catalog rows —
appear in Jellyfin as collections that stay current on their own, including titles the user has no
file for.

The user opens **Collections** in Jellyfin and finds "Netflix", "Trending Movies", "The Matrix
Collection". Every member is present and playable through the existing Gelato stream path. The sets
track their sources over time without manual intervention.

## 2. Scope

**In scope**

- Three source kinds: TMDB franchise collections, TMDB watch-provider (platform) line-ups, and
  AIOStreams Stremio catalogs.
- Materialisation as Jellyfin `BoxSet` collections inside the existing Movies and Shows libraries.
- A scheduled sync that reconciles collection membership against its source.
- Configuration UI for adding, removing and tuning collection rows.

**Out of scope**

- New Jellyfin libraries per source. Collections only. (Deferred — see backlog #20.)
- Any change to stream resolution or playback. `SyncStreams` and the playback path are untouched.
- Per-Jellyfin-user collections. Sync is server-wide, as catalog import is today.
- Reimplementing AIOStreams' stream filtering, sorting or dedup. See
  [MISSING-FEATURES.md](../../../MISSING-FEATURES.md) for why that division is deliberate.

## 3. Invariants

Everything below follows from these four. They are the contract.

1. **The library is an archive.** This feature creates item rows and never deletes them. Removal
   from a source affects collection membership only.
2. **A collection is a mirror.** After a successful sync, a `BoxSet`'s membership equals its source
   list as of that sync — after the row's cap and the global ceiling have been applied.
3. **Identity is TMDB + IMDb.** A title already present in the library — local file or existing
   Gelato item — is reused, never duplicated. Three collections containing the same film share one
   row.
4. **AIOStreams owns playback.** TMDB determines what a title is and which lists it belongs to. How
   to stream it comes from nowhere but AIOStreams.

Invariants 1 and 2 together resolve the feed-versus-set conflict in the current importer, where
library items accumulate forever (`CatalogImportService`) while collection membership is wiped and
refilled on every run (`CatalogImportService.cs:246-258`).

## 4. Architecture

### 4.1 `ICollectionSource`

One interface, three implementations. A source answers a single question: *which titles belong in
this collection right now?*

```
interface ICollectionSource
    CollectionKind Kind { get; }
    IAsyncEnumerable<TitleRef> EnumerateAsync(CollectionRow row, CancellationToken ct)
```

> **Amended after implementation.** This originally declared `string DisplayName { get; }`. The
> implemented member is `CollectionKind Kind`, and that is the correct one: the sync service and
> the settings API both need to select *which source serves a row*, which is what `Kind` answers.
> `DisplayName` had no consumer anywhere — the name a user sees comes from `CollectionRow.Name`,
> not from the source.

`TitleRef` carries a TMDB id, an IMDb id where known, and a media type. It also carries an
optional `GroupKey`/`GroupName` pair — see the Auto-mode grouping note below. Nothing else —
metadata acquisition is the sync service's job, not the source's.

**Auto-mode grouping (decided during implementation; neither this spec nor the plan covered
it).** One config row does not always mean one BoxSet. Franchise Auto mode discovers *N*
franchises from the library in a single enumeration, and reconciling them all into the row's
single BoxSet would produce one box of hundreds of unrelated films — not the "The Matrix
Collection" this document promises, and Auto is the default mode. So:

- A source may tag each `TitleRef` with a `GroupKey` (for franchises, the TMDB collection id)
  and a `GroupName` (`belongs_to_collection.name`, falling back to the collection response's own
  `name`; a franchise with neither is skipped rather than given an invented name).
- The sync service groups resolved titles by `GroupKey` and reconciles **each group into its own
  BoxSet**: provider id `gelato-collection.{rowId}.{groupKey}`, named `GroupName`.
- A null `GroupKey` means the row's own BoxSet — provider id `gelato-collection.{rowId}`, named
  `CollectionRow.Name`. Picked mode leaves both null, so its behaviour is unchanged.
- `MaxItems` still applies across the row as a whole, not per group: it bounds how much work one
  config entry can cause, and the user did not choose the group count.

**Auto mode's blind spot.** Discovery reads `belongs_to_collection` from library movies that
carry a TMDB provider id. Items created by earlier Gelato features (catalog import, search)
generally carry only an IMDb id, because Stremio/AIOStreams metadata is IMDb-keyed. On an
existing installation Auto mode is therefore blind to most of the library until those items are
backfilled with TMDB ids. This is a known limitation of the design, not a defect in it.

| Implementation | Enumeration |
|---|---|
| `TmdbFranchiseSource` | `/collection/{id}` for picked ids; `belongs_to_collection` on library titles for Auto mode; TMDB daily ID export for All mode |
| `TmdbPlatformSource` | `/discover/{movie,tv}` with `with_watch_providers` and `watch_region`, paged |
| `StremioCatalogSource` | AIOStreams `/catalog/{type}/{id}`, paged with `skip=` |

### 4.2 `CollectionSyncService`

Consumes any source. Three phases per row:

**Enumerate** — pull the source's title list, honouring the row's cap and the global ceiling.

**Ensure** — for each `TitleRef`, resolve to a library item:

- Look up by TMDB then IMDb id via `FindExistingItem`. A hit is reused regardless of whether it is a
  local file or an existing Gelato item.
- On a miss, create the row. Movies get a `Movie` carrying both provider ids. Series get a `Series`
  plus its season and episode tree via the existing `SyncSeriesTreesAsync` — deferred to phase 4,
  subject to §9.2.

**Reconcile** — diff the `BoxSet`'s current children against the resolved item set. Apply additions
and removals only.

The diff replaces `UpdateCollectionAsync`'s current remove-all-then-add-all. That is less code than
what exists, and it is what makes a 5,800-item collection viable — the present implementation would
rewrite every child on every run.

### 4.3 Metadata strategy

TMDB is primary. Items are created carrying TMDB and IMDb provider ids; Jellyfin's own TMDB metadata
provider populates title, overview, artwork and cast.

This is a deliberate inversion of the existing import path, which fetches metadata from AIOStreams
`/meta`. Rationale:

- AIOStreams' tmdb-addon is itself a TMDB wrapper. Going through it is an extra hop to the same data,
  normalised on the way.
- It removes an incompleteness: a franchise entry the user's meta addon does not know would
  otherwise be silently skipped.
- Collections stop depending on which meta addon happens to be enabled in AIOStreams.

Stream resolution is unaffected. `StremioUri.FromBaseItem` (`Common.cs:32`) prefers the IMDb id and
builds `tt123` or `tt123:S:E`; TMDB supplies `imdb_id` on movie detail and via series external ids.

**Series carry a risk** — see §9.2.

## 5. Source kinds and modes

| Kind | Modes | Default | Approximate size |
|---|---|---|---|
| Franchise | Auto (from library) · All · Picked | **Auto** | Auto: bounded by library. All: 10–15k collections |
| Platform | All providers in region · Picked | **Picked** | All: 100+ providers. Netflix/GB alone ≈ 5,800 titles |
| Catalog | All in manifest · Picked | **All** | 8 importable catalogs in the reference config |

Auto mode for franchises derives membership from `belongs_to_collection` on titles already in the
library, so franchises materialise from what the user owns without any picking.

## 6. Configuration

A **Collections** tab in the plugin settings.

```
Gelato ▸ Collections                                     [ + Add ]

  Franchises        Auto (from library)      every 7 days    ● on
  Netflix           Platform · GB            every 1 day     ● on
  Trending Movies   Catalog · tmdb.trending  every 1 day     ● on

  TMDB API key        [ ·············· ]
  Global item ceiling [ 25000 ]
```

### 6.1 `CollectionRow`

| Field | Meaning |
|---|---|
| `Name` | BoxSet display name |
| `Kind` | Franchise / Platform / Catalog |
| `Mode` | Auto / All / Picked |
| `SourceId` | TMDB collection id, watch-provider id, or Stremio catalog id |
| `Region` | Platform rows only |
| `MaxItems` | 0 = unlimited |
| — | *Global ceiling: when the total item count created by this feature reaches it, sync stops creating new rows, logs the shortfall, and continues reconciling existing membership. Nothing is deleted.* |
| `MinIntervalDays` | Refresh floor — see §7 |
| `Enabled` | |
| `LastSyncedUtc` | Last **successful completion** |
| `Checkpoint` | Backfill resume state — see §8.2 |

### 6.2 TMDB API key

Server-level, not a per-user setting. Resolved in order:

1. The explicit Gelato config field.
2. The Jellyfin TMDB plugin's key, read by reflection as `GetTmdbApiKey()` already does.
3. **Nothing** — collection sync stays disabled with a clear message on the settings page.

There is deliberately **no hardcoded fallback for this feature**. The key currently baked into
`GelatoStremioProvider.cs:205` is shared by every Gelato installation and is sized for occasional
release-date lookups, not for backfills in the tens of thousands of requests. `EnrichDigitalRelease
DateAsync` keeps its existing fallback unchanged.

## 7. Scheduling

Two independent clocks. This distinction is the one most likely to be misread, and the UI wording
should reflect it.

| | Controls | Set in |
|---|---|---|
| Task trigger | Whether the sync runs at all | Dashboard → Scheduled Tasks |
| Row floor (`MinIntervalDays`) | Whether a given row is due when it does run | Collections tab |

The row floor **never causes a sync** — it only suppresses one. Effective cadence is the task
frequency rounded up to the row's floor, and a row can never sync more often than the task runs.

Default triggers, following `SyncSeriesTreesTask`: startup plus every 24 hours. Note the existing
`GelatoCatalogSyncTask` declares `GetDefaultTriggers() => []` and therefore never runs unattended —
backlog #34.

UI label: `Refresh at most every [ N ] days`. A value of 0 means every task run.

Manual runs bypass the floor.

## 8. Rate limiting and backfill

### 8.1 Volume

| Operation | First run | Steady state |
|---|---|---|
| Platform discover paging (20/page) | ~290 requests | ~290 |
| TMDB detail per **new** title | ~5,800 | ~40 |
| Franchise All | 60,000+ | small |
| Franchise Auto | bounded by library | small |

The backfill is the spike; steady state is trivial.

### 8.2 Mechanisms

- **Shared concurrency limiter** across all TMDB calls, with backoff on 429 and `Retry-After`
  honoured.

  > **Amended after implementation.** This said "shared rate limiter", which implies a
  > requests-per-interval budget. What ships is a *concurrency* limiter: a semaphore bounding
  > how many TMDB requests are in flight at once, plus retry with exponential backoff that
  > honours `Retry-After` on 429. Throughput is therefore shaped by in-flight count and
  > observed pushback rather than by a fixed rate. The distinction matters if anyone later
  > reasons about worst-case request rates from this document.
- **Persistent detail cache.** The existing `_metaCache` is in-memory with a 5-minute TTL. TMDB
  detail responses want disk and a long TTL — `imdb_id` never changes.
- **Checkpointed backfill.** Progress is recorded per row so an interrupted first run resumes rather
  than restarts, and can span multiple nights.
- **Enumerate All modes via TMDB's daily ID export files** rather than walking endpoints.

`LastSyncedUtc` advances only on successful completion. Partial progress advances `Checkpoint`. A
backfill spanning three nights must not mark the row synced after night one, or the user sits on an
incomplete collection until the floor expires.

## 9. Risks

### 9.1 Jellyfin's TMDB provider on Gelato items — **unverified**

Gelato items carry `gelato://stub/...` paths, are flagged remote, and `IProviderManager` is
decorated. Whether Jellyfin's own TMDB metadata provider populates them cleanly is untested.

*If it does not:* movies need a small TMDB-to-`BaseItem` mapper. The architecture is unaffected.

### 9.2 TMDB episode numbering versus stream resolution — **unverified**

Episode streams resolve as `tt123:S:E`, built from the series IMDb id plus season and episode
numbers (`Common.cs:65-76`). Today those numbers come from the same addon chain that serves the
streams, so they agree by construction. Sourcing them from TMDB breaks that guarantee — TMDB, TVDB
and scene numbering diverge on aired-versus-DVD order, specials placement, and anime in particular.

The codebase already carries a workaround for this class of problem: `TvdbEpisodeId()`
(`GelatoStremioProvider.cs:510`) scrapes a TVDB episode id out of the thumbnail URL.

*If numbering diverges:* series fall back to the existing Stremio meta path; movies stay on TMDB.
`SyncSeriesTreesAsync` is fed a flat list of `(season, episode, title, air date)` and does not care
where it came from, so this fallback is an adapter choice, not a rewrite.

### 9.3 Jellyfin-triggered metadata refresh

`GelatoMovieMetadataProvider.FetchAsync` calls AIOStreams `/meta` once per item. A full library
metadata refresh across a large imported set would hammer AIOStreams — and Jellyfin triggers this,
not us. TMDB-sourced items should carry enough metadata that a refresh does not need to go back out.

### 9.4 Database scale

At maximum settings this is 100k+ rows, each accruing stream rows once played. Upstream has open
issues on database locking (#173) and library query performance at scale (#149). The global item
ceiling exists as a blunt safety valve; the honest mitigation is to start modest and turn it up.

## 10. Prerequisite

**Backlog #33 must be fixed first.** `CatalogService.GetCatalogsAsync` rebuilds `config.Catalogs`
from the live manifest and calls `SaveConfiguration()` on every read — a GET that writes. Any catalog
absent from the current manifest silently loses its settings. Catalog-kind rows would inherit this
bug and lose their configuration the first time an addon changes in AIOStreams.

## 11. Verification

There is no test project, and per [CLAUDE.md](../../../CLAUDE.md) a successful build proves very
little in this codebase — nearly everything is decorator behaviour that only manifests inside
Jellyfin's request pipeline. Verification is therefore manual and explicit:

1. **Warning-set diff.** Non-incremental build before and after; the baseline is 17 unique warnings.
2. **Spike, before implementation.** One item created with a TMDB id, refreshed, checked for
   metadata and artwork (§9.1). Episode numbering checked against three shows — a plain drama, one
   with specials, one anime (§9.2).
3. **Franchise row, small.** One picked collection. Confirm members appear, dedup against an
   existing local title works, and playback resolves.
4. **Diff reconcile.** Force a source change; confirm additions and removals apply, that library rows
   survive removal, and that watch state is intact.
5. **Interrupted backfill.** Cancel mid-run; confirm resume rather than restart, and that
   `LastSyncedUtc` did not advance.
6. **Floor semantics.** Confirm a row inside its floor is skipped and that a manual run bypasses it.

## 12. Phasing

Each phase ships independently.

| Phase | Content |
|---|---|
| **0** | Fix #33. Spike §9.1 and §9.2. |
| **1** | `ICollectionSource`, `CollectionSyncService`, diff reconcile, config model, settings tab, scheduled task with real triggers, rate limiter and detail cache. `TmdbFranchiseSource` in Picked and Auto modes, movies only. |
| **2** | `TmdbPlatformSource`. All modes and the daily ID export path. Checkpointed backfill. |
| **3** | `StremioCatalogSource`, porting existing catalog import onto the engine and retiring the old path. Requires the required-extra and `genre=` fixes (backlog #23). |
| **4** | Series support, subject to §9.2. |

Franchise leads because it carries no numbering risk and no addon dependency, proving the whole loop
with the fewest unknowns in play.
