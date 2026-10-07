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
    /// 3D 疊放的 bitmap：俯視的高度圖，每格是那格最上面的積木在第幾層（1 = 放在桌面上，0 = 空），
    /// 例如站起來的 L 是一列 "311"（左邊疊 3 層、右邊兩格各 1 層）。layerOf[i] 是 placed[i] 的層數；
    /// 格子跟 Build 相同，cells 一併回傳（Unity 比對的預期格，Z 是各自的頂面）。
    /// </summary>
    public static (List<string> Rows, List<ExpectedCell> Cells, double CellXM, double CellYM) HeightMap(
        IReadOnlyList<SceneObject> placed, IReadOnlyList<int> layerOf)
    {
        var (rows, cells, cellX, cellY) = Build(placed);
        var grid = rows.Select(r => new int[r.Length]).ToArray();
        for (int i = 0; i < cells.Count; i++)
        {
            grid[cells[i].Row][cells[i].Col] = Math.Max(grid[cells[i].Row][cells[i].Col], layerOf[i]);
            if (cells[i].SecondRow >= 0)
                grid[cells[i].SecondRow][cells[i].SecondCol] = Math.Max(grid[cells[i].SecondRow][cells[i].SecondCol], layerOf[i]);
        }
        return (grid.Select(r => string.Concat(r.Select(l => (char)('0' + Math.Min(l, 9))))).ToList(), cells, cellX, cellY);
    }

    /// <summary>印高度圖：數字之間空一格，0 印成 ·（例如 "3 1 1"）。</summary>
    public static void PrintHeightMap(string title, IEnumerable<string> rows)
    {
        Console.WriteLine(title);
        foreach (var row in rows) Console.WriteLine("           " + string.Join(" ", row.Select(c => c == '0' ? '·' : c)));
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

    // 同一列相鄰物件的 X 間距取中位數當 X 格距，同一行相鄰物件的 Y 間距當 Y 格距；
    // 只有一個方向量得到時兩個方向用同一個，都量不到就用預設格距
    static (double X, double Y) CellSize(IReadOnlyList<SceneObject> placed)
    {
        double? x = MedianNeighbourGap(placed, o => o.X, o => o.Y);
        double? y = MedianNeighbourGap(placed, o => o.Y, o => o.X);
        return (Math.Clamp(x ?? y ?? DefaultCellM, MinCellM, MaxCellM), Math.Clamp(y ?? x ?? DefaultCellM, MinCellM, MaxCellM));
    }

    // along：量間距的軸；across：判斷是不是同一列（行）的軸
    static double? MedianNeighbourGap(IReadOnlyList<SceneObject> placed, Func<SceneObject, double> along, Func<SceneObject, double> across)
    {
        var gaps = new List<double>();
        foreach (var o in placed)
        {
            var neighbours = placed.Where(p => !ReferenceEquals(p, o) && Math.Abs(across(p) - across(o)) < SameLineM)
                .Select(p => Math.Abs(along(p) - along(o))).Where(d => d > SameLineM).ToList();
            if (neighbours.Count > 0) gaps.Add(neighbours.Min());
        }
        if (gaps.Count == 0) return null;
        gaps.Sort();
        return gaps[gaps.Count / 2];
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
