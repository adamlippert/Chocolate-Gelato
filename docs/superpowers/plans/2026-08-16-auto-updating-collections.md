# Auto-Updating Collections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make TMDB franchise collections appear in Jellyfin as `BoxSet` collections that refresh themselves on a schedule, including films the user has no file for.

**Architecture:** A pluggable `ICollectionSource` answers "which titles belong in this collection right now"; a `CollectionSyncService` resolves each title to a library item (reusing the existing `InsertMeta` / `FindExistingItem` path) and then diffs the `BoxSet`'s membership against that list. TMDB supplies metadata and membership; AIOStreams remains the sole source of streams. All decision logic — diffing, scheduling floors, caps, backoff — lives in Jellyfin-free static classes so it can be unit tested without a running server.

**Tech Stack:** C# / net9.0, Jellyfin 10.11.6 plugin APIs, xunit for the pure-logic tests, csharpier for formatting.

**Spec:** [`docs/superpowers/specs/2026-08-16-catalog-collections-design.md`](../specs/2026-08-16-catalog-collections-design.md)

## Global Constraints

- **Target framework `net9.0`.** CI pins the SDK to `9.0.*`. Do not raise the target.
- **Formatting is csharpier.** Run `dotnet csharpier format .` before every commit; CI lints with it. `.editorconfig` sets 4-space indent and a 100-character line limit.
- **Conventional commits are enforced in CI** (`webiny/action-conventional-commits`) on pushes to `main` and on PRs. Use `feat:` / `fix:` / `chore:` / `docs:` / `test:`.
- **Every new `PluginConfiguration` key MUST be added to `UserConfig.ApplyOverrides`**, or it silently resets to its default whenever a per-user override exists. `EnableJavaScriptInjection` and `LazyImages` already have this bug upstream — do not copy the omission.
- **`Config/config.html` is an embedded resource.** A UI change requires a rebuild to take effect; the file is compiled into `Gelato.dll`, not served from disk.
- **Warning baseline is 17 unique warnings.** Verify with:
  ```sh
  dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
  ```
  Note `--no-incremental`: a plain rebuild after a successful build recompiles nothing and reports zero warnings, which is easy to mistake for a clean result.
- **No hardcoded TMDB API key fallback for this feature.** The key at `GelatoStremioProvider.cs:205` is shared by every Gelato installation. If no user-supplied or Jellyfin-TMDB-plugin key is present, collection sync stays disabled. The existing `EnrichDigitalReleaseDateAsync` path keeps its current fallback, unchanged.
- **The four invariants from the spec §3 are the contract:**
  1. The library is an archive — this feature creates item rows and never deletes them.
  2. A collection is a mirror — after a successful sync, membership equals the source list after caps.
  3. Identity is TMDB + IMDb — a title already in the library is reused, never duplicated.
  4. AIOStreams owns playback — no code in this feature touches stream resolution.
- **The test project is deliberately NOT added to `Gelato.sln`.** Both CI jobs operate on the solution (`zyactions/dotnet-lint` with `workspace: Gelato.sln`, and the shared `jellyfin/jellyfin-meta-plugins` build workflow). Adding a test project to the solution risks changing what CI builds and publishes. Tests are run by explicit path instead.
- **Scope is spec phases 0 and 1 only.** Platform sources, catalog sources, All modes, the daily-ID-export path, checkpointed backfill, and series support are spec phases 2–4 and get their own plans.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `Collections/CollectionModels.cs` | `TitleRef`, `TitleMediaType`, `CollectionKind`, `CollectionMode`, `CollectionDelta` — no Jellyfin types |
| `Collections/CollectionDiff.cs` | Pure: current membership vs desired → additions and removals |
| `Collections/SyncSchedule.cs` | Pure: is this row due, given its floor and last successful sync |
| `Collections/CapPolicy.cs` | Pure: row cap and global ceiling arithmetic |
| `Collections/ICollectionSource.cs` | The source interface |
| `Collections/Sources/TmdbFranchiseSource.cs` | Picked and Auto franchise enumeration |
| `Collections/CollectionSyncService.cs` | Orchestration: enumerate → ensure → reconcile |
| `Tmdb/TmdbModels.cs` | DTOs for `/collection/{id}` and `/movie/{id}` |
| `Tmdb/TmdbBackoff.cs` | Pure: retry delay from attempt count and `Retry-After` |
| `Tmdb/TmdbKeyResolver.cs` | Pure: key precedence with no hardcoded fallback |
| `Tmdb/TmdbDetailCache.cs` | Persistent on-disk cache of TMDB detail responses |
| `Tmdb/TmdbClient.cs` | HTTP, throttling, retry |
| `ScheduledTasks/SyncCollectionsTask.cs` | The Jellyfin scheduled task |
| `Controllers/CollectionsController.cs` | Settings-tab backend |
| `tests/Gelato.Tests/Gelato.Tests.csproj` | Test project, outside the solution |
| `tests/Gelato.Tests/*.cs` | Unit tests for the pure classes |
| `docs/superpowers/spikes/2026-08-16-tmdb-provider-and-numbering.md` | Spike findings |

**Modified:**

