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
    // Back to Regular ("GenSenRounded2 TW R", confirmed against this
    // machine's actual InstalledFontCollection). Light ("GenSenRounded2 TW
    // L") was tried as a way to get thinner strokes from the font itself,
    // and erosion (integer- then fractional-radius) was tried as a
    // post-process; stroke thickness is now handled by skeletonizing the
    // rendered glyph down to a 1px-wide centerline (see Skeletonize below)
    // instead of tuning either of those — Regular is the weight that's
    // actually confirmed installed under this exact name.
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
    // second line of defense — either mismatch is treated as "unavailable"
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
            Console.WriteLine(
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
            // Second line of defense: even with the family confirmed
            // installed, confirm GDI+ actually resolved to it (not a
            // near-match or locale-specific substitution) before trusting
            // the render.
            if (!string.Equals(font.Name, FontFamilyName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
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
            bool[,] skeleton = Skeletonize(BuildInkMask(image));
            return Downsample(skeleton, bounds, outputRows, outputCols);
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

    // A pixel counts as "ink" if its brightness is below this. Separate from
    // FindInkBounds's own, more permissive 0.92f threshold, which is
    // deliberately generous so faint anti-aliased edges still count toward
    // the crop/pad region.
    private const double InkBrightnessThreshold = 0.80;

    private static bool[,] BuildInkMask(Bitmap image)
    {
        var mask = new bool[image.Width, image.Height];
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            mask[x, y] = image.GetPixel(x, y).GetBrightness() < InkBrightnessThreshold;
        return mask;
    }

    /// <summary>
    /// Zhang-Suen thinning: iteratively removes boundary ink pixels that can
    /// be deleted without breaking connectivity or erasing a line entirely,
    /// alternating two sub-iterations until nothing changes. Unlike a
    /// fixed-radius erosion, there is no thickness parameter to tune —
    /// thick and thin source strokes both converge to a skeleton that's
    /// (with rare, unavoidable exceptions at junctions) exactly 1 pixel
    /// wide, so downstream cell-occupancy just needs "does the skeleton
    /// pass through this cell at all", not a coverage fraction.
    /// </summary>
    private static bool[,] Skeletonize(bool[,] mask)
    {
        int width = mask.GetLength(0), height = mask.GetLength(1);
        var img = new int[width, height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            img[x, y] = mask[x, y] ? 1 : 0;

        // Real convergence is normally well under this; it's only a safety
        // net against an unexpected non-terminating edge case.
        const int maxIterations = 200;
        bool changed;
        int guard = 0;
        do
        {
            changed = ThinPass(img, width, height, subIteration: 1);
            changed |= ThinPass(img, width, height, subIteration: 2);
            guard++;
        } while (changed && guard < maxIterations);

        var result = new bool[width, height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            result[x, y] = img[x, y] == 1;
        return result;
    }

    /// <summary>
    /// One Zhang-Suen sub-iteration. Marks then deletes (after a full scan,
    /// not in place — deleting during the scan would let a pixel's own
    /// deletion affect the neighbor count of pixels visited later in the
    /// same pass) every ink pixel P1 whose 8-neighborhood (P2 at 12 o'clock,
    /// continuing clockwise to P9) satisfies all four Zhang-Suen conditions
    /// for the given sub-iteration.
    /// </summary>
    private static bool ThinPass(int[,] img, int width, int height, int subIteration)
    {
        var toDelete = new List<(int X, int Y)>();
        for (int y = 1; y < height - 1; y++)
        for (int x = 1; x < width - 1; x++)
        {
            if (img[x, y] != 1) continue;

            int p2 = img[x, y - 1];
            int p3 = img[x + 1, y - 1];
            int p4 = img[x + 1, y];
            int p5 = img[x + 1, y + 1];
            int p6 = img[x, y + 1];
            int p7 = img[x - 1, y + 1];
            int p8 = img[x - 1, y];
            int p9 = img[x - 1, y - 1];

            // Condition 1: 2 <= B(P1) <= 6 — not an isolated point or the
            // interior of a >=2px-thick fill.
            int b = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
            if (b < 2 || b > 6) continue;

            // Condition 2: A(P1) == 1 — exactly one 0->1 transition walking
            // the ring P2..P9..P2, i.e. P1 sits on a single simple boundary,
            // not a junction that would disconnect the skeleton if removed.
            Span<int> ring = stackalloc int[] { p2, p3, p4, p5, p6, p7, p8, p9, p2 };
            int a = 0;
            for (int i = 0; i < 8; i++)
                if (ring[i] == 0 && ring[i + 1] == 1) a++;
            if (a != 1) continue;

            // Conditions 3 & 4 differ between the two sub-iterations so that
            // alternating passes peel from all four sides evenly instead of
            // eating strokes asymmetrically from just north/west.
            if (subIteration == 1)
            {
                if (p2 * p4 * p6 != 0) continue;
                if (p4 * p6 * p8 != 0) continue;
            }
            else
            {
                if (p2 * p4 * p8 != 0) continue;
                if (p2 * p6 * p8 != 0) continue;
            }

            toDelete.Add((x, y));
        }

        foreach (var (x, y) in toDelete) img[x, y] = 0;
        return toDelete.Count > 0;
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
        bool[,] skeleton, Rectangle bounds, int rows, int cols)
    {
        int width = skeleton.GetLength(0), height = skeleton.GetLength(1);

        // Add proportional whitespace so endpoints and relative stroke lengths
        // remain visible to the judges instead of being cropped edge-to-edge.
        int padX = Math.Max(2, bounds.Width / 10);
        int padY = Math.Max(2, bounds.Height / 10);
        int left = Math.Max(0, bounds.Left - padX);
        int top = Math.Max(0, bounds.Top - padY);
        int right = Math.Min(width, bounds.Right + padX);
        int bottom = Math.Min(height, bounds.Bottom + padY);
        var area = Rectangle.FromLTRB(left, top, right, bottom);

        // The skeleton is (barring rare junction pixels) exactly 1px wide, so
        // a 2D area-coverage fraction like the old CellInkCoverageThreshold
        // would almost always read near-zero. But "any single skeleton pixel
        // in the cell" is the wrong replacement: at low output resolution
        // (e.g. 5x5) a real stroke that runs the length of a row or column
        // hits nearly every cell along it, and even a stroke that only
        // grazes a cell's corner counts the same as one that runs straight
        // through it — most non-trivial glyphs collapse into a solid block
        // regardless of their actual shape.
        //
        // Instead, occupancy is judged against how far the skeleton actually
        // travels through the cell: a straight 1px line fully transiting a
        // cell contributes roughly max(cellWidth, cellHeight) ink pixels, so
        // requiring ink >= CellTransitFraction * max(cellWidth, cellHeight)
        // accepts a real through-stroke while rejecting a corner graze or a
        // stray pixel from a nearby junction.
        const double CellTransitFraction = 0.35;

        var result = new List<string>(rows);
        for (int r = 0; r < rows; r++)
        {
            var line = new StringBuilder(cols);
            int y0 = area.Top + r * area.Height / rows;
            int y1 = area.Top + (r + 1) * area.Height / rows;
            int cellHeight = Math.Max(1, y1 - y0);
            for (int c = 0; c < cols; c++)
            {
                int x0 = area.Left + c * area.Width / cols;
                int x1 = area.Left + (c + 1) * area.Width / cols;
                int cellWidth = Math.Max(1, x1 - x0);
                int threshold = Math.Max(1, (int)Math.Round(
                    CellTransitFraction * Math.Max(cellWidth, cellHeight)));

                int ink = 0;
                for (int y = y0; y < Math.Max(y0 + 1, y1); y++)
                for (int x = x0; x < Math.Max(x0 + 1, x1); x++)
                    if (skeleton[x, y]) ink++;
                line.Append(ink >= threshold ? '1' : '0');
            }
            result.Add(line.ToString());
        }
        return result;
    }
}
