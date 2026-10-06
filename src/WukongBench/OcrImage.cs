using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WukongBench.Core;

namespace WukongBench;

// Menu values are deliberately dim when their row is not selected. Preserve the original
// screenshot as evidence and brighten/enlarge only the separate image sent to OCR.
internal static class OcrImage
{
    public static double Prepare(string sourcePath, string destinationPath, Rectangle? region = null,
        double maximumScale = 2, double gamma = .35, bool invert = false)
    {
        using var source = new Bitmap(sourcePath);
        var area = region ?? new Rectangle(0, 0, source.Width, source.Height);
        if (area.Width <= 0 || area.Height <= 0 || !new Rectangle(0, 0, source.Width, source.Height).Contains(area))
            throw new InvalidDataException("OCR region is outside the screenshot.");
        double scale = Math.Min(maximumScale, 2560d / Math.Max(area.Width, area.Height));
        using var bitmap = new Bitmap((int)(area.Width * scale), (int)(area.Height * scale), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height), area, GraphicsUnit.Pixel);
        }
        byte[] lookup = Enumerable.Range(0, 256).Select(i =>
        {
            int value = (int)Math.Round(255 * Math.Pow(i / 255d, gamma));
            return (byte)(invert ? 255 - value : value);
        }).ToArray();
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr pointer = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(pointer, row, 0, row.Length);
                for (int x = 0; x < bitmap.Width * 3; x++) row[x] = lookup[row[x]];
                Marshal.Copy(row, 0, pointer, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(destinationPath, ImageFormat.Png);
        return scale;
    }
    public static OcrPage OriginalCoordinates(OcrPage page, double scale, double offsetX = 0, double offsetY = 0) => page with
    {
        Lines = page.Lines.Select(l => l with
        {
            Words = l.Words.Select(w => w with { X = w.X / scale + offsetX, Y = w.Y / scale + offsetY, Width = w.Width / scale, Height = w.Height / scale }).ToArray()
        }).ToArray()
    };
}
