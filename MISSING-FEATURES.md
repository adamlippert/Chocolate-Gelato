# Missing features

A backlog of gaps found by reading the codebase on 2026-08-16, against Jellyfin 10.11 and the
AIOStreams starter config in [aiostreams-config.json](aiostreams-config.json).

Everything here comes from static reading — there is no test project, and per
[CLAUDE.md](CLAUDE.md) a successful build proves very little about runtime behaviour in this
codebase. Treat file references as accurate and behavioural claims as needing confirmation against a
running server.

Priorities are a rough ordering, not a commitment. Upstream issue numbers refer to
[lostb1t/Gelato](https://github.com/lostb1t/Gelato/issues).

---

## A. Stream selection & playback

The plugin performs no selection logic of its own. `SyncStreams` numbers streams in the order the
addon returned them (`GelatoManager.cs:497`) and the media source list is a plain
`.OrderBy(index)`. All filtering and sorting is delegated to AIOStreams.

| # | Feature | Pri | Notes |
|---|---------|-----|-------|
| 1 | **Failover on dead streams** — pre-flight range check, advance to next source | P1 | Nothing validates the URL today; under direct play the server never observes the failure at all |
| 2 | **Client-aware auto-selection** — resolution/bitrate by device profile | P1 | Upstream #136, closed unfixed. A single AIOStreams manifest structurally cannot vary by client |
| 3 | **Binge-group continuity** — keep the same release across episodes | P1 | `bingeGroup` already persisted at `GelatoManager.cs:566`, read into an unused local at `MediaSourceManagerDecorator.cs:547` |
| 4 | **Honour `notWebReady`** → force proxy instead of direct play | P1 | Not modelled at all. Fork-critical |
| 5 | **Honour `proxyHeaders`** → `MediaSourceInfo.RequiredHttpHeaders`, or force proxy | P1 | Not modelled. Streams needing a Referer or custom UA cannot play |
| 6 | **Use `behaviorHints.videoSize`** to skip the network ffprobe | P2 | Field is parsed, never read. Cheap win |
| 7 | **Per-user stream filters** — max size, resolution, codec, audio language, uploader blocklist | P2 | Only worth building for what AIOStreams cannot know per-client |
| 8 | **Next-episode prefetch** — streams and probe warmed during playback | P2 | The subtitle prewarm at `MediaSourceManagerDecorator.cs:141` is the pattern to copy |
| 9 | **Playback-failure telemetry / auto-deprioritise** a source that failed | P3 | |
| 10 | **Cached vs uncached awareness** | — | **Not achievable** over the addon protocol; the signal exists only inside formatter-rendered text. Document `excludeUncachedFrom*` instead |

## B. Direct play (fork-specific)

| # | Feature | Pri |
|---|---------|-----|
| 11 | **Per-user `EnableDirectPlay`** — requires a new field in `UserConfig.ApplyOverrides` | P1 |
| 12 | **LAN direct / WAN proxy** policy split | P2 |
| 13 | **302-redirect endpoint** — keeps bandwidth off the server without putting the debrid token in the PlaybackInfo body | P2 |
| 14 | **Automatic client gating** — Tizen/webOS/Roku are called out as unsupported in `Config/config.html:120` as advisory text; make it enforced | P2 |

## C. Subtitles

The subtitle path is one of the better-developed areas: release-name Jaccard matching with
trusted/non-AI bonuses (`SubtitleProvider.cs:186`), filename repair in the decorator, and a
metadata-folder rescan at playback time to work around Jellyfin's non-file-protocol bail-out. The
gaps are:

| # | Feature | Pri |
|---|---------|-----|
| 15 | **Per-user manifest for subtitles** — `SubtitleProvider.cs:76` hardcodes `GetConfig(Guid.Empty)`, and the cache key omits the user, so results leak across accounts | P1 |
| 16 | **Re-fetch the list on cache miss** instead of throwing `FileNotFoundException` (`SubtitleProvider.cs:246`) | P1 |
| 17 | **Wire up or delete `/gelato/subtitles/{itemId}`** — `SetStremioSubtitlesCache` has zero callers, so the endpoint always returns `[]` | P2 |
| 18 | **Correct language tagging** — `ThreeLetterISOLanguageName = request.Language` echoes the request (`SubtitleProvider.cs:237`) | P3 |
| 19 | **Multi-language batch download** | P3 |

## D. Catalogs & library

> Items 20–26 are under active design — see the brainstorming thread on turning catalogs into
> auto-updating collections.

| # | Feature | Pri | Notes |
|---|---------|-----|-------|
| 20 | **Catalog → library/folder mapping** | P1 | Upstream #172 and #163, both open. Only `MoviePath`/`SeriesPath` exist |
| 21 | **Catalog pruning** — items dropped from a catalog remain in the library forever | P1 | Only *collection membership* is reconciled (`CatalogImportService.cs:254`). The sole cleanup is the nuclear purge task |
| 22 | **Multiple search catalogs per type** | P2 | `GelatoStremioProvider.cs:109-129` picks exactly one per type; the rest are never queried |
| 23 | **`genre` extra, and catalogs with required extras** | P2 | `IsImportable()` (`GelatoStremioProvider.cs:372`) excludes them outright — this blocks the Year and Language catalogs in the starter config |
| 24 | **`addon_catalogs` resource** | P3 | Modelled at `GelatoStremioProvider.cs:355`, never read |
| 25 | **Tag items with source platform** | P3 | Upstream #185 |
| 26 | **Hardcoded 100-item collection cap** at `CatalogImportService.cs:161` | P2 | The `MaxCollectionItems` config key exists and is unused. Upstream #143 is marked closed but this appears live |

## E. Configuration & multi-user

| # | Feature | Pri |
|---|---------|-----|
| 27 | **`Catalogs` missing from `ApplyOverrides`** (`PluginConfiguration.cs:85`) — latent today, will bite once effective config is read for catalogs | P2 |
| 28 | **`EnableJavaScriptInjection` / `LazyImages` missing from `ApplyOverrides`** — known upstream bug, already flagged in CLAUDE.md | P2 |
| 29 | **No UI for `P2PEnabled`, `P2PDLSpeed`, `P2PULSpeed`, `CatalogMaxItems`** — editable only by hand-editing the plugin XML | P2 |
| 30 | **Dead config keys** — `DisableSourceCount`, `CreateCollections`, `MaxCollectionItems` have no functional reads; implement or remove | P3 |
| 31 | **Multiple manifests per user / per-device manifest selection** — the cheapest route to per-client quality without building a filter engine; `GelatoStremioProviderFactory` already caches per base URL | P2 |
| 32 | **Link out to the AIOStreams configure page** from plugin settings | P3 (upstream #176) |
| 33 | **Catalog config is rebuilt from the live manifest on every read** — `CatalogService.GetCatalogsAsync` overwrites `config.Catalogs` and calls `SaveConfiguration()` on a GET. Any catalog absent from the current manifest silently loses its `Enabled` / `MaxItems` / `CreateCollection` settings | P1 |
| 34 | **The catalog sync task declares no default trigger** (`GetDefaultTriggers() => []`), so scheduled import never runs unless a trigger is added by hand | P2 |

## F. P2P

| # | Feature | Pri | Notes |
|---|---------|-----|-------|
| 35 | **Persistent torrent engine and piece cache** | P2 | A fresh `ClientEngine` is constructed per request (`GelatoApiController.cs:87`), so every seek or reconnect restarts the torrent from scratch |
| 36 | **Cleanup on normal completion** — teardown is registered only via `ct.Register`, i.e. on cancellation | P3 |

## G. Metadata

| # | Feature | Pri |
|---|---------|-----|
| 37 | **Chapters** | P2 (upstream #114) |
| 38 | **Trailers** — `trailers` / `trailerStreams` are parsed and never used | P3 |
| 39 | **`links`** (genre and cast link data) — parsed, never used | P3 |

## H. Operations & bigger bets

| # | Feature | Pri | Notes |
|---|---------|-----|-------|
| 40 | **Diagnostics view** — resolve times, failure counts, cache hit rate | P2 | Currently log-diving only |
| 41 | **Save a local copy / archive to library** | P2 | Upstream #186. `DownloadFilter` proxies bytes to the *client*; there is no server-side archive |
| 42 | **Watchlist / Trakt sync** as an import source | P3 | Catalog import is the only ingress |
| 43 | **Multi-addon support** beyond AIOStreams | P3 | The largest structural job — `GelatoStremioProvider` assumes a single base URL |

---

## Suggested order

If only one cluster gets done: **1–5**. Failover and `notWebReady` / `proxyHeaders` close holes that
opting into direct play opened, and binge-group continuity is nearly free since the data is already
persisted.

**15 and 16** are next — correctness bugs in a feature that already ships.

**33** is a prerequisite for any further catalog work.

## Where AIOStreams already covers the gap

The following need no plugin work; they run server-side in AIOStreams and the plugin consumes the
result: quality and resolution preferences, sort criteria, deduplication, title/year/season matching,
result limits, regex and stream-expression exclusions, proxying via MediaFlow or StremThru, and
stream-name formatting (the `formatter` template *is* the media source label rendered by
`MediaSourceManagerDecorator.cs:548`).

Duplicating any of these in the plugin produces a second filter stage unaware of the first. The
division worth keeping: **AIOStreams knows addons, debrid and cache state; the plugin knows the user,
the client, the session and the library.**
