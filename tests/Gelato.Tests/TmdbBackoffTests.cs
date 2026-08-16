using Gelato.Tmdb;
using Xunit;

namespace Gelato.Tests;

public class TmdbBackoffTests
{
    [Fact]
    public void RetryAfterHeaderWins()
    {
        var delay = TmdbBackoff.Compute(attempt: 1, retryAfter: TimeSpan.FromSeconds(12));

        Assert.Equal(TimeSpan.FromSeconds(12), delay);
    }

    [Fact]
    public void RetryAfterIsClampedToMax()
    {
        var delay = TmdbBackoff.Compute(attempt: 1, retryAfter: TimeSpan.FromMinutes(30));

        Assert.Equal(TmdbBackoff.MaxDelay, delay);
    }

    [Fact]
    public void FirstAttemptWithoutHeaderWaitsOneSecond()
    {
        var delay = TmdbBackoff.Compute(attempt: 1, retryAfter: null);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    [Fact]
    public void DelayDoublesPerAttempt()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), TmdbBackoff.Compute(2, null));
        Assert.Equal(TimeSpan.FromSeconds(4), TmdbBackoff.Compute(3, null));
        Assert.Equal(TimeSpan.FromSeconds(8), TmdbBackoff.Compute(4, null));
    }

    [Fact]
    public void ExponentialDelayIsClampedToMax()
    {
        Assert.Equal(TmdbBackoff.MaxDelay, TmdbBackoff.Compute(attempt: 20, retryAfter: null));
    }

    [Fact]
    public void AttemptBelowOneIsTreatedAsOne()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), TmdbBackoff.Compute(attempt: 0, retryAfter: null));
    }

    [Fact]
    public void NegativeRetryAfterIsIgnored()
    {
        var delay = TmdbBackoff.Compute(attempt: 2, retryAfter: TimeSpan.FromSeconds(-5));

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }
}
