public static class ExperimentChecks
{
    // Perception coordinates fluctuate between consecutive frames.  Keep the
    // selected object identity by nearest-neighbour association, but refuse to
    // guess when two same-kind objects are similarly close.
    const double SourceTrackingRadiusM = 0.035;
    const double SourceUniquenessMarginM = 0.010;
    const double SourceHeightToleranceM = 0.025;
    // 任務重置：實體桌面要跟初始配置一對一吻合（同名、同形狀、XY / Z / domino 方向都在容許內）
    public const double ResetXYToleranceM = 0.015;
    public const double ResetZToleranceM = 0.010;
    public const double ResetSkewToleranceDeg = 10;
    public static bool Matches(IReadOnlyList<SceneObject> expected, IReadOnlyList<SceneObject> actual)
    {
        if (expected.Count == 0 || expected.Count != actual.Count) return false;
        var owners = Enumerable.Repeat(-1, actual.Count).ToArray();
        bool Match(int i, bool[] visited)
        {
            for (int j = 0; j < actual.Count; j++)
            {
                var a = actual[j]; var e = expected[i];
                if (visited[j] || a.Name != e.Name || a.Shape != e.Shape || Distance(a, e) > ResetXYToleranceM || Math.Abs(a.Z - e.Z) > ResetZToleranceM ||
                    (e.Shape == "domino" && (a.Orientation != e.Orientation || Math.Abs(a.SkewDeg - e.SkewDeg) > ResetSkewToleranceDeg))) continue;
                visited[j] = true;
                if (owners[j] == -1 || Match(owners[j], visited)) { owners[j] = i; return true; }
            }
            return false;
        }
        return Enumerable.Range(0, expected.Count).All(i => Match(i, new bool[actual.Count]));
    }

