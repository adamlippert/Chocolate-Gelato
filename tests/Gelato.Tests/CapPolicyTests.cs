using Gelato.Collections;
using Xunit;

namespace Gelato.Tests;

public class CapPolicyTests
{
    [Fact]
    public void ZeroRowCapMeansUnlimited()
    {
        Assert.Equal(int.MaxValue, CapPolicy.RowLimit(0));
    }

    [Fact]
    public void NegativeRowCapMeansUnlimited()
    {
        Assert.Equal(int.MaxValue, CapPolicy.RowLimit(-1));
    }

    [Fact]
    public void PositiveRowCapIsUsedAsIs()
    {
        Assert.Equal(250, CapPolicy.RowLimit(250));
    }

    [Fact]
    public void ZeroCeilingMeansUnlimitedBudget()
    {
        Assert.Equal(int.MaxValue, CapPolicy.RemainingBudget(0, itemsAlreadyCreated: 9999));
    }

    [Fact]
    public void BudgetIsCeilingMinusWhatExists()
    {
        Assert.Equal(400, CapPolicy.RemainingBudget(1000, itemsAlreadyCreated: 600));
    }

    [Fact]
    public void BudgetIsNeverNegative()
    {
        Assert.Equal(0, CapPolicy.RemainingBudget(1000, itemsAlreadyCreated: 1500));
    }

    [Fact]
    public void BudgetIsZeroExactlyAtTheCeiling()
    {
        Assert.Equal(0, CapPolicy.RemainingBudget(1000, itemsAlreadyCreated: 1000));
    }
}
