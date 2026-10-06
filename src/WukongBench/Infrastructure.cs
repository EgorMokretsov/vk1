using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WukongBench;

internal static class Shell
{
    public static async Task<string> PowerShell(string script, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
          StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(args)) start.ArgumentList.Add(a);
        using var p = Process.Start(start) ?? throw new IOException("Cannot start Windows PowerShell.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch { if (!p.HasExited) p.Kill(true); throw; }
        var output = await stdout; var error = await stderr;
        if (p.ExitCode != 0) throw new IOException("PowerShell failed: " + error);
        return output.Trim().TrimStart('\uFEFF');
    }
}

internal sealed record SteamInstallation(string SteamExe, string GameDirectory, string BuildId)
{
    public const int AppId = 3132990;
    public string GameExe => Path.Combine(GameDirectory, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe");
    public static SteamInstallation Discover(string? explicitGame)
    {
        var steamRoot = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
            ?? throw new DirectoryNotFoundException("Steam installation not found.");
        var steamExe = Path.Combine(steamRoot, "steam.exe");
        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace("\\\\", "\\")));
        foreach (var root in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = Path.Combine(root, "steamapps", $"appmanifest_{AppId}.acf");
            if (!File.Exists(manifest)) continue;
            var text = File.ReadAllText(manifest);
            string Get(string key) => Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]+)\"").Groups[1].Value;
            var directory = explicitGame ?? Path.Combine(root, "steamapps", "common", Get("installdir"));
            var result = new SteamInstallation(steamExe, Path.GetFullPath(directory), Get("buildid"));
            if (!File.Exists(result.GameExe)) throw new FileNotFoundException("Benchmark executable not found.", result.GameExe);
            return result;
        }
        throw new DirectoryNotFoundException("Install Black Myth: Wukong Benchmark Tool (Steam AppID 3132990), then run again.");
    }
}

internal sealed record BackupEntry(string RelativePath, FileAttributes Attributes);
internal sealed record BackupManifest(string ConfigDirectory, BackupEntry[] Files);

internal sealed class SettingsBackup
{
    private readonly string directory;
    private readonly BackupManifest manifest;
    private SettingsBackup(string directory, BackupManifest manifest) { this.directory = directory; this.manifest = manifest; }
    public static SettingsBackup Create(string configDirectory, string backupDirectory)
    {
        Directory.CreateDirectory(backupDirectory);
        var entries = Directory.EnumerateFiles(configDirectory, "*.ini", SearchOption.AllDirectories)
            .Select(p => new BackupEntry(Path.GetRelativePath(configDirectory, p), File.GetAttributes(p))).ToArray();
        var manifest = new BackupManifest(Path.GetFullPath(configDirectory), entries);
        foreach (var e in entries)
        {
            var destination = SafePath(backupDirectory, e.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(SafePath(configDirectory, e.RelativePath), destination);
        }
        File.WriteAllText(Path.Combine(backupDirectory, "backup.json"), JsonSerializer.Serialize(manifest, Program.Json));
        return new SettingsBackup(backupDirectory, manifest);
    }
    public static SettingsBackup Load(string directory) => new(directory,
        JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(directory, "backup.json")), Program.Json)
        ?? throw new InvalidDataException("Invalid backup manifest."));
    public void Restore()
    {
        // Validate every target before any file operation; never recursively delete a directory.
        foreach (var e in manifest.Files) { _ = SafePath(manifest.ConfigDirectory, e.RelativePath); _ = SafePath(directory, e.RelativePath); }
        var originals = manifest.Files.Select(e => e.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var current in Directory.EnumerateFiles(manifest.ConfigDirectory, "*.ini", SearchOption.AllDirectories))
        {
            if (!originals.Contains(Path.GetRelativePath(manifest.ConfigDirectory, current)))
            { File.SetAttributes(current, FileAttributes.Normal); File.Delete(current); }
        }
        foreach (var e in manifest.Files)
        {
            var target = SafePath(manifest.ConfigDirectory, e.RelativePath);
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(SafePath(directory, e.RelativePath), target, true);
            File.SetAttributes(target, e.Attributes);
        }
        File.WriteAllText(Path.Combine(directory, "restored.txt"), DateTimeOffset.Now.ToString("O"));
    }
    private static string SafePath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup path escapes its directory.");
        return path;
    }
}
