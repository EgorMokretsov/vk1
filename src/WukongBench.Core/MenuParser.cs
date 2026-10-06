using System.Text.RegularExpressions;

namespace WukongBench.Core;

public static class MenuParser
{
    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static OcrLine? FindRow(OcrPage page, string label, int width = 0) => page.Lines
        .Where(l => l.Words.Length > 0 && Normalize(l.Text).StartsWith(Normalize(label), StringComparison.Ordinal)
            && (Normalize(label) != "superresolution" || !Normalize(l.Text).Contains("sampling", StringComparison.Ordinal)))
        // A section heading can repeat a control's label. Prefer the row with an actual value.
        .OrderByDescending(l => width > 0 && ValueWords(page, l, width).Any()).FirstOrDefault();
    private static IEnumerable<OcrWord> ValueWords(OcrPage page, OcrLine label, int width)
    {
        if (label.Words.Length == 0) yield break;
        var words = label.Words.OrderBy(w => w.X).ToArray();
        var first = words[0];
        double labelRight = first.X + first.Width;
        // OCR sometimes merges the value into the label line: a large gap ends the label.
        foreach (var word in words.Skip(1))
        {
            if (word.X - labelRight > width * .04) break;
            labelRight = Math.Max(labelRight, word.X + word.Width);
        }
        // The separate help column contains descriptions, not setting values.
        double helpLeft = page.Lines.Where(l => l.Words.Length > 0)
            .Select(l => l.Words.Min(w => w.X)).Where(x => x >= width * .60)
            .DefaultIfEmpty(width).Min();
        double lastRight = 0;
        foreach (var word in page.Words.Where(w => Math.Abs(w.CenterY - first.CenterY) < first.Height
                     && w.X >= labelRight + width * .015 && w.X + w.Width < helpLeft)
                 .OrderBy(w => w.X))
        {
            if (lastRight > 0 && word.X - lastRight > width * .04) yield break;
            yield return word;
            lastRight = word.X + word.Width;
        }
    }
    public static string Value(OcrPage page, OcrLine label, int width) => string.Join(" ", ValueWords(page, label, width).Select(w => w.Text));
    public static OcrWord Control(OcrPage page, OcrLine label, int width) =>
        ValueWords(page, label, width).FirstOrDefault(w => Regex.IsMatch(w.Text, @"[A-Za-z0-9]"))
        ?? throw new InvalidDataException("The value of " + label.Text + " was not recognized; no blind click was made.");
}
