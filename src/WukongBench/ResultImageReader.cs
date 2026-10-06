using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;
using WukongBench.Core;

namespace WukongBench;

internal static class ResultImageReader
{
    internal static readonly string[] Labels = [@"^aver(age)?$|^avg\.?$", @"^min(imum)?\.?$", @"^max(imum)?\.?$"];
    internal sealed record NumberCrop(OcrWord Label, Rectangle Area);

    internal static Rectangle Column(OcrPage page, int width, int height)
    {
        var title = page.Lines.Single(l => Regex.IsMatch(l.Text, @"^benchmark\s+results?$", RegexOptions.IgnoreCase));
        double left = title.Words.Min(w => w.X), top = title.Words.Min(w => w.Y);
        var neighbor = page.Lines.Where(l => Regex.IsMatch(l.Text, @"^operating\s+system$", RegexOptions.IgnoreCase))
            .SelectMany(l => l.Words).Min(w => w.X);
        var captions = page.Words.Where(w => Labels.Any(p => Regex.IsMatch(w.Text, p, RegexOptions.IgnoreCase))).ToArray();
        if (captions.Length < 2 || neighbor <= left) throw new InvalidDataException("Cannot locate the FPS result column.");
        return Rectangle.FromLTRB(Math.Max(0, (int)Math.Floor(left - width * .01)),
            Math.Max(0, (int)Math.Floor(top - height * .015)), (int)Math.Floor(neighbor - width * .04),
            Math.Min(height, (int)Math.Ceiling(captions.Max(w => w.Y + w.Height * 7))));
    }

    internal static NumberCrop[] NumberAreas(OcrPage original, OcrPage column, Rectangle bounds)
    {
        return Labels.Select(pattern =>
        {
            // Prefer exact labels from the enlarged column, then the original reading.
            // No spelling correction is used for labels or numeric values.
            var labels = column.Words.Where(w => Regex.IsMatch(w.Text, pattern, RegexOptions.IgnoreCase)).ToArray();
            if (labels.Length == 0) labels = original.Words.Where(w => Regex.IsMatch(w.Text, pattern, RegexOptions.IgnoreCase)).ToArray();
            if (labels.Length != 1) throw new InvalidDataException("Missing or ambiguous result label: " + pattern);
            var label = labels[0];
            var words = column.Words.Where(w => w.Y > label.Y + label.Height + 2
                && w.Y < label.Y + label.Height * 7 && w.X >= label.X - label.Height
                && w.X < label.X + label.Width && !Regex.IsMatch(w.Text, @"^FPS$", RegexOptions.IgnoreCase))
                .OrderBy(w => w.Y).ThenBy(w => w.X).ToArray();
            if (words.Length == 0) throw new InvalidDataException("Missing numeric image below " + label.Text);
            // An OCR token such as I locates pixels only; it is never converted to 1.
            var glyph = words[0];
            var area = Rectangle.FromLTRB((int)Math.Floor(glyph.X - 3), (int)Math.Floor(glyph.Y - 3),
                (int)Math.Ceiling(glyph.X + glyph.Width + 3), (int)Math.Ceiling(glyph.Y + glyph.Height + 3));
            if (!bounds.Contains(area)) throw new InvalidDataException("Numeric crop leaves FPS column.");
            return new NumberCrop(label, area);
        }).ToArray();
    }

    internal static OcrPage MapNumbers(OcrPage recognized, NumberCrop[] crops, Rectangle[] tiles, double scale)
    {
        if (recognized.Words.Count() != crops.Length) throw new InvalidDataException("Ambiguous numeric OCR strip.");
        var lines = new List<OcrLine> { new("Benchmark Results", [new("Results", 0, 0, 1, 1)]) };
        for (int i = 0; i < crops.Length; i++)
        {
            var tile = tiles[i];
            var values = recognized.Words.Where(w => tile.Contains((int)w.CenterX, (int)w.CenterY)).ToArray();
            if (values.Length != 1 || !Regex.IsMatch(values[0].Text, @"^\d{1,4}(?:[.,]\d{1,3})?$"))
                throw new InvalidDataException("No unambiguous numeric reading for " + crops[i].Label.Text);
            var value = values[0];
            var mapped = value with { X = crops[i].Area.X + (value.X - tile.X) / scale,
                Y = crops[i].Area.Y + (value.Y - tile.Y) / scale, Width = value.Width / scale, Height = value.Height / scale };
            lines.Add(new(crops[i].Label.Text, [crops[i].Label]));
            lines.Add(new(value.Text, [mapped]));
        }
        var page = new OcrPage(string.Join("\n", lines.Select(l => l.Text)), lines.ToArray());
        _ = ResultParser.Parse(page);
        return page;
    }

    internal static async Task<OcrPage> Read(string source, string stem, OcrPage page,
        Func<string, double, int, int, Task<OcrPage>> recognize)
    {
        try { _ = ResultParser.Parse(page); return page; }
        catch (InvalidDataException) { }
        using var bitmap = new Bitmap(source);
        try
        {
            var area = Column(page, bitmap.Width, bitmap.Height);
            string columnImage = stem + "-results-column-ocr.png";
            double scale = OcrImage.Prepare(source, columnImage, area, maximumScale: 4, gamma: 1);
            var column = await recognize(columnImage, scale, area.X, area.Y);
            File.WriteAllText(stem + "-results-column.json", JsonSerializer.Serialize(column, Program.Json));
            try { _ = ResultParser.Parse(column); return column; }
            catch (InvalidDataException) { }
            var crops = NumberAreas(page, column, area);
            const int padding = 30;
            const double digitScale = 6;
            int stripWidth = padding + crops.Sum(c => c.Area.Width * (int)digitScale + padding);
            int stripHeight = crops.Max(c => c.Area.Height) * (int)digitScale + padding * 2;
            if (Math.Max(stripWidth, stripHeight) > 2560) throw new InvalidDataException("Numeric strip exceeds OCR dimensions.");
            using var strip = new Bitmap(stripWidth, stripHeight, PixelFormat.Format24bppRgb);
            var tiles = new Rectangle[crops.Length];
            using (var graphics = Graphics.FromImage(strip))
            {
                graphics.Clear(Color.Black);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                int x = padding;
                for (int i = 0; i < crops.Length; i++)
                {
                    tiles[i] = new Rectangle(x, padding, crops[i].Area.Width * (int)digitScale, crops[i].Area.Height * (int)digitScale);
                    graphics.DrawImage(bitmap, tiles[i], crops[i].Area, GraphicsUnit.Pixel);
                    x += tiles[i].Width + padding;
                }
            }
            string digitsImage = stem + "-results-digits-ocr.png";
            strip.Save(digitsImage, ImageFormat.Png);
            var digits = await recognize(digitsImage, 1, 0, 0);
            File.WriteAllText(stem + "-results-digits.json", JsonSerializer.Serialize(digits, Program.Json));
            return MapNumbers(digits, crops, tiles, digitScale);
        }
        catch (InvalidDataException) { return page; } // Keep evidence; the caller can retry another capture.
        catch (InvalidOperationException) { return page; } // Missing layout anchors are not guessed.
    }
}

