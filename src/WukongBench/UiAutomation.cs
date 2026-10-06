using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using WukongBench.Core;

namespace WukongBench;

internal sealed class UiAutomation(Desktop desktop, string output, CancellationToken ct)
{
    private int sequence;
    private int width, height;
    public async Task<OcrPage> Read(string stage)
    {
        ct.ThrowIfCancellationRequested();
        var stem = Path.Combine(output, $"{++sequence:D3}-{stage}");
        (width, height) = desktop.Capture(stem + ".png");
        var page = await MenuImageReader.Read(stem + ".png", stem, ct);
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(page, Program.Json));
        return page;
    }
    private bool Click(OcrPage page, string pattern, bool exact = false)
    {
        var found = page.Lines.Where(l => Regex.IsMatch(l.Text.Trim(), pattern, RegexOptions.IgnoreCase))
            .Where(l => l.Words.Length > 0)
            .OrderBy(l => exact ? l.Text.Length : -l.Words.Average(w => w.Y)).FirstOrDefault();
        if (found is null) return false;
        var word = found.Words[0];
        desktop.Click(word.CenterX, word.CenterY, width, height);
        return true;
    }
    public async Task MainMenu()
    {
        var clock = Stopwatch.StartNew();
        WindowFocusException? focusError = null;
        while (clock.Elapsed < TimeSpan.FromMinutes(3))
        {
            if (!desktop.Ready) { await Task.Delay(1000, ct); continue; }
            OcrPage page;
            try { page = await Read("startup"); focusError = null; }
            catch (WindowFocusException e) { focusError = e; await Task.Delay(1000, ct); continue; }
            if (Regex.IsMatch(page.Text, @"\bsettings\b", RegexOptions.IgnoreCase) && Regex.IsMatch(page.Text, @"\bbenchmark\b", RegexOptions.IgnoreCase)) return;
            if (Regex.IsMatch(page.Text, @"privacy|agreement", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("First-launch agreement: initialize the installed tool once yourself, then rerun. No agreement was accepted automatically.");
            if (Regex.IsMatch(page.Text, @"press|continue|black\s+myth", RegexOptions.IgnoreCase)) desktop.Key(0x0D);
            await Task.Delay(2000, ct);
        }
        if (focusError is not null) throw focusError;
        throw new TimeoutException("Main menu did not appear within 3 minutes (Steam login, update, or shader compilation).");
    }
    public async Task ConfigureAndInspect(Profile profile, string configFile)
    {
        var page = await Read("main-menu");
        if (!Click(page, @"^settings$", true)) throw new InvalidDataException("Settings menu was not found.");
        await Task.Delay(1500, ct);
        page = await Read("settings");
        var loop = page.Lines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"loop.*benchmark|benchmark.*loop", RegexOptions.IgnoreCase));
        if (loop is not null && !Regex.IsMatch(Value(page, loop), @"\boff\b", RegexOptions.IgnoreCase))
        {
            string loopLabel = Regex.Replace(loop.Text, @"\s+\b(on|off)\b\s*$", "", RegexOptions.IgnoreCase).Trim();
            await Select(loopLabel, "Off");
            page = await Read("loop-disabled");
        }
        if (!Click(page, @"^(graphics|graphics settings)$", true)) throw new InvalidDataException("Graphics menu was not found.");
        await Task.Delay(1000, ct);
        // UISettingData enums can differ between builds. Select these two controls by visible text.
        await Select("super resolution sampling", "TSR");
        await SetSlider("super resolution", profile.RenderPercent);
        await InspectRow("frame generation", @"\boff\b");
        await InspectRayTracing(profile, configFile);
        if (profile.RayTracing) await Select("full ray tracing level", "Very High");
        foreach (var label in new[] { "view distance", "vegetation quality" }) await InspectRow(label, @"cinematic");
        foreach (var label in new[] { "anti-aliasing", "post-effects", "shadow quality", "texture quality", "visual effect", "hair quality", "global illumination", "reflection quality" })
            await InspectRow(label, profile.IsCpu ? @"\blow\b" : @"cinematic");
        await Task.Delay(500, ct);
        await Read("graphics-top");
        desktop.Scroll(-960); await Task.Delay(500, ct);
        page = await Read("graphics-bottom");
        // At least two independent quality labels must be visible; INI validation after exit covers every field.
        if (!Regex.IsMatch(page.Text, "reflection|vegetation|texture", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Cannot verify the graphics settings screen.");
        if (MenuParser.HasPendingGraphicsChanges(page, height))
        {
            // The observed menu advertises T: Apply Graphics Changes in its footer.
            desktop.Key(0x54); await Task.Delay(750, ct);
            page = await Read("apply-graphics");
            if (Regex.IsMatch(page.Text, "save.*changes|apply.*changes|restart", RegexOptions.IgnoreCase)
                && Click(page, @"^(confirm|yes)$", true))
            { await Task.Delay(750, ct); page = await Read("confirm-graphics"); }
            if (MenuParser.HasPendingGraphicsChanges(page, height))
                throw new InvalidDataException("Graphics changes remain pending after Apply; benchmark was not started.");
        }
        desktop.Key(0x1B); await Task.Delay(500, ct);
        // Save/confirm applies the UI changes when the build asks for it.
        page = await Read("leave-graphics");
        if (Regex.IsMatch(page.Text, "save.*changes|apply.*changes", RegexOptions.IgnoreCase))
        {
            if (!Click(page, @"^(confirm|yes|apply)$", true)) throw new InvalidDataException("Cannot confirm settings changes.");
            await Task.Delay(500, ct);
        }
        for (int i = 0; i < 3; i++)
        {
            page = await Read("return-menu");
            if (Regex.IsMatch(page.Text, @"\bquit\b|\bexit\b", RegexOptions.IgnoreCase) && Regex.IsMatch(page.Text, @"\bbenchmark\b", RegexOptions.IgnoreCase)) return;
            desktop.Key(0x1B); await Task.Delay(500, ct);
        }
        throw new InvalidDataException("Cannot return to the benchmark menu.");
    }
    private async Task<(OcrPage Page, OcrLine Line)> Row(string label)
    {
        desktop.Scroll(2400); await Task.Delay(400, ct);
        for (int i = 0; i < 8; i++)
        {
            var page = await Read("row");
            var line = MenuParser.FindRow(page, label, width);
            if (line is not null) return (page, line);
            desktop.Scroll(-360); await Task.Delay(400, ct);
        }
        throw new InvalidDataException("Cannot locate setting: " + label);
    }
    private string Value(OcrPage page, OcrLine label) => MenuParser.Value(page, label, width);
    private async Task InspectRow(string label, string expected)
    {
        var (page, row) = await Row(label);
        if (!Regex.IsMatch(Value(page, row), expected, RegexOptions.IgnoreCase))
            throw new InvalidDataException($"Visible {label} is not the requested value: {Value(page, row)}");
    }
    private async Task InspectRayTracing(Profile profile, string configFile)
    {
        var (page, row) = await Row("full ray tracing");
        string actual = Value(page, row).Trim();
        if (actual.Equals(profile.RayTracing ? "On" : "Off", StringComparison.OrdinalIgnoreCase)) return;
        if (!profile.RayTracing && actual.Length == 0)
        {
            var reference = MenuParser.FindRow(page, "frame generation", width);
            if (reference is not null)
            {
                var evidence = MenuAppearance.Inspect(Path.Combine(output, $"{sequence:D3}-row.png"), row, reference);
                if (evidence.Disabled)
                {
                    // Unsupported hardware can gray out the entire RT row without displaying Off.
                    // Require actual dimmed UI evidence and both known configuration switches.
                    // Profile.Verify repeats the configuration check after the game closes.
                    Profile.VerifyRayTracingDisabled(File.ReadAllText(configFile));
                    File.WriteAllText(Path.Combine(output, "rt-disabled.json"), JsonSerializer.Serialize(evidence, Program.Json));
                    Console.WriteLine("Full RT: пункт меню недоступен; выключение проверено по конфигурации.");
                    return;
                }
            }
        }
        throw new InvalidDataException("Cannot verify Full RT: " + actual);
    }
    private async Task Select(string label, string value)
    {
        var (page, row) = await Row(label);
        await MenuSelector.Select(label, value, width, page, async control =>
        {
            desktop.Click(control.CenterX, control.CenterY, width, height);
            await Task.Delay(400, ct);
            return await Read("select-focus");
        }, async direction =>
        {
            ct.ThrowIfCancellationRequested();
            desktop.Key(direction == SelectionDirection.Right ? (ushort)0x27 : (ushort)0x25);
            await Task.Delay(400, ct);
            return await Read("select-" + direction.ToString().ToLowerInvariant());
        });
    }
    private async Task SetSlider(string label, int value)
    {
        var (page, row) = await Row(label);
        if (row.Text.Contains("Sampling", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Render scale slider cannot be distinguished from the upscaler selector.");
        var current = Value(page, row);
        if (Regex.IsMatch(current, $@"\b{value}\b")) return;
        var control = Control(page, row);
        desktop.Click(control.CenterX, control.CenterY, width, height);
        // Move to an endpoint, then approach the requested value. Works without knowing the slider's INI encoding.
        for (int i = 0; i < 120; i++) { desktop.Key(value == 100 ? (ushort)0x27 : (ushort)0x25); await Task.Delay(15, ct); }
        for (int i = 0; i < 101; i++)
        {
            page = await Read("render-scale");
            var newRow = MenuParser.FindRow(page, label, width)
                ?? throw new InvalidDataException("Render scale slider disappeared.");
            if (Regex.IsMatch(Value(page, newRow), $@"\b{value}\b")) return;
            if (value == 100) break;
            desktop.Key(0x27); await Task.Delay(30, ct);
        }
        throw new InvalidDataException("Cannot verify render scale " + value);
    }
    private OcrWord Control(OcrPage page, OcrLine label) => MenuParser.Control(page, label, width);
    public async Task<(Metrics Metrics, string Screenshot)> Run(TimeSpan timeout)
    {
        var menu = await Read("before-start");
        if (!Click(menu, @"^(start\s+|run\s+)?benchmark$", true)) throw new InvalidDataException("Benchmark start button not found.");
        await Task.Delay(1500, ct);
        var confirm = await Read("confirm-start");
        if (Regex.IsMatch(confirm.Text, @"\bconfirm\b", RegexOptions.IgnoreCase))
        { if (!Click(confirm, @"^confirm$", true)) throw new InvalidDataException("Start confirmation not found."); }
        // Require an in-flight benchmark screen so an old result cannot be accepted.
        var start = Stopwatch.StartNew();
        bool running = false;
        while (start.Elapsed < TimeSpan.FromSeconds(60))
        {
            var page = await Read("starting");
            if (Regex.IsMatch(page.Text, @"\bcurrent\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(page.Text, @"\bresults?\b", RegexOptions.IgnoreCase)) { running = true; break; }
            await Task.Delay(2000, ct);
        }
        if (!running) throw new TimeoutException("The benchmark did not enter its running state.");
        // Keep OCR and PowerShell out of the measured portion (~142s in the reference workflow).
        await Task.Delay(TimeSpan.FromSeconds(145), ct);
        while (start.Elapsed < timeout)
        {
            var page = await Read("waiting-results");
            if (Regex.IsMatch(page.Text, @"\bresults?\b", RegexOptions.IgnoreCase))
            {
                Metrics first;
                try { first = ResultParser.Parse(page); }
                catch (InvalidDataException) { await Task.Delay(3000, ct); continue; }
                await Task.Delay(3000, ct);
                var second = await Read("results");
                var metrics = ResultParser.Parse(second);
                if (metrics != first) throw new InvalidDataException("FPS OCR was inconsistent across two result captures.");
                return (metrics, $"{sequence:D3}-results.png");
            }
            await Task.Delay(5000, ct);
        }
        throw new TimeoutException("No complete benchmark result screen before the timeout.");
    }
}
