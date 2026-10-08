using OpenCvSharp;

/// <summary>
/// Unity 畫面比對圖（三格：Unity 俯視畫面 / 預期 / 疊合）加上標題：畫面重疊率、通過與否、每格的說明與顏色圖例；
/// 3D 疊放在預期那格標出每一處疊到第幾層。OpenCV 的字型只有英文，所以圖上的字用英文。
/// 顏色跟 Unity JsonExecutor.FigureImageOverlap 相同。
/// </summary>
public static class SimCheckImage
{
    const int HeaderPx = 104;
    const int MarginPx = 8;
    // BGR；跟 Unity 的 RGB 對應：重疊 (60,170,80)、少了 (220,60,50)、多出來 (50,110,230)、高度不對 (255,150,0)
    static readonly (string Label, Scalar Colour)[] Legend =
    {
        ("match", new Scalar(80, 170, 60)),
        ("missing", new Scalar(50, 60, 220)),
        ("extra", new Scalar(230, 110, 50)),
        ("wrong height", new Scalar(0, 150, 255)),
    };

    public static void Annotate(string source, string destination, SimulationCheckReport report, IReadOnlyList<ExpectedCell>? cells)
    {
        using var panels = Cv2.ImRead(source, ImreadModes.Color);
        if (panels.Empty()) throw new InvalidOperationException("讀不到比對圖 " + source);
        int TextWidth(string text, double scale, int thickness) =>
            Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, scale, thickness, out _).Width;

        // 第一行：重疊率與結果；第二行：顏色圖例；第三行：每一格的標題。字比圖寬時把底圖往右補白
        string verdict = $"Overlap {report.OverlapRatio * 100:F1}% (needs > {report.OverlapThreshold * 100:F0}%)  " +
                         (report.Passed ? "PASS" : "FAIL");
        int legendWidth = Legend.Sum(l => 18 + TextWidth(l.Label, 0.5, 1) + 16);
        int width = Math.Max(panels.Cols, Math.Max(TextWidth(verdict, 0.75, 2), legendWidth) + 2 * MarginPx);
        using var canvas = new Mat(panels.Rows + HeaderPx, width, MatType.CV_8UC3, Scalar.White);
        panels.CopyTo(canvas[new Rect(0, HeaderPx, panels.Cols, panels.Rows)]);

        var verdictColour = report.Passed ? new Scalar(60, 150, 40) : new Scalar(40, 40, 210);
        Cv2.PutText(canvas, verdict, new Point(MarginPx, 28), HersheyFonts.HersheySimplex, 0.75, verdictColour, 2, LineTypes.AntiAlias);
        int x = MarginPx;
        foreach (var (label, colour) in Legend)
        {
            Cv2.Rectangle(canvas, new Rect(x, 43, 14, 14), colour, -1);
            Cv2.PutText(canvas, label, new Point(x + 18, 56), HersheyFonts.HersheySimplex, 0.5, Scalar.Black, 1, LineTypes.AntiAlias);
            x += 18 + TextWidth(label, 0.5, 1) + 16;
        }

        int panel = report.ImagePanelWidth, gap = report.ImageGap;
        if (panel > 0)
        {
            string[] titles = { "Unity top view", "Expected (layers)", "Overlay" };
            for (int i = 0; i < titles.Length; i++)
                Cv2.PutText(canvas, titles[i], new Point(i * (panel + gap) + 4, HeaderPx - 10),
                    HersheyFonts.HersheySimplex, 0.5, Scalar.Black, 1, LineTypes.AntiAlias);
        }

        // 3D：預期那格在每一處標出疊到第幾層（同一處取最上面那塊）
        if (cells != null && panel > 0 && report.ImagePxPerM > 0)
        {
            var tops = cells.GroupBy(c => (Math.Round(c.X, 3), Math.Round(c.Y, 3)))
                .Select(g => (X: g.Key.Item1, Y: g.Key.Item2, Layer: g.Max(c => Math.Max(1, (int)Math.Round(c.Z / LayeredGraspGeometry.BlockLayerM)))))
                .ToList();
            if (tops.Any(t => t.Layer > 1))
                foreach (var (cx, cy, layer) in tops)
                {
                    string text = layer.ToString();
                    var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, 0.6, 2, out _);
                    int px = panel + gap + (int)Math.Round((cx - report.ImageX0M) * report.ImagePxPerM) - size.Width / 2;
                    int py = HeaderPx + (int)Math.Round((report.ImageY1M - cy) * report.ImagePxPerM) + size.Height / 2;
                    Cv2.PutText(canvas, text, new Point(px, py), HersheyFonts.HersheySimplex, 0.6, Scalar.Black, 2, LineTypes.AntiAlias);
                }
        }
        if (!Cv2.ImWrite(destination, canvas)) throw new IOException("寫不出比對圖 " + destination);
    }
}
