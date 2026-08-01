using System;
using System.Collections.Generic;
using System.Linq;

// PlacementPlanner：把 bitmap + 補貨區 supplies（cube + domino 混合）→ 一連串 PlacementStep。
// 內含 PackingSolver（把 bitmap 拆成 cube / horizontal domino / vertical domino），
// 分池 greedy 配對 supply、決定擺放順序。
public static class PlacementPlanner
{
    // ------------------------------------------------------------------
    // 常數（QR 工作平面座標系，單位公尺）
    // ------------------------------------------------------------------
    // 補貨區與擺放區的分界（cube 跟 domino 用同一個門檻）：x < 這個值算補貨區。
    private const double SUPPLY_ZONE_X_MAX = 0.30;

    // 擺放區左下角（bitmap 的 col=0, row=最後一列 對應到這個座標）。
    private const double TARGET_ORIGIN_X = 0.35;
    private const double TARGET_ORIGIN_Y = 0.05;

    // 每一格的實際大小 = 4 cm（2.5 cm 立方體 + 1.5 cm 間隙）。
    // Domino（5 cm 長）跨相鄰 2 格（8 cm span），實體只佔中間 5 cm，
    // 兩端各留 1.5 cm 不會撞到隔壁 piece。
    private const double CELL_SIZE = 0.055

    // 積木放到桌面上時的頂面 Z（2.5 cm 立方體的頂面高度）。
    private const double DEFAULT_BLOCK_Z = 0.015;

    // 擺放區可用格數上限（5 × 5）。超出的 pattern 會被拒絕。
    private const int MAX_GRID_ROWS = 8;
    private const int MAX_GRID_COLS = 8;

    // ------------------------------------------------------------------
    // 對外資料類別
    // ------------------------------------------------------------------
    public class PlanResult
    {
        public List<PlacementStep>? Steps { get; set; }
        public string? Error { get; set; }
    }

    // ------------------------------------------------------------------
    // 內部：packing 後的每一塊 piece
    // ------------------------------------------------------------------
    private class Piece
    {
        public string Shape = "cube";           // "cube" or "domino"
        public string? Orientation;             // null / "horizontal" / "vertical"
        public int Row;                          // 覆蓋的 top-left 格（bitmap 座標）
        public int Col;
    }

    // ------------------------------------------------------------------
    // 主流程
    // ------------------------------------------------------------------
    public static PlanResult Plan(
        int[,] bitmap,
        List<SceneObject> supplies,
        string? blockColor)
    {
        if (bitmap == null)
            return new PlanResult { Error = "bitmap 為空，無法規劃擺放。" };

        int bmpRows = bitmap.GetLength(0);
        int bmpCols = bitmap.GetLength(1);
        if (bmpRows > MAX_GRID_ROWS || bmpCols > MAX_GRID_COLS)
        {
            return new PlanResult
            {
                Error = $"pattern 尺寸 {bmpRows}×{bmpCols}（高×寬）超出擺放區可用範圍 {MAX_GRID_ROWS}×{MAX_GRID_COLS}，請改用較小的圖案。",
            };
        }

        string color = string.IsNullOrWhiteSpace(blockColor) ? "yellow" : blockColor;
        string expectedCube = $"{color}_cube";
        string expectedDomino = $"{color}_domino";

        // Step 1：把 supplies 分成 cube 池跟 domino 池（都只保留補貨區內、指定顏色）
        List<SceneObject> supplyCubes = supplies
            .Where(s => s != null
                        && string.Equals(s.Name, expectedCube, StringComparison.Ordinal)
                        && s.X < SUPPLY_ZONE_X_MAX)
            .ToList();
        List<SceneObject> supplyDominos = supplies
            .Where(s => s != null
                        && string.Equals(s.Name, expectedDomino, StringComparison.Ordinal)
                        && s.X < SUPPLY_ZONE_X_MAX)
            .ToList();

        // Step 2：packing — 依 supply 有幾個 domino 決定要不要用 domino
        List<Piece> pieces = PackPieces(bitmap, dominoBudget: supplyDominos.Count);

        if (pieces.Count == 0)
            return new PlanResult { Error = "pattern 沒有任何要放置積木的格子。" };

        int cubesNeeded = pieces.Count(p => p.Shape == "cube");
        int dominosNeeded = pieces.Count(p => p.Shape == "domino");

        // 診斷 log：讓使用者看到 supply 跟 packing 結果
        Console.WriteLine($"[PlacementPlanner] {color} supply: {supplyCubes.Count} cube + {supplyDominos.Count} domino "
                          + $"→ packed: {cubesNeeded} cube + {dominosNeeded} domino "
                          + $"（bitmap 共 {cubesNeeded + dominosNeeded * 2} 格）");

        // Step 3：數量檢查（cube 需求可能因為 domino 不夠而變多；分開回報）
        if (cubesNeeded > supplyCubes.Count)
        {
            return new PlanResult
            {
                Error = $"需要 {cubesNeeded} 顆 {color} cube，補貨區只有 {supplyCubes.Count} 顆" +
                        (dominosNeeded > 0
                            ? $"（已用 {dominosNeeded} 個 domino 減少 cube 需求）"
                            : "") +
                        "，無法完成拚圖。",
            };
        }
        // dominosNeeded 不會超過 supplyDominos.Count，因為 PackPieces 內就有做 budget 限制

        // Step 4：每個 piece → target world 座標 SceneObject
        List<(Piece Piece, SceneObject Target)> placements =
            pieces.Select(p => (p, PieceToTarget(p, bmpRows))).ToList();

        // Step 5：擺放順序 — 遠端角落先放（Y 大、X 大），避免手臂之後跨過已放的積木
        placements.Sort((a, b) =>
        {
            int cmpY = b.Target.Y.CompareTo(a.Target.Y);
            if (cmpY != 0) return cmpY;
            return b.Target.X.CompareTo(a.Target.X);
        });

        // Step 6：分池 greedy — 每個 target piece 挑對應形狀池中最近的 supply
        List<SceneObject> remainingCubes = new List<SceneObject>(supplyCubes);
        List<SceneObject> remainingDominos = new List<SceneObject>(supplyDominos);
        List<PlacementStep> steps = new List<PlacementStep>();

        foreach (var (piece, target) in placements)
        {
            List<SceneObject> pool = piece.Shape == "domino" ? remainingDominos : remainingCubes;
            SceneObject nearest = pool.OrderBy(s => Dist2DSquared(s, target)).First();

            steps.Add(new PlacementStep
            {
                SourcePosition = nearest,          // 帶著 nearest.Orientation（pick 時 wrist 角度依據）
                TargetPosition = target,           // 帶著 piece.Orientation（place 時 wrist 角度依據）
            });

            pool.Remove(nearest);
        }

        return new PlanResult { Steps = steps };
    }

