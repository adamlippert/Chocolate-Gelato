namespace Gelato.Collections;

/// <summary>
/// The one decision that stands between a transient source failure and a wiped collection.
///
/// <para>Sources cannot always tell "this collection is legitimately empty" from "the upstream
/// API is down": a null response becomes an empty enumeration, the diff then reports every
/// current member as a removal, and the BoxSet is emptied. Layered defences push most of that
/// out of the sources themselves (a hard fetch failure throws rather than truncating), but a
/// new failure shape will eventually slip past them, so the service refuses the destructive
/// outcome outright.</para>
///
/// <para>Deliberately free of Jellyfin types so it can be tested without a server.</para>
/// </summary>
public static class CollectionSafety
{
    /// <summary>
    /// True when reconciliation must be skipped because it would empty a populated collection
    /// in a single step.
    ///
    /// <para>A row going from populated to completely empty in one run is far more likely to
    /// be a failure than a real change — franchises do not lose all their films. The false
    /// negative (a collection that really did become empty stays populated) is recoverable by
    /// deleting the row; the false positive is not, because the next run advances the refresh
    /// floor and the collection stays empty for a week.</para>
    ///
    /// <para>Note the asymmetry: this only guards the all-or-nothing case. Partial shortfalls
    /// are handled upstream, in the sources, by never dropping a title just because its detail
    /// lookup failed.</para>
    /// </summary>
    public static bool ShouldSkipEmptyReconcile(int desiredCount, int currentCount) =>
        desiredCount == 0 && currentCount > 0;
}
