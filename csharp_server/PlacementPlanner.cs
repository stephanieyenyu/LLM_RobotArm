using System;
using System.Collections.Generic;
using System.Linq;

// PlacementPlanner：把 bitmap + 補貨區積木清單 → 一連串 PlacementStep。
// 負責 grid → 世界座標映射、積木配對、擺放順序、數量檢查。
public static class PlacementPlanner
{
    // ------------------------------------------------------------------
    // 常數（QR 工作平面座標系，單位公尺）
    // ------------------------------------------------------------------
    // 補貨區與擺放區的分界：x < 這個值算補貨區、x >= 這個值不算補貨區。
    private const double SUPPLY_ZONE_X_MAX = 0.30;

    // 擺放區左下角（bitmap 的 col=0, row=0 對應到這個座標）。
    private const double TARGET_ORIGIN_X = 0.35;
    private const double TARGET_ORIGIN_Y = 0.05;

    // 每一格的實際大小，跟你手上 5 cm 立方體對齊。
    private const double CELL_SIZE = 0.05;

    // 積木放到桌面上時的頂面 Z（等於 perception 端的 OBJECT_HEIGHT_OFFSET_M）。
    // Z_CORRECTION 由 JsonExecutor 再加一次，不用在這裡補。
    private const double DEFAULT_BLOCK_Z = 0.013;

    // ------------------------------------------------------------------
    // 對外資料類別
    // ------------------------------------------------------------------
    public class PlanResult
    {
        public List<PlacementStep>? Steps { get; set; }   // 成功時為 pick_and_place 步驟清單
        public string? Error { get; set; }                // 失敗（例如積木不夠）時為中文原因
    }

    // ------------------------------------------------------------------
    // 主流程
    // ------------------------------------------------------------------
    public static PlanResult Plan(
        int[,] bitmap,
        List<SceneObject> supplyCubes,
        string? blockColor)
    {
        if (bitmap == null)
            return new PlanResult { Error = "bitmap 為空，無法規劃擺放。" };

        string color = string.IsNullOrWhiteSpace(blockColor) ? "yellow" : blockColor;
        string expectedName = $"{color}_cube";

        // Step 1：篩選補貨區內、指定顏色的積木
        List<SceneObject> available = supplyCubes
            .Where(c => c != null
                        && string.Equals(c.Name, expectedName, StringComparison.Ordinal)
                        && c.X < SUPPLY_ZONE_X_MAX)
            .ToList();

        // Step 2：把 bitmap 每個「1」格轉成世界座標
        List<SceneObject> targets = BitmapToTargetPositions(bitmap);

        if (targets.Count == 0)
            return new PlanResult { Error = "pattern 沒有任何要放置積木的格子。" };

        // Step 3：數量檢查
        if (available.Count < targets.Count)
        {
            return new PlanResult
            {
                Error = $"需要 {targets.Count} 顆 {color} 積木，補貨區內只有 {available.Count} 顆，無法完成拚圖。",
            };
        }

        // Step 4：擺放順序 — 從遠端角落（較大 Y、較大 X）先放，
        // 讓手臂之後放近處時比較不會需要跨過已擺好的積木。
        List<SceneObject> orderedTargets = targets
            .OrderByDescending(t => t.Y)
            .ThenByDescending(t => t.X)
            .ToList();

        // Step 5：Greedy 配對 — 每個 target 挑離自己 2D 距離最近的補貨積木
        List<SceneObject> remaining = new List<SceneObject>(available);
        List<PlacementStep> steps = new List<PlacementStep>();

        foreach (SceneObject target in orderedTargets)
        {
            SceneObject nearest = remaining
                .OrderBy(c => Dist2DSquared(c, target))
                .First();

            steps.Add(new PlacementStep
            {
                SourcePosition = nearest,
                TargetPosition = target,
            });

            remaining.Remove(nearest);
        }

        return new PlanResult { Steps = steps };
    }

    // ------------------------------------------------------------------
    // 內部工具
    // ------------------------------------------------------------------

    // bitmap (row, col) → 擺放區世界座標
    //   row  → Y（bitmap row 0 = 擺放區靠近 QR1-QR2 那一側；row 越大越遠離）
    //   col  → X（bitmap col 0 = 擺放區靠近 QR1 那一側；col 越大越靠 QR2）
    private static List<SceneObject> BitmapToTargetPositions(int[,] bitmap)
    {
        int rows = bitmap.GetLength(0);
        int cols = bitmap.GetLength(1);
        var targets = new List<SceneObject>();

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (bitmap[r, c] != 1)
                    continue;

                targets.Add(new SceneObject
                {
                    Name = $"grid_r{r}_c{c}",
                    X = TARGET_ORIGIN_X + c * CELL_SIZE,
                    Y = TARGET_ORIGIN_Y + r * CELL_SIZE,
                    Z = DEFAULT_BLOCK_Z,
                });
            }
        }

        return targets;
    }

    // 2D 平方距離（不用開根號，比較大小就夠）
    private static double Dist2DSquared(SceneObject a, SceneObject b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
