using System.Globalization;

namespace Gelato.Collections;

/// <summary>
/// Maps a row plus an optional group onto the identity of a BoxSet.
///
/// <para>One config row does not always mean one BoxSet. Franchise Auto mode discovers N
/// franchises from the library in a single enumeration; collapsing them into the row's single
/// BoxSet would produce one box of hundreds of unrelated films instead of "The Matrix
/// Collection", "Alien Collection", and so on. Each group therefore gets its own BoxSet,
/// keyed by a provider id derived from the row id and the group key so that renaming either
/// the row or the franchise never orphans the collection.</para>
///
/// <para>Deliberately free of Jellyfin types so it can be tested without a server.</para>
/// </summary>
public static class CollectionGrouping
{
    /// <summary>Provider id prefix; also the exact id of an ungrouped row's BoxSet.</summary>
    public const string ProviderIdPrefix = "gelato-collection.";

    /// <summary>
    /// The stable id stored under the <c>Stremio</c> provider key.
    /// A null or blank group key yields the row's own id, unchanged from before grouping
    /// existed, so BoxSets created by earlier versions are still found.
    /// </summary>
    public static string ProviderId(string rowId, string? groupKey) =>
        string.IsNullOrWhiteSpace(groupKey)
            ? ProviderIdPrefix + rowId
            : $"{ProviderIdPrefix}{rowId}.{groupKey}";

    /// <summary>
    /// True when <paramref name="providerId"/> identifies a BoxSet owned by the given row —
    /// either the row's own BoxSet or one of its per-group BoxSets.
    /// </summary>
    public static bool OwnedByRow(string? providerId, string rowId)
    {
        if (string.IsNullOrEmpty(providerId))
            return false;

        var mine = ProviderId(rowId, null);
        return providerId.Equals(mine, StringComparison.Ordinal)
            || providerId.StartsWith(mine + ".", StringComparison.Ordinal);
    }

    /// <summary>
    /// The name a group's BoxSet should carry. Prefers the name carried on the title's
    /// <c>belongs_to_collection</c>, falls back to the collection response's own name, and
    /// returns null when neither is usable — the caller must then skip the group rather than
    /// create a BoxSet with a blank or invented name.
    /// </summary>
    public static string? ResolveGroupName(string? belongsToName, string? collectionName)
    {
        if (!string.IsNullOrWhiteSpace(belongsToName))
            return belongsToName.Trim();

        if (!string.IsNullOrWhiteSpace(collectionName))
            return collectionName.Trim();

        return null;
    }

    /// <summary>TMDB collection ids are ints; the group key is their invariant string form.</summary>
    public static string GroupKeyFor(int tmdbCollectionId) =>
        tmdbCollectionId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The display name for a reconciled group. Ungrouped titles use the row's own name;
    /// grouped titles use the group name, which the source has already validated as non-blank.
    /// </summary>
    public static string BoxSetName(string rowName, string? groupKey, string? groupName) =>
        string.IsNullOrWhiteSpace(groupKey) || string.IsNullOrWhiteSpace(groupName)
            ? rowName
            : groupName;
}
