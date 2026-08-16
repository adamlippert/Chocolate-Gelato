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
