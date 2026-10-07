using System.Globalization;
using System.Text.RegularExpressions;

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
        + string.Join("\n", Rows)
        + "\n為讓 Unity 在你選的位置比較原始 bitmap，請在計畫中另列一行 bitmap_grid(left_x, top_y, cell_x, cell_y, top_z)，五個參數皆填你決定的實際公尺數值。left_x/top_y 是 bitmap 第 0 列第 0 欄的格中心（即使該格是空白），cell_x/cell_y 是你選的格距，top_z 是放置後頂面高度。第 r 列第 c 欄的中心為 (left_x+c*cell_x, top_y-r*cell_y, top_z)，請自行讓每個放置目標對應此 bitmap 的佔用格，不另設計另一張圖。這行只描述你的擺放座標系，不是手臂動作。仍依執行介面逐一列出 source index 與 target 座標，以及完整函式呼叫。";

    // Parse the LLM's chosen coordinate frame; never infer the target bitmap from its placements.
    public (List<string> Rows, List<ExpectedCell> Cells, double CellXM, double CellYM) ForUnity(string plan)
    {
        const string number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
        var match = Regex.Match(plan, $@"bitmap_grid\s*\(\s*({number})\s*,\s*({number})\s*,\s*({number})\s*,\s*({number})\s*,\s*({number})\s*\)", RegexOptions.IgnoreCase);
        if (!match.Success) throw new TranslationContractException("轉譯失敗：未提供 bitmap_grid 的實際擺放座標系。", new FormatException("Missing bitmap_grid."));
        var values = Enumerable.Range(1, 5).Select(i => double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Any(v => !double.IsFinite(v)) || values[2] <= 0 || values[3] <= 0)
            throw new TranslationContractException("轉譯失敗：bitmap_grid 格距須為正數且參數須為有限數值。", new FormatException("Invalid bitmap_grid."));
        var cells = new List<ExpectedCell>();
        for (int r = 0; r < Rows.Count; r++)
            for (int c = 0; c < Rows[r].Length; c++)
                if (Rows[r][c] == '■') cells.Add(new() {
                    Row = r, Col = c, X = values[0] + c * values[2], Y = values[1] - r * values[3], Z = values[4]
                });
        return (Rows, cells, values[2], values[3]);
    }
}
