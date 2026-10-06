using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WukongBench.Core;

namespace WukongBench;

internal sealed record PassReport(Profile Settings, Metrics Results, DateTimeOffset Started, DateTimeOffset Finished, string SettingsValidation, string Screenshot);
internal sealed record BenchmarkReport(DateTimeOffset Created, string SteamBuildId, JsonElement Hardware, List<PassReport> Passes, string InitialStatus, string? InitialError)
{
    public string Status { get; set; } = InitialStatus;
    public string? Error { get; set; } = InitialError;
}
internal static class ReportWriter
{
    public static void Save(string output, BenchmarkReport report)
    {
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, Program.Json));
        string E(string? value) => WebUtility.HtmlEncode(value ?? "—");
        var html = new StringBuilder("""
            <!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Wukong Benchmark — результаты</title>
            <style>body{font:16px/1.6 system-ui,sans-serif;background:#11151d;color:#e9edf4;max-width:1100px;margin:40px auto;padding:0 24px}h1{color:#eecb82}section{background:#1c2330;padding:24px;border-radius:12px;margin:24px 0}table{border-collapse:collapse;width:100%}td,th{text-align:left;border-bottom:1px solid #394355;padding:10px}pre{white-space:pre-wrap;overflow-wrap:anywhere;font-size:13px}img{max-width:100%;border-radius:8px}a{color:#89c5ff}.note{color:#b9c4d4}.error{color:#ffaaaa}</style>
            <h1>Black Myth: Wukong Benchmark Tool</h1>
            """);
        html.Append($"<p>{E(report.Created.ToString("yyyy-MM-dd HH:mm:ss zzz"))} · Steam build {E(report.SteamBuildId)} · Статус: <strong>{E(report.Status)}</strong></p>");
        if (report.Error is not null) html.Append($"<p class=error>{E(report.Error)}</p>");
        html.Append("<section><h2>Компьютер</h2><table><tr><th>Компонент</th><th>Характеристики</th></tr>");
        foreach (var cpu in report.Hardware.GetProperty("Cpu").EnumerateArray())
            html.Append($"<tr><td>CPU</td><td>{E(cpu.GetProperty("Name").GetString())}; {cpu.GetProperty("NumberOfCores")} ядер / {cpu.GetProperty("NumberOfLogicalProcessors")} потоков</td></tr>");
        foreach (var gpu in report.Hardware.GetProperty("Gpu").EnumerateArray())
            html.Append($"<tr><td>GPU</td><td>{E(gpu.GetProperty("Name").GetString())}; драйвер {E(gpu.GetProperty("DriverVersion").GetString())}</td></tr>");
        double ram = report.Hardware.GetProperty("System").GetProperty("TotalPhysicalMemory").GetDouble() / Math.Pow(1024, 3);
        html.Append($"<tr><td>RAM</td><td>{ram.ToString("F1", CultureInfo.InvariantCulture)} GiB</td></tr></table><details><summary>ОС, память и дополнительные данные</summary><pre>{E(JsonSerializer.Serialize(report.Hardware, Program.Json))}</pre></details></section>");
        html.Append("<section><h2>Результаты Benchmark Tool</h2><table><tr><th>Проход</th><th>Средний FPS</th><th>Минимальный FPS</th><th>Максимальный FPS</th></tr>");
        foreach (var pass in report.Passes)
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(pass.Settings.Name)}</td><td>{pass.Results.AverageFps:F1}</td><td>{pass.Results.MinimumFps:F1}</td><td>{pass.Results.MaximumFps:F1}</td></tr>");
        html.Append("</table><p class=note>FPS распознаны с итогового экрана самого бенчмарка; скриншоты и OCR сохранены. Пропущенные/ошибочные проходы не получают выдуманных результатов.</p></section>");
        foreach (var pass in report.Passes)
        {
            var p = pass.Settings;
            html.Append($"<section><h2>{E(p.Name)}: настройки и подтверждение</h2><p>{p.Width}×{p.Height}; масштаб {p.RenderPercent}%; TSR; Frame Generation OFF; VSync OFF; FPS без ограничения; Full RT {(p.RayTracing ? "Very High" : "OFF")}</p>");
            html.Append($"<p>{(p.IsCpu ? "Дальность и растительность Cinematic, остальные группы Low." : "Все группы Cinematic.")} Проверка настроек: {E(pass.SettingsValidation)}.</p>");
            html.Append($"<details><summary>Значения ScalabilityGroups</summary><pre>{E(JsonSerializer.Serialize(p.Scalability, Program.Json))}</pre></details>");
            html.Append($"<p><a href=\"{E(p.Name.ToLowerInvariant())}/effective.ini\">Настройки, сохранённые бенчмарком</a></p><img alt=\"Экран результатов {E(p.Name)}\" src=\"{E(pass.Screenshot)}\"></section>");
        }
        html.Append("<p class=note>CPU-профиль снижает нагрузку на GPU, но сцена Wukong не гарантирует чистую изоляцию процессора. GPU-профиль использует максимальное качество при выбранном разрешении. Обе оценки зависят от всего ПК.</p></html>");
        File.WriteAllText(Path.Combine(output, "report.html"), html.ToString(), new UTF8Encoding(false));
    }
}
