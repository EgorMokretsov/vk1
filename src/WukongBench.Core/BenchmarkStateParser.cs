using System.Globalization;
using System.Text.RegularExpressions;

namespace WukongBench.Core;

public static class BenchmarkStateParser
{
    public static bool IsRunning(OcrPage page, int width, int height)
    {
        if (Regex.IsMatch(page.Text, @"\bresults?\b|\baverage\b|\bminimum\b|\bmaximum\b|\bsettings\b|\bconfirm\b|\bpurchase\s+now\b", RegexOptions.IgnoreCase)) return false;
        if (!page.Words.Any(w => w.Text.Equals("Exit", StringComparison.OrdinalIgnoreCase)
            && w.X > width * .7 && w.Y > height * .8)) return false;

        foreach (var line in page.Lines.Where(l => l.Words.Length > 0))
        {
            var first = line.Words[0];
            if (first.X > width * .2 || first.Y > height * .2) continue;
            string label = new(line.Text.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
            // Small HUD text may be merged or read as CurratFPS / Cur-rat FPS.
            // Allow two character edits only for the header, never for FPS numbers.
            if (Distance(label, "currentfps") > 2) continue;
            if (page.Words.Any(w => w.X < width * .2
                && w.CenterY > first.CenterY
                && w.CenterY - first.CenterY <= Math.Max(first.Height * 6, 28)
                && Regex.IsMatch(w.Text.Trim(), @"^\d{1,4}(?:[.,]\d{1,3})?(?:\s*FPS)?$", RegexOptions.IgnoreCase)
                && double.TryParse(Regex.Replace(w.Text.Trim(), @"\s*FPS$", "", RegexOptions.IgnoreCase).Replace(',', '.'),
                    NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double fps) && fps <= 5000)) return true;
        }
        return false;
    }

    private static int Distance(string actual, string expected)
    {
        if (Math.Abs(actual.Length - expected.Length) > 2) return 3;
        int[] previous = Enumerable.Range(0, expected.Length + 1).ToArray();
        for (int i = 1; i <= actual.Length; i++)
        {
            var current = new int[expected.Length + 1]; current[0] = i;
            for (int j = 1; j <= expected.Length; j++)
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (actual[i - 1] == expected[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }
}
