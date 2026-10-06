namespace WukongBench.Core;

public sealed class ProcessLaunchTracker(DateTime requestUtc, IEnumerable<int> existingIds)
{
    private readonly HashSet<int> excluded = existingIds.ToHashSet();

    // Steam dispatch is asynchronous. The previous pass may remain enumerable
    // while it is exiting; only a process created for the new request is owned.
    public bool MayAttach(int id, DateTime startUtc, bool hasExited) =>
        id > 0 && !hasExited && !excluded.Contains(id) && startUtc >= requestUtc;
}