| File | Change |
|---|---|
| `Services/CatalogService.cs:7-55` | Stop mutating and persisting config on read (backlog #33) |
| `Config/PluginConfiguration.cs` | Add `CollectionRows`, `TmdbApiKey`, `GlobalItemCeiling`; add all three to `ApplyOverrides` |
| `Config/config.html` | Add the Collections tab |
| `ServiceRegistrator.cs:42-44` | Register the new services |

---

# Phase 0 — Prerequisite and spikes

## Task 1: Stop `CatalogService` rewriting config on read

`GetCatalogsAsync` currently rebuilds `config.Catalogs` from the live manifest and calls `SaveConfiguration()` — a GET that writes. Any catalog absent from the current manifest silently loses its `Enabled` / `MaxItems` / `CreateCollection` settings. Collection rows would inherit this bug.

**Files:**
- Modify: `Services/CatalogService.cs:7-55`
- Test: `tests/Gelato.Tests/CatalogMergeTests.cs` (created in Task 4 — if you are doing Task 1 first, skip the test steps and rely on the manual check in Step 4)

**Interfaces:**
- Consumes: nothing from earlier tasks
- Produces: `CatalogService.GetCatalogsAsync(Guid)` keeps its signature `Task<List<CatalogConfig>>` but no longer mutates persisted state

- [ ] **Step 1: Read the current implementation**

Open `Services/CatalogService.cs`. The problem is these three behaviours in `GetCatalogsAsync`:
1. `config.Catalogs = catalogs;` — replaces the persisted list with only what the manifest currently returns
2. `GelatoPlugin.Instance.SaveConfiguration();` — persists that replacement on every read
3. Rows for catalogs missing from the manifest are dropped entirely

- [ ] **Step 2: Rewrite the merge so it is non-destructive**

Replace the body of `GetCatalogsAsync` with:

```csharp
public async Task<List<CatalogConfig>> GetCatalogsAsync(Guid userId)
{
    var config = GelatoPlugin.Instance!.Configuration;
    var provider = stremioFactory.Create(userId);
    var manifest = await provider.GetManifestAsync();

    if (manifest?.Catalogs == null)
    {
        // Manifest unreachable — return what is persisted rather than an empty list.
        return config.Catalogs;
    }

    // Start from persisted rows so settings for catalogs missing from the current
    // manifest survive. A catalog vanishing from the manifest is usually transient
    // (addon disabled, upstream hiccup) and must not destroy the user's config.
    var merged = config.Catalogs.ToList();

    foreach (var mCatalog in manifest.Catalogs)
    {
        if (!mCatalog.IsImportable())
            continue;

        var existing = merged.FirstOrDefault(c =>
            c.Id == mCatalog.Id && c.Type == mCatalog.Type
        );

        if (existing is null)
        {
            merged.Add(
                new CatalogConfig
                {
                    Id = mCatalog.Id,
                    Type = mCatalog.Type,
                    Name = mCatalog.Name,
                    Enabled = false,
                    MaxItems = 0,
                    CreateCollection = false,
                    Url = "",
                }
            );
        }
        else
        {
            existing.Name = mCatalog.Name;
        }
    }

    return merged;
}
```

Note what is gone: no `config.Catalogs = ...` assignment and no `SaveConfiguration()` call. Reads no longer write.

- [ ] **Step 3: Format and build**

```sh
dotnet csharpier format .
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: the same 17 unique warnings as the baseline. If the count changed, you introduced or removed one — investigate before continuing.

- [ ] **Step 4: Verify against a running server**

1. Configure two catalogs in the plugin UI, enable both, set `MaxItems` on one.
2. In AIOStreams, disable the addon providing one of them. Restart Jellyfin.
3. Open the plugin config page — this calls `GetCatalogsAsync`.
4. Re-enable the addon in AIOStreams. Restart Jellyfin. Open the config page.

Expected: the disabled catalog's `Enabled` and `MaxItems` are still set after step 4. Before this fix they would have been wiped at step 3.

- [ ] **Step 5: Commit**

```sh
git add Services/CatalogService.cs
git commit -m "fix: stop catalog config being rewritten from the manifest on read

GetCatalogsAsync replaced config.Catalogs with only the catalogs present in
the current manifest and persisted that on every read, so any catalog missing
from the manifest lost its Enabled/MaxItems/CreateCollection settings.

Merge is now additive and reads no longer write."
```

---

## Task 2: Spike — does Jellyfin's TMDB provider populate Gelato items?

Spec §9.1. This is an investigation, not a feature. Its output is a written finding.

**Files:**
- Create: `docs/superpowers/spikes/2026-08-16-tmdb-provider-and-numbering.md`

**Interfaces:**
- Consumes: nothing
- Produces: a documented yes/no that Task 13 depends on

- [ ] **Step 1: Create a test item carrying only TMDB and IMDb ids**

On a running Jellyfin with the plugin installed, use the existing search to pull in one film — say *The Matrix*. Then in the Jellyfin database confirm the item's provider ids include `Tmdb` and `Imdb`.

- [ ] **Step 2: Enable Jellyfin's TMDB provider for the Gelato movie library**

Dashboard → Libraries → your Gelato movies library → Metadata downloaders. Ensure **TheMovieDb** is checked. Note its position relative to the Gelato providers.

- [ ] **Step 3: Force a metadata refresh and watch the log**

Trigger "Refresh metadata" on that single item with "Replace all metadata" selected. Tail the Jellyfin log:

```sh
tail -f /path/to/jellyfin/log/log_*.log | grep -iE "tmdb|themoviedb|provider"
```

- [ ] **Step 4: Record the outcome**

Write `docs/superpowers/spikes/2026-08-16-tmdb-provider-and-numbering.md` with a section:

```markdown
## Spike 1 — Jellyfin TMDB provider on Gelato items

**Question:** Does Jellyfin's built-in TMDB metadata provider populate items with
`gelato://stub/...` paths, given `IProviderManager` is decorated?

**Method:** [what you did]

**Result:** [yes / no / partial — which fields landed, which did not]

**Consequence:** If no, Task 13 must map TMDB detail responses onto `BaseItem`
directly instead of relying on Jellyfin's provider. The architecture is unaffected.
```

Fill in every bracket. A spike with an unanswered question is a failed spike.

- [ ] **Step 5: Commit**

```sh
git add docs/superpowers/spikes/2026-08-16-tmdb-provider-and-numbering.md
git commit -m "docs: record TMDB provider spike findings"
```

---

## Task 3: Spike — does TMDB episode numbering resolve streams?

Spec §9.2. Determines whether spec phase 4 can use TMDB for series. Not needed for phase 1, but cheap to answer while you have a server in front of you, and the answer shapes later work.

**Files:**
- Modify: `docs/superpowers/spikes/2026-08-16-tmdb-provider-and-numbering.md`

**Interfaces:**
- Consumes: the file created in Task 2
- Produces: a documented finding for spec phase 4

- [ ] **Step 1: Pick three shows**

One plain drama with no numbering complications, one with a specials season, and one anime with a long continuous run. Record their IMDb ids.

- [ ] **Step 2: For each, compare TMDB numbering against what resolves**

For a given show, take an episode partway through — season 2, episode 5, say. Look up TMDB's season/episode numbers via `https://api.themoviedb.org/3/tv/{id}/season/2?api_key=KEY` and note the episode's number.

Then request the stream endpoint directly with the corresponding Stremio id:

```sh
curl -s "https://YOUR-AIOSTREAMS/stream/series/tt0903747:2:5.json" | head -c 500
```

- [ ] **Step 3: Record whether streams come back for all three**

Append to the spike document:

```markdown
## Spike 2 — TMDB episode numbering vs stream resolution

**Question:** Do season/episode numbers sourced from TMDB resolve streams via
`tt{imdb}:{season}:{episode}`?

| Show | Type | TMDB S/E | Streams returned? |
|---|---|---|---|
| [name] | drama | 2/5 | [yes/no] |
| [name] | has specials | [S/E] | [yes/no] |
| [name] | anime | [S/E] | [yes/no] |

**Result:** [all three resolve / anime diverges / other]

**Consequence:** All three resolve → spec phase 4 uses TMDB for series throughout.
Any divergence → series fall back to the existing Stremio meta path and movies
stay on TMDB. `SyncSeriesTreesAsync` takes a flat list of (season, episode, title,
air date) and does not care about its origin, so the fallback is an adapter choice,
not a rewrite.
```

- [ ] **Step 4: Commit**

```sh
git add docs/superpowers/spikes/2026-08-16-tmdb-provider-and-numbering.md
git commit -m "docs: record TMDB episode numbering spike findings"
```

---

# Phase 1 — The engine and the franchise source

## Task 4: Test project scaffold

The repo has no test project. Phase 1 introduces several pure decision functions — diffing, scheduling floors, cap arithmetic, backoff — that are worth testing properly and that need no Jellyfin server.

**Files:**
- Create: `tests/Gelato.Tests/Gelato.Tests.csproj`
- Create: `tests/Gelato.Tests/SmokeTest.cs`
- Create: `tests/README.md`

**Interfaces:**
- Consumes: nothing
- Produces: `dotnet test tests/Gelato.Tests/Gelato.Tests.csproj` as the test command used by every later task

- [ ] **Step 1: Create the test project file**

Create `tests/Gelato.Tests/Gelato.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../Gelato.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write a smoke test that proves the harness runs**

Create `tests/Gelato.Tests/SmokeTest.cs`:

```csharp
namespace Gelato.Tests;

public class SmokeTest
{
    [Fact]
    public void TestHarnessRuns()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 3: Run it**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj
```

Expected: `Passed! - Failed: 0, Passed: 1`.

If restore fails because the project reference drags in Jellyfin packages, that is expected and fine — they resolve from NuGet exactly as they do for the main project.

- [ ] **Step 4: Confirm the solution is untouched**

```sh
grep -c "Gelato.Tests" Gelato.sln || echo "not in solution — correct"
dotnet build Gelato.sln -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: `not in solution — correct`, and the same 17 warnings. The test project must not appear in `Gelato.sln` — see Global Constraints.

- [ ] **Step 5: Document how to run tests**

Create `tests/README.md`:

```markdown
# Tests

Unit tests for the Jellyfin-free decision logic — diffing, scheduling floors, cap
arithmetic, TMDB backoff and key precedence.

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj
```

This project is deliberately **not** in `Gelato.sln`. Both CI jobs operate on the
solution, and adding a test project there risks changing what CI builds and ships.

Anything that touches Jellyfin's request pipeline is not tested here — per
`CLAUDE.md`, that behaviour only manifests inside a running server and is verified
manually.
```

- [ ] **Step 6: Commit**

```sh
git add tests/
git commit -m "test: add xunit project for pure decision logic

Kept out of Gelato.sln so CI lint and the shared jellyfin build workflow are
unaffected. Run explicitly by path."
```

---

## Task 5: Configuration model

**Files:**
- Create: `Collections/CollectionModels.cs`
- Modify: `Config/PluginConfiguration.cs:11-40` (add fields) and `:85-113` (`ApplyOverrides`)

**Interfaces:**
- Consumes: nothing
- Produces:
  - `Gelato.Collections.CollectionKind` — `Franchise | Platform | Catalog`
  - `Gelato.Collections.CollectionMode` — `Auto | All | Picked`
  - `Gelato.Collections.TitleMediaType` — `Movie | Series`
  - `Gelato.Collections.TitleRef` — `readonly record struct TitleRef(int TmdbId, string? ImdbId, TitleMediaType MediaType)`
  - `Gelato.Collections.CollectionDelta` — `readonly record struct CollectionDelta(IReadOnlyList<Guid> ToAdd, IReadOnlyList<Guid> ToRemove)`
  - `Gelato.Config.CollectionRow` — the persisted row
  - `PluginConfiguration.CollectionRows` (`List<CollectionRow>`), `.TmdbApiKey` (`string`), `.GlobalItemCeiling` (`int`)

- [ ] **Step 1: Create the shared models**

Create `Collections/CollectionModels.cs`:

```csharp
namespace Gelato.Collections;

/// <summary>Which kind of source produces a collection's membership.</summary>
public enum CollectionKind
{
    Franchise,
    Platform,
    Catalog,
}

/// <summary>How much of a kind to pull in.</summary>
public enum CollectionMode
{
    /// <summary>Derived from what is already in the library.</summary>
    Auto,

    /// <summary>Everything the source offers.</summary>
    All,

    /// <summary>Only explicitly chosen ids.</summary>
    Picked,
}

public enum TitleMediaType
{
    Movie,
    Series,
}

/// <summary>
/// A title as a source knows it, before it has been resolved to a library item.
/// Deliberately free of Jellyfin types so sources stay unit testable.
/// </summary>
public readonly record struct TitleRef(int TmdbId, string? ImdbId, TitleMediaType MediaType);

/// <summary>The additions and removals needed to bring a BoxSet in line with its source.</summary>
public readonly record struct CollectionDelta(
    IReadOnlyList<Guid> ToAdd,
    IReadOnlyList<Guid> ToRemove
);
```

- [ ] **Step 2: Add `CollectionRow` to the config namespace**

Append to `Config/PluginConfiguration.cs`, after the `CatalogConfig` class:

```csharp
/// <summary>
/// One tracked collection. Persisted in the plugin's XML configuration, so every
/// property must be a public settable type the XML serializer can round-trip.
/// </summary>
public class CollectionRow
{
    /// <summary>Stable identifier for this row, independent of its source id.</summary>
    public string Id { get; set; } = "";

    /// <summary>BoxSet display name.</summary>
    public string Name { get; set; } = "";

    public Gelato.Collections.CollectionKind Kind { get; set; } =
        Gelato.Collections.CollectionKind.Franchise;

    public Gelato.Collections.CollectionMode Mode { get; set; } =
        Gelato.Collections.CollectionMode.Auto;

    /// <summary>TMDB collection id, watch-provider id, or Stremio catalog id.</summary>
    public string SourceId { get; set; } = "";

    /// <summary>ISO 3166-1 region. Platform rows only.</summary>
    public string Region { get; set; } = "";

    /// <summary>0 means unlimited.</summary>
    public int MaxItems { get; set; }

    /// <summary>
    /// Refresh floor in days. This never causes a sync — it only suppresses one.
    /// 0 means "every task run".
    /// </summary>
    public int MinIntervalDays { get; set; } = 7;

    public bool Enabled { get; set; } = true;

    /// <summary>Last <em>successful completion</em>. Failed or cancelled runs must not set this.</summary>
    public DateTime? LastSyncedUtc { get; set; }

    /// <summary>Opaque resume state for a partial backfill. Unused until spec phase 2.</summary>
    public string Checkpoint { get; set; } = "";
}
```

- [ ] **Step 3: Add the three configuration keys**

In `Config/PluginConfiguration.cs`, alongside the existing properties (after `EnableDirectPlay`):

```csharp
    /// <summary>Tracked collections. See docs/superpowers/specs/2026-08-16-catalog-collections-design.md.</summary>
    public List<CollectionRow> CollectionRows { get; set; } = [];

    /// <summary>
    /// TMDB API key for collection sync. Falls back to the Jellyfin TMDB plugin's key.
    /// There is deliberately no hardcoded fallback — without a key the feature stays off.
    /// </summary>
    public string TmdbApiKey { get; set; } = "";

    /// <summary>
    /// Maximum items this feature may create in total. 0 means unlimited. On reaching it,
    /// sync stops creating new rows, logs the shortfall, and keeps reconciling existing
    /// membership. Nothing is deleted.
    /// </summary>
    public int GlobalItemCeiling { get; set; } = 25000;
```

- [ ] **Step 4: Add all three to `ApplyOverrides`**

This is the step that is easy to skip and expensive to miss — see Global Constraints. In `UserConfig.ApplyOverrides`, in the "All other fields from base config" block:

```csharp
            CollectionRows = baseConfig.CollectionRows,
            TmdbApiKey = baseConfig.TmdbApiKey,
            GlobalItemCeiling = baseConfig.GlobalItemCeiling,
```

- [ ] **Step 5: Format, build, verify warnings**

```sh
dotnet csharpier format .
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: 17 unique warnings, unchanged.

- [ ] **Step 6: Commit**

```sh
git add Collections/CollectionModels.cs Config/PluginConfiguration.cs
git commit -m "feat: add collection row configuration model"
```

---

## Task 6: `CollectionDiff` — membership reconciliation

Replaces the wipe-and-refill in `CatalogImportService.UpdateCollectionAsync:246-258`. That version removes every child then re-adds them, which is tolerable at 100 items and punishing at several thousand.

**Files:**
- Create: `Collections/CollectionDiff.cs`
- Test: `tests/Gelato.Tests/CollectionDiffTests.cs`

**Interfaces:**
- Consumes: `CollectionDelta` from Task 5
- Produces: `Gelato.Collections.CollectionDiff.Compute(IEnumerable<Guid> current, IEnumerable<Guid> desired) → CollectionDelta`

- [ ] **Step 1: Write the failing tests**

Create `tests/Gelato.Tests/CollectionDiffTests.cs`:

```csharp
using Gelato.Collections;

namespace Gelato.Tests;

public class CollectionDiffTests
{
    private static Guid G(int n) => new Guid(n, 0, 0, new byte[8]);

    [Fact]
    public void AddsTitlesNotYetInTheCollection()
    {
        var delta = CollectionDiff.Compute(current: [G(1)], desired: [G(1), G(2)]);

        Assert.Equal([G(2)], delta.ToAdd);
        Assert.Empty(delta.ToRemove);
    }

    [Fact]
    public void RemovesTitlesNoLongerInTheSource()
    {
        var delta = CollectionDiff.Compute(current: [G(1), G(2)], desired: [G(1)]);

        Assert.Empty(delta.ToAdd);
        Assert.Equal([G(2)], delta.ToRemove);
    }

    [Fact]
    public void NoChangesWhenAlreadyInSync()
    {
        var delta = CollectionDiff.Compute(current: [G(1), G(2)], desired: [G(2), G(1)]);

        Assert.Empty(delta.ToAdd);
        Assert.Empty(delta.ToRemove);
    }

    [Fact]
    public void IgnoresDuplicatesInEitherSide()
    {
        var delta = CollectionDiff.Compute(current: [G(1), G(1)], desired: [G(1), G(2), G(2)]);

        Assert.Equal([G(2)], delta.ToAdd);
        Assert.Empty(delta.ToRemove);
    }

    [Fact]
    public void EmptyDesiredRemovesEverything()
    {
        var delta = CollectionDiff.Compute(current: [G(1), G(2)], desired: []);

        Assert.Empty(delta.ToAdd);
        Assert.Equal(2, delta.ToRemove.Count);
    }

    [Fact]
    public void EmptyCurrentAddsEverything()
    {
        var delta = CollectionDiff.Compute(current: [], desired: [G(1), G(2)]);

        Assert.Equal(2, delta.ToAdd.Count);
        Assert.Empty(delta.ToRemove);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter CollectionDiffTests
```

Expected: build failure — `CollectionDiff` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Collections/CollectionDiff.cs`:

```csharp
namespace Gelato.Collections;

/// <summary>
/// Reconciles a BoxSet's membership against its source list.
/// Deliberately free of Jellyfin types so it can be tested without a server.
/// </summary>
public static class CollectionDiff
{
    /// <summary>
    /// Returns the additions and removals needed to make <paramref name="current"/>
    /// match <paramref name="desired"/>. Duplicates on either side are ignored.
    /// </summary>
    public static CollectionDelta Compute(IEnumerable<Guid> current, IEnumerable<Guid> desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var currentSet = current.ToHashSet();
        var desiredSet = desired.ToHashSet();

        var toAdd = desiredSet.Where(id => !currentSet.Contains(id)).ToList();
        var toRemove = currentSet.Where(id => !desiredSet.Contains(id)).ToList();

        return new CollectionDelta(toAdd, toRemove);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter CollectionDiffTests
```

Expected: `Failed: 0, Passed: 6`.

- [ ] **Step 5: Format and commit**

```sh
dotnet csharpier format .
git add Collections/CollectionDiff.cs tests/Gelato.Tests/CollectionDiffTests.cs
git commit -m "feat: add collection membership diff"
```

---

## Task 7: `SyncSchedule` — the refresh floor

The row floor never causes a sync; it only suppresses one. Getting this backwards is the most likely misreading of the feature, so it gets its own tested unit.

**Files:**
- Create: `Collections/SyncSchedule.cs`
- Test: `tests/Gelato.Tests/SyncScheduleTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Gelato.Collections.SyncSchedule.IsDue(DateTime? lastSyncedUtc, int minIntervalDays, DateTime nowUtc, bool manual) → bool`

- [ ] **Step 1: Write the failing tests**

Create `tests/Gelato.Tests/SyncScheduleTests.cs`:

```csharp
using Gelato.Collections;

namespace Gelato.Tests;

public class SyncScheduleTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NeverSyncedIsAlwaysDue()
    {
        Assert.True(SyncSchedule.IsDue(null, minIntervalDays: 7, Now, manual: false));
    }

    [Fact]
    public void InsideTheFloorIsNotDue()
    {
        var twoDaysAgo = Now.AddDays(-2);

        Assert.False(SyncSchedule.IsDue(twoDaysAgo, minIntervalDays: 7, Now, manual: false));
    }

    [Fact]
    public void OutsideTheFloorIsDue()
    {
        var eightDaysAgo = Now.AddDays(-8);

        Assert.True(SyncSchedule.IsDue(eightDaysAgo, minIntervalDays: 7, Now, manual: false));
    }

    [Fact]
    public void ExactlyAtTheFloorIsDue()
    {
        var sevenDaysAgo = Now.AddDays(-7);

        Assert.True(SyncSchedule.IsDue(sevenDaysAgo, minIntervalDays: 7, Now, manual: false));
    }

    [Fact]
    public void ZeroFloorMeansEveryRun()
    {
        var oneSecondAgo = Now.AddSeconds(-1);

        Assert.True(SyncSchedule.IsDue(oneSecondAgo, minIntervalDays: 0, Now, manual: false));
    }

    [Fact]
    public void ManualRunsBypassTheFloor()
    {
        var oneHourAgo = Now.AddHours(-1);

        Assert.True(SyncSchedule.IsDue(oneHourAgo, minIntervalDays: 30, Now, manual: true));
    }

    [Fact]
    public void NegativeFloorIsTreatedAsZero()
    {
        var oneSecondAgo = Now.AddSeconds(-1);

        Assert.True(SyncSchedule.IsDue(oneSecondAgo, minIntervalDays: -5, Now, manual: false));
    }

    [Fact]
    public void ClockSkewIntoTheFutureDoesNotStrandARow()
    {
        // A timestamp in the future would otherwise suppress the row indefinitely.
        var tomorrow = Now.AddDays(1);

        Assert.True(SyncSchedule.IsDue(tomorrow, minIntervalDays: 7, Now, manual: false));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter SyncScheduleTests
```

Expected: build failure — `SyncSchedule` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Collections/SyncSchedule.cs`:

```csharp
namespace Gelato.Collections;

/// <summary>
/// Decides whether a collection row is due for sync.
///
/// The floor is an upper bound on frequency, not a trigger: it never causes a sync,
/// it only suppresses one. Effective cadence is the scheduled task's frequency rounded
/// up to the row's floor, so a row can never sync more often than the task runs.
/// </summary>
public static class SyncSchedule
{
    public static bool IsDue(
        DateTime? lastSyncedUtc,
        int minIntervalDays,
        DateTime nowUtc,
        bool manual
    )
    {
        if (manual)
            return true;

        // Never synced, or only ever failed — LastSyncedUtc is set on success alone.
        if (lastSyncedUtc is not { } last)
            return true;

        if (minIntervalDays <= 0)
            return true;

        // A future timestamp means the clock moved backwards. Treat it as due rather
        // than letting skew strand the row until the future catches up.
        if (last > nowUtc)
            return true;

        return nowUtc - last >= TimeSpan.FromDays(minIntervalDays);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter SyncScheduleTests
```

Expected: `Failed: 0, Passed: 8`.

- [ ] **Step 5: Format and commit**

```sh
dotnet csharpier format .
git add Collections/SyncSchedule.cs tests/Gelato.Tests/SyncScheduleTests.cs
git commit -m "feat: add collection refresh floor logic"
```

---

## Task 8: `CapPolicy` — row caps and the global ceiling

Two different limits that are easy to conflate. The row cap truncates a collection's membership; the global ceiling limits how many item rows the feature may create in total across all collections.

**Files:**
- Create: `Collections/CapPolicy.cs`
- Test: `tests/Gelato.Tests/CapPolicyTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `Gelato.Collections.CapPolicy.RowLimit(int rowMaxItems) → int`
  - `Gelato.Collections.CapPolicy.RemainingBudget(int globalCeiling, int itemsAlreadyCreated) → int`

- [ ] **Step 1: Write the failing tests**

Create `tests/Gelato.Tests/CapPolicyTests.cs`:

```csharp
using Gelato.Collections;

namespace Gelato.Tests;

public class CapPolicyTests
{
    [Fact]
    public void ZeroRowCapMeansUnlimited()
    {
        Assert.Equal(int.MaxValue, CapPolicy.RowLimit(0));
    }

    [Fact]
    public void NegativeRowCapMeansUnlimited()
    {
        Assert.Equal(int.MaxValue, CapPolicy.RowLimit(-1));
    }

    [Fact]
    public void PositiveRowCapIsUsedAsIs()
    {
        Assert.Equal(250, CapPolicy.RowLimit(250));
    }

    [Fact]
    public void ZeroCeilingMeansUnlimitedBudget()
    {
        Assert.Equal(int.MaxValue, CapPolicy.RemainingBudget(0, itemsAlreadyCreated: 9999));
    }

    [Fact]
    public void BudgetIsCeilingMinusWhatExists()
    {
        Assert.Equal(400, CapPolicy.RemainingBudget(1000, itemsAlreadyCreated: 600));
    }

    [Fact]
    public void BudgetIsNeverNegative()
    {
        Assert.Equal(0, CapPolicy.RemainingBudget(1000, itemsAlreadyCreated: 1500));
    }

    [Fact]
    public void BudgetIsZeroExactlyAtTheCeiling()
    {
        Assert.Equal(0, CapPolicy.RemainingBudget(1000, itemsAlreadyCreated: 1000));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter CapPolicyTests
```

Expected: build failure — `CapPolicy` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Collections/CapPolicy.cs`:

```csharp
namespace Gelato.Collections;

/// <summary>
/// Two distinct limits.
///
/// <para><see cref="RowLimit"/> truncates one collection's membership.</para>
/// <para><see cref="RemainingBudget"/> caps how many item rows this feature may create
/// in total, across every collection. On reaching it, sync stops creating rows and keeps
/// reconciling existing membership — nothing is deleted.</para>
/// </summary>
public static class CapPolicy
{
    /// <summary>Maximum members for one collection. Zero or less means unlimited.</summary>
    public static int RowLimit(int rowMaxItems) =>
        rowMaxItems <= 0 ? int.MaxValue : rowMaxItems;

    /// <summary>
    /// How many new item rows may still be created. Zero or less ceiling means unlimited.
    /// Never returns a negative value.
    /// </summary>
    public static int RemainingBudget(int globalCeiling, int itemsAlreadyCreated) =>
        globalCeiling <= 0 ? int.MaxValue : Math.Max(0, globalCeiling - itemsAlreadyCreated);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter CapPolicyTests
```

Expected: `Failed: 0, Passed: 7`.

- [ ] **Step 5: Format and commit**

```sh
dotnet csharpier format .
git add Collections/CapPolicy.cs tests/Gelato.Tests/CapPolicyTests.cs
git commit -m "feat: add collection cap and ceiling policy"
```

---

## Task 9: TMDB key resolution

Precedence with no hardcoded fallback. Kept separate from the HTTP client so the precedence rule can be tested without touching Jellyfin's plugin registry.

**Files:**
- Create: `Tmdb/TmdbKeyResolver.cs`
- Test: `tests/Gelato.Tests/TmdbKeyResolverTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Gelato.Tmdb.TmdbKeyResolver.Resolve(string? configuredKey, Func<string?> jellyfinTmdbKey) → string?`

- [ ] **Step 1: Write the failing tests**

Create `tests/Gelato.Tests/TmdbKeyResolverTests.cs`:

```csharp
using Gelato.Tmdb;

namespace Gelato.Tests;

public class TmdbKeyResolverTests
{
    [Fact]
    public void ConfiguredKeyWins()
    {
        var key = TmdbKeyResolver.Resolve("mine", () => "jellyfins");

        Assert.Equal("mine", key);
    }

    [Fact]
    public void FallsBackToJellyfinTmdbPluginKey()
    {
        var key = TmdbKeyResolver.Resolve("", () => "jellyfins");

        Assert.Equal("jellyfins", key);
    }

    [Fact]
    public void WhitespaceConfiguredKeyIsIgnored()
    {
        var key = TmdbKeyResolver.Resolve("   ", () => "jellyfins");

        Assert.Equal("jellyfins", key);
    }

    [Fact]
    public void ReturnsNullWhenNoKeyIsAvailable()
    {
        // No hardcoded fallback: without a key the feature must stay disabled rather
        // than borrow the shared key baked into the plugin.
        var key = TmdbKeyResolver.Resolve(null, () => null);

        Assert.Null(key);
    }

    [Fact]
    public void ReturnsNullWhenBothAreWhitespace()
    {
        var key = TmdbKeyResolver.Resolve("  ", () => "  ");

        Assert.Null(key);
    }

    [Fact]
    public void FallbackThrowingIsTreatedAsAbsent()
    {
        // The fallback reads another plugin's config by reflection and may throw.
        var key = TmdbKeyResolver.Resolve(null, () => throw new InvalidOperationException());

        Assert.Null(key);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbKeyResolverTests
```

Expected: build failure — `TmdbKeyResolver` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Tmdb/TmdbKeyResolver.cs`:

```csharp
namespace Gelato.Tmdb;

/// <summary>
/// Resolves the TMDB API key for collection sync.
///
/// There is deliberately no hardcoded fallback here. The key at
/// <c>GelatoStremioProvider.cs:205</c> is shared by every Gelato installation and is
/// sized for occasional release-date lookups, not for backfills in the tens of
/// thousands of requests. Without a real key the feature stays disabled.
/// </summary>
public static class TmdbKeyResolver
{
    public static string? Resolve(string? configuredKey, Func<string?> jellyfinTmdbKey)
    {
        ArgumentNullException.ThrowIfNull(jellyfinTmdbKey);

        if (!string.IsNullOrWhiteSpace(configuredKey))
            return configuredKey.Trim();

        string? fallback;
        try
        {
            fallback = jellyfinTmdbKey();
        }
        catch
        {
            // The fallback reads another plugin's configuration by reflection.
            // Treat any failure as "no key available".
            return null;
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    /// <summary>
    /// Reads the Jellyfin TMDB plugin's key without a compile-time dependency on
    /// MediaBrowser.Providers, mirroring the approach in
    /// <c>GelatoStremioProvider.GetTmdbApiKey</c>.
    /// </summary>
    public static string? FromJellyfinTmdbPlugin()
    {
        var pluginType = Type.GetType(
            "MediaBrowser.Providers.Plugins.Tmdb.Plugin, Jellyfin.Providers",
            throwOnError: false
        );
        var instance = pluginType?.GetProperty("Instance")?.GetValue(null);
        var cfg = instance?.GetType().GetProperty("Configuration")?.GetValue(instance);
        return cfg?.GetType().GetProperty("TmdbApiKey")?.GetValue(cfg) as string;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbKeyResolverTests
```

Expected: `Failed: 0, Passed: 6`.

- [ ] **Step 5: Format and commit**

```sh
dotnet csharpier format .
git add Tmdb/TmdbKeyResolver.cs tests/Gelato.Tests/TmdbKeyResolverTests.cs
git commit -m "feat: add TMDB key resolution without shared-key fallback"
```

---

## Task 10: TMDB backoff

Pure delay computation, separated from the client so it is testable without sleeping.

**Files:**
- Create: `Tmdb/TmdbBackoff.cs`
- Test: `tests/Gelato.Tests/TmdbBackoffTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `Gelato.Tmdb.TmdbBackoff.Compute(int attempt, TimeSpan? retryAfter) → TimeSpan`, and `TmdbBackoff.MaxDelay` (`TimeSpan`, 60s)

- [ ] **Step 1: Write the failing tests**

Create `tests/Gelato.Tests/TmdbBackoffTests.cs`:

```csharp
using Gelato.Tmdb;

namespace Gelato.Tests;

public class TmdbBackoffTests
{
    [Fact]
    public void RetryAfterHeaderWins()
    {
        var delay = TmdbBackoff.Compute(attempt: 1, retryAfter: TimeSpan.FromSeconds(12));

        Assert.Equal(TimeSpan.FromSeconds(12), delay);
    }

    [Fact]
    public void RetryAfterIsClampedToMax()
    {
        var delay = TmdbBackoff.Compute(attempt: 1, retryAfter: TimeSpan.FromMinutes(30));

        Assert.Equal(TmdbBackoff.MaxDelay, delay);
    }

    [Fact]
    public void FirstAttemptWithoutHeaderWaitsOneSecond()
    {
        var delay = TmdbBackoff.Compute(attempt: 1, retryAfter: null);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void DelayDoublesPerAttempt()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), TmdbBackoff.Compute(2, null));
        Assert.Equal(TimeSpan.FromSeconds(4), TmdbBackoff.Compute(3, null));
        Assert.Equal(TimeSpan.FromSeconds(8), TmdbBackoff.Compute(4, null));
    }

    [Fact]
    public void ExponentialDelayIsClampedToMax()
    {
        Assert.Equal(TmdbBackoff.MaxDelay, TmdbBackoff.Compute(attempt: 20, retryAfter: null));
    }

    [Fact]
    public void AttemptBelowOneIsTreatedAsOne()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), TmdbBackoff.Compute(attempt: 0, retryAfter: null));
    }

    [Fact]
    public void NegativeRetryAfterIsIgnored()
    {
        var delay = TmdbBackoff.Compute(attempt: 2, retryAfter: TimeSpan.FromSeconds(-5));

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbBackoffTests
```

Expected: build failure — `TmdbBackoff` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Tmdb/TmdbBackoff.cs`:

```csharp
namespace Gelato.Tmdb;

/// <summary>
/// How long to wait before retrying a throttled TMDB request. Pure so it can be
/// tested without sleeping.
/// </summary>
public static class TmdbBackoff
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Honours <c>Retry-After</c> when TMDB sends one, otherwise backs off
    /// exponentially from one second. Always clamped to <see cref="MaxDelay"/>.
    /// </summary>
    public static TimeSpan Compute(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } after && after > TimeSpan.Zero)
            return after > MaxDelay ? MaxDelay : after;

        var n = attempt < 1 ? 1 : attempt;

        // Cap the exponent before shifting so large attempt counts cannot overflow.
        var seconds = n >= 7 ? MaxDelay.TotalSeconds : Math.Pow(2, n - 1);
        var delay = TimeSpan.FromSeconds(seconds);

        return delay > MaxDelay ? MaxDelay : delay;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbBackoffTests
```

Expected: `Failed: 0, Passed: 7`.

- [ ] **Step 5: Format and commit**

```sh
dotnet csharpier format .
git add Tmdb/TmdbBackoff.cs tests/Gelato.Tests/TmdbBackoffTests.cs
git commit -m "feat: add TMDB retry backoff computation"
```

---

## Task 11: TMDB response models and meta adapter

Spec §4.3 makes TMDB primary: items are created from TMDB data, **not** by calling AIOStreams `/meta`. That inversion is the whole point — it removes the dependency on which meta addon happens to be enabled, and stops franchise entries being silently skipped when the addon does not know them.

Rather than reimplement item creation, the adapter maps a TMDB detail onto the `StremioMeta` shape that `GelatoManager.IntoBaseItem` and `InsertMeta` already consume. TMDB becomes the source of truth while the battle-tested creation path is reused unchanged.

**Files:**
- Create: `Tmdb/TmdbModels.cs`
- Create: `Tmdb/TmdbMetaAdapter.cs`
- Test: `tests/Gelato.Tests/TmdbModelsTests.cs`
- Test: `tests/Gelato.Tests/TmdbMetaAdapterTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `Gelato.Tmdb.TmdbCollection` — `int Id`, `string? Name`, `List<TmdbCollectionPart>? Parts`
  - `Gelato.Tmdb.TmdbCollectionPart` — `int Id`, `string? Title`, `string? ReleaseDate`
  - `Gelato.Tmdb.TmdbMovieDetail` — `int Id`, `string? ImdbId`, `string? Title`, `string? Overview`, `string? ReleaseDate`, `string? PosterPath`, `string? BackdropPath`, `int? Runtime`, `List<TmdbGenre>? Genres`, `double? VoteAverage`, `TmdbCollectionRef? BelongsToCollection`
  - `Gelato.Tmdb.TmdbGenre` — `int Id`, `string? Name`
  - `Gelato.Tmdb.TmdbCollectionRef` — `int Id`, `string? Name`
  - `Gelato.Tmdb.TmdbJson.Options` — the shared `JsonSerializerOptions`
  - `Gelato.Tmdb.TmdbMetaAdapter.ToStremioMeta(TmdbMovieDetail detail) → StremioMeta`

- [ ] **Step 1: Write the failing tests**

Create `tests/Gelato.Tests/TmdbModelsTests.cs`:

```csharp
using System.Text.Json;
using Gelato.Tmdb;

namespace Gelato.Tests;

public class TmdbModelsTests
{
    [Fact]
    public void ParsesACollectionResponse()
    {
        const string json = """
            {
              "id": 2344,
              "name": "The Matrix Collection",
              "parts": [
                { "id": 603, "title": "The Matrix", "release_date": "1999-03-30" },
                { "id": 604, "title": "The Matrix Reloaded", "release_date": "2003-05-15" }
              ]
            }
            """;

        var collection = JsonSerializer.Deserialize<TmdbCollection>(json, TmdbJson.Options);

        Assert.NotNull(collection);
        Assert.Equal(2344, collection!.Id);
        Assert.Equal("The Matrix Collection", collection.Name);
        Assert.Equal(2, collection.Parts!.Count);
        Assert.Equal(603, collection.Parts[0].Id);
        Assert.Equal("1999-03-30", collection.Parts[0].ReleaseDate);
    }

    [Fact]
    public void ParsesAMovieDetailWithImdbIdAndCollection()
    {
        const string json = """
            {
              "id": 603,
              "imdb_id": "tt0133093",
              "title": "The Matrix",
              "belongs_to_collection": { "id": 2344, "name": "The Matrix Collection" }
            }
            """;

        var movie = JsonSerializer.Deserialize<TmdbMovieDetail>(json, TmdbJson.Options);

        Assert.NotNull(movie);
        Assert.Equal("tt0133093", movie!.ImdbId);
        Assert.Equal(2344, movie.BelongsToCollection!.Id);
    }

    [Fact]
    public void HandlesAMovieWithNoCollection()
    {
        const string json = """
            { "id": 27205, "imdb_id": "tt1375666", "belongs_to_collection": null }
            """;

        var movie = JsonSerializer.Deserialize<TmdbMovieDetail>(json, TmdbJson.Options);

        Assert.NotNull(movie);
        Assert.Null(movie!.BelongsToCollection);
    }

    [Fact]
    public void HandlesAMissingImdbId()
    {
        const string json = """{ "id": 999999, "title": "Obscure" }""";

        var movie = JsonSerializer.Deserialize<TmdbMovieDetail>(json, TmdbJson.Options);

        Assert.NotNull(movie);
        Assert.Null(movie!.ImdbId);
    }

    [Fact]
    public void HandlesACollectionWithNoParts()
    {
        const string json = """{ "id": 1, "name": "Empty" }""";

        var collection = JsonSerializer.Deserialize<TmdbCollection>(json, TmdbJson.Options);

        Assert.NotNull(collection);
        Assert.Null(collection!.Parts);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbModelsTests
```

Expected: build failure — the TMDB types do not exist.

- [ ] **Step 3: Write the implementation**

Create `Tmdb/TmdbModels.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbModelsTests
```

Expected: `Failed: 0, Passed: 5`.

- [ ] **Step 5: Write the failing adapter tests**

Create `tests/Gelato.Tests/TmdbMetaAdapterTests.cs`:

```csharp
using Gelato;
using Gelato.Tmdb;

namespace Gelato.Tests;

public class TmdbMetaAdapterTests
{
    private static TmdbMovieDetail Matrix() =>
        new()
        {
            Id = 603,
            ImdbId = "tt0133093",
            Title = "The Matrix",
            Overview = "A hacker learns the truth.",
            ReleaseDate = "1999-03-30",
            PosterPath = "/abc.jpg",
            Runtime = 136,
            VoteAverage = 8.2,
            Genres = [new TmdbGenre { Id = 28, Name = "Action" }],
        };

    [Fact]
    public void PrefersImdbIdAsTheStremioId()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal("tt0133093", meta.Id);
    }

    [Fact]
    public void FallsBackToTmdbPrefixedIdWhenImdbIsMissing()
    {
        var detail = Matrix();
        detail.ImdbId = null;

        var meta = TmdbMetaAdapter.ToStremioMeta(detail);

        Assert.Equal("tmdb:603", meta.Id);
    }

    [Fact]
    public void MapsCoreFields()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal(StremioMediaType.Movie, meta.Type);
        Assert.Equal("The Matrix", meta.Name);
        Assert.Equal("A hacker learns the truth.", meta.Description);
        Assert.Equal("tt0133093", meta.ImdbId);
        Assert.Equal(new DateTime(1999, 3, 30), meta.Released);
        Assert.Equal(1999, meta.GetYear());
        Assert.Equal(8.2f, meta.ImdbRating);
        Assert.Equal(["Action"], meta.Genres);
    }

    [Fact]
    public void BuildsAnAbsolutePosterUrl()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal("https://image.tmdb.org/t/p/original/abc.jpg", meta.Poster);
    }

    [Fact]
    public void LeavesPosterNullWhenTmdbHasNoImage()
    {
        var detail = Matrix();
        detail.PosterPath = null;

        var meta = TmdbMetaAdapter.ToStremioMeta(detail);

        Assert.Null(meta.Poster);
    }

    [Fact]
    public void FormatsRuntimeAsMinutes()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(Matrix());

        Assert.Equal("136 min", meta.Runtime);
    }

    [Fact]
    public void SurvivesAnAlmostEmptyDetail()
    {
        var meta = TmdbMetaAdapter.ToStremioMeta(new TmdbMovieDetail { Id = 1 });

        Assert.Equal("tmdb:1", meta.Id);
        Assert.Null(meta.Released);
        Assert.Null(meta.Runtime);
        Assert.Null(meta.Poster);
    }

    [Fact]
    public void IgnoresAnUnparseableReleaseDate()
    {
        var detail = Matrix();
        detail.ReleaseDate = "";

        var meta = TmdbMetaAdapter.ToStremioMeta(detail);

        Assert.Null(meta.Released);
    }
}
```

- [ ] **Step 6: Run adapter tests to verify they fail**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbMetaAdapterTests
```

Expected: build failure — `TmdbMetaAdapter` does not exist.

- [ ] **Step 7: Write the adapter**

Create `Tmdb/TmdbMetaAdapter.cs`:

```csharp
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
```

- [ ] **Step 8: Run adapter tests to verify they pass**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbMetaAdapterTests
```

Expected: `Failed: 0, Passed: 8`.

If `GetYear()` does not return 1999, check that `Year` is being set — `StremioMeta.GetYear()` prefers `Year`, then `Released`, then `ReleaseInfo`.

- [ ] **Step 9: Format and commit**

```sh
dotnet csharpier format .
git add Tmdb/TmdbModels.cs Tmdb/TmdbMetaAdapter.cs tests/Gelato.Tests/TmdbModelsTests.cs tests/Gelato.Tests/TmdbMetaAdapterTests.cs
git commit -m "feat: add TMDB response models and StremioMeta adapter"
```

---

## Task 12: `TmdbDetailCache` and `TmdbClient`

Movie detail responses are fetched once per new title and are the bulk of a backfill. `imdb_id` never changes, so they belong on disk with a long TTL — unlike the existing in-memory `_metaCache` with its five-minute TTL.

**Files:**
- Create: `Tmdb/TmdbDetailCache.cs`
- Create: `Tmdb/TmdbClient.cs`
- Test: `tests/Gelato.Tests/TmdbDetailCacheTests.cs`

**Interfaces:**
- Consumes: `TmdbJson`, `TmdbMovieDetail`, `TmdbCollection` (Task 11); `TmdbBackoff` (Task 10); `TmdbKeyResolver` (Task 9)
- Produces:
  - `Gelato.Tmdb.TmdbDetailCache(string cacheDirectory)` with `TryGet<T>(string key, out T? value)` and `Set<T>(string key, T value)`
  - `Gelato.Tmdb.TmdbClient` with:
    - `bool IsEnabled { get; }`
    - `Task<TmdbCollection?> GetCollectionAsync(int collectionId, CancellationToken ct)`
    - `Task<TmdbMovieDetail?> GetMovieAsync(int movieId, CancellationToken ct)`

- [ ] **Step 1: Write the failing cache tests**

Create `tests/Gelato.Tests/TmdbDetailCacheTests.cs`:

```csharp
using Gelato.Tmdb;

namespace Gelato.Tests;

public class TmdbDetailCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "gelato-tests-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void MissOnAnEmptyCache()
    {
        var cache = new TmdbDetailCache(_dir);

        Assert.False(cache.TryGet<TmdbMovieDetail>("movie:603", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void RoundTripsAValue()
    {
        var cache = new TmdbDetailCache(_dir);
        cache.Set("movie:603", new TmdbMovieDetail { Id = 603, ImdbId = "tt0133093" });

        Assert.True(cache.TryGet<TmdbMovieDetail>("movie:603", out var value));
        Assert.Equal("tt0133093", value!.ImdbId);
    }

    [Fact]
    public void SurvivesANewCacheInstanceOverTheSameDirectory()
    {
        new TmdbDetailCache(_dir).Set("movie:603", new TmdbMovieDetail { Id = 603 });

        var reopened = new TmdbDetailCache(_dir);

        Assert.True(reopened.TryGet<TmdbMovieDetail>("movie:603", out var value));
        Assert.Equal(603, value!.Id);
    }

    [Fact]
    public void KeysWithPathSeparatorsDoNotEscapeTheDirectory()
    {
        var cache = new TmdbDetailCache(_dir);
        cache.Set("../../etc/passwd", new TmdbMovieDetail { Id = 1 });

        Assert.True(cache.TryGet<TmdbMovieDetail>("../../etc/passwd", out _));
        Assert.All(
            Directory.GetFiles(_dir),
            f => Assert.Equal(_dir, Path.GetDirectoryName(f))
        );
    }

    [Fact]
    public void CorruptEntriesAreTreatedAsMisses()
    {
        var cache = new TmdbDetailCache(_dir);
        cache.Set("movie:603", new TmdbMovieDetail { Id = 603 });

        File.WriteAllText(Directory.GetFiles(_dir)[0], "{ not json");

        Assert.False(cache.TryGet<TmdbMovieDetail>("movie:603", out _));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbDetailCacheTests
```

Expected: build failure — `TmdbDetailCache` does not exist.

- [ ] **Step 3: Write the cache**

Create `Tmdb/TmdbDetailCache.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gelato.Tmdb;

/// <summary>
/// On-disk cache of TMDB detail responses.
///
/// Movie details are the bulk of a backfill and their <c>imdb_id</c> never changes,
/// so they are cached indefinitely rather than with the five-minute in-memory TTL
/// used for Stremio metadata.
/// </summary>
public sealed class TmdbDetailCache(string cacheDirectory)
{
    private readonly string _dir = cacheDirectory;

    private string PathFor(string key)
    {
        // Hash the key so arbitrary ids cannot escape the cache directory.
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_dir, hash + ".json");
    }

    public bool TryGet<T>(string key, out T? value)
    {
        value = default;
        var path = PathFor(key);

        if (!File.Exists(path))
            return false;

        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), TmdbJson.Options);
            return value is not null;
        }
        catch
        {
            // A truncated or corrupt entry is a miss, not a failure.
            return false;
        }
    }

    public void Set<T>(string key, T value)
    {
        Directory.CreateDirectory(_dir);

        try
        {
            File.WriteAllText(PathFor(key), JsonSerializer.Serialize(value, TmdbJson.Options));
        }
        catch
        {
            // A cache that cannot be written is a performance problem, not a correctness
            // one. Never fail a sync because of it.
        }
    }
}
```

- [ ] **Step 4: Run cache tests to verify they pass**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj --filter TmdbDetailCacheTests
```

Expected: `Failed: 0, Passed: 5`.

- [ ] **Step 5: Write the client**

`TmdbClient` performs network I/O and is verified manually rather than unit tested.

Create `Tmdb/TmdbClient.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging;

namespace Gelato.Tmdb;

/// <summary>
/// TMDB HTTP access for collection sync: key gating, a single shared concurrency
/// limit, and retry with backoff.
/// </summary>
public sealed class TmdbClient(IHttpClientFactory httpFactory, ILogger<TmdbClient> log)
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int MaxAttempts = 5;

    // One shared gate across every caller. TMDB throttles per key and per IP, so
    // parallelism beyond this buys nothing but 429s.
    private static readonly SemaphoreSlim Gate = new(8, 8);

    private readonly TmdbDetailCache _cache = new(
        Path.Combine(Path.GetTempPath(), "gelato", "tmdb-cache")
    );

    private static string? CurrentKey() =>
        TmdbKeyResolver.Resolve(
            GelatoPlugin.Instance?.Configuration.TmdbApiKey,
            TmdbKeyResolver.FromJellyfinTmdbPlugin
        );

    /// <summary>False when no API key is available — the feature must stay off.</summary>
    public bool IsEnabled => CurrentKey() is not null;

    public Task<TmdbCollection?> GetCollectionAsync(int collectionId, CancellationToken ct) =>
        GetAsync<TmdbCollection>($"collection/{collectionId}", $"collection:{collectionId}", ct);

    public Task<TmdbMovieDetail?> GetMovieAsync(int movieId, CancellationToken ct) =>
        GetAsync<TmdbMovieDetail>($"movie/{movieId}", $"movie:{movieId}", ct);

    private async Task<T?> GetAsync<T>(string path, string cacheKey, CancellationToken ct)
        where T : class
    {
        if (_cache.TryGet<T>(cacheKey, out var cached))
            return cached;

        var key = CurrentKey();
        if (key is null)
        {
            log.LogWarning("TMDB request skipped: no API key configured for collection sync");
            return null;
        }

        var url = $"{BaseUrl}/{path}?api_key={Uri.EscapeDataString(key)}";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            HttpResponseMessage resp;
            try
            {
                var client = httpFactory.CreateClient(nameof(TmdbClient));
                client.Timeout = TimeSpan.FromSeconds(30);
                resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var value = System.Text.Json.JsonSerializer.Deserialize<T>(
                        body,
                        TmdbJson.Options
                    );
                    if (value is not null)
                        _cache.Set(cacheKey, value);
                    return value;
                }

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    log.LogDebug("TMDB 404 for {Path}", path);
                    return null;
                }

                var retryable =
                    resp.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)resp.StatusCode >= 500;

                if (!retryable || attempt == MaxAttempts)
                {
                    log.LogWarning(
                        "TMDB request failed for {Path}: {Status}",
                        path,
                        resp.StatusCode
                    );
                    return null;
                }

                var delay = TmdbBackoff.Compute(attempt, resp.Headers.RetryAfter?.Delta);
                log.LogDebug(
                    "TMDB {Status} for {Path}, retrying in {Delay}s (attempt {Attempt})",
                    resp.StatusCode,
                    path,
                    delay.TotalSeconds,
                    attempt
                );
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        return null;
    }
}
```

- [ ] **Step 6: Format, build, verify warnings**

```sh
dotnet csharpier format .
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: 17 unique warnings, unchanged.

- [ ] **Step 7: Commit**

```sh
git add Tmdb/TmdbDetailCache.cs Tmdb/TmdbClient.cs tests/Gelato.Tests/TmdbDetailCacheTests.cs
git commit -m "feat: add TMDB client with persistent detail cache and backoff"
```

---

## Task 13: `ICollectionSource` and `TmdbFranchiseSource`

Picked mode enumerates one named TMDB collection. Auto mode derives franchises from films already in the library via `belongs_to_collection`.

**Files:**
- Create: `Collections/ICollectionSource.cs`
- Create: `Collections/Sources/TmdbFranchiseSource.cs`

**Interfaces:**
- Consumes: `TitleRef`, `CollectionKind`, `CollectionMode`, `CollectionRow` (Task 5); `TmdbClient` (Task 12)
- Produces:
  - `Gelato.Collections.ICollectionSource` with `CollectionKind Kind { get; }` and `IAsyncEnumerable<TitleRef> EnumerateAsync(CollectionRow row, CancellationToken ct)`
  - `Gelato.Collections.Sources.TmdbFranchiseSource` implementing it

- [ ] **Step 1: Write the interface**

Create `Collections/ICollectionSource.cs`:

```csharp
using Gelato.Config;

namespace Gelato.Collections;

/// <summary>
/// Answers one question: which titles belong in this collection right now.
///
/// Sources deal in <see cref="TitleRef"/> only. Turning a reference into a library
/// item is <c>CollectionSyncService</c>'s job, so sources stay free of Jellyfin types.
/// </summary>
public interface ICollectionSource
{
    CollectionKind Kind { get; }

    IAsyncEnumerable<TitleRef> EnumerateAsync(CollectionRow row, CancellationToken ct);
}
```

- [ ] **Step 2: Write the franchise source**

Create `Collections/Sources/TmdbFranchiseSource.cs`:

```csharp
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
/// each movie's <c>belongs_to_collection</c>. Bounded by the library rather than by TMDB.</para>
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
```

- [ ] **Step 3: Format, build, verify warnings**

```sh
dotnet csharpier format .
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: 17 unique warnings, unchanged.

- [ ] **Step 4: Commit**

```sh
git add Collections/ICollectionSource.cs Collections/Sources/TmdbFranchiseSource.cs
git commit -m "feat: add TMDB franchise collection source"
```

---

## Task 14: `CollectionSyncService`

Orchestration. Enumerate the source, resolve each `TitleRef` to a library item, diff the BoxSet.

**Files:**
- Create: `Collections/CollectionSyncService.cs`

**Interfaces:**
- Consumes: `ICollectionSource` (Task 13), `CollectionDiff` (Task 6), `SyncSchedule` (Task 7), `CapPolicy` (Task 8), `CollectionRow` (Task 5), `TmdbClient` and `TmdbMetaAdapter` (Tasks 11–12)
- Produces:
  - `Gelato.Collections.CollectionSyncService.SyncAllAsync(CancellationToken ct, IProgress<double>? progress, bool manual)`
  - `Gelato.Collections.CollectionSyncService.SyncRowAsync(CollectionRow row, CancellationToken ct, bool manual) → Task<bool>` (true when it ran to completion)

- [ ] **Step 1: Write the service**

Create `Collections/CollectionSyncService.cs`:

```csharp
using Gelato.Config;
using Gelato.Tmdb;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Collections;
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
    Gelato.Tmdb.TmdbClient tmdb,
    ILogger<CollectionSyncService> log
)
{
    public async Task SyncAllAsync(
        CancellationToken ct,
        IProgress<double>? progress = null,
        bool manual = false
    )
    {
        var cfg = GelatoPlugin.Instance!.Configuration;
        var rows = cfg.CollectionRows.Where(r => r.Enabled).ToList();

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
                throw;
            }
            catch (Exception ex)
            {
                // One bad row must not stop the rest.
                log.LogError(ex, "Collection sync failed for {Name}", rows[i].Name);
            }

            progress?.Report((i + 1) / (double)rows.Count * 100.0);
        }

        GelatoPlugin.Instance.SaveConfiguration();
        progress?.Report(100);
    }

    /// <summary>Returns true when the row ran to completion; false when it was skipped.</summary>
    public async Task<bool> SyncRowAsync(CollectionRow row, CancellationToken ct, bool manual)
    {
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

        var cfg = GelatoPlugin.Instance!.Configuration;
        var rowLimit = CapPolicy.RowLimit(row.MaxItems);
        var budget = CapPolicy.RemainingBudget(cfg.GlobalItemCeiling, CountFeatureItems());

        var desired = new List<Guid>();
        var skippedForBudget = 0;

        await foreach (var titleRef in source.EnumerateAsync(row, ct).ConfigureAwait(false))
        {
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

        // Only a completed run advances the clock. A cancelled or failed run throws
        // before reaching here, so the row stays due.
        row.LastSyncedUtc = DateTime.UtcNow;

        log.LogInformation(
            "Collection {Name} synced: {Count} members",
            row.Name,
            desired.Count
        );

        return true;
    }

    /// <summary>
    /// How many item rows exist under the Gelato movie folder, excluding per-stream rows.
    /// Stream rows are created by <c>SyncStreams</c> on playback, not by this feature, so
    /// counting them would exhaust the ceiling for the wrong reason.
    /// </summary>
    private int CountFeatureItems()
    {
        var parent = manager.TryGetMovieFolder(GelatoPlugin.Instance!.Configuration);
        if (parent is null)
            return 0;

        return libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    ParentId = parent.Id,
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Video>()
            .Count(v => !v.IsStream());
    }

    private BaseItem? FindExisting(TitleRef titleRef)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(MetadataProvider.Tmdb)] = titleRef.TmdbId.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            ),
        };

        if (!string.IsNullOrWhiteSpace(titleRef.ImdbId))
            ids[nameof(MetadataProvider.Imdb)] = titleRef.ImdbId!;

        var probe = new Movie { ProviderIds = ids };
        return manager.FindExistingItem(probe);
    }

    private async Task<BaseItem?> CreateAsync(TitleRef titleRef, CancellationToken ct)
    {
        var parent = manager.TryGetMovieFolder(GelatoPlugin.Instance!.Configuration);
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

        var (item, _) = await manager
            .InsertMeta(
                parent,
                meta,
                user: null,
                allowRemoteRefresh: true,
                refreshItem: true,
                queueRefreshItem: false,
                ct
            )
            .ConfigureAwait(false);

        return item;
    }

    private async Task ReconcileAsync(CollectionRow row, List<Guid> desired, CancellationToken ct)
    {
        var boxSet = await GetOrCreateBoxSetAsync(row).ConfigureAwait(false);
        if (boxSet is null)
            return;

        var current = libraryManager
            .GetItemList(new InternalItemsQuery { Parent = boxSet, Recursive = false })
            .Select(i => i.Id)
            .ToList();

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
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        { "Stremio", providerId },
                    },
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
```

- [ ] **Step 2: Format, build, verify warnings**

```sh
dotnet csharpier format .
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: 17 unique warnings, unchanged. If `FindExistingItem` or `InsertMeta` signatures do not match what is written above, correct the call sites to match `GelatoManager` — do not change `GelatoManager`.

- [ ] **Step 3: Commit**

```sh
git add Collections/CollectionSyncService.cs
git commit -m "feat: add collection sync service"
```

---

## Task 15: Scheduled task

**Files:**
- Create: `ScheduledTasks/SyncCollectionsTask.cs`

**Interfaces:**
- Consumes: `CollectionSyncService.SyncAllAsync` (Task 14)
- Produces: `Gelato.ScheduledTasks.SyncCollectionsTask`, key `SyncGelatoCollections`

- [ ] **Step 1: Write the task**

Create `ScheduledTasks/SyncCollectionsTask.cs`, following the trigger shape already used by `SyncSeriesTreesTask`:

```csharp
using Gelato.Collections;
using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks;

public sealed class SyncCollectionsTask(CollectionSyncService syncService) : IScheduledTask
{
    public string Name => "Sync Gelato collections";
    public string Key => "SyncGelatoCollections";

    public string Description =>
        "Refreshes tracked collections against their sources. Each row is skipped if it "
        + "synced more recently than its own refresh floor, so this task can run often "
        + "without re-fetching expensive sources.";

    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await syncService
            .SyncAllAsync(cancellationToken, progress, manual: false)
            .ConfigureAwait(false);
        progress.Report(100);
    }
}
```

Note the default triggers are declared, unlike `GelatoCatalogSyncTask` which returns an empty array and therefore never runs unattended (backlog #34).

- [ ] **Step 2: Format, build, verify warnings**

```sh
dotnet csharpier format .
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: 17 unique warnings, unchanged.

- [ ] **Step 3: Commit**

```sh
git add ScheduledTasks/SyncCollectionsTask.cs
git commit -m "feat: add collection sync scheduled task"
```

---

## Task 16: Settings-tab API

**Files:**
- Create: `Controllers/CollectionsController.cs`

**Interfaces:**
- Consumes: `CollectionRow` (Task 5), `TmdbClient` (Task 12), `CollectionSyncService` (Task 14)
- Produces: `GET/POST/DELETE gelato/collections`, `POST gelato/collections/{id}/sync`, `GET gelato/collections/status`

- [ ] **Step 1: Write the controller**

Create `Controllers/CollectionsController.cs`, matching the attribute pattern in `CatalogController`:

```csharp
using Gelato.Collections;
using Gelato.Config;
using Gelato.Tmdb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gelato.Controllers;

[ApiController]
[Route("gelato/collections")]
[Authorize]
public class CollectionsController(
    ILogger<CollectionsController> logger,
    CollectionSyncService syncService,
    TmdbClient tmdb
) : ControllerBase
{
    [HttpGet]
    public ActionResult<List<CollectionRow>> GetRows() =>
        GelatoPlugin.Instance!.Configuration.CollectionRows;

    /// <summary>Whether a TMDB key is available. The settings tab greys itself out when false.</summary>
    [HttpGet("status")]
    public ActionResult<object> GetStatus() => Ok(new { Enabled = tmdb.IsEnabled });

    [HttpPost]
    public ActionResult<CollectionRow> UpsertRow([FromBody] CollectionRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Name))
            return BadRequest("Name is required");

        var cfg = GelatoPlugin.Instance!.Configuration;

        if (string.IsNullOrWhiteSpace(row.Id))
            row.Id = Guid.NewGuid().ToString("N");

        var existing = cfg.CollectionRows.FirstOrDefault(r => r.Id == row.Id);
        if (existing is null)
        {
            cfg.CollectionRows.Add(row);
        }
        else
        {
            existing.Name = row.Name;
            existing.Kind = row.Kind;
            existing.Mode = row.Mode;
            existing.SourceId = row.SourceId;
            existing.Region = row.Region;
            existing.MaxItems = row.MaxItems;
            existing.MinIntervalDays = row.MinIntervalDays;
            existing.Enabled = row.Enabled;
            // LastSyncedUtc and Checkpoint are server-owned — never taken from the client.
        }

        GelatoPlugin.Instance.SaveConfiguration();
        return Ok(row);
    }

    [HttpDelete("{id}")]
    public ActionResult DeleteRow([FromRoute] string id)
    {
        var cfg = GelatoPlugin.Instance!.Configuration;
        var row = cfg.CollectionRows.FirstOrDefault(r => r.Id == id);
        if (row is null)
            return NotFound();

        // Removes the tracking row only. The BoxSet and its members are left alone,
        // consistent with the archive invariant.
        cfg.CollectionRows.Remove(row);
        GelatoPlugin.Instance.SaveConfiguration();
        return Ok();
    }

    [HttpPost("{id}/sync")]
    public ActionResult SyncRow([FromRoute] string id)
    {
        var row = GelatoPlugin.Instance!.Configuration.CollectionRows.FirstOrDefault(r =>
            r.Id == id
        );
        if (row is null)
            return NotFound();

        // Manual runs bypass the refresh floor. Fire and forget: a large collection
        // takes far longer than a browser will wait.
        _ = Task.Run(async () =>
        {
            try
            {
                await syncService.SyncRowAsync(row, CancellationToken.None, manual: true);
                GelatoPlugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Manual collection sync failed for {Name}", row.Name);
            }
        });

        return Accepted();
    }
}
```

- [ ] **Step 2: Format, build, verify warnings**

```sh
dotnet csharpier format .
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: 17 unique warnings, unchanged.

- [ ] **Step 3: Commit**

```sh
git add Controllers/CollectionsController.cs
git commit -m "feat: add collections settings API"
```

---

## Task 17: Registration and end-to-end verification

**Files:**
- Modify: `ServiceRegistrator.cs:42-44`
- Modify: `Config/config.html`

**Interfaces:**
- Consumes: every service from Tasks 12–16
- Produces: a working feature

- [ ] **Step 1: Register the services**

In `ServiceRegistrator.RegisterServices`, after the existing `services.AddSingleton<CatalogImportService>();` line:

```csharp
        services.AddSingleton<Gelato.Tmdb.TmdbClient>();
        services.AddSingleton<Gelato.Collections.Sources.TmdbFranchiseSource>();
        services.AddSingleton<Gelato.Collections.ICollectionSource>(sp =>
            sp.GetRequiredService<Gelato.Collections.Sources.TmdbFranchiseSource>()
        );
        services.AddSingleton<Gelato.Collections.CollectionSyncService>();
```

Jellyfin discovers `IScheduledTask` implementations by assembly scanning, so `SyncCollectionsTask` needs no explicit registration — matching how `SyncSeriesTreesTask` is handled.

- [ ] **Step 2: Add the Collections tab to the config page**

`Config/config.html` already uses a tab pattern — see `<div id="tab-catalogs" class="tab-content">` at line 141. Add a sibling tab following the same structure:

```html
          <!-- Collections Tab -->
          <div id="tab-collections" class="tab-content" style="display: none;">
            <div class="verticalSection">
              <div class="sectionTitleContainer">
                <h3 class="sectionTitle">Auto-updating collections</h3>
                <div class="fieldDescription" id="collectionsDisabledNotice"
                  style="display: none; color: #ff8a80;">
                  Collection sync is disabled. Add a TMDB API key to enable it.
                </div>
              </div>

              <div class="inputContainer">
                <label class="inputLabel inputLabelUnfocused" for="txtTmdbApiKey">
                  TMDB API key:
                </label>
                <input is="emby-input" type="text" id="txtTmdbApiKey" class="emby-input" />
                <div class="fieldDescription">
                  Falls back to the Jellyfin TMDB plugin's key. Without either, sync stays off.
                </div>
              </div>

              <div class="inputContainer">
                <label class="inputLabel inputLabelUnfocused" for="txtGlobalItemCeiling">
                  Global item ceiling:
                </label>
                <input is="emby-input" type="number" id="txtGlobalItemCeiling"
                  class="emby-input" />
                <div class="fieldDescription">
                  Maximum items this feature may create in total. 0 means unlimited.
                </div>
              </div>

              <div id="collectionRowsList"
                style="max-height: 600px; overflow-y: auto; border: 1px solid rgba(255,255,255,0.1); border-radius: 4px; background: rgba(0,0,0,0.2);">
                <!-- Rows rendered here -->
              </div>

              <div style="margin-top: 1.5em; display: flex; justify-content: flex-end;">
                <button type="button" is="emby-button" class="raised emby-button"
                  id="btnAddCollectionRow">
                  <span>Add collection</span>
                </button>
              </div>
            </div>
          </div>
```

Add the matching tab button. The header list lives at `Config/config.html:25-32`; insert this immediately after the `data-tab="catalogs"` button:

```html
              <button type="button" class="paper-icon-button-light tab-button" data-tab="collections"
                style="padding: 0.5em 0; border-bottom: 2px solid transparent; cursor: pointer; background: none; color: inherit; font-weight: bold; opacity: 0.6;">Collections</button>
```

The existing tab-switching code keys off `data-tab` and the corresponding `#tab-<name>` element id, so no JavaScript change is needed to make the tab selectable.

In the page's `loadConfig` function, alongside the existing field population:

```javascript
            if (txtTmdbApiKey) txtTmdbApiKey.value = cfg.TmdbApiKey || "";
            if (txtGlobalItemCeiling) txtGlobalItemCeiling.value = cfg.GlobalItemCeiling ?? 25000;

            const status = await window.ApiClient.getJSON(
              window.ApiClient.getUrl('gelato/collections/status')
            );
            document.querySelector('#collectionsDisabledNotice').style.display =
              status.Enabled ? 'none' : 'block';

            await loadCollectionRows();
```

In `saveConfig`, alongside the existing assignments:

```javascript
            cfg.TmdbApiKey = txtTmdbApiKey.value.trim();
            cfg.GlobalItemCeiling = parseInt(txtGlobalItemCeiling.value) || 0;
```

Collection rows are managed through `gelato/collections` rather than the plugin config blob, so `saveConfig` must not write `cfg.CollectionRows` — doing so would race with the controller.

- [ ] **Step 3: Format, build, verify warnings**

```sh
dotnet csharpier format .
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning (CS|MSB)" | sed -E 's/\([0-9]+,[0-9]+\)//' | sort -u
```

Expected: 17 unique warnings, unchanged.

- [ ] **Step 4: Run the full unit suite**

```sh
dotnet test tests/Gelato.Tests/Gelato.Tests.csproj
```

Expected: `Failed: 0, Passed: 53` (1 smoke + 6 diff + 8 schedule + 7 cap + 6 key + 7 backoff + 5 models + 8 adapter + 5 cache).

- [ ] **Step 5: Verify end to end on a running server**

Install the built plugin and work through spec §11:

1. **No key.** Clear both the Gelato key and the Jellyfin TMDB plugin's key. Open the Collections tab. Expect the disabled notice and no sync on task run.
2. **Franchise row, Picked.** Add a key. Add a row: Kind Franchise, Mode Picked, `SourceId` 2344 (The Matrix Collection), floor 7 days. Press Run. Expect a "The Matrix Collection" BoxSet containing four films, playable through the normal stream path.
3. **Dedup.** Confirm that if you already had *The Matrix* in the library — local file or Gelato item — it was reused rather than duplicated. Check for exactly one item with IMDb id `tt0133093`.
4. **Diff reconcile.** Change the row's `MaxItems` to 2 and re-run manually. Expect two members remain in the BoxSet, and the removed two still present in the Movies library with watch state intact.
5. **Floor semantics.** Run the scheduled task immediately after a successful sync. Expect a "not due" debug line and no TMDB traffic. Then press Run on the row itself and expect it to sync — manual bypasses the floor.
6. **Failure does not advance the clock.** Set an invalid TMDB key, run the row, confirm `LastSyncedUtc` is unchanged in the plugin XML.
7. **Auto mode.** Switch the row to Mode Auto with an empty `SourceId`. Expect franchises derived from films already in your library.

- [ ] **Step 6: Commit**

```sh
dotnet csharpier format .
git add ServiceRegistrator.cs Config/config.html
git commit -m "feat: wire up auto-updating collections

Registers the TMDB client, franchise source and sync service, and adds the
Collections tab to the plugin settings page."
```

---

## Done criteria for Phase 1

- `dotnet test tests/Gelato.Tests/Gelato.Tests.csproj` passes.
- `dotnet build -c Release --no-incremental` reports the baseline 17 unique warnings.
- All seven end-to-end checks in Task 17 Step 5 pass on a running Jellyfin.
- Both spike documents are filled in with actual findings.

Spec phases 2–4 — platform sources, catalog sources, All modes, checkpointed backfill, series support — get their own plans.
