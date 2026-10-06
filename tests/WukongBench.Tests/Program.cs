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
var launchRequest = new DateTime(2026, 10, 6, 16, 49, 26, DateTimeKind.Utc);
var excludedProcesses = new HashSet<int> { 20816 };
var processTracker = new ProcessLaunchTracker(launchRequest, excludedProcesses);
Check(!processTracker.MayAttach(20816, launchRequest.AddMinutes(-8), false), "GPU launch cannot attach exiting CPU PID still enumerated by Windows");
Check(!processTracker.MayAttach(20816, launchRequest.AddMinutes(-8), true), "terminated previous pass cannot become new process ownership");
Check(processTracker.MayAttach(10836, launchRequest.AddSeconds(8), false), "delayed fresh GPU PID is accepted after asynchronous Steam dispatch");
Check(!processTracker.MayAttach(10836, launchRequest.AddSeconds(8), true), "new process which already exited cannot be attached");
Check(!processTracker.MayAttach(19408, launchRequest.AddMinutes(-8), false), "unseen process predating launch request is excluded by creation time");
excludedProcesses.Clear();
Check(!processTracker.MayAttach(20816, launchRequest.AddSeconds(1), false), "launch snapshot remains stable when caller collection changes");
Check(!processTracker.MayAttach(0, launchRequest.AddSeconds(1), false), "invalid PID cannot become benchmark process ownership");
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
OcrPage Fixture(string name) => JsonSerializer.Deserialize<OcrPage>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), WukongBench.Program.Json)!;
var fullResult = Fixture("results-full.json");
var columnResult = Fixture("results-column.json");
Check(ResultParser.IsResultScreen(fullResult), "actual result settings table is a report, not interactive controls");
Reject(() => ResultParser.Parse(fullResult), "incomplete full-screen OCR cannot silently become valid FPS");
Reject(() => ResultParser.Parse(columnResult), "letter I in numeric position cannot be guessed as one");
var resultBounds = ResultImageReader.Column(fullResult, 1280, 720);
Check(resultBounds.Right < 400 && resultBounds.Contains(218, 317), "anchored FPS crop excludes system and graphics settings columns");
var numberAreas = ResultImageReader.NumberAreas(fullResult, columnResult, resultBounds);
Check(numberAreas[0].Label.Text == "Average" && numberAreas[1].Label.Text == "Minimum" && numberAreas[2].Label.Text == "Maximum"
    && numberAreas[1].Area.Contains(220, 317), "actual labels locate numeric pixels even when initial OCR reads I");
var numeralReading = Fixture("results-numbers.json");
// Actual experimental strip: average, maximum, minimum, with source pixel regions.
ResultImageReader.NumberCrop[] actualCrops = [new(numberAreas[0].Label, new(50, 239, 42, 30)),
    new(numberAreas[2].Label, new(53, 306, 22, 24)), new(numberAreas[1].Label, new(214, 306, 12, 24))];
System.Drawing.Rectangle[] actualTiles = [new(20, 30, 252, 180), new(307, 30, 132, 144), new(474, 30, 72, 144)];
Check(ResultParser.Parse(ResultImageReader.MapNumbers(numeralReading, actualCrops, actualTiles, 6)) == new Metrics(19, 1, 25),
    "real numeric-strip OCR maps CPU 19/1/25 to original metric labels");
