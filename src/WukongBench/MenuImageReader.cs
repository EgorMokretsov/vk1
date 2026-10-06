using System.Text.Json;
using System.Text.RegularExpressions;
using WukongBench.Core;

namespace WukongBench;

internal static class MenuImageReader
{
    public static async Task<OcrPage> Read(string source, string stem, CancellationToken ct)
    {
        async Task<OcrPage> Recognize(string image, double scale, int x = 0, int y = 0)
        {
            var json = await Shell.PowerShell(Path.Combine(AppContext.BaseDirectory, "scripts", "Ocr.ps1"), ct, "-ImagePath", image);
            var recognized = JsonSerializer.Deserialize<OcrPage>(json, Program.Json)
                ?? throw new InvalidDataException("Empty OCR response.");
            return OcrImage.OriginalCoordinates(recognized, scale, x, y);
        }
        string fullImage = stem + "-ocr.png";
        double scale = OcrImage.Prepare(source, fullImage);
        var page = await Recognize(fullImage, scale);
        if (Regex.IsMatch(page.Text, @"\bresults?\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(page.Text, @"\baverage\b|\bminimum\b|\bmaximum\b", RegexOptions.IgnoreCase))
        {
            try { _ = ResultParser.Parse(page); }
            catch (InvalidDataException)
            {
                // Brightening used for dim menus can erase an otherwise clear FPS digit.
                // Retry the unchanged contrast/size and accept only a complete valid result.
                string resultsImage = stem + "-results-ocr.png";
                scale = OcrImage.Prepare(source, resultsImage, maximumScale: 1, gamma: 1);
                var alternative = await Recognize(resultsImage, scale);
                File.WriteAllText(stem + "-results.json", JsonSerializer.Serialize(alternative, Program.Json));
                try { _ = ResultParser.Parse(alternative); page = alternative; }
                catch (InvalidDataException) { /* Preserve the failed reading for diagnostics. */ }
            }
        }
        // The final report also lists graphics settings, but has no menu controls.
        // Never run slider/RT enhancement against its read-only settings table.
        if (ResultParser.IsResultScreen(page)) return await ResultImageReader.Read(source, stem, page, Recognize);
        using var bitmap = new Bitmap(source);
        async Task Enhance(string label, string pattern, string suffix, double gamma)
        {
            var row = MenuParser.FindRow(page, label, bitmap.Width);
            if (row is null || Regex.IsMatch(MenuParser.Value(page, row, bitmap.Width), pattern, RegexOptions.IgnoreCase)) return;
            var region = MenuParser.ValueRegion(page, row, bitmap.Width, bitmap.Height);
            string valuesImage = stem + suffix + "-ocr.png";
            // Preserve contrast of the small gray numeric box. Full-screen brightening
            // flattens its contrast; a larger inverted crop reads its digits separately.
            scale = OcrImage.Prepare(source, valuesImage, new Rectangle(region.X, region.Y, region.Width, region.Height),
                maximumScale: 6, gamma: gamma, invert: true);
            var values = await Recognize(valuesImage, scale, region.X, region.Y);
            File.WriteAllText(stem + suffix + ".json", JsonSerializer.Serialize(values, Program.Json));
            var readings = values.Lines.Where(l => l.Words.Length > 0
                && Regex.IsMatch(l.Text.Trim(), pattern, RegexOptions.IgnoreCase)
                && Math.Abs(l.Words[0].CenterY - row.Words[0].CenterY) < row.Words[0].Height).ToArray();
            // Ambiguous/missing readings remain a failure; no expected number is inserted.
            if (readings.Length == 1) page = page with { Lines = page.Lines.Concat(readings).ToArray(), Text = page.Text + "\n" + readings[0].Text };
        }
        await Enhance("super resolution", @"^\d{1,3}$", "-values", 1);
        await Enhance("full ray tracing", @"^(on|off)$", "-rt-values", .35);
        return page;
    }
}

