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
