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
///
/// <para><see cref="GroupKey"/> and <see cref="GroupName"/> let one row fan out into several
/// BoxSets. A source that yields several distinct collections in one enumeration (franchise
/// Auto mode, which discovers N franchises from the library) tags each title with the
/// franchise it belongs to; the sync service then reconciles each group into its own BoxSet.
/// Both null means "one row, one BoxSet" — the original behaviour, still used by Picked
/// mode.</para>
/// </summary>
public readonly record struct TitleRef(
    int TmdbId,
    string? ImdbId,
    TitleMediaType MediaType,
    string? GroupKey = null,
    string? GroupName = null
);

/// <summary>The additions and removals needed to bring a BoxSet in line with its source.</summary>
public readonly record struct CollectionDelta(
    IReadOnlyList<Guid> ToAdd,
    IReadOnlyList<Guid> ToRemove
);