    /// <summary>
    /// 逐物件說明目前桌面跟初始配置差在哪（跟 Matches 同一組門檻），給等待重置時顯示。
    /// 同名物件依距離由近到遠配對，每個觀測只配一次；位置不符時附上要往哪邊移。
    /// </summary>
    public static List<string> DescribeMismatch(IReadOnlyList<SceneObject> expected, IReadOnlyList<SceneObject> actual)
    {
        var match = new Dictionary<int, int>();
        var used = new HashSet<int>();
        var pairs = expected.SelectMany((e, i) => actual.Select((a, j) => (i, j, d: Distance(a, e)))
                .Where(p => actual[p.j].Name == e.Name))
            .OrderBy(p => p.d);
        foreach (var (i, j, _) in pairs)
            if (!match.ContainsKey(i) && used.Add(j)) match[i] = j;

        var lines = new List<string>();
        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            if (!match.TryGetValue(i, out int j))
            {
                lines.Add($"不符 {e.Name}：相機看不到（基準在 ({e.X:F3}, {e.Y:F3})）");
                continue;
            }
            var a = actual[j];
            double xyMm = Distance(a, e) * 1000, dzMm = Math.Abs(a.Z - e.Z) * 1000;
            var problems = new List<string>();
            if (a.Shape != e.Shape) problems.Add($"形狀 {a.Shape}，基準 {e.Shape}");
            if (xyMm > ResetXYToleranceM * 1000)
                problems.Add($"XY 差 {xyMm:F0} mm（允許 {ResetXYToleranceM * 1000:F0}），{MoveHint(a, e)}");
            if (dzMm > ResetZToleranceM * 1000) problems.Add($"Z 差 {dzMm:F0} mm（允許 {ResetZToleranceM * 1000:F0}）");
            if (e.Shape == "domino" && (a.Orientation != e.Orientation || Math.Abs(a.SkewDeg - e.SkewDeg) > ResetSkewToleranceDeg))
                problems.Add($"方向 {a.Orientation ?? "無"} {a.SkewDeg:F0}°，基準 {e.Orientation ?? "無"} {e.SkewDeg:F0}°（允許 {ResetSkewToleranceDeg:F0}°）");
            lines.Add(problems.Count == 0
                ? $"符合 {e.Name}：XY 差 {xyMm:F0} mm、Z 差 {dzMm:F0} mm"
                : $"不符 {e.Name}：{string.Join("；", problems)}");
        }
        for (int j = 0; j < actual.Count; j++)
            if (!used.Contains(j)) lines.Add($"不符 多出 {actual[j].Name}（在 ({actual[j].X:F3}, {actual[j].Y:F3})），基準沒有");
        return lines;
    }

    // QR 座標：+X = QR1→QR2（相機畫面往右），+Y = QR1→QR3（離相機較遠）
    static string MoveHint(SceneObject from, SceneObject to)
    {
        var parts = new List<string>();
        double dx = to.X - from.X, dy = to.Y - from.Y;
        if (Math.Abs(dx) >= 0.005) parts.Add($"往{(dx > 0 ? "右（QR2 側）" : "左（QR1 側）")} {Math.Abs(dx) * 100:F1} cm");
        if (Math.Abs(dy) >= 0.005) parts.Add($"往{(dy > 0 ? "遠（QR3 側）" : "近（相機側）")} {Math.Abs(dy) * 100:F1} cm");
        return "要" + string.Join("、", parts);
    }
    /// <summary>
    /// target 描述的就是被搬的來源物件，名稱與形狀由程式依 source_index 補上。這是內部記帳欄位，
    /// 不是解題決策；交給轉譯器填時，它曾把「放在誰上面」誤填成身分，每次都被執行前檢查擋下。
    /// </summary>
    public static void FillTargetIdentity(IEnumerable<TranslatedStep> steps, IReadOnlyList<SceneObject> scene)
    {
        foreach (var step in steps)
            if (step.Target != null && step.SourceIndex >= 0 && step.SourceIndex < scene.Count)
            {
                step.Target.Name = scene[step.SourceIndex].Name;
                step.Target.Shape = scene[step.SourceIndex].Shape;
            }
    }

    public static bool Resolve(TranslatedStep step, IReadOnlyList<SceneObject> plannedScene, IReadOnlyList<SceneObject> current, out Assignment assignment, out string error)
    {
        // 錯誤訊息只寫事實（哪個欄位、實際值、欄位定義），不寫應該填多少，避免替 LLM 解題。
        assignment = new Assignment(); error = "";
        if (step.SourceIndex < 0 || step.SourceIndex >= plannedScene.Count)
        { error = $"source_index={step.SourceIndex} 不在場景物件編號範圍 0～{plannedScene.Count - 1}。"; return false; }
        if (step.Target == null) { error = "轉譯資料沒有 target。"; return false; }
        if (step.Actions == null || step.Actions.Count == 0) { error = "這個操作沒有任何函式。"; return false; }
        var source = plannedScene[step.SourceIndex];
        var candidates = current
            .Where(o => o.Name == source.Name && o.Shape == source.Shape &&
                        Math.Abs(o.Z - source.Z) <= SourceHeightToleranceM)
            .Select(o => new { Object = o, Distance = Distance(o, source) })
            .Where(x => x.Distance <= SourceTrackingRadiusM)
            .OrderBy(x => x.Distance)
            .ToList();
        if (candidates.Count == 0)
        {
            error = $"目前觀測不到原先選定的來源 {source.Name}（原位置 ({source.X:F3}, {source.Y:F3})，" +
                    $"{SourceTrackingRadiusM * 1000:F0} mm 內沒有同名同形、高度相近的物件）；可能是感知暫時遺漏或場景已改變。";
            return false;
        }
        if (candidates.Count > 1 &&
            candidates[1].Distance - candidates[0].Distance < SourceUniquenessMarginM)
        {
            error = $"原先選定的來源 {source.Name} 附近有多個同類物件（距原位置 {candidates[0].Distance * 1000:F0} mm 與 " +
                    $"{candidates[1].Distance * 1000:F0} mm，差距小於 {SourceUniquenessMarginM * 1000:F0} mm），無法判定是哪一個。";
            return false;
        }
        var target = step.Target;
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(target.Z))
        { error = $"target 座標不是有效數字（x={target.X}, y={target.Y}, z={target.Z}）。"; return false; }
        if (target.Z <= 0)
        { error = $"target.z={target.Z:F4} ≤ 0，不合法；target.z 指來源物件放好後的頂面高度（與場景物件 z 同一慣例）。"; return false; }
        // 名稱與形狀由轉譯後的程式依 source_index 補上，正常一定一致；不一致代表內部資料錯誤
        if (target.Shape != source.Shape || target.Name != source.Name)
        { error = $"內部資料錯誤：target 身分 {target.Name}/{target.Shape} 與來源 {source.Name}/{source.Shape} 不一致。"; return false; }
        assignment.Source = candidates[0].Object;
        assignment.Target = new TargetCell { WorldX = target.X, WorldY = target.Y, WorldZ = target.Z, ExpectedShape = source.Shape,
            ExpectedColor = source.Name.Split('_')[0], ExpectedOrientation = target.Orientation };
        return true;
    }
    public static bool IsAlreadyAtTarget(Assignment assignment, double toleranceM = 0.020)
    {
        if (assignment.Source == null || assignment.Target == null) return false;
        double dx = assignment.Source.X - assignment.Target.WorldX;
        double dy = assignment.Source.Y - assignment.Target.WorldY;
        return Math.Sqrt(dx * dx + dy * dy) <= toleranceM &&
               assignment.Source.Shape == assignment.Target.ExpectedShape &&
               assignment.Source.Name.Contains(assignment.Target.ExpectedColor,
                   StringComparison.OrdinalIgnoreCase);
    }
    static double Distance(SceneObject a, SceneObject b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
