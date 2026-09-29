/// <summary>
/// 3D 疊放批次每一步的真實高度：來源積木頂面、放好之後的頂面（公尺，2.5 cm 層高的整數倍），
/// 送給 Unity 當 descend 的依據（StepEnvelope.SourceTopM / TargetTopM）。
/// perception 的 z 對黑色積木偏差太大（桌面上的觀測 −32～+3 mm，超過一層），不能單靠 z 判層，
/// 所以加上場景結構：佔地（XY 範圍）跟目標重疊的積木就是支撐，放好的頂面至少是最高支撐 + 1 層；
/// 同一批裡前面步驟放下的積木也算進去。LLM 的 target.z（對齊層高後）只能把放開的高度往上調，
/// 不會讓夾爪往下壓進支撐。來源積木假設上面沒有壓著別的積木（目前不支援從疊好的積木中間抽出）。
/// isaac_sim/block_layers.py 用同一套規則投影場景，兩邊改動要一致。
/// </summary>
public static class LayeredHeights
{
    // 佔地要重疊超過這個量才算疊在上面：並排貼齊的積木加上感知的 XY 誤差，不會被當成支撐
    const double OverlapMarginM = 0.002;

    sealed class Block
    {
        public double X, Y, HalfX, HalfY, Top;
    }

    /// <summary>只有 HSV 偵測的積木（yellow_cube、black_domino …）有固定尺寸，杯子等 YOLO 物件不算。</summary>
    public static bool IsBlock(SceneObject o) =>
        (o.Shape == "cube" || o.Shape == "domino") && (o.Name.EndsWith("_cube") || o.Name.EndsWith("_domino"));

    static (double HalfX, double HalfY) HalfExtents(string shape, string? orientation)
    {
        double half = LayeredGraspGeometry.BlockLayerM / 2;
        if (shape != "domino") return (half, half);
        return orientation == "vertical" ? (half, 2 * half) : (2 * half, half);
    }

    static bool Overlaps(Block b, double x, double y, double halfX, double halfY) =>
        Math.Abs(b.X - x) < b.HalfX + halfX - OverlapMarginM &&
        Math.Abs(b.Y - y) < b.HalfY + halfY - OverlapMarginM;

    /// <summary>
    /// 場景裡每塊積木的真實頂面（key = 場景 index）：依 perception z 由低到高處理，取「z 對齊層高」與
    /// 「佔地重疊的下層 + 1 層」兩者較高的；桌面上的積木是第 1 層。
    /// </summary>
    public static Dictionary<int, double> SceneTops(IReadOnlyList<SceneObject> scene)
    {
        var placed = new List<Block>();
        var tops = new Dictionary<int, double>();
        foreach (int i in Enumerable.Range(0, scene.Count).Where(i => IsBlock(scene[i]))
                     .OrderBy(i => scene[i].Z).ThenBy(i => i))
        {
            var o = scene[i];
            var (halfX, halfY) = HalfExtents(o.Shape, o.Orientation);
            double top = LayeredGraspGeometry.SnapTopToLayer(o.Z);
            foreach (var below in placed.Where(b => Overlaps(b, o.X, o.Y, halfX, halfY)))
                top = Math.Max(top, below.Top + LayeredGraspGeometry.BlockLayerM);
            placed.Add(new Block { X = o.X, Y = o.Y, HalfX = halfX, HalfY = halfY, Top = top });
            tops[i] = top;
        }
        return tops;
    }

    /// <summary>依序算每一步的（來源頂面, 放好後頂面）；scene 是規劃時的觀測，source_index 對應它。</summary>
    public static List<(double SourceTopM, double TargetTopM)> ForSteps(IReadOnlyList<TranslatedStep> steps,
        IReadOnlyList<SceneObject> scene)
    {
        var blocks = new Dictionary<int, Block>();
        foreach (var (i, top) in SceneTops(scene))
        {
            var (halfX, halfY) = HalfExtents(scene[i].Shape, scene[i].Orientation);
            blocks[i] = new Block { X = scene[i].X, Y = scene[i].Y, HalfX = halfX, HalfY = halfY, Top = top };
        }
        var result = new List<(double, double)>();
        foreach (var step in steps)
        {
            var source = step.SourceIndex >= 0 && step.SourceIndex < scene.Count ? scene[step.SourceIndex] : null;
            double sourceTop = blocks.TryGetValue(step.SourceIndex, out var picked)
                ? picked.Top
                : LayeredGraspGeometry.SnapTopToLayer(source?.Z ?? 0);
            blocks.Remove(step.SourceIndex);
            var target = step.Target;
            if (source == null || target == null)
            {
                // 執行前檢查（ExperimentChecks.Resolve）會先擋下，這裡只是不讓計算中斷
                result.Add((sourceTop, sourceTop));
                continue;
            }
            var (halfX, halfY) = HalfExtents(source.Shape, target.Orientation ?? source.Orientation);
            double supportTop = blocks.Values.Where(b => Overlaps(b, target.X, target.Y, halfX, halfY))
                .Select(b => b.Top).DefaultIfEmpty(0).Max();
            double targetTop = Math.Max(supportTop + LayeredGraspGeometry.BlockLayerM,
                LayeredGraspGeometry.SnapTopToLayer(target.Z));
            if (EndsReleased(step.Actions))
                blocks[step.SourceIndex] = new Block { X = target.X, Y = target.Y, HalfX = halfX, HalfY = halfY, Top = targetTop };
            result.Add((sourceTop, targetTop));
        }
        return result;
    }

    // 這一步結束時已放開（最後一次 release 在最後一次 grasp 之後）；還夾著的積木不在桌上
    static bool EndsReleased(List<RobotFunctionCall> actions)
    {
        int lastGrasp = actions.FindLastIndex(a => a.Function == "grasp");
        int lastRelease = actions.FindLastIndex(a => a.Function == "release");
        return lastRelease > lastGrasp;
    }
}
