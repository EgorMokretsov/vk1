using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using WukongBench.Core;

namespace WukongBench;

internal static class ProcessExitDiagnostics
{
    public static void RetainHandle(Process process) => _ = process.SafeHandle;

    internal static InvalidOperationException Error(int pid, Func<int> readExitCode)
    {
        string code;
        try { code = readExitCode().ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        { code = "unavailable"; }
        return new InvalidOperationException($"Benchmark process {pid} exited unexpectedly (exit code {code}).");
    }

    public static CrashSummary? SaveCrash(string gameDirectory, string output, int pid, DateTime requestedUtc)
    {
        string root = Path.Combine(gameDirectory, "b1", "Saved", "Crashes");
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Take(10))
            {
                string file = Path.Combine(folder.FullName, "CrashContext.runtime-xml");
                if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) < requestedUtc || new FileInfo(file).Length > 4 * 1024 * 1024) continue;
                CrashSummary? summary;
                try { summary = CrashSummary.Parse(File.ReadAllText(file), pid); }
                catch (XmlException) { continue; }
                if (summary is null) continue;
                // Store selected diagnostic fields only. The original context can
                // contain account identifiers and is never copied into the report.
                File.WriteAllText(Path.Combine(output, "crash.json"), JsonSerializer.Serialize(summary, Program.Json));
                return summary;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { /* Optional evidence must not replace the original process failure. */ }
        return null;
    }
}

