using Gelato.Collections;
using Xunit;

namespace Gelato.Tests;

/// <summary>
/// Pins how a row plus an optional group maps onto a BoxSet identity and display name.
/// Auto mode discovers many franchises in one enumeration; without grouping they all land
/// in one BoxSet.
/// </summary>
public class CollectionGroupingTests
{
    [Fact]
    public void UngroupedProviderIdIsUnchangedFromBeforeGroupingExisted()
    {
        // BoxSets created by earlier versions must still be found, not re-created.
        Assert.Equal("gelato-collection.abc123", CollectionGrouping.ProviderId("abc123", null));
    }

    [Fact]
    public void BlankGroupKeyIsTreatedAsUngrouped()
    {
        Assert.Equal("gelato-collection.abc123", CollectionGrouping.ProviderId("abc123", "  "));
    }

    [Fact]
    public void GroupedProviderIdNestsUnderTheRow()
    {
        Assert.Equal("gelato-collection.abc123.10", CollectionGrouping.ProviderId("abc123", "10"));
    }

    [Fact]
    public void DistinctFranchisesGetDistinctProviderIds()
    {
        Assert.NotEqual(
            CollectionGrouping.ProviderId("row", "10"),
            CollectionGrouping.ProviderId("row", "1241")
        );
    }

    [Fact]
    public void RowOwnsItsOwnBoxSetAndItsGroups()
    {
        Assert.True(CollectionGrouping.OwnedByRow("gelato-collection.row", "row"));
        Assert.True(CollectionGrouping.OwnedByRow("gelato-collection.row.10", "row"));
    }

    [Fact]
    public void RowDoesNotOwnAnotherRowsBoxSets()
    {
        Assert.False(CollectionGrouping.OwnedByRow("gelato-collection.other", "row"));
        Assert.False(CollectionGrouping.OwnedByRow("gelato-collection.other.10", "row"));
        Assert.False(CollectionGrouping.OwnedByRow(null, "row"));

        // A row id that is a prefix of another row's id must not swallow it: the separator
        // is what distinguishes "row" from "row2".
        Assert.False(CollectionGrouping.OwnedByRow("gelato-collection.row2", "row"));
    }

    [Fact]
    public void GroupNamePrefersBelongsToCollection()
    {
        Assert.Equal(
            "The Matrix Collection",
            CollectionGrouping.ResolveGroupName("The Matrix Collection", "Fallback")
        );
    }

    [Fact]
    public void GroupNameFallsBackToTheCollectionResponse()
    {
        Assert.Equal(
            "Alien Collection",
            CollectionGrouping.ResolveGroupName(null, "Alien Collection")
        );
        Assert.Equal(
            "Alien Collection",
            CollectionGrouping.ResolveGroupName("  ", "Alien Collection")
        );
    }

    [Fact]
    public void GroupNameIsTrimmed()
    {
        Assert.Equal(
            "Rocky Collection",
            CollectionGrouping.ResolveGroupName(" Rocky Collection ", null)
        );
    }

    [Fact]
    public void GroupNameIsNullWhenNeitherSourceIsUsable()
    {
        // The caller must skip the franchise rather than create a blank-named BoxSet.
        Assert.Null(CollectionGrouping.ResolveGroupName(null, null));
        Assert.Null(CollectionGrouping.ResolveGroupName("", "   "));
    }

    [Fact]
    public void GroupKeyIsTheInvariantFormOfTheTmdbId()
    {
        Assert.Equal("1241", CollectionGrouping.GroupKeyFor(1241));
    }

    [Fact]
    public void UngroupedBoxSetUsesTheRowName()
    {
        Assert.Equal("Franchises", CollectionGrouping.BoxSetName("Franchises", null, null));
    }

    [Fact]
    public void GroupedBoxSetUsesTheGroupName()
    {
        Assert.Equal(
            "The Matrix Collection",
            CollectionGrouping.BoxSetName("Franchises", "2344", "The Matrix Collection")
        );
    }

    [Fact]
    public void GroupedBoxSetFallsBackToTheRowNameRatherThanGoingBlank()
    {
        Assert.Equal("Franchises", CollectionGrouping.BoxSetName("Franchises", "2344", null));
    }
}
