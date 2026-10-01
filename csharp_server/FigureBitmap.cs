using System.Text.RegularExpressions;

/// <summary>
/// 把這一輪計畫放下的物件畫成 bitmap（■ 有物件、□ 空），印在 terminal，也送給 Unity 當模擬結束比對的預期格
/// （BatchEnvelope.Bitmap / ExpectedCells）。方向跟相機畫面一致：上方是 +Y（手臂基座那側）、往右是 +X；
/// X、Y 格距分開取（手指沿 X 開合，X 方向通常要排得比 Y 疏）。這只是把 LLM 決定的座標畫出來，
/// LLM 照樣只輸出自然語言座標，不填 bitmap。排字母時每個字母最多 5×5 格（LetterSizeProblem）。
/// </summary>
public static class FigureBitmap
{
    public const double DefaultCellM = 0.03;
    public const int MaxLetterCells = 5;
    const double MinCellM = LayeredGraspGeometry.BlockLayerM;
    const double MaxCellM = 0.08;
    const double DominoLengthM = 2 * LayeredGraspGeometry.BlockLayerM;
    // 中心在另一軸相差這麼多以內算同一列（行）
    const double SameLineM = 0.01;

    public static (List<string> Rows, List<ExpectedCell> Cells, double CellXM, double CellYM) Build(IReadOnlyList<SceneObject> placed)
    {
        var (cellX, cellY) = CellSize(placed);
        var centers = placed.Select(o => CellCenters(o, cellX, cellY)).ToList();
        double left = centers.SelectMany(c => c).Min(p => p.X);
        double top = centers.SelectMany(c => c).Max(p => p.Y);
        (int Row, int Col) Grid((double X, double Y) p) =>
            ((int)Math.Round((top - p.Y) / cellY), (int)Math.Round((p.X - left) / cellX));

        var cells = new List<ExpectedCell>();
        for (int i = 0; i < placed.Count; i++)
        {
            var first = Grid(centers[i][0]);
            var second = centers[i].Count > 1 ? Grid(centers[i][1]) : (Row: -1, Col: -1);
            cells.Add(new ExpectedCell
            {
                Row = first.Row, Col = first.Col, SecondRow = second.Row, SecondCol = second.Col,
                X = placed[i].X, Y = placed[i].Y, Z = placed[i].Z,
                Shape = placed[i].Shape, Orientation = placed[i].Orientation,
            });
        }
        int rows = cells.Max(c => Math.Max(c.Row, c.SecondRow)) + 1;
        int cols = cells.Max(c => Math.Max(c.Col, c.SecondCol)) + 1;
        var grid = Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat('□', cols).ToArray()).ToArray();
        foreach (var c in cells)
        {
            grid[c.Row][c.Col] = '■';
            if (c.SecondRow >= 0) grid[c.SecondRow][c.SecondCol] = '■';
        }
        return (grid.Select(r => new string(r)).ToList(), cells, cellX, cellY);
    }

    /// <summary>
    /// 3D 疊放逐層畫，由下往上：每一層都用整個圖形的同一組格子（格距、原點相同），上下層才對得齊。
    /// layerOf[i] 是 placed[i] 在第幾層（1 = 放在桌面上）。
    /// </summary>
    public static List<(int Layer, List<string> Rows)> Layers(IReadOnlyList<SceneObject> placed, IReadOnlyList<int> layerOf)
    {
        var (rows, cells, _, _) = Build(placed);
        var result = new List<(int, List<string>)>();
        foreach (int layer in layerOf.Distinct().OrderBy(l => l))
        {
            var grid = rows.Select(r => Enumerable.Repeat('□', r.Length).ToArray()).ToArray();
            for (int i = 0; i < cells.Count; i++)
            {
                if (layerOf[i] != layer) continue;
                grid[cells[i].Row][cells[i].Col] = '■';
                if (cells[i].SecondRow >= 0) grid[cells[i].SecondRow][cells[i].SecondCol] = '■';
            }
            result.Add((layer, grid.Select(r => new string(r)).ToList()));
        }
        return result;
    }

    public static void Print(string title, IEnumerable<string> rows)
    {
        Console.WriteLine(title);
        foreach (var row in rows) Console.WriteLine("           " + row);
    }

    /// <summary>
    /// 目標要排幾個字母：獨立的大寫英文字母（前後不接英數字，例如「排一個L」、「排出CAT」；QR1、3D 不算），
    /// 目標寫了「字母」卻看不出是哪個時算 1 個。0 = 不是排字母的任務。
    /// </summary>
    public static int LetterCount(string goal)
    {
        int letters = Regex.Matches(goal, @"(?<![A-Za-z0-9])[A-Z]+(?![A-Za-z0-9])").Sum(m => m.Length);
        return letters > 0 ? letters : goal.Contains("字母") ? 1 : 0;
    }

    /// <summary>
    /// 排字母時每個字母最多 5×5 格：高最多 5 格；寬最多 5 格（幾個字母並排時每個字母 5 格，字母之間最多空 2 格）。
    /// 回傳違反說明，null = 沒有超過。
    /// </summary>
    public static string? LetterSizeProblem(int letters, IReadOnlyList<string> rows)
    {
        if (letters <= 0 || rows.Count == 0) return null;
        int height = rows.Count, width = rows[0].Length;
        int maxWidth = MaxLetterCells * letters + 2 * (letters - 1);
        if (height <= MaxLetterCells && width <= maxWidth) return null;
        return $"排字母時每個字母最多 {MaxLetterCells}×{MaxLetterCells} 格，這一輪計畫的圖形是高 {height} 格 × 寬 {width} 格" +
               (letters > 1 ? $"（{letters} 個字母最多高 {MaxLetterCells} 格 × 寬 {maxWidth} 格）" : "");
    }

    // X、Y 各自估格距；有 cube 時只用 cube 量（佔兩格的 domino 中心落在兩格中間），
    // 只有一個方向量得到時兩個方向用同一個，都量不到就用預設格距
    static (double X, double Y) CellSize(IReadOnlyList<SceneObject> placed)
    {
        var cubes = placed.Where(o => o.Shape != "domino").ToList();
        double? x = Pitch(cubes, o => o.X) ?? Pitch(placed, o => o.X);
        double? y = Pitch(cubes, o => o.Y) ?? Pitch(placed, o => o.Y);
        return (Math.Clamp(x ?? y ?? DefaultCellM, MinCellM, MaxCellM), Math.Clamp(y ?? x ?? DefaultCellM, MinCellM, MaxCellM));
    }

    // 採用一個格距時，落在格點上（離格點不到 OnGridTolerance 格）的物件至少要佔這個比例
    // （其餘可能是移開讓位、不屬於圖形的物件）
    const double OnGridShare = 0.75;
    const double OnGridTolerance = 0.1;

    // 一個軸的格距：中心相差 SameLineM 以內算同一列（行），相鄰兩列的距離（不超過 MaxCellM 的）都是候選格距，
    // 由大到小取第一個讓 OnGridShare 以上的物件落在格點上的；都不到就取落在格點上最多的。看的是所有列之間的距離、
    // 不是同一列裡的鄰居：Z、N、X 的斜筆畫每列只有一塊，同一行裡的鄰居隔好幾格，拿來當格距會把兩列併成一列。
    // 每兩列都隔超過 MaxCellM 時改用最小距離的等分（中間留空列），格子才會等距。
    static double? Pitch(IReadOnlyList<SceneObject> objects, Func<SceneObject, double> axis)
    {
        var lines = new List<List<double>>();
        foreach (double v in objects.Select(axis).OrderBy(v => v))
        {
            if (lines.Count == 0 || v - lines[^1][^1] > SameLineM) lines.Add(new List<double>());
            lines[^1].Add(v);
        }
        if (lines.Count < 2) return null;
        var centres = lines.Select(l => l.Average()).ToList();
        var gaps = centres.Zip(centres.Skip(1), (a, b) => b - a).ToList();
        var candidates = gaps.Where(g => g <= MaxCellM + 1e-9).Select(g => Math.Max(g, MinCellM)).ToList();
        if (candidates.Count == 0) candidates.Add(gaps.Min() / Math.Ceiling(gaps.Min() / MaxCellM - 1e-9));
        candidates = candidates.Distinct().OrderByDescending(p => p).ToList();
        // 以其中一列為原點、落在格點上的物件數，取最好的原點
        int OnGrid(double pitch) => centres.Max(origin => lines.Where((_, i) =>
        {
            double k = (centres[i] - origin) / pitch;
            return Math.Abs(k - Math.Round(k)) <= OnGridTolerance;
        }).Sum(line => line.Count));
        foreach (double pitch in candidates)
            if (OnGrid(pitch) >= OnGridShare * objects.Count) return pitch;
        return candidates.OrderByDescending(OnGrid).ThenByDescending(p => p).First();
    }

    // cube 佔一格；domino 格距放得下兩格時沿長軸佔兩格。第一格是左邊（較小 x）或上面（較大 y）那格，
    // 跟 Unity JsonExecutor.RunBitmapCheck 由預期格推回格子座標的算法一致。
    static List<(double X, double Y)> CellCenters(SceneObject o, double cellX, double cellY)
    {
        if (o.Shape != "domino") return new() { (o.X, o.Y) };
        if (o.Orientation == "vertical")
            return DominoLengthM / cellY < 1.5 ? new() { (o.X, o.Y) } : new() { (o.X, o.Y + cellY / 2), (o.X, o.Y - cellY / 2) };
        return DominoLengthM / cellX < 1.5 ? new() { (o.X, o.Y) } : new() { (o.X - cellX / 2, o.Y), (o.X + cellX / 2, o.Y) };
    }
}
