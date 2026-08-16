# Spike findings — TMDB provider and episode numbering

Run 2026-08-16 against a live server: `jellyfin.survivalbunker.xyz`, Jellyfin **10.11.11**, Gelato
**0.26.16.0** (the released build — none of the collections branch was deployed). Library: 1,626 movies
and 1,657 series under `/gelato/movies` and `/gelato/shows`, backed by a self-hosted AIOStreams.

Both spikes are answered. Neither required the new build, which is why they could run before deployment.

---

## Spike 1 — Does Jellyfin's TMDb provider populate Gelato items?

**Question.** Gelato items carry `gelato://stub/...` paths, are flagged remote, and `IProviderManager`
is decorated. Does Jellyfin's own TMDb metadata provider still enrich them, or does it decline?

**Answer: yes, comprehensively.**

Method: sampled 600 of the 1,626 movies in the Gelato Movies library via the Jellyfin API.

| Field | Coverage |
|---|---|
| `Tmdb` provider id | 596 / 600 — **99%** |
| `Imdb` provider id | 577 / 600 — 96% |
| `TmdbCollection` provider id | 203 / 600 — 33% |
| Overview | 599 / 600 — 99% |
| Genres | 593 / 600 — 98% |

`TmdbCollection` is the decisive one. **Gelato's code never sets that key anywhere** — verified by grep
across the whole repository; every occurrence is our own unrelated DTO class of the same name. Its
presence on a third of the library can only come from Jellyfin's own TMDb plugin, which is enabled as a
metadata fetcher on both Gelato libraries (ranked after Gelato in the fetcher order).

### Consequences

1. **The design's central premise holds.** Items created from TMDB data will be enriched natively by
   Jellyfin. No custom metadata mapper is needed.

2. **This gives F3 a clean fix.** The queued AIOStreams `/meta` refresh in `CreateAsync` is *redundant*
   for metadata — Jellyfin's TMDb provider already covers it. Removing `refreshItem` eliminates both
   the per-title `/meta` storm and the roughly 2× traffic against the community TMDB key hardcoded at
   `GelatoStremioProvider.cs:205`. This was the last item blocked on a server.

3. **A documented limitation was wrong.** The source comments, spec §4.1 and the follow-ups doc all
   state that pre-existing Gelato items carry no TMDB provider id, so Auto mode would be blind to most
   of an existing installation. **99% carry one.** The blind spot is negligible in practice, and that
   documentation understates the feature. See the correction applied to those files.

4. **An optimisation is now visible.** A third of items already carry `TmdbCollection` — Jellyfin has
   *already resolved their franchise*. `EnumerateAutoAsync` currently spends one `GetMovieAsync` per
   library movie rediscovering exactly that. Reading the existing provider id first would cut Auto-mode
   discovery cost substantially, and for those items to zero.

---

## Spike 2 — Does TMDB episode numbering resolve streams?

**Question.** Episode streams resolve as `tt{imdb}:{season}:{episode}`. Today those numbers come from
the same addon chain that serves the streams, so they agree by construction. Sourcing them from TMDB
breaks that guarantee — TMDB, TVDB and scene numbering diverge on aired-versus-DVD order, specials
placement, and anime in particular.

**Answer: they agree, for all three cases tested — including the hard one.**

Method: for each show, read the episode from TMDB's `/tv/{id}/season/{n}`, then request
`{aiostreams}/stream/series/{imdb}:{s}:{e}.json` and inspect what came back.

| Case | Show | S/E | TMDB title | Streams | Verdict |
|---|---|---|---|---|---|
| Plain drama | 13 Reasons Why | 2/5 | *The Chalk Machine* | 43 | ✅ |
| Has specials | Adventure Time | 5/10 | *Little Dude* | 48 | ✅ |
| Anime | Ace of the Diamond | 1/25 | *Anti-Furuya Strategy* | 28 | ✅ |
| Anime, later season | Ace of the Diamond | 2/10 | *Did You Just Pitch…* | 17 | ✅ |
| Anime, later season | Ace of the Diamond | 3/5 | *Joining* | 7 | ✅ |

Stream count alone would only prove the id resolves, not that it resolves to the *right* episode — so
the returned releases were inspected. They consistently self-label with the same season and episode
requested, and the release names use season-based numbering matching TMDB's:

```
[ASW] Ace of Diamond Act II S2 - 10 …
[zza] Diamond no Ace - S03 - 05 [1080p.x265] …
Adventure.Time.S05E10 …
```

### Consequence

**Series can use TMDB numbering in a later phase.** The fallback plan — keeping series on the Stremio
metadata path while movies use TMDB — is not needed on this evidence.

### Honest limits of this result

- Three shows on one AIOStreams configuration. It is strong evidence, not a proof over all anime.
  Absolute-numbered releases exist and would break the mapping; none appeared here.
- Ace of the Diamond was chosen because it is exactly the shape that usually diverges — long-running,
  multi-season, seasonally re-titled ("Act II"). That it agrees across seasons 1, 2 and 3 is the most
  informative single result in this table.
- Before shipping series support, re-run this against a couple of shows known to use absolute
  numbering in the wild.

---

## Not answered here

These need the collections branch actually deployed, which was not possible over the Jellyfin API —
plugin installation requires a configured repository or filesystem access:

1. Does `[Authorize(Policy = Policies.RequiresElevation)]` resolve for a plugin controller?
2. Does a row rename reach the BoxSet title (the `GetItemById` instance-identity question)?
3. Does the "removals are not taking effect" warning ever fire?
4. How many BoxSets does an Auto row create on a real library?
5. Does the empty-source safety net hold when the TMDB key is blanked?
6. Do catalog settings survive an addon disappearing from the manifest (deferred from Task 1)?

See [the follow-ups doc](../specs/2026-08-16-collections-followups.md) for what each outcome changes.
