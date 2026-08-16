using Gelato.Collections;
using Xunit;

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
