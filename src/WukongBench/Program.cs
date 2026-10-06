using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WukongBench.Core;

namespace WukongBench;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Console.OutputEncoding = Encoding.UTF8;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        try { return Execute(args, cancel.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { Console.Error.WriteLine("Отменено. Статус восстановления настроек сохранён в отчёте и backup/restored.txt."); return 130; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
    private static async Task<int> Execute(string[] args, CancellationToken ct)
    {
        var options = Options.Parse(args);
        if (options.Help) { Console.WriteLine(Options.HelpText); return 0; }
        if (options.OcrImage is not null)
        {
            string temporary = Path.Combine(Path.GetTempPath(), "WukongBench-ocr-" + Guid.NewGuid().ToString("N"));
            try
            {
                var page = await MenuImageReader.Read(Path.GetFullPath(options.OcrImage), temporary, ct);
                Console.WriteLine(options.ParseImage ? JsonSerializer.Serialize(ResultParser.Parse(page), Json) : JsonSerializer.Serialize(page, Json));
            }
            finally
            {
                foreach (string suffix in new[] { "-ocr.png", "-values-ocr.png", "-values.json", "-rt-values-ocr.png", "-rt-values.json", "-results-ocr.png", "-results.json", "-results-column-ocr.png", "-results-column.json", "-results-digits-ocr.png", "-results-digits.json", "-results-digits-spaced-ocr.png", "-results-digits-spaced.json", "-results-error.txt" })
                    if (File.Exists(temporary + suffix)) File.Delete(temporary + suffix);
            }
            return 0;
        }
        using var gate = new Semaphore(1, 1, @"Local\WukongBench-3132990");
        if (!gate.WaitOne(0)) throw new InvalidOperationException("Another WukongBench session is already active.");
        try
        {
            if (options.Restore is not null)
            {
                if (Process.GetProcessesByName("b1-Win64-Shipping").Length > 0) throw new InvalidOperationException("Close Wukong before restoring the settings backup.");
                SettingsBackup.Load(Path.GetFullPath(options.Restore)).Restore(); Console.WriteLine("Настройки восстановлены."); return 0;
            }
            var hardwareText = await Shell.PowerShell(Path.Combine(AppContext.BaseDirectory, "scripts", "Hardware.ps1"), ct);
            var hardware = JsonDocument.Parse(hardwareText).RootElement.Clone();
            if (options.Doctor)
            {
                Console.WriteLine(hardwareText);
                try { var s = SteamInstallation.Discover(options.GameDirectory); Console.WriteLine($"Steam AppID 3132990; build {s.BuildId}; {s.GameDirectory}"); }
                catch (DirectoryNotFoundException e) { Console.Error.WriteLine(e.Message); return 2; }
                return 0;
            }
            return await Benchmark(options, hardware, ct);
        }
        finally { gate.Release(); }
    }
    private static async Task<int> Benchmark(Options options, JsonElement hardware, CancellationToken ct)
    {
        var steam = SteamInstallation.Discover(options.GameDirectory);
        if (FindGameProcesses(steam).Any()) throw new InvalidOperationException("Close the already running Benchmark Tool before starting this runner.");
        var configFile = Path.GetFullPath(options.Config ?? Path.Combine(steam.GameDirectory, "b1", "Saved", "Config", "Windows", "GameUserSettings.ini"));
        if (!File.Exists(configFile)) throw new FileNotFoundException("Initialize Benchmark Tool once (first-launch agreement and language), close it, then run again. Use --config for a non-standard settings location.", configFile);
        string original = File.ReadAllText(configFile);
        var screen = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("Interactive Windows desktop is required.");
        var (gw, gh) = options.GpuResolution ?? (screen.Width, screen.Height);
        if (gw > screen.Width || gh > screen.Height) throw new ArgumentException("GPU resolution must fit the primary monitor. Set its highest display mode in Windows before running.");
        bool rayTracing = !options.NoRayTracing && hardware.GetProperty("Gpu").EnumerateArray().Any(g =>
            Regex.IsMatch(g.GetProperty("Name").GetString() ?? "", @"\bRTX\b|\bRX\s*[679]\d{3}|\bArc\b", RegexOptions.IgnoreCase));
        var profiles = new[] { new Profile("CPU", 1280, 720, 50, false), new Profile("GPU", gw, gh, 100, rayTracing) };
        // Validate format before modifying any user's settings.
        _ = profiles[0].Apply(original);
        var output = Path.GetFullPath(Path.Combine(options.Output, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(output);
        var backup = SettingsBackup.Create(Path.GetDirectoryName(configFile)!, Path.Combine(output, "backup"));
        var report = new BenchmarkReport(DateTimeOffset.Now, steam.BuildId, hardware, [], "running", null);
        Process? owned = null;
        Console.WriteLine("Отчёт и подтверждающие файлы: " + output);
        try
        {
            foreach (var requested in profiles)
            {
                ct.ThrowIfCancellationRequested();
                var profile = requested;
                string passDirectory = Path.Combine(output, profile.Name.ToLowerInvariant());
                Directory.CreateDirectory(passDirectory);
                Console.WriteLine($"{profile.Name}: {profile.Width}×{profile.Height}, scale {profile.RenderPercent}%, RT {profile.RayTracing}");
                File.SetAttributes(configFile, FileAttributes.Normal);
                File.WriteAllText(configFile, profile.Apply(original), new UTF8Encoding(false));
                File.Copy(configFile, Path.Combine(passDirectory, "requested.ini"), true);
                owned = await Launch(steam, ct);
                var desktop = new Desktop(owned);
                var ui = new UiAutomation(desktop, passDirectory, ct);
                await ui.MainMenu();
                await ui.ConfigureAndInspect(profile, configFile);
                var start = DateTimeOffset.Now;
                var (metrics, screenshot) = await ui.Run(TimeSpan.FromSeconds(options.TimeoutSeconds));
                await Stop(owned); owned.Dispose(); owned = null;
                string effective = File.ReadAllText(configFile);
                File.WriteAllText(Path.Combine(passDirectory, "effective.ini"), effective);
                profile.Verify(effective);
                report.Passes.Add(new PassReport(profile, metrics, start, DateTimeOffset.Now, "verified", profile.Name.ToLowerInvariant() + "/" + screenshot));
                ReportWriter.Save(output, report);
                Console.WriteLine($"{profile.Name}: avg {metrics.AverageFps:F1}, min {metrics.MinimumFps:F1}, max {metrics.MaximumFps:F1} FPS");
            }
            report.Status = "completed";
        }
        catch (Exception e)
        {
            report.Status = e is OperationCanceledException ? "cancelled" : "failed";
            report.Error = e.Message;
            File.WriteAllText(Path.Combine(output, "error.txt"), e.ToString());
            throw;
        }
        finally
        {
            try
            {
                if (owned is not null) { await Stop(owned); owned.Dispose(); }
                backup.Restore();
            }
            catch (Exception e)
            {
                report.Status = "restoration-failed";
                report.Error = (report.Error ?? "") + " Settings restoration failed: " + e.Message;
                Console.Error.WriteLine("Не удалось восстановить настройки. Закройте Benchmark Tool и выполните --restore \"" + Path.Combine(output, "backup") + "\". " + e.Message);
            }
            ReportWriter.Save(output, report);
        }
        if (report.Status != "completed") return 1;
        Console.WriteLine("Готово: " + Path.Combine(output, "report.html"));
        if (options.OpenReport) Process.Start(new ProcessStartInfo(Path.Combine(output, "report.html")) { UseShellExecute = true });
        return 0;
    }
    private static IEnumerable<Process> FindGameProcesses(SteamInstallation steam)
    {
        foreach (var p in Process.GetProcessesByName("b1-Win64-Shipping"))
        {
            bool matches;
            try { matches = string.Equals(p.MainModule?.FileName, steam.GameExe, StringComparison.OrdinalIgnoreCase); }
            catch { p.Dispose(); throw new InvalidOperationException("Cannot inspect an existing Wukong process; close it before starting."); }
            if (matches) yield return p; else p.Dispose();
        }
    }
    private static async Task<Process> Launch(SteamInstallation steam, CancellationToken ct)
    {
        var start = new ProcessStartInfo(steam.SteamExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(steam.SteamExe)! };
        foreach (string arg in new[] { "-applaunch", "3132990", "-culture=en", "-dx12" }) start.ArgumentList.Add(arg);
        using var launcher = Process.Start(start) ?? throw new IOException("Steam did not start.");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMinutes(3))
        {
            ct.ThrowIfCancellationRequested();
            var process = FindGameProcesses(steam).FirstOrDefault();
            if (process is not null)
            {
                // Return ownership immediately, including a process that has not created its window yet.
                return process;
            }
            await Task.Delay(1000, ct);
        }
        throw new TimeoutException("Steam did not launch Benchmark Tool. Check login/download/launch dialogs.");
    }
    private static async Task Stop(Process p)
    {
        if (p.HasExited) return;
        p.CloseMainWindow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!p.HasExited) p.Kill(true); await p.WaitForExitAsync(); }
    }
}

internal sealed class Options
{
    public bool Help, Doctor, ParseImage, NoRayTracing, OpenReport;
    public string? GameDirectory, Config, Restore, OcrImage;
    public string Output = "results";
    public int TimeoutSeconds = 900;
    public (int Width, int Height)? GpuResolution;
    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value for " + args[i - 1]);
            switch (args[i])
            {
                case "--help": case "-h": o.Help = true; break;
                case "--doctor": o.Doctor = true; break;
                case "--no-ray-tracing": o.NoRayTracing = true; break;
                case "--open-report": o.OpenReport = true; break;
                case "--game-dir": o.GameDirectory = Value(); break;
                case "--config": o.Config = Value(); break;
                case "--output": o.Output = Value(); break;
                case "--restore": o.Restore = Value(); break;
                case "--ocr": o.OcrImage = Value(); break;
                case "--parse-image": o.OcrImage = Value(); o.ParseImage = true; break;
                case "--timeout": o.TimeoutSeconds = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--gpu-resolution":
                    var parts = Value().Split('x', 'X');
                    if (parts.Length != 2) throw new ArgumentException("Expected WIDTHxHEIGHT.");
                    o.GpuResolution = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture)); break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (o.TimeoutSeconds < 180 || o.TimeoutSeconds > 7200) throw new ArgumentException("--timeout must be between 180 and 7200 seconds.");
        if (o.GpuResolution is { } resolution && (resolution.Width < 1280 || resolution.Height < 720)) throw new ArgumentException("GPU resolution must be at least 1280x720.");
        return o;
    }
    public const string HelpText = """
        WukongBench — автоматические CPU и GPU проходы Steam Benchmark Tool.
        Без аргументов: оба прохода, восстановление настроек, JSON + HTML + скриншоты.
          --doctor                 Характеристики ПК и проверка установки
          --game-dir PATH          Папка установленного Benchmark Tool
          --config PATH            Путь к GameUserSettings.ini
          --output PATH            Каталог результатов (по умолчанию results)
          --gpu-resolution WxH     GPU разрешение (по умолчанию основной монитор)
          --no-ray-tracing         GPU Cinematic без full RT
          --timeout SECONDS        Таймаут прохода (900 по умолчанию)
          --open-report            Открыть HTML после успешного выполнения
          --restore BACKUP_DIR     Восстановить настройки после аварийного завершения
          --ocr IMAGE.png          Распознать изображение, вывести JSON
          --parse-image IMAGE.png  Извлечь FPS из изображения результата
          --help                   Эта справка
        """;
}