Reject(() => ResultImageReader.MapNumbers(numeralReading with { Lines = [] }, actualCrops, actualTiles, 6), "missing strip numbers cannot produce completed metrics");
var letterStrip = numeralReading with { Lines = numeralReading.Lines.Select(l => l with { Words = l.Words.Select(w => w.Text == "1" ? w with { Text = "I" } : w).ToArray() }).ToArray() };
Reject(() => ResultImageReader.MapNumbers(letterStrip, actualCrops, actualTiles, 6), "numeric-strip letter remains an error rather than a replacement digit");
var crossedTile = numeralReading with { Lines = numeralReading.Lines.Select(l => l with { Words = l.Words.Select(w => w.Text == "1" ? w with { X = 280 } : w).ToArray() }).ToArray() };
Reject(() => ResultImageReader.MapNumbers(crossedTile, actualCrops, actualTiles, 6), "strip value outside its source tile is rejected");
var duplicateAverage = columnResult with { Lines = columnResult.Lines.Append(columnResult.Lines.Single(l => l.Text == "Average")).ToArray() };
Reject(() => ResultImageReader.NumberAreas(fullResult, duplicateAverage, resultBounds), "ambiguous result captions cannot select numeric regions");
var missingAverage = Fixture("results-missing-average.json");
Reject(() => ResultImageReader.NumberAreas(fullResult, missingAverage, resultBounds), "OCR without Average digits requires actual image evidence");
using (var pixelStream = new MemoryStream(Convert.FromBase64String(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "results-missing-average.png.base64")))))
using (var averageImage = new System.Drawing.Bitmap(pixelStream))
{
    var caption = missingAverage.Words.Single(w => w.Text == "Average") with { X = 12.5, Y = 8.5 };
    var imageBounds = new System.Drawing.Rectangle(0, 0, averageImage.Width, averageImage.Height);
    var glyphArea = ResultImageReader.MissingNumberArea(averageImage, caption, imageBounds);
    Check(glyphArea.Contains(30, 40) && glyphArea.Contains(52, 40) && glyphArea.Right < 65,
        "actual omitted Average 20 pixels are isolated without the small FPS suffix");
    using var blankImage = new System.Drawing.Bitmap(averageImage.Width, averageImage.Height);
    Reject(() => ResultImageReader.MissingNumberArea(blankImage, caption, imageBounds), "empty pixel region cannot infer a missing FPS number");
    using var ambiguousImage = new System.Drawing.Bitmap(averageImage);
    using (var drawing = System.Drawing.Graphics.FromImage(ambiguousImage))
        drawing.FillRectangle(System.Drawing.Brushes.White, 110, 25, 10, 25);
    Reject(() => ResultImageReader.MissingNumberArea(ambiguousImage, caption, imageBounds), "unrelated bright region cannot be joined to omitted FPS glyphs");
}
var runningMerged = Fixture("running-current-merged.json");
var runningNoSuffix = Fixture("running-current-no-fps-suffix.json");
Check(BenchmarkStateParser.IsRunning(runningMerged, 1280, 720), "actual CurratFPS merged header identifies running scene");
Check(BenchmarkStateParser.IsRunning(runningNoSuffix, 1280, 720), "actual HUD number remains valid without separate FPS suffix");
Check(!BenchmarkStateParser.IsRunning(Fixture("running-transition-no-number.json"), 1280, 720), "loading transition without numeric FPS cannot prove running scene");
var exactCurrent = runningMerged with { Lines = runningMerged.Lines.Select(l => l.Text == "CurratFPS"
    ? l with { Text = "Current FPS", Words = [W("Current", 40, 43, 40, 8.5), W("FPS", 84, 43, 18, 8.5)] } : l).ToArray() };
Check(BenchmarkStateParser.IsRunning(exactCurrent, 1280, 720), "accept exact two-word Current FPS HUD label");
var otherCounter = runningMerged with { Lines = runningMerged.Lines.Select(l => l.Text == "CurratFPS" ? l with { Text = "GPU Temperature" } : l).ToArray() };
Check(!BenchmarkStateParser.IsRunning(otherCounter, 1280, 720), "unrelated counter cannot establish benchmark start");
var menuExit = runningMerged with { Lines = runningMerged.Lines.Select(l => l.Text == "Exit"
    ? l with { Words = l.Words.Select(w => w with { X = 50, Y = 430 }).ToArray() } : l).ToArray() };
Check(!BenchmarkStateParser.IsRunning(menuExit, 1280, 720), "main-menu Exit location cannot prove running scene");
var distantNumber = runningMerged with { Lines = runningMerged.Lines.Select(l => l.Text == "17 FPS"
    ? l with { Words = l.Words.Select(w => w with { X = 900 }).ToArray() } : l).ToArray() };
