using Gelato.Collections;
using Xunit;

namespace Gelato.Tests;

/// <summary>
/// Pins the "do not empty a populated collection in one step" rule.
///
/// This is load-bearing: without it, any TMDB failure that yields an empty enumeration —
/// a missing or revoked key, exhausted retries — reconciles to nothing, the BoxSet is
/// wiped, and the refresh floor then keeps it wiped for a week.
/// </summary>
public class CollectionSafetyTests
{
    [Fact]
    public void SkipsWhenSourceIsEmptyButCollectionIsPopulated()
    {
        Assert.True(CollectionSafety.ShouldSkipEmptyReconcile(desiredCount: 0, currentCount: 12));
    }

    [Fact]
    public void OneRemainingMemberIsStillWorthProtecting()
    {
        Assert.True(CollectionSafety.ShouldSkipEmptyReconcile(desiredCount: 0, currentCount: 1));
    }

    [Fact]
    public void AllowsEmptySourceWhenCollectionIsAlsoEmpty()
    {
        // Nothing to lose, so the run may complete and advance the refresh floor. Otherwise a
        // legitimately empty row would be permanently "due" and retried every single pass.
        Assert.False(CollectionSafety.ShouldSkipEmptyReconcile(desiredCount: 0, currentCount: 0));
    }

    [Fact]
    public void AllowsNormalReconcileWhenSourceHasTitles()
    {
        Assert.False(CollectionSafety.ShouldSkipEmptyReconcile(desiredCount: 5, currentCount: 12));
    }

    [Fact]
    public void DoesNotGuardPartialShortfalls()
    {
        // Deliberate: only the all-or-nothing case is treated as a presumed failure. Partial
        // losses are prevented in the sources, which never drop a title over a failed detail
        // lookup, and a real removal of most-but-not-all members must still be honoured.
        Assert.False(CollectionSafety.ShouldSkipEmptyReconcile(desiredCount: 1, currentCount: 40));
    }

    [Fact]
    public void AllowsFirstPopulationOfAnEmptyCollection()
    {
        Assert.False(CollectionSafety.ShouldSkipEmptyReconcile(desiredCount: 9, currentCount: 0));
    }
}
