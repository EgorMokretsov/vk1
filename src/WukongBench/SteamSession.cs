using System.Text;
using System.Text.Json;
using WukongBench.Core;

namespace WukongBench;

internal static class SteamSession
{
    internal static bool? ReadRunningState(string logPath)
    {
        try
        {
            using var file = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // The end of this log contains the latest running-list event; do
            // not copy command lines or account information into diagnostics.
            file.Seek(Math.Max(0, file.Length - 128 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            return SteamRunState.IsRunning(reader.ReadToEnd());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task WaitForRelease(Func<bool?> readState, Func<CancellationToken, Task> delay,
        CancellationToken ct, int attempts = 60)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (readState() == false) return;
            await delay(ct);
        }
        throw new TimeoutException("Steam has not confirmed the previous Benchmark Tool session closed. See steam-release.json and Steam logs/gameprocess_log.txt.");
    }

    internal static async Task WaitForRelease(SteamInstallation steam, string output, CancellationToken ct)
    {
        Console.WriteLine("Ожидаю, пока Steam завершит предыдущий запуск Benchmark Tool.");
        string log = Path.Combine(Path.GetDirectoryName(steam.SteamExe)!, "logs", "gameprocess_log.txt");
        string diagnostic = Path.Combine(output, "steam-release.json");
        DateTime requested = DateTime.UtcNow;
        bool? lastState = null;
        void Save(string status) => File.WriteAllText(diagnostic, JsonSerializer.Serialize(
            new { RequestedUtc = requested, CheckedUtc = DateTime.UtcNow, Status = status, SteamReportsRunning = lastState }, Program.Json));
        Save("waiting");
        try
        {
            await WaitForRelease(() => lastState = ReadRunningState(log), token => Task.Delay(1000, token), ct);
            Save("released");
            Console.WriteLine("Steam подтвердил завершение предыдущего запуска.");
        }
        catch { Save("not-confirmed"); throw; }
    }
}