    // ------------------------------------------------------------------
    // Packing：greedy 優先用 domino 減少 pick 次數
    // dominoBudget = supply 池的 domino 數量，超過就不再分配 domino
    // ------------------------------------------------------------------
    private static List<Piece> PackPieces(int[,] bitmap, int dominoBudget)
    {
        int rows = bitmap.GetLength(0);
        int cols = bitmap.GetLength(1);
        bool[,] occupied = new bool[rows, cols];
        var pieces = new List<Piece>();
        int dominosUsed = 0;

        // Pass 1：橫向 domino
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols - 1; c++)
            {
                if (dominosUsed >= dominoBudget) break;
                if (bitmap[r, c] == 1 && bitmap[r, c + 1] == 1
                    && !occupied[r, c] && !occupied[r, c + 1])
                {
                    pieces.Add(new Piece { Shape = "domino", Orientation = "horizontal", Row = r, Col = c });
                    occupied[r, c] = true;
                    occupied[r, c + 1] = true;
                    dominosUsed++;
                }
            }
        }

        // Pass 2：縱向 domino
        for (int r = 0; r < rows - 1; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (dominosUsed >= dominoBudget) break;
                if (bitmap[r, c] == 1 && bitmap[r + 1, c] == 1
                    && !occupied[r, c] && !occupied[r + 1, c])
                {
                    pieces.Add(new Piece { Shape = "domino", Orientation = "vertical", Row = r, Col = c });
                    occupied[r, c] = true;
                    occupied[r + 1, c] = true;
                    dominosUsed++;
                }
            }
        }

        // Pass 3：剩下的 1 → cube
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (bitmap[r, c] == 1 && !occupied[r, c])
                {
                    pieces.Add(new Piece { Shape = "cube", Orientation = null, Row = r, Col = c });
                    occupied[r, c] = true;
                }
            }
        }

        return pieces;
    }

    // ------------------------------------------------------------------
    // Piece → 目標世界座標（幾何中心）
    // row 對 Y 反向：row 0 = 字母頂 = 遠離觀察者（Y 大），這樣字才不會上下顛倒
    // ------------------------------------------------------------------
    private static SceneObject PieceToTarget(Piece p, int rows)
    {
        double centerX;
        double centerY;

        if (p.Shape == "cube")
        {
            centerX = TARGET_ORIGIN_X + p.Col * CELL_SIZE;
            centerY = TARGET_ORIGIN_Y + (rows - 1 - p.Row) * CELL_SIZE;
        }
        else if (p.Orientation == "horizontal")
        {
            // 覆蓋 (r, c) 跟 (r, c+1)：中心 X = 兩格中點；Y 同 row
            centerX = TARGET_ORIGIN_X + (p.Col + 0.5) * CELL_SIZE;
            centerY = TARGET_ORIGIN_Y + (rows - 1 - p.Row) * CELL_SIZE;
        }
        else // vertical
        {
            // 覆蓋 (r, c) 跟 (r+1, c)：X 同 col；Y = 兩 row 中點（記得 row 反向）
            centerX = TARGET_ORIGIN_X + p.Col * CELL_SIZE;
            centerY = TARGET_ORIGIN_Y + (rows - 1 - p.Row - 0.5) * CELL_SIZE;
        }

        return new SceneObject
        {
            Name = $"grid_{p.Shape}_r{p.Row}_c{p.Col}",
            X = centerX,
            Y = centerY,
            Z = DEFAULT_BLOCK_Z,
            Shape = p.Shape,
            Orientation = p.Orientation,
        };
    }

    // ------------------------------------------------------------------
    // 2D 平方距離（不用開根號，比大小夠用）
    // ------------------------------------------------------------------
    private static double Dist2DSquared(SceneObject a, SceneObject b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
