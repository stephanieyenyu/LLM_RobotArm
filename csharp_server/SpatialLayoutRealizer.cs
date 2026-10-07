using System.Collections.Generic;
using System.Linq;

// -----------------------------------------------------------------
// Layer 2（立體）：Spatial Layout Realizer
// 純數學：canonical 高度圖（SpatialPattern.ColumnHeights）→ 每一格、每一層的 TargetCell。
// 不呼叫 LLM，deterministic，規則跟 2D 的 LayoutRealizer 相同：
//   - 位置寫死：第 r 列第 c 欄的中心 X = TargetOriginX + c × SpatialCellSize、
//     Y = SpatialTargetOriginY + (rows − 1 − r) × SpatialCellSize（row 反向，正面讀起來不上下顛倒）
//   - 第 k 層放好後的頂面 = k × DefaultBlockZ；立體只用 cube
//   - 超出畫布（列、欄、層）、庫存不足或超出 UR 目標半徑就回報錯誤，不自動平移、不送出實體手臂
// -----------------------------------------------------------------
public static class SpatialLayoutRealizer
{
    public static LayoutRealizer.RealizeResult Realize(SpatialPattern pattern, WorkspaceBounds ws, int cubeBudget)
    {
        if (pattern.ColumnHeights == null)
            return new LayoutRealizer.RealizeResult { Error = "立體 pattern 的高度圖為空。" };

        int[,] heights = pattern.ColumnHeights;
        int rows = heights.GetLength(0), cols = heights.GetLength(1);
        if (rows > ws.SpatialRows || cols > ws.SpatialCols)
            return new LayoutRealizer.RealizeResult
            {
                Error = $"立體 pattern 尺寸 {rows}×{cols} 超出擺放區 {ws.SpatialRows}×{ws.SpatialCols}",
            };

        var targets = new List<TargetCell>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int height = heights[r, c];
                if (height < 0 || height > ws.SpatialLayers)
                    return new LayoutRealizer.RealizeResult
                    {
                        Error = $"r{r}c{c} 要疊 {height} 層，超出 0..{ws.SpatialLayers} 層",
                    };
                for (int layer = 1; layer <= height; layer++)
                    targets.Add(new TargetCell
                    {
                        Row = r,
                        Col = c,
                        Layer = layer,
                        WorldX = ws.TargetOriginX + c * ws.SpatialCellSize,
                        WorldY = ws.SpatialTargetOriginY + (rows - 1 - r) * ws.SpatialCellSize,
                        WorldZ = layer * ws.DefaultBlockZ,
                        ExpectedShape = "cube",
                        ExpectedColor = pattern.BlockColor,
                        ExpectedOrientation = null,
                    });
            }

        if (targets.Count == 0)
            return new LayoutRealizer.RealizeResult { Error = "立體 pattern 沒有任何要放置積木的格子。" };
        if (targets.Count > cubeBudget)
            return new LayoutRealizer.RealizeResult
            {
                Error = $"cube 不足：需要 {targets.Count} 顆，但目前只有 {cubeBudget} 顆。",
            };
        if (!LayoutRealizer.AreTargetsWithinSafeReach(targets.Where(t => t.Layer == 1).ToList(), ws))
            return new LayoutRealizer.RealizeResult
            {
                Error = $"固定原點的立體圖案超出擺放區或 UR 目標半徑 0.18..0.48 m " +
                        $"(SpatialCellSize={ws.SpatialCellSize:F3} m)；不會自動平移或送出實體手臂。",
            };

        return new LayoutRealizer.RealizeResult { Targets = targets };
    }
}
