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
    // Source Han Sans-derived rounded font (源泉圓體 / GenSenRounded), SIL OFL
    // 1.1 licensed: https://github.com/ButTaiwan/gensen-font
    //
    // Name is "GenSenRounded2 TW R", WITH the weight suffix — confirmed against
    // this machine's actual InstalledFontCollection dump, not against
    // `fc-scan --format '%{family}'` on Linux (which reported "GenSenRounded2 TW",
    // no suffix, and was wrong for this purpose). This font ships each weight
    // (EL/L/R/M/B/H) without a shared typographic-family (nameID 16) grouping,
    // so Windows GDI+ registers each weight as its own standalone family
    // including the weight letter, rather than as a style variant within one
    // family. If a different weight is ever installed instead of Regular,
    // this string needs the matching suffix (e.g. "GenSenRounded2 TW B" for
    // Bold) — check the real InstalledFontCollection list, don't assume.
    private const string FontFamilyName = "GenSenRounded2 TW R";

    // GDI+ font family resolution does NOT throw when FontFamilyName isn't
    // installed: `new Font(name, ...)` silently substitutes a fallback font
    // (Microsoft's own documented behavior) and returns successfully as if
    // nothing were wrong. A previous version of this file assumed a missing
    // font would throw and be caught below — it does not. Left unguarded,
    // that means a wrong/serif fallback font gets rendered, downsampled, and
    // handed to the ballot judges as if it were the real reference glyph,
    // with no error anywhere in the log. So the family is checked against
    // System.Drawing.Text.InstalledFontCollection BEFORE constructing the
    // Font, and the constructed Font's resolved Name is checked AFTER, as a
    // second  of defense — either mismatch is treated as "unavailable"
    // (return null), never as "close enough, render with substitute font".
    private static bool IsFamilyInstalled(string familyName, out string[] installedNames)
    {
        using var installed = new InstalledFontCollection();
        installedNames = installed.Families.Select(f => f.Name).ToArray();
        foreach (var name in installedNames)
            if (string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static List<string>? TryRenderFromCommand(
        string command, int outputRows = 15, int outputCols = 15)
    {
        string? target = ExtractTarget(command);
        if (string.IsNullOrWhiteSpace(target)) return null;

        if (!IsFamilyInstalled(FontFamilyName, out string[] installedNames))
        {
            // Print what InstalledFontCollection actually enumerated instead of
            // just asserting "not installed" — after two rounds of guessing at
            // install-scope causes (per-user vs all-users) that didn't fix it,
            // the only way to stop guessing is to see GDI+'s real list. Filter
            // to plausible near-matches first (cheap to read); if none, dump
            // everything so a naming mismatch is visible directly.
            var nearMatches = installedNames
                .Where(n => n.Contains("Gen", StringComparison.OrdinalIgnoreCase)
                         || n.Contains("Round", StringComparison.OrdinalIgnoreCase)
                         || n.Contains("源泉", StringComparison.Ordinal))
                .ToArray();
            Console.Write(
                $"[Glyph reference] unavailable: font family \"{FontFamilyName}\" is not " +
                $"among the {installedNames.Length} families InstalledFontCollection sees. " +
                (nearMatches.Length > 0
                    ? $"Near-matches found instead: [{string.Join(", ", nearMatches.Select(n => $"\"{n}\""))}] " +
                      "— compare these character-by-character against the expected name (stray " +
                      "space, different digit/spacing, CJK vs ASCII variant) rather than reinstalling again."
                    : "No near-match (nothing containing \"Gen\", \"Round\", or \"源泉\") — " +
                      "InstalledFontCollection is not seeing this font at all, regardless of name. " +
                      $"Full list: [{string.Join(", ", installedNames)}]"));
            return null;
        }

        try
        {
            using var image = new Bitmap(RenderSize, RenderSize);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            using var font = new Font(
                FontFamilyName, 190f, FontStyle.Regular, GraphicsUnit.Pixel);
            // Second  of defense: even with the family confirmed
            // installed, confirm GDI+ actually resolved to it (not a
            // near-match or locale-specific substitution) before trusting
            // the render.
            if (!string.Equals(font.Name, FontFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(
                    $"[Glyph reference] unavailable: requested \"{FontFamilyName}\" but GDI+ " +
                    $"resolved to \"{font.Name}\" instead — treating as a silent substitution, " +
                    "not rendering with the wrong font.");
                return null;
            }
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
                line.Append(total > 0 && (double)ink / total >= 0.35 ? '1' : '0');
            }
            result.Add(line.ToString());
        }
        return result;
    }
}
