using Gelato.Collections;
using Xunit;

namespace Gelato.Tests;

public class SyncScheduleTests
{
    private static readonly DateTime Now = new(2026, 8, 16, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NeverSyncedIsAlwaysDue()
    {
        Assert.True(SyncSchedule.IsDue(null, minIntervalDays: 7, Now, manual: false));
    }

    [Fact]
    public void InsideTheFloorIsNotDue()
    {
        var twoDaysAgo = Now.AddDays(-2);

        Assert.False(SyncSchedule.IsDue(twoDaysAgo, minIntervalDays: 7, Now, manual: false));
    }

    [Fact]
    public void OutsideTheFloorIsDue()
    {
        var eightDaysAgo = Now.AddDays(-8);

        Assert.True(SyncSchedule.IsDue(eightDaysAgo, minIntervalDays: 7, Now, manual: false));
    }

    [Fact]
    public void ExactlyAtTheFloorIsDue()
    {
        var sevenDaysAgo = Now.AddDays(-7);

        Assert.True(SyncSchedule.IsDue(sevenDaysAgo, minIntervalDays: 7, Now, manual: false));
    }

    [Fact]
    public void ZeroFloorMeansEveryRun()
    {
        var oneSecondAgo = Now.AddSeconds(-1);

        Assert.True(SyncSchedule.IsDue(oneSecondAgo, minIntervalDays: 0, Now, manual: false));
    }

    [Fact]
    public void ManualRunsBypassTheFloor()
    {
        var oneHourAgo = Now.AddHours(-1);

        Assert.True(SyncSchedule.IsDue(oneHourAgo, minIntervalDays: 30, Now, manual: true));
    }

    [Fact]
    public void NegativeFloorIsTreatedAsZero()
    {
        var oneSecondAgo = Now.AddSeconds(-1);

        Assert.True(SyncSchedule.IsDue(oneSecondAgo, minIntervalDays: -5, Now, manual: false));
    }

    [Fact]
    public void ClockSkewIntoTheFutureDoesNotStrandARow()
    {
        // A timestamp in the future would otherwise suppress the row indefinitely.
        var tomorrow = Now.AddDays(1);

        Assert.True(SyncSchedule.IsDue(tomorrow, minIntervalDays: 7, Now, manual: false));
    }
}
