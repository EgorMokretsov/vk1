using WukongBench.Core;

namespace WukongBench;

internal sealed record DisabledSettingEvidence(double LabelContrast, double ReferenceContrast, bool Disabled);

internal static class MenuAppearance
{
    public static DisabledSettingEvidence Inspect(string screenshot, OcrLine label, OcrLine reference)
    {
        using var bitmap = new Bitmap(screenshot);
        double Contrast(OcrLine line)
        {
            if (line.Words.Length == 0) throw new InvalidDataException("Cannot inspect empty setting label.");
            var word = line.Words[0];
            int left = (int)Math.Floor(word.X), top = (int)Math.Floor(word.Y);
            int right = (int)Math.Ceiling(word.X + word.Width), bottom = (int)Math.Ceiling(word.Y + word.Height);
            if (left < 0 || top < 0 || right > bitmap.Width || bottom > bitmap.Height || right <= left || bottom <= top)
                throw new InvalidDataException("Setting label lies outside screenshot.");
            double min = 255, max = 0;
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var color = bitmap.GetPixel(x, y);
                double luminance = .2126 * color.R + .7152 * color.G + .0722 * color.B;
                min = Math.Min(min, luminance); max = Math.Max(max, luminance);
            }
            return max - min;
        }
        double actual = Contrast(label), active = Contrast(reference);
        return new DisabledSettingEvidence(actual, active, active >= 60 && actual < active * .35);
    }
}
