using System;
using System.Collections.Generic;
using System.Linq;

// -----------------------------------------------------------------
// Layer 2：Layout Realizer
// 純數學：canonical bitmap → List<TargetCell>（每個 target 的世界座標 + 期望形狀）
// 不呼叫 LLM，deterministic。可寫 unit test。
//
// 內含 domino packing（bitmap 中相鄰兩個 1 → 一個 domino target）。
// 每個 target 的 shape/orientation/color 是「期望」，由 Layer 3 決定實際用哪顆 supply 對應。
// -----------------------------------------------------------------

public static class LayoutRealizer
{
    public class RealizeResult
    {
        public List<TargetCell>? Targets { get; set; }
        public string? Error { get; set; }
        public double PlacementShiftX { get; set; }
        public double PlacementShiftY { get; set; }
    }

    /// <summary>
    /// bitmap + workspace → 每一格對應的世界座標。
    /// dominoBudget = supply 池中可用 domino 數量，用來決定要不要把相鄰 1 合成 domino。
    /// 傳 0 → 全部用 cube。
    /// </summary>
    public static RealizeResult Realize(
        CanonicalPattern pattern,
        WorkspaceBounds ws,
        int cubeBudget,
    int dominoBudget)
    {
        if (pattern.Bitmap == null)
            return new RealizeResult { Error = "canonical pattern bitmap 為空。" };

        int rows = pattern.Bitmap.GetLength(0);
        int cols = pattern.Bitmap.GetLength(1);
        if (rows > ws.MaxRows || cols > ws.MaxCols)
        {
            return new RealizeResult
            {
                Error = $"pattern 尺寸 {rows}×{cols} 超出擺放區 {ws.MaxRows}×{ws.MaxCols}",
            };
        }

        var occupied = new bool[rows, cols];
        var targets = new List<TargetCell>();
        int dominosUsed = 0;

        // Pass 1：橫向 domino（相鄰兩個 1 → 一個 horizontal domino）
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols - 1; c++)
            {
                if (dominosUsed >= dominoBudget) break;
                if (pattern.Bitmap[r, c] == 1 && pattern.Bitmap[r, c + 1] == 1
                    && !occupied[r, c] && !occupied[r, c + 1])
                {
                    targets.Add(BuildDomino(r, c, r, c + 1, rows, ws, pattern.BlockColor, "horizontal"));
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
                if (pattern.Bitmap[r, c] == 1 && pattern.Bitmap[r + 1, c] == 1
                    && !occupied[r, c] && !occupied[r + 1, c])
                {
                    targets.Add(BuildDomino(r, c, r + 1, c, rows, ws, pattern.BlockColor, "vertical"));
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
                if (pattern.Bitmap[r, c] == 1 && !occupied[r, c])
                {
                    targets.Add(BuildCube(r, c, rows, ws, pattern.BlockColor));
                    occupied[r, c] = true;
                }
            }
        }

        if (targets.Count == 0)
    return new RealizeResult { Error = "pattern 沒有任何要放置積木的格子。" };

int cubesNeeded = targets.Count(t => t.ExpectedShape == "cube");
int dominosNeeded = targets.Count(t => t.ExpectedShape == "domino");

if (cubesNeeded > cubeBudget)
{
    return new RealizeResult
    {
        Error = $"cube 不足：需要 {cubesNeeded} 顆，但目前只有 {cubeBudget} 顆。",
    };
}

if (dominosNeeded > dominoBudget)
{
    return new RealizeResult
    {
        Error = $"domino 不足：需要 {dominosNeeded} 顆，但目前只有 {dominoBudget} 顆。",
    };
}

        if (!TryFitTargetsToSafeReach(targets, ws, out double shiftX, out double shiftY))
        {
            return new RealizeResult
            {
                Error = $"CellSize={ws.CellSize:F3} m 的本次圖案無法在 placement area 內平移到 " +
                        "UR 半徑篩選 0.14..0.45 m；未停用 IK 與碰撞限制。",
            };
        }

        foreach (var target in targets)
        {
            target.WorldX += shiftX;
            target.WorldY += shiftY;
        }

        return new RealizeResult
        {
            Targets = targets,
            PlacementShiftX = shiftX,
            PlacementShiftY = shiftY,
        };
    }

