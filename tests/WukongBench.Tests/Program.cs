using WukongBench.Core;
using WukongBench;
using System.Text.Json;

int count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
string original = """
    ; comment
    [/Script/GSGameSettings.GSGameUserSettings]
    UISettingData=(("PrivacyAgreement", "1"),("UnknownFutureSetting", "42"))
    MainMonitorID=unchanged
    [OtherSection]
    ResolutionSizeX=17
    """;
var cpu = new Profile("CPU", 1280, 720, 50, false);
var gpu = new Profile("GPU", 3840, 2160, 100, true);
var patched = new IniFile(cpu.Apply(original));
Check(patched.Get("OtherSection", "ResolutionSizeX") == "17", "section boundaries");
Check(patched.Get(Profile.Section, "MainMonitorID") == "unchanged", "preserve unknown keys");
Check(patched.ReadMap(Profile.Section, "UISettingData")["UnknownFutureSetting"] == "42", "preserve custom map entries");
Check(patched.Get("ScalabilityGroups", "sg.ShadowQuality") == "0", "CPU GPU effects low");
Check(patched.Get("ScalabilityGroups", "sg.ViewDistanceQuality") == "4", "CPU view distance retained");
cpu.Verify(cpu.Apply(original)); gpu.Verify(gpu.Apply(original)); Check(true, "verify CPU/GPU profiles");
patched.Set("ScalabilityGroups", "sg.ShadowQuality", "4"); Reject(() => cpu.Verify(patched.ToString()), "reject silently overridden settings");
Reject(() => cpu.Apply("[Unrelated]\nFoo=Bar"), "reject unknown config schema");
OcrWord W(string text, double x, double y, double w = 100, double h = 20) => new(text, x, y, w, h);
OcrPage Page(bool separate, string avg = "60.5", string min = "40", string max = "90")
{
    var labels = new[] { "Average", "Minimum", "Maximum" };
    var values = new[] { avg, min, max };
    var lines = new List<OcrLine> { new("Benchmark Results", [W("Results", 0, 0)]) };
    for (int i = 0; i < 3; i++)
    {
        double x = i * 300;
        lines.Add(new(labels[i], [W(labels[i], x, 150)]));
        lines.Add(new(values[i], [W(values[i], separate ? x : x + 130, separate ? 110 : 150, 80)]));
    }
    return new OcrPage(string.Join("\n", lines.Select(l => l.Text)), lines.ToArray());
}
Check(ResultParser.Parse(Page(false)) == new Metrics(60.5, 40, 90), "FPS on same row");
Check(ResultParser.Parse(Page(true)) == new Metrics(60.5, 40, 90), "FPS above labels");
Check(ResultParser.Parse(Page(true, "60,5")) == new Metrics(60.5, 40, 90), "decimal comma");
Reject(() => ResultParser.Parse(Page(false, "30", "40", "90")), "reject invalid FPS ordering");
Reject(() => ResultParser.Parse(Page(false) with { Text = "Current FPS 60" }), "reject running screen");
string testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WukongBench-tests-" + Guid.NewGuid().ToString("N")));
if (!testRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid temporary path");
Directory.CreateDirectory(testRoot);
try
{
    string config = Path.Combine(testRoot, "config"); Directory.CreateDirectory(config);
    string iniPath = Path.Combine(config, "GameUserSettings.ini");
    byte[] bytes = [0xff, 0xfe, 0x41, 0x00, 0x0d, 0x00, 0x0a, 0x00];
    File.WriteAllBytes(iniPath, bytes);
    File.SetAttributes(iniPath, FileAttributes.ReadOnly);
    string backupPath = Path.Combine(testRoot, "backup");
    var backup = SettingsBackup.Create(config, backupPath);
    File.SetAttributes(iniPath, FileAttributes.Normal); File.WriteAllText(iniPath, "modified");
    File.WriteAllText(Path.Combine(config, "generated.ini"), "new config");
    File.WriteAllText(Path.Combine(config, "unrelated.txt"), "keep me");
    backup.Restore();
    Check(File.ReadAllBytes(iniPath).SequenceEqual(bytes), "restore original bytes including encoding");
    Check(File.GetAttributes(iniPath).HasFlag(FileAttributes.ReadOnly), "restore original file attributes");
    Check(!File.Exists(Path.Combine(config, "generated.ini")) && File.Exists(Path.Combine(config, "unrelated.txt")), "remove only generated INI files");
    Check(File.Exists(Path.Combine(backupPath, "restored.txt")), "persistent restoration marker");
    string invalid = Path.Combine(testRoot, "invalid-backup"); Directory.CreateDirectory(invalid);
    File.WriteAllText(Path.Combine(invalid, "backup.json"), JsonSerializer.Serialize(new BackupManifest(config, [new BackupEntry("../escape.ini", FileAttributes.Normal)])));
    Reject(() => SettingsBackup.Load(invalid).Restore(), "reject backup path traversal before writing");
    var hardware = JsonDocument.Parse("""
        {"Cpu":[{"Name":"<script>bad</script>","NumberOfCores":6,"NumberOfLogicalProcessors":12}],"Gpu":[{"Name":"Test GPU","DriverVersion":"1"}],"System":{"TotalPhysicalMemory":17179869184}}
        """).RootElement.Clone();
    var report = new BenchmarkReport(DateTimeOffset.Now, "test", hardware,
        [new PassReport(cpu, new Metrics(60,40,90), DateTimeOffset.Now, DateTimeOffset.Now, "verified", "cpu/result.png")], "completed", null);
    ReportWriter.Save(testRoot, report);
    string html = File.ReadAllText(Path.Combine(testRoot, "report.html"));
    Check(html.Contains("&lt;script&gt;bad&lt;/script&gt;") && !html.Contains("<script>bad"), "escape hardware strings in HTML");
    Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(testRoot, "report.json"))).RootElement.GetProperty("Passes").GetArrayLength() == 1, "write machine-readable results");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(testRoot, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(testRoot, true);
}
Console.WriteLine($"{count} checks passed.");
