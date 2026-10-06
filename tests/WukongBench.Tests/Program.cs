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
var loopRow = new OcrLine("Loop Benchmark", [W("Loop", 361, 185, 30, 13), W("Benchmark", 396, 184, 70, 11)]);
var menu = new OcrPage("Loop Benchmark On Off", [loopRow,
    new OcrLine("On", [W("On", 718, 142, 18, 11)]), new OcrLine("Off", [W("Off", 717, 184, 21, 11)])]);
Check(MenuParser.Value(menu, loopRow, 1280) == "Off", "read loop Off from separate OCR line");
Check(MenuParser.Control(menu, loopRow, 1280).CenterX == 727.5, "click recognized control instead of 72 percent of window");
Reject(() => MenuParser.Control(menu with { Lines = [loopRow] }, loopRow, 1280), "no blind click when control OCR is absent");
var aa = new OcrLine("Anti Aliasing Quality", [W("Anti", 360, 190)]);
Check(MenuParser.FindRow(new OcrPage(aa.Text, [aa]), "anti-aliasing") == aa, "accept OCR variations in punctuation");
var sampling = new OcrLine("Super Resolution Sampling", [W("Super", 360, 140)]);
var slider = new OcrLine("Super Resolution", [W("Super", 360, 180)]);
Check(MenuParser.FindRow(new OcrPage("", [sampling, slider]), "super resolution") == slider, "distinguish scale slider from sampling selector");
var normalized = OcrImage.OriginalCoordinates(menu, 2);
Check(normalized.Words.Last().CenterX == 363.75, "map enlarged OCR coordinates to screenshot coordinates");
var graphics = JsonSerializer.Deserialize<OcrPage>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphics-menu.json")), WukongBench.Program.Json)!;
var graphicsRow = MenuParser.FindRow(graphics, "super resolution sampling", 1280)!;
Check(MenuParser.Value(graphics, graphicsRow, 1280) == "FSR", "recognize actual graphics menu FSR left of old 48 percent boundary");
Check(MenuParser.Control(graphics, graphicsRow, 1280).CenterX == 588, "actual graphics control coordinates");
var heading = graphics.Lines.First(l => l.Text.Contains("Samphng")) with { Text = "Super Resolution Sampling" };
var withHeading = graphics with { Lines = new[] { heading }.Concat(graphics.Lines).ToArray() };
Check(MenuParser.FindRow(withHeading, "super resolution sampling", 1280) == graphicsRow, "prefer actual value row to duplicate section heading");
var fsr = graphics.Lines.Single(l => l.Text == "FSR").Words[0];
var merged = graphicsRow with { Text = graphicsRow.Text + " FSR", Words = graphicsRow.Words.Append(fsr).ToArray() };
var mergedPage = graphics with { Lines = graphics.Lines.Where(l => l != graphicsRow && l.Text != "FSR").Append(merged).ToArray() };
Check(MenuParser.Value(mergedPage, merged, 1280) == "FSR", "value merged into OCR label line");
var help = new OcrLine("FSR improves image quality", [W("FSR", 799, 312, 24, 12), W("improves", 830, 312, 60, 12)]);
var absent = graphics with { Lines = graphics.Lines.Where(l => l.Text != "FSR").Append(help).ToArray() };
Reject(() => MenuParser.Control(absent, graphicsRow, 1280), "never treat help text as missing setting control");
var enlarged = graphics with { Lines = graphics.Lines.Select(l => l with { Words = l.Words.Select(w => w with { X = w.X * 1.5, Y = w.Y * 1.5, Width = w.Width * 1.5, Height = w.Height * 1.5 }).ToArray() }).ToArray() };
Check(MenuParser.Value(enlarged, MenuParser.FindRow(enlarged, "super resolution sampling", 1920)!, 1920) == "FSR", "same menu geometry at 1920 width");
var selectedGraphics = JsonSerializer.Deserialize<OcrPage>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphics-selected.json")), WukongBench.Program.Json)!;
OcrPage WithSampling(string value) => selectedGraphics with
{
    Lines = selectedGraphics.Lines.Select(l => l.Text == "FSR"
        ? l with { Text = value, Words = l.Words.Select(w => w with { Text = value }).ToArray() } : l).ToArray()
};
var withArrows = selectedGraphics with { Lines = selectedGraphics.Lines.Append(new OcrLine("< >", [W("<", 505, 312, 8, 12), W(">", 661, 312, 8, 12)])).ToArray() };
Check(MenuParser.Value(withArrows, MenuParser.FindRow(withArrows, "super resolution sampling", 1280)!, 1280) == "FSR", "ignore selector arrows in OCR values");
int focusCount = 0, selectionSteps = 0;
await MenuSelector.Select("super resolution sampling", "TSR", 1280, graphics, control =>
{
    focusCount++;
    if (control.CenterX != 588) throw new Exception("Wrong focus coordinates");
    return Task.FromResult(selectedGraphics);
}, direction =>
{
    if (direction != SelectionDirection.Right) throw new Exception("Unexpected direction");
    return Task.FromResult(WithSampling(++selectionSteps == 1 ? "XeSS" : "TSR"));
});
Check(focusCount == 1 && selectionSteps == 2, "click focuses actual row, Right cycles to confirmed TSR");
selectionSteps = 0;
await MenuSelector.Select("super resolution sampling", "TSR", 1280, selectedGraphics, _ => Task.FromResult(selectedGraphics), direction =>
{
    selectionSteps++;
    return Task.FromResult(WithSampling(direction == SelectionDirection.Right ? "FSR" : "TSR"));
});
Check(selectionSteps == 2, "try Left when Right reaches endpoint");
bool selectedAlready = false;
await MenuSelector.Select("super resolution sampling", "TSR", 1280, WithSampling("TSR"), _ => { selectedAlready = true; return Task.FromResult(selectedGraphics); }, _ => throw new Exception("Unexpected key"));
Check(!selectedAlready, "already selected TSR requires no input");
selectionSteps = 0;
try
{
    await MenuSelector.Select("super resolution sampling", "TSR", 1280, selectedGraphics, _ => Task.FromResult(selectedGraphics), _ =>
    {
        selectionSteps++;
        return Task.FromResult(selectedGraphics);
    });
    throw new Exception("Expected selection failure");
}
catch (InvalidDataException) { Check(selectionSteps == 2, "unchanged selector stops after both directions"); }
selectionSteps = 0;
try
{
    await MenuSelector.Select("super resolution sampling", "TSR", 1280, selectedGraphics, _ => Task.FromResult(selectedGraphics), _ =>
    {
        selectionSteps++;
        return Task.FromResult(selectedGraphics with { Lines = selectedGraphics.Lines.Where(l => l.Text != "FSR").ToArray() });
    });
    throw new Exception("Expected missing value failure");
}
catch (InvalidDataException) { Check(selectionSteps == 1, "missing value stops further input"); }
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