Check(!BenchmarkStateParser.IsRunning(distantNumber, 1280, 720), "HUD value must be near Current FPS header");
Check(!BenchmarkStateParser.IsRunning(runningMerged with { Text = "Benchmark Results " + runningMerged.Text }, 1280, 720), "old results cannot establish running state");
Check(!BenchmarkStateParser.IsRunning(runningMerged with { Text = "Settings " + runningMerged.Text }, 1280, 720), "settings with overlay FPS cannot establish running state");
var badNumber = runningMerged with { Lines = runningMerged.Lines.Select(l => l.Text == "17 FPS"
    ? l with { Words = l.Words.Select(w => w.Text == "17" ? w with { Text = "?7" } : w).ToArray() } : l).ToArray() };
Check(!BenchmarkStateParser.IsRunning(badNumber, 1280, 720), "do not guess unreadable HUD numbers");
var largerHud = runningMerged with { Lines = runningMerged.Lines.Select(l => l with { Words = l.Words.Select(w => w with { X = w.X * 1.5, Y = w.Y * 1.5, Width = w.Width * 1.5, Height = w.Height * 1.5 }).ToArray() }).ToArray() };
Check(BenchmarkStateParser.IsRunning(largerHud, 1920, 1080), "HUD layout detection scales to GPU window");
Reject(() => ResultParser.Parse(runningMerged), "actual running FPS cannot be reported as completed benchmark metrics");
var benchmarkSettings = Fixture("benchmark-settings.json");
var displayFps = MenuParser.FindRow(benchmarkSettings, "display frame rate information", 1280)!;
Check(displayFps is not null && MenuParser.Value(benchmarkSettings, displayFps, 1280) == "On", "actual benchmark settings expose own FPS HUD switch");
bool hudInput = false;
await MenuSelector.Select("display frame rate information", "On", 1280, benchmarkSettings,
    _ => { hudInput = true; return Task.FromResult(benchmarkSettings); }, _ => throw new Exception("Unexpected HUD key"));
Check(!hudInput, "already enabled benchmark HUD needs no input");
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
var sliderMenu = JsonSerializer.Deserialize<OcrPage>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphics-slider.json")), WukongBench.Program.Json)!;
var sliderRow = MenuParser.FindRow(sliderMenu, "super resolution", 1280)!;
Check(sliderRow.Text == "Super Resolution", "actual Samphng heading cannot be selected as scale slider");
Check(MenuParser.FindRow(sliderMenu with { Lines = sliderMenu.Lines.Where(l => l != sliderRow).ToArray() }, "super resolution", 1280) is null, "reject headings and help when slider label is missing");
var numericReading = JsonSerializer.Deserialize<OcrPage>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "slider-value.json")), WukongBench.Program.Json)!;
var number = numericReading.Words.Single();
var mergedSlider = sliderRow with { Text = "Super Resolution 100", Words = sliderRow.Words.Append(number).ToArray() };
Check(MenuParser.FindRow(new OcrPage("", [mergedSlider]), "super resolution", 1280) == mergedSlider, "accept slider value merged with exact label");
var helperSlider = sliderRow with { Words = sliderRow.Words.Select(w => w with { X = w.X + 600 }).ToArray() };
Check(MenuParser.FindRow(new OcrPage("", [helperSlider]), "super resolution", 1280) is null, "exclude exact slider label in help column");
var region = MenuParser.ValueRegion(sliderMenu, sliderRow, 1280, 720);
Check(number.CenterX > region.X && number.CenterX < region.X + region.Width
    && number.CenterY > region.Y && number.CenterY < region.Y + region.Height && region.X + region.Width < 799,
    "actual numeric crop contains box and excludes help");
