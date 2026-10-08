using System.Text.RegularExpressions;

namespace WukongBench.Core;

public static class MenuParser
{
    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static bool HasPendingGraphicsChanges(OcrPage page, int height) => page.Lines.Any(l =>
        Normalize(l.Text) == "applygraphicschanges" && l.Words.Length > 0 && l.Words.Min(w => w.Y) > height * .8);
    public static OcrLine? FindRow(OcrPage page, string label, int width = 0) => page.Lines
        .Where(l => l.Words.Length > 0 && Normalize(l.Text).StartsWith(Normalize(label), StringComparison.Ordinal)
            // The slider label has no word suffix. This also excludes misspelled section
            // headings such as "Super Resolution Samphng" without guessing OCR spelling.
            && (Normalize(label) != "superresolution" || Regex.IsMatch(Normalize(l.Text), @"^superresolution\d{0,3}$"))
            && (width <= 0 || l.Words.Min(w => w.X) < width * .60))
        // A section heading can repeat a control's label. Prefer the row with an actual value.
        .OrderByDescending(l => width > 0 && ValueWords(page, l, width).Any()).FirstOrDefault();
    private static double LabelRight(OcrLine label, int width)
    {
        var words = label.Words.OrderBy(w => w.X).ToArray();
        if (words.Length == 0) throw new InvalidDataException("Empty setting label.");
        var first = words[0];
        double labelRight = first.X + first.Width;
        // OCR sometimes merges the value into the label line: a large gap ends the label.
        foreach (var word in words.Skip(1))
        {
            if (word.X - labelRight > width * .04) break;
            labelRight = Math.Max(labelRight, word.X + word.Width);
        }
        return labelRight;
    }
    // The separate help column contains descriptions, not setting values.
    private static double HelpLeft(OcrPage page, int width) => page.Lines.Where(l => l.Words.Length > 0)
            .Select(l => l.Words.Min(w => w.X)).Where(x => x >= width * .60)
            .DefaultIfEmpty(width).Min();
    private static (OcrWord Left, OcrWord Right)? SliderEnds(OcrPage page, OcrLine label, int width)
    {
        if (!Regex.IsMatch(Normalize(label.Text), @"^superresolution\d{0,3}$")) return null;
        var first = label.Words[0];
        var numbers = page.Words.Where(w => Regex.IsMatch(w.Text, @"^\d{1,3}$")
            && Math.Abs(w.CenterY - first.CenterY) < first.Height
            && w.X > LabelRight(label, width) + width * .015 && w.X + w.Width < HelpLeft(page, width)).OrderBy(w => w.X).ToArray();
        // On the focused slider the observed endpoint captions are 33 and 100.
        // The moving value box is between them, even when its value is also 100.
        if (numbers.Length >= 2 && numbers[0].Text == "33" && numbers[^1].Text == "100"
            && numbers[^1].X - numbers[0].X > width * .08)
            return (numbers[0], numbers[^1]);
        return null;
    }
    public static MenuRegion ValueRegion(OcrPage page, OcrLine label, int width, int height)
    {
        var first = label.Words[0];
        int x = (int)Math.Ceiling(LabelRight(label, width) + width * .015);
        int right = (int)Math.Floor(HelpLeft(page, width) - width * .04);
        if (SliderEnds(page, label, width) is { } ends)
        {
            // Crop strictly inside the visible range captions, preserving the box
            // near either endpoint but excluding the captions themselves.
            x = (int)Math.Ceiling(ends.Left.X + ends.Left.Width + width * .003);
            right = (int)Math.Floor(ends.Right.X - width * .003);
        }
        double padding = Math.Max(12, first.Height * 1.8);
        int y = Math.Max(0, (int)Math.Floor(first.CenterY - padding));
        int bottom = Math.Min(height, (int)Math.Ceiling(first.CenterY + padding));
        if (right <= x || bottom <= y) throw new InvalidDataException("Cannot isolate setting value region.");
        return new MenuRegion(x, y, right - x, bottom - y);
    }
    private static IEnumerable<OcrWord> ValueWords(OcrPage page, OcrLine label, int width)
    {
        if (label.Words.Length == 0) yield break;
        var first = label.Words.OrderBy(w => w.X).First();
        double labelRight = LabelRight(label, width);
        double helpLeft = HelpLeft(page, width);
        var ends = SliderEnds(page, label, width);
        double lastRight = 0;
        foreach (var word in page.Words.Where(w => Regex.IsMatch(w.Text, @"[A-Za-z0-9]") && Math.Abs(w.CenterY - first.CenterY) < first.Height
                     && w.X >= labelRight + width * .015 && w.X + w.Width < helpLeft
                     && (ends is null || (w.X > ends.Value.Left.X + ends.Value.Left.Width && w.X + w.Width < ends.Value.Right.X)))
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

public sealed record MenuRegion(int X, int Y, int Width, int Height);

