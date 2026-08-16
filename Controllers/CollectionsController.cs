using Gelato.Collections;
using Gelato.Config;
using Gelato.Tmdb;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gelato.Controllers;

/// <summary>
/// Every endpoint here writes server-wide plugin configuration or starts unbounded background
/// work against TMDB and AIOStreams, so a bare <c>[Authorize]</c> is not enough — that would
/// let any account with a login mutate the whole server's collection rows. The other Gelato
/// controllers are read-and-trigger surfaces and are not comparable precedent.
/// </summary>
[ApiController]
[Route("gelato/collections")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CollectionsController(
    ILogger<CollectionsController> logger,
    CollectionSyncService syncService,
    IEnumerable<ICollectionSource> sources,
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

        if (!Enum.IsDefined(row.Kind))
            return BadRequest($"Unknown collection kind: {row.Kind}");

        if (!Enum.IsDefined(row.Mode))
            return BadRequest($"Unknown collection mode: {row.Mode}");

        // A defined enum value is not the same as an implemented one. Platform and Catalog
        // are declared for phase 2 and have no ICollectionSource, so a row using one would be
        // accepted, saved, and then fail on every scheduled run forever with nothing in the UI
        // to explain it. Validate against what is actually registered instead.
        if (!sources.Any(s => s.Kind == row.Kind))
        {
            var available = string.Join(", ", sources.Select(s => s.Kind.ToString()).Distinct());
            return BadRequest(
                $"Collection kind {row.Kind} is not implemented yet. Available: {available}."
            );
        }

        // Same reasoning, one level down: TmdbFranchiseSource throws on All, which needs the
        // TMDB daily ID export path (spec phase 2).
        if (row.Mode == CollectionMode.All)
        {
            return BadRequest(
                "Mode 'All' is not implemented yet. Use 'Auto' (from your library) or 'Picked'."
            );
        }

        var cfg = GelatoPlugin.Instance!.Configuration;

        if (string.IsNullOrWhiteSpace(row.Id))
            row.Id = Guid.NewGuid().ToString("N");

        var existing = cfg.CollectionRows.FirstOrDefault(r => r.Id == row.Id);
        if (existing is null)
        {
            // Server-owned state. The sync service writes these; a client must not be able to
            // seed them, or it can suppress a row's scheduled sync by claiming it just ran.
            row.LastSyncedUtc = null;
            row.Checkpoint = "";
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
