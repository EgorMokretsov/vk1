using System.Globalization;
using System.Text.RegularExpressions;

namespace WukongBench.Core;

public sealed record OcrWord(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}
public sealed record OcrLine(string Text, OcrWord[] Words);
public sealed record OcrPage(string Text, OcrLine[] Lines)
{
    public IEnumerable<OcrWord> Words => Lines.SelectMany(l => l.Words);
}
public sealed record Metrics(double AverageFps, double MinimumFps, double MaximumFps);

public static class ResultParser
{
    public static bool IsResultScreen(OcrPage page) =>
        Regex.IsMatch(page.Text, @"\b(results?|benchmark\s+results?)\b", RegexOptions.IgnoreCase);
    // Numbers may be above their labels, so association is geometric instead of relying on OCR line order.
    public static Metrics Parse(OcrPage page)
    {
        if (!IsResultScreen(page))
            throw new InvalidDataException("This is not a benchmark result screen.");
        double Get(string pattern)
        {
            var labels = page.Words.Where(w => Regex.IsMatch(w.Text, pattern, RegexOptions.IgnoreCase)).ToArray();
            if (labels.Length != 1) throw new InvalidDataException($"Missing or ambiguous FPS label: {pattern}");
            var label = labels[0];
            var candidates = page.Words.Select(w => (Word: w, Value: Number(w.Text)))
                .Where(p => p.Value.HasValue &&
                    ((Math.Abs(p.Word.CenterY - label.CenterY) <= label.Height * 1.3 && p.Word.X > label.X && p.Word.X - label.X < label.Height * 16) ||
                     (Math.Abs(p.Word.CenterX - label.CenterX) < Math.Max(label.Width, label.Height * 4) && Math.Abs(p.Word.CenterY - label.CenterY) < label.Height * 7)))
                .OrderBy(p => Math.Abs(p.Word.CenterY - label.CenterY) * 2 + Math.Abs(p.Word.CenterX - label.CenterX)).ToArray();
            if (candidates.Length == 0) throw new InvalidDataException("Cannot associate a numeric FPS value with " + label.Text);
            return candidates[0].Value!.Value;
        }
        var m = new Metrics(Get(@"^aver(age)?$|^avg\.?$"), Get(@"^min(imum)?\.?$"), Get(@"^max(imum)?\.?$"));
        // Benchmark Tool rounds very slow frames to 0 FPS. A zero minimum is
        // legitimate; the average must still be positive and ordering preserved.
        if (m.MinimumFps < 0 || m.AverageFps <= 0 || m.MinimumFps > m.AverageFps || m.AverageFps > m.MaximumFps || m.MaximumFps > 5000)
            throw new InvalidDataException("Invalid FPS ordering/range; preserve screenshot for inspection.");
        return m;
    }
    private static double? Number(string text)
    {
        var match = Regex.Match(text.Trim(), @"^(\d{1,4}(?:[.,]\d{1,3})?)(?:\s*FPS)?$", RegexOptions.IgnoreCase);
        return match.Success ? double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : null;
    }
}
