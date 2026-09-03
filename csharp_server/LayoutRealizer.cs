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
    }

    /// <summary>
    /// Builds a ladder of candidate (rows, cols, cellSize) resolutions that all
    /// share the SAME physical footprint as the base workspace's starting size
    /// (InitialPatternRows/Cols * CellSize stays constant), by shrinking
    /// CellSize as rows/cols grow from there. Stops at whichever comes first:
    /// MaxRows/MaxCols (the workspace's own ceiling — e.g. where an expanded
    /// layout like ExpandedCellSize takes over instead of linear shrinking),
    /// CellSize dropping below MinCellSize (adjacent cubes would physically
    /// collide), or maxSteps (safety net). Caller tries each entry in order
    /// and stops at the first one PatternDesigner accepts as feasible.
    /// </summary>
    public static List<(int Rows, int Cols, double CellSize)> BuildResolutionLadder(
        WorkspaceBounds baseWs, int step = 2, int maxSteps = 6)
    {
        double footprintW = baseWs.InitialPatternCols * baseWs.CellSize;
        double footprintH = baseWs.InitialPatternRows * baseWs.CellSize;
        var ladder = new List<(int, int, double)>();
        int rows = baseWs.InitialPatternRows, cols = baseWs.InitialPatternCols;
        for (int i = 0; i < maxSteps && rows <= baseWs.MaxRows && cols <= baseWs.MaxCols; i++)
        {
            double cellSize = System.Math.Min(footprintW / cols, footprintH / rows);
            if (cellSize < baseWs.MinCellSize) break;
            ladder.Add((rows, cols, cellSize));
            rows += step;
            cols += step;
        }
        return ladder;
    }

    /// <summary>
    /// Copies a WorkspaceBounds with MaxRows/MaxCols/CellSize overridden for one
    /// resolution-ladder attempt, leaving every other field (origins, zones,
    /// 3D fields) identical to the base workspace.
    /// </summary>
    public static WorkspaceBounds WithResolution(WorkspaceBounds baseWs, int rows, int cols, double cellSize)
        => new()
        {
            SupplyZoneXMax = baseWs.SupplyZoneXMax,
            TargetZoneXMin = baseWs.TargetZoneXMin,
            TargetOriginX = baseWs.TargetOriginX,
            TargetOriginY = baseWs.TargetOriginY,
            CellSize = cellSize,
            MinCellClearanceM = baseWs.MinCellClearanceM,
            DefaultBlockZ = baseWs.DefaultBlockZ,
            MaxRows = rows,
            MaxCols = cols,
            MaxLayers = baseWs.MaxLayers,
            SpatialRows = baseWs.SpatialRows,
            SpatialCols = baseWs.SpatialCols,
            SpatialLayers = baseWs.SpatialLayers,
            SpatialTargetOriginY = baseWs.SpatialTargetOriginY,
        };

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

return new RealizeResult { Targets = targets };
    }

    private static TargetCell BuildCube(int r, int c, int rows, WorkspaceBounds ws, string color)
    {
        (double originX, double cellSize) = PlanarGrid(rows, ws);
        return new TargetCell
        {
            Row = r,
            Col = c,
            WorldX = originX + c * cellSize,
            WorldY = ws.TargetOriginY + (rows - 1 - r) * cellSize,  // row 反向：字母不上下顛倒
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
        (double originX, double cellSize) = PlanarGrid(rows, ws);
        // 中心點 = 兩格中點；row 反向處理
        double cx = originX + (c1 + c2) * 0.5 * cellSize;
        double cy = ws.TargetOriginY + ((rows - 1 - r1) + (rows - 1 - r2)) * 0.5 * cellSize;
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

    private static (double OriginX, double CellSize) PlanarGrid(int rows, WorkspaceBounds ws)
        => rows > ws.InitialPatternRows
            ? (ws.ExpandedTargetOriginX, ws.ExpandedCellSize)
            : (ws.TargetOriginX, ws.CellSize);
}
