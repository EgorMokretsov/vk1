using System.ComponentModel;
using System.Diagnostics;
using WukongBench.Core;

namespace WukongBench;

internal sealed record GameProcessSnapshot(string? Executable, DateTime StartedUtc, bool HasExited);
internal sealed record ProcessProbeFailure(int ProcessId, string ErrorType, string Message, int? NativeErrorCode);

internal static class BenchmarkProcessProbe
{
    internal static GameProcessSnapshot? Read(int pid, string expectedExecutable,
        Func<GameProcessSnapshot> inspect, ProcessLaunchTracker? tracker = null,
        Action<ProcessProbeFailure>? onFailure = null)
    {
        // Do not even open an excluded PID: an exiting process can still be
        // enumerated while Windows already refuses MainModule/StartTime reads.
        if (tracker is not null && !tracker.MayInspect(pid)) return null;
        try
        {
            var candidate = inspect();
            if (candidate.HasExited || !string.Equals(candidate.Executable, expectedExecutable, StringComparison.OrdinalIgnoreCase)) return null;
            return tracker is null || tracker.MayAttach(pid, candidate.StartedUtc, candidate.HasExited) ? candidate : null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            var failure = new ProcessProbeFailure(pid, error.GetType().Name, error.Message,
                error is Win32Exception native ? native.NativeErrorCode : null);
            if (onFailure is null)
                throw new InvalidOperationException($"Cannot inspect Wukong PID {pid}: {error.Message}", error);
            onFailure(failure);
            return null;
        }
    }

    internal static GameProcessSnapshot Inspect(Process process)
    {
        if (process.HasExited) return new(null, default, true);
        return new(process.MainModule?.FileName, process.StartTime.ToUniversalTime(), process.HasExited);
    }
}