var refinedMenu = sliderMenu with { Lines = sliderMenu.Lines.Concat(numericReading.Lines).ToArray() };
Check(MenuParser.Value(refinedMenu, sliderRow, 1280) == "100"
    && Math.Abs(MenuParser.Control(refinedMenu, sliderRow, 1280).CenterX - 635.67) < .01, "actual cropped OCR provides number and click coordinates");
var croppedCoordinate = OcrImage.OriginalCoordinates(new OcrPage("100", [new OcrLine("100", [W("100", 60, 12, 18, 12)])]), 6, 352, 250).Words.Single();
Check(croppedCoordinate.X == 362 && croppedCoordinate.Y == 252, "map numeric crop scale and offset back to window");
Check(MenuParser.HasPendingGraphicsChanges(sliderMenu, 720) && !MenuParser.HasPendingGraphicsChanges(graphics, 720), "actual footer distinguishes pending graphics changes");
var applyDescription = sliderMenu.Lines.Single(l => l.Text == "Apply Graphics Changes") with { Words = [W("Apply", 800, 140)] };
Check(!MenuParser.HasPendingGraphicsChanges(new OcrPage("", [applyDescription]), 720), "help text cannot trigger Apply shortcut");
Profile.VerifyRayTracingDisabled(cpu.Apply(original)); Check(true, "verify both Full RT disable switches");
var rtEnabled = new IniFile(cpu.Apply(original)); rtEnabled.UpdateMap(Profile.Section, "UISettingData", new Dictionary<string, string> { ["Rtx"] = "1" });
Reject(() => Profile.VerifyRayTracingDisabled(rtEnabled.ToString()), "reject RT enabled by UI config");
rtEnabled = new IniFile(cpu.Apply(original)); rtEnabled.Set("RayTracing", "r.RayTracing.EnableInGame", "True");
Reject(() => cpu.Verify(rtEnabled.ToString()), "reject RT enabled by engine config after pass");
var scrollBefore = JsonSerializer.Deserialize<OcrPage>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphics-scroll-before.json")), WukongBench.Program.Json)!;
var scrollStuck = JsonSerializer.Deserialize<OcrPage>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "graphics-scroll-stuck.json")), WukongBench.Program.Json)!;
var anchor = MenuNavigation.ScrollAnchor(scrollBefore, 1280, 720);
Check(anchor.CenterX > 208 && anchor.CenterX < 718 && anchor.CenterY > 125 && anchor.CenterY < 605,
    "wheel pointer belongs to actual graphics list, not help column at x853");
Check(!MenuNavigation.Moved(scrollBefore, scrollStuck, 1280), "actual repeated screenshots reveal graphics list did not scroll");
var shifted = scrollBefore with { Lines = scrollBefore.Lines.Select(l => l with { Words = l.Words.Select(w => w with { Y = w.Y - 10 }).ToArray() }).ToArray() };
Check(MenuNavigation.Moved(scrollBefore, shifted, 1280), "detect list movement from label positions");
Reject(() => MenuNavigation.ScrollAnchor(new OcrPage("", [helperSlider]), 1280, 720), "help text cannot provide scroll anchor");
int scrollCalls = 0;
var distanceRow = new OcrLine("View Distance Quality", [W("View", 227, 400, 30, 13), W("Distance", 263, 400, 55, 13), W("Quality", 324, 400, 48, 13)]);
var detailedMenu = scrollBefore with { Lines = scrollBefore.Lines.Append(distanceRow).Append(new OcrLine("Cinematic", [W("Cinematic", 563, 400, 70, 13)])).ToArray() };
var located = await MenuNavigation.FindRow("view distance", 1280, 720, scrollBefore, (delta, scrollPoint) =>
{
    scrollCalls++;
    if (scrollPoint.CenterX >= 718) throw new Exception("Scroll was sent outside settings list");
    if (scrollCalls == 1 && delta != 2400 || scrollCalls == 2 && delta != -240) throw new Exception("Unexpected wheel direction");
    return Task.FromResult(scrollCalls == 1 ? scrollBefore : detailedMenu);
});
Check(scrollCalls == 2 && located.Line == distanceRow && MenuParser.Value(located.Page, located.Line, 1280) == "Cinematic",
    "navigation finds and verifies detailed quality after wheel over list");
