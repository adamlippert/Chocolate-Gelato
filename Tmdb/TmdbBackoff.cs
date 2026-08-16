namespace Gelato.Tmdb;

/// <summary>
/// How long to wait before retrying a throttled TMDB request. Pure so it can be
/// tested without sleeping.
/// </summary>
public static class TmdbBackoff
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Honours <c>Retry-After</c> when TMDB sends one, otherwise backs off
    /// exponentially from one second. Always clamped to <see cref="MaxDelay"/>.
    /// </summary>
    public static TimeSpan Compute(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } after && after > TimeSpan.Zero)
            return after > MaxDelay ? MaxDelay : after;

        var n = attempt < 1 ? 1 : attempt;

        // Cap the exponent before shifting so large attempt counts cannot overflow.
        var seconds = n >= 7 ? MaxDelay.TotalSeconds : Math.Pow(2, n - 1);
        var delay = TimeSpan.FromSeconds(seconds);

        return delay > MaxDelay ? MaxDelay : delay;
    }
}
