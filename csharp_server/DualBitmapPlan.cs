// ZIP generation/review/vote; placement is chosen by the motion-planning LLM.
public sealed class DualBitmapPlan
{
    public required List<string> Rows { get; init; }

    public static async Task<DualBitmapPlan> Design(string goal, List<SceneObject> scene)
    {
        int cubes = scene.Count(s => s.Shape == "cube");
        int dominoes = scene.Count(s => s.Shape == "domino");
        var pattern = await new PatternDesigner(5, 5).DesignAsync(goal, "依原始使用者指令自行選擇", cubes, dominoes);
        var bitmap = pattern.Bitmap!;
        return new() { Rows = Enumerable.Range(0, bitmap.GetLength(0)).Select(r =>
            new string(Enumerable.Range(0, bitmap.GetLength(1)).Select(c => bitmap[r, c] == 1 ? '■' : '□').ToArray())).ToList() };
    }

    public string PlanningConstraint => "以下是雙 LLM 選出的目標 bitmap。請根據目前場景與影像，自行找空白的地方擺放，決定來源積木、擺放位置、X/Y 格距、放置高度及動作順序。沒有預先指定的供料區、擺放區、原點或固定目標座標；顏色與形狀依使用者原始要求及實際積木自行選擇。圖形上方為 +Y、右方為 +X。\n"
        + string.Join("\n", Rows);

    // Uses the LLM's own chosen coordinate frame (bitmap_grid in its structured output);
    // never infer the target bitmap from its placements.
    public (List<string> Rows, List<ExpectedCell> Cells, double CellXM, double CellYM) ForUnity(BitmapGridChoice grid)
    {
        if (!double.IsFinite(grid.LeftX) || !double.IsFinite(grid.TopY) || !double.IsFinite(grid.CellX) ||
            !double.IsFinite(grid.CellY) || !double.IsFinite(grid.TopZ) || grid.CellX <= 0 || grid.CellY <= 0)
            throw new TranslationContractException("轉譯失敗：bitmap_grid 格距須為正數且參數須為有限數值。", new FormatException("Invalid bitmap_grid."));
        var cells = new List<ExpectedCell>();
        for (int r = 0; r < Rows.Count; r++)
            for (int c = 0; c < Rows[r].Length; c++)
                if (Rows[r][c] == '■') cells.Add(new() {
                    Row = r, Col = c, X = grid.LeftX + c * grid.CellX, Y = grid.TopY - r * grid.CellY, Z = grid.TopZ
                });
        return (Rows, cells, grid.CellX, grid.CellY);
    }
}
