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

    internal static NumberCrop[] NumberAreas(OcrPage original, OcrPage column, Rectangle bounds, Bitmap? source = null)
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
            if (words.Length == 0)
            {
                if (source is null) throw new InvalidDataException("Missing numeric image below " + label.Text);
                return new NumberCrop(label, MissingNumberArea(source, label, bounds));
            }
            // An OCR token such as I locates pixels only; it is never converted to 1.
            var glyph = words[0];
            var area = Rectangle.FromLTRB((int)Math.Floor(glyph.X - 3), (int)Math.Floor(glyph.Y - 3),
                (int)Math.Ceiling(glyph.X + glyph.Width + 3), (int)Math.Ceiling(glyph.Y + glyph.Height + 3));
            if (!bounds.Contains(area)) throw new InvalidDataException("Numeric crop leaves FPS column.");
            return new NumberCrop(label, area);
        }).ToArray();
    }

    internal static Rectangle MissingNumberArea(Bitmap source, OcrWord label, Rectangle bounds)
    {
        // OCR can omit the entire large Average number. Locate its bright glyphs
        // below the recognized caption, then ask OCR to read their original pixels.
        // Pixel geometry selects an area only; it does not infer any digit or FPS.
        var search = Rectangle.Intersect(bounds, Rectangle.FromLTRB((int)Math.Floor(label.X),
            (int)Math.Ceiling(label.Y + label.Height * 1.6),
            (int)Math.Ceiling(label.X + label.Width + label.Height * 10),
            (int)Math.Floor(label.Y + label.Height * 7)));
        if (search.Width <= 0 || search.Height <= 0 || !new Rectangle(0, 0, source.Width, source.Height).Contains(search))
            throw new InvalidDataException("Invalid missing-number search area.");
        var bright = new bool[search.Width, search.Height];
        for (int y = 0; y < search.Height; y++)
            for (int x = 0; x < search.Width; x++)
            {
                var color = source.GetPixel(search.X + x, search.Y + y);
                bright[x, y] = (color.R + color.G + color.B) / 3 >= 140;
            }
        var components = new List<Rectangle>();
        for (int y = 0; y < search.Height; y++)
            for (int x = 0; x < search.Width; x++)
            {
                if (!bright[x, y]) continue;
                var queue = new Queue<Point>();
                queue.Enqueue(new Point(x, y)); bright[x, y] = false;
                int left = x, right = x, top = y, bottom = y;
                while (queue.TryDequeue(out var point))
                {
                    left = Math.Min(left, point.X); right = Math.Max(right, point.X);
                    top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = point.X + dx, ny = point.Y + dy;
                            if (nx >= 0 && ny >= 0 && nx < search.Width && ny < search.Height && bright[nx, ny])
                            { bright[nx, ny] = false; queue.Enqueue(new Point(nx, ny)); }
                        }
                }
                components.Add(Rectangle.FromLTRB(search.X + left, search.Y + top, search.X + right + 1, search.Y + bottom + 1));
            }
        // The large numeral is taller than the small FPS suffix. Keep one contiguous
        // group; extra bright regions must fail instead of joining unrelated pixels.
        var glyphs = components.Where(c => c.Height >= label.Height * 1.5).OrderBy(c => c.X).ToArray();
        if (glyphs.Length == 0 || glyphs.Zip(glyphs.Skip(1)).Any(p => p.Second.Left - p.First.Right > label.Height * 1.5))
            throw new InvalidDataException("Cannot isolate missing FPS glyphs.");
        var area = Rectangle.FromLTRB(glyphs.Min(c => c.Left) - 3, glyphs.Min(c => c.Top) - 3,
            glyphs.Max(c => c.Right) + 3, glyphs.Max(c => c.Bottom) + 3);
        if (!bounds.Contains(area)) throw new InvalidDataException("Missing FPS glyphs leave result column.");
        return area;
    }

    internal static OcrPage MapNumbers(OcrPage recognized, NumberCrop[] crops, Rectangle[] tiles, double scale)
    {
        if (recognized.Words.Count() != crops.Length) throw new InvalidDataException("Ambiguous numeric OCR strip: " + recognized.Text);
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
            var crops = NumberAreas(page, column, area, bitmap);
            File.WriteAllText(stem + "-results-crops.json", JsonSerializer.Serialize(crops, Program.Json));
            InvalidDataException? lastError = null;
            foreach (int padding in new[] { 30, 90 })
            {
                try { return await ReadStrip(bitmap, stem, crops, padding, recognize); }
                catch (InvalidDataException error) { lastError = error; }
            }
            try { return await ReadIndividual(bitmap, source, stem, column, area, crops, recognize); }
            catch (InvalidDataException error) { throw new InvalidDataException(error.Message + " Previous strip error: " + lastError!.Message, error); }
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            File.WriteAllText(stem + "-results-error.txt", error.Message);
            return page; // Preserve evidence and retry another capture without guessing.
        }
    }

    internal static OcrWord ReadIndividualValue(OcrPage page, Rectangle numericTile, NumberCrop crop)
    {
        if (!Regex.IsMatch(page.Text.Trim(), @"^\d{1,4}(?:[.,]\d{1,3})?\s+FPS$", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Individual FPS crop did not yield an exact number and FPS suffix: " + page.Text);
        var values = page.Words.Where(w => Regex.IsMatch(w.Text, @"^\d{1,4}(?:[.,]\d{1,3})?$")).ToArray();
        if (values.Length != 1 || !numericTile.Contains((int)values[0].CenterX, (int)values[0].CenterY))
            throw new InvalidDataException("Individual FPS reading is outside its numeric pixels.");
        return values[0] with { X = crop.Area.X, Y = crop.Area.Y, Width = crop.Area.Width, Height = crop.Area.Height };
    }

    private static async Task<OcrPage> ReadIndividual(Bitmap bitmap, string source, string stem, OcrPage column,
        Rectangle bounds, NumberCrop[] crops, Func<string, double, int, int, Task<OcrPage>> recognize)
    {
        var lines = new List<OcrLine> { new("Benchmark Results", [new("Results", 0, 0, 1, 1)]) };
        string[] names = ["avg", "min", "max"];
        for (int i = 0; i < crops.Length; i++)
        {
            var crop = crops[i];
            OcrWord? value = null;
            // First enlarge the actual number and its actual FPS suffix together.
            var suffixes = column.Words.Where(w => string.Equals(w.Text, "FPS", StringComparison.OrdinalIgnoreCase)
                && w.X >= crop.Area.Right - 3 && w.X < crop.Area.Right + Math.Max(crop.Area.Width * 2, crop.Label.Height * 4)
                && Math.Abs(w.CenterY - (crop.Area.Top + crop.Area.Height / 2d)) < crop.Area.Height).ToArray();
            if (suffixes.Length == 1)
            {
                var suffix = suffixes[0];
                var row = Rectangle.Union(crop.Area, Rectangle.FromLTRB((int)Math.Floor(suffix.X - 3), (int)Math.Floor(suffix.Y - 3),
                    (int)Math.Ceiling(suffix.X + suffix.Width + 3), (int)Math.Ceiling(suffix.Y + suffix.Height + 3)));
                if (!bounds.Contains(row)) throw new InvalidDataException("Individual FPS crop leaves result column.");
                string image = stem + "-results-" + names[i] + "-native-ocr.png";
                double scale = OcrImage.Prepare(source, image, row, maximumScale: 8, gamma: 1);
                var reading = await recognize(image, 1, 0, 0);
                File.WriteAllText(stem + "-results-" + names[i] + "-native.json", JsonSerializer.Serialize(reading, Program.Json));
                var tile = new Rectangle((int)((crop.Area.X - row.X) * scale), (int)((crop.Area.Y - row.Y) * scale),
                    (int)(crop.Area.Width * scale), (int)(crop.Area.Height * scale));
                try { value = ReadIndividualValue(reading, tile, crop); }
                catch (InvalidDataException) { }
            }
            // A standalone serif 1 can be omitted even in a native crop. Add only
            // the letters FPS at the numeral's height to give OCR text context.
            // Every numeric pixel still comes from the screenshot. No numeral,
            // expected result or substitution of I/O is supplied to the engine.
            if (value is null)
            {
                foreach (int height in new[] { 80, 100, 120 })
                {
                    string contextStem = stem + "-results-" + names[i] + "-context-" + height;
                    var tile = PrepareContext(bitmap, crop, contextStem + "-ocr.png", height);
                    var reading = await recognize(contextStem + "-ocr.png", 1, 0, 0);
                    File.WriteAllText(contextStem + ".json", JsonSerializer.Serialize(reading, Program.Json));
                    OcrWord candidate;
                    try { candidate = ReadIndividualValue(reading, tile, crop); }
                    catch (InvalidDataException) { continue; }
                    if (value is not null && candidate.Text != value.Text)
                        throw new InvalidDataException("Conflicting individual FPS readings for " + crop.Label.Text);
                    value = candidate;
                }
            }
            if (value is null) throw new InvalidDataException("Cannot read individual FPS pixels for " + crop.Label.Text);
            lines.Add(new(crop.Label.Text, [crop.Label]));
            lines.Add(new(value.Text, [value]));
        }
        var result = new OcrPage(string.Join("\n", lines.Select(l => l.Text)), lines.ToArray());
        _ = ResultParser.Parse(result);
        return result;
    }

    internal static Rectangle PrepareContext(Bitmap source, NumberCrop crop, string destination, int height)
    {
        double scale = (double)height / crop.Area.Height;
        var tile = new Rectangle(40, 40, (int)(crop.Area.Width * scale), height);
        using var image = new Bitmap(tile.Width + height * 4 + 100, height + 80, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, tile, crop.Area, GraphicsUnit.Pixel);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float fontSize = (float)((crop.Area.Height - 6) * scale * 1.4);
            using var font = new Font("Arial", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.DrawString("FPS", font, Brushes.White, tile.Right - 5, (float)(40 + 3 * scale - fontSize * .18));
        }
        // Invert contrast only; the original numeral shape is preserved.
        using (var attributes = new ImageAttributes())
        {
            attributes.SetColorMatrix(new ColorMatrix(new[] {
                new float[] {-1, 0, 0, 0, 0}, new float[] {0, -1, 0, 0, 0}, new float[] {0, 0, -1, 0, 0},
                new float[] {0, 0, 0, 1, 0}, new float[] {1, 1, 1, 0, 1} }));
            using var inverted = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(inverted))
                graphics.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            inverted.Save(destination, ImageFormat.Png);
        }
        return tile;
    }

    private static async Task<OcrPage> ReadStrip(Bitmap bitmap, string stem, NumberCrop[] crops, int padding,
        Func<string, double, int, int, Task<OcrPage>> recognize)
    {
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
        string digitStem = stem + (padding == 30 ? "-results-digits" : "-results-digits-spaced");
        string digitsImage = digitStem + "-ocr.png";
        strip.Save(digitsImage, ImageFormat.Png);
        var digits = await recognize(digitsImage, 1, 0, 0);
        File.WriteAllText(digitStem + ".json", JsonSerializer.Serialize(digits, Program.Json));
        return MapNumbers(digits, crops, tiles, digitScale);
    }
}