    private static bool TryFitTargetsToSafeReach(
        IReadOnlyList<TargetCell> targets,
        WorkspaceBounds ws,
        out double bestShiftX,
        out double bestShiftY)
    {
        const double qrToRobotX = -0.38824;
        const double qrToRobotY = -0.35473;
        const double minReach = 0.14;
        const double maxReach = 0.45;
        const double placementMaxX = 0.72;
        const double placementMinY = 0.00;
        const double placementMaxY = 0.45;
        const double searchStep = 0.001;

        double minX = targets.Min(t => t.WorldX);
        double maxX = targets.Max(t => t.WorldX);
        double minY = targets.Min(t => t.WorldY);
        double maxY = targets.Max(t => t.WorldY);
        double minShiftX = ws.TargetZoneXMin - minX;
        double maxShiftX = placementMaxX - maxX;
        double minShiftY = placementMinY - minY;
        double maxShiftY = placementMaxY - maxY;

        bestShiftX = 0.0;
        bestShiftY = 0.0;
        double bestCost = double.PositiveInfinity;
        double bestMargin = double.NegativeInfinity;

        for (double dx = minShiftX; dx <= maxShiftX + 1e-9; dx += searchStep)
        {
            for (double dy = minShiftY; dy <= maxShiftY + 1e-9; dy += searchStep)
            {
                bool safe = true;
                double minimumMargin = double.PositiveInfinity;
                foreach (var target in targets)
                {
                    double robotX = qrToRobotX + target.WorldX + dx;
                    double robotY = qrToRobotY + target.WorldY + dy;
                    double radius = Math.Sqrt(robotX * robotX + robotY * robotY);
                    if (radius < minReach || radius > maxReach)
                    {
                        safe = false;
                        break;
                    }
                    minimumMargin = Math.Min(minimumMargin,
                        Math.Min(radius - minReach, maxReach - radius));
                }
                if (!safe) continue;

                double cost = dx * dx + dy * dy;
                if (cost < bestCost - 1e-12 ||
                    (Math.Abs(cost - bestCost) <= 1e-12 && minimumMargin > bestMargin))
                {
                    bestCost = cost;
                    bestMargin = minimumMargin;
                    bestShiftX = dx;
                    bestShiftY = dy;
                }
            }
        }

        return !double.IsPositiveInfinity(bestCost);
    }

    private static TargetCell BuildCube(int r, int c, int rows, WorkspaceBounds ws, string color)
    {
        return new TargetCell
        {
            Row = r,
            Col = c,
            WorldX = ws.TargetOriginX + c * ws.CellSize,
            WorldY = ws.TargetOriginY + (rows - 1 - r) * ws.CellSize,  // row 反向：字母不上下顛倒
            WorldZ = ws.DefaultBlockZ,
            ExpectedShape = "cube",
            ExpectedColor = color,
            ExpectedOrientation = null,
        };
    }

    private static TargetCell BuildDomino(
        int r1, int c1, int r2, int c2,
        int rows, WorkspaceBounds ws, string color, string orientation)
    {
        // 中心點 = 兩格中點；row 反向處理
        double cx = ws.TargetOriginX + (c1 + c2) * 0.5 * ws.CellSize;
        double cy = ws.TargetOriginY + ((rows - 1 - r1) + (rows - 1 - r2)) * 0.5 * ws.CellSize;
        return new TargetCell
        {
            Row = r1,
            Col = c1,
            SecondRow = r2,
            SecondCol = c2,
            WorldX = cx,
            WorldY = cy,
            WorldZ = ws.DefaultBlockZ,
            ExpectedShape = "domino",
            ExpectedColor = color,
            ExpectedOrientation = orientation,
        };
    }
}
