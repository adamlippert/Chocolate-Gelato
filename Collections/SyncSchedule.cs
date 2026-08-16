namespace Gelato.Collections;

/// <summary>
/// Decides whether a collection row is due for sync.
///
/// The floor is an upper bound on frequency, not a trigger: it never causes a sync,
/// it only suppresses one. Effective cadence is the scheduled task's frequency rounded
/// up to the row's floor, so a row can never sync more often than the task runs.
/// </summary>
public static class SyncSchedule
{
    public static bool IsDue(
        DateTime? lastSyncedUtc,
        int minIntervalDays,
        DateTime nowUtc,
        bool manual
    )
    {
        if (manual)
            return true;

        // Never synced, or only ever failed — LastSyncedUtc is set on success alone.
        if (lastSyncedUtc is not { } last)
            return true;

        if (minIntervalDays <= 0)
            return true;

        // A future timestamp means the clock moved backwards. Treat it as due rather
        // than letting skew strand the row until the future catches up.
        if (last > nowUtc)
            return true;

        return nowUtc - last >= TimeSpan.FromDays(minIntervalDays);
    }
}
