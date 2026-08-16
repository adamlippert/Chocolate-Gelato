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