scrollCalls = 0;
await MenuNavigation.FindRow("view distance", 1280, 720, detailedMenu, (_, _) => { scrollCalls++; return Task.FromResult(detailedMenu); });
Check(scrollCalls == 0, "visible row needs no reset or scroll");
scrollCalls = 0;
try
{
    await MenuNavigation.FindRow("view distance", 1280, 720, scrollBefore, (_, _) => { scrollCalls++; return Task.FromResult(scrollStuck); });
    throw new Exception("Expected stopped navigation");
}
catch (InvalidDataException) { Check(scrollCalls == 2, "unchanged list stops instead of repeating ineffective wheel input"); }
string testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WukongBench-tests-" + Guid.NewGuid().ToString("N")));
if (!testRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid temporary path");
Directory.CreateDirectory(testRoot);
try
{
    string appearanceFile = Path.Combine(testRoot, "appearance.png");
    using (var appearance = new System.Drawing.Bitmap(80, 30))
    {
        using var drawing = System.Drawing.Graphics.FromImage(appearance);
        drawing.Clear(System.Drawing.Color.Black);
        using var disabledBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(25, 25, 25));
        using var enabledBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 200, 200));
        drawing.FillRectangle(disabledBrush, 2, 2, 10, 10);
        drawing.FillRectangle(enabledBrush, 32, 2, 10, 10);
        appearance.Save(appearanceFile, System.Drawing.Imaging.ImageFormat.Png);
    }
    var disabledLabel = new OcrLine("Full Ray Tracing", [W("Full", 0, 0, 20, 20)]);
    var enabledLabel = new OcrLine("Frame Generation", [W("Frame", 30, 0, 20, 20)]);
    Check(MenuAppearance.Inspect(appearanceFile, disabledLabel, enabledLabel).Disabled, "dimmed label evidence relative to active setting");
    Check(!MenuAppearance.Inspect(appearanceFile, enabledLabel, enabledLabel).Disabled, "active RT label cannot bypass visible verification");
    Check(!MenuAppearance.Inspect(appearanceFile, disabledLabel, disabledLabel).Disabled, "dark reference cannot prove unavailable RT");
    if (args.Length == 2 && args[0] == "--menu-evidence")
    {
        string capturedImage = Path.GetFullPath(args[1]);
        var actualMenu = await MenuImageReader.Read(capturedImage, Path.Combine(testRoot, "captured-menu"), CancellationToken.None);
        using var capturedBitmap = new System.Drawing.Bitmap(capturedImage);
        var actualSlider = MenuParser.FindRow(actualMenu, "super resolution", capturedBitmap.Width)!;
        Check(MenuParser.Value(actualMenu, actualSlider, capturedBitmap.Width) == "100", "real Windows OCR reads saved slider box as 100");
        var evidence = MenuAppearance.Inspect(capturedImage, MenuParser.FindRow(actualMenu, "full ray tracing", capturedBitmap.Width)!,
            MenuParser.FindRow(actualMenu, "frame generation", capturedBitmap.Width)!);
        Check(evidence.Disabled, "real saved screenshot confirms dimmed RT row");
        Console.WriteLine(JsonSerializer.Serialize(evidence));
    }
    if (args.Length is 2 or 3 && args[0] == "--result-evidence")
    {
        var actualResult = await MenuImageReader.Read(Path.GetFullPath(args[1]), Path.Combine(testRoot, "captured-result"), CancellationToken.None);
        string resultError = Path.Combine(testRoot, "captured-result-results-error.txt");
        if (File.Exists(resultError)) throw new InvalidDataException(File.ReadAllText(resultError));
        var values = (args.Length == 3 ? args[2] : "19,1,25").Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var expected = new Metrics(values[0], values[1], values[2]);
        Check(ResultParser.Parse(actualResult) == expected, "Windows OCR and full reader recover actual saved CPU result " + string.Join('/', values));
    }
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

