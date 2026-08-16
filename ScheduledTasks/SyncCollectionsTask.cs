using Gelato.Collections;
using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks;

public sealed class SyncCollectionsTask(CollectionSyncService syncService) : IScheduledTask
{
    public string Name => "Sync Gelato collections";
    public string Key => "SyncGelatoCollections";

    public string Description =>
        "Refreshes tracked collections against their sources. Each row is skipped if it "
        + "synced more recently than its own refresh floor, so this task can run often "
        + "without re-fetching expensive sources.";

    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await syncService
            .SyncAllAsync(cancellationToken, progress, manual: false)
            .ConfigureAwait(false);
        progress.Report(100);
    }
}
