namespace WukongBench.Core;

public sealed class ProcessLaunchTracker(DateTime requestUtc, IEnumerable<int> existingIds)
{
    private readonly HashSet<int> excluded = existingIds.ToHashSet();

    // Steam dispatch is asynchronous. The previous pass may remain enumerable
    // while it is exiting; only a process created for the new request is owned.
    public bool MayInspect(int id) => id > 0 && !excluded.Contains(id);

    public bool MayAttach(int id, DateTime startUtc, bool hasExited) =>
        MayInspect(id) && !hasExited && startUtc >= requestUtc;
}
