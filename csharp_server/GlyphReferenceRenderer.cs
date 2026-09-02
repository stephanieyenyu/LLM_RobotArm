using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

/// <summary>
/// Builds a reference glyph dynamically from an installed Unicode font. This
/// is not a per-character dataset: any character covered by the font can be
/// rendered on demand. The reference is used only by anonymous ballot judges.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GlyphReferenceRenderer
{
    private const int RenderSize = 256;
    private const string FontFamilyName = "Microsoft JhengHei";

    public static List<string>? TryRenderFromCommand(
        string command, int outputRows = 15, int outputCols = 15)
    {
        string? target = ExtractTarget(command);
        if (string.IsNullOrWhiteSpace(target)) return null;

        try
        {
            using var image = new Bitmap(RenderSize, RenderSize);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            using var font = new Font(
                FontFamilyName, 190f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            graphics.DrawString(target, font, Brushes.Black,
                new RectangleF(0, 0, RenderSize, RenderSize), format);

            Rectangle bounds = FindInkBounds(image);
            if (bounds.Width == 0 || bounds.Height == 0) return null;
            return Downsample(image, bounds, outputRows, outputCols);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Glyph reference] unavailable: {ex.Message}");
            return null;
        }
    }

    private static string? ExtractTarget(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string text = command.Trim();
        foreach (char marker in new[] { '排', '拼', '擺' })
        {
            int index = text.LastIndexOf(marker);
            if (index >= 0 && index + 1 < text.Length)
            {
                string tail = text[(index + 1)..].Trim(' ', '出', '成', '為', '「', '」', '『', '』', '"');
                if (tail.Length > 0)
                    return FirstTextElement(tail);
            }
        }
        return FirstTextElement(text);
    }

    private static string? FirstTextElement(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        return enumerator.MoveNext() ? enumerator.GetTextElement() : null;
    }

    private static Rectangle FindInkBounds(Bitmap image)
    {
        int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            if (image.GetPixel(x, y).GetBrightness() > 0.92f) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        return maxX < minX ? Rectangle.Empty
            : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static List<string> Downsample(
        Bitmap image, Rectangle bounds, int rows, int cols)
    {
        // Add proportional whitespace so endpoints and relative stroke lengths
        // remain visible to the judges instead of being cropped edge-to-edge.
        int padX = Math.Max(2, bounds.Width / 10);
        int padY = Math.Max(2, bounds.Height / 10);
        int left = Math.Max(0, bounds.Left - padX);
        int top = Math.Max(0, bounds.Top - padY);
        int right = Math.Min(image.Width, bounds.Right + padX);
        int bottom = Math.Min(image.Height, bounds.Bottom + padY);
        var area = Rectangle.FromLTRB(left, top, right, bottom);

        var result = new List<string>(rows);
        for (int r = 0; r < rows; r++)
        {
            var line = new StringBuilder(cols);
            int y0 = area.Top + r * area.Height / rows;
            int y1 = area.Top + (r + 1) * area.Height / rows;
            for (int c = 0; c < cols; c++)
            {
                int x0 = area.Left + c * area.Width / cols;
                int x1 = area.Left + (c + 1) * area.Width / cols;
                int ink = 0, total = 0;
                for (int y = y0; y < Math.Max(y0 + 1, y1); y++)
                for (int x = x0; x < Math.Max(x0 + 1, x1); x++)
                {
                    total++;
                    if (image.GetPixel(x, y).GetBrightness() < 0.80f) ink++;
                }
                line.Append(total > 0 && (double)ink / total >= 0.12 ? '1' : '0');
            }
            result.Add(line.ToString());
        }
        return result;
    }
}
