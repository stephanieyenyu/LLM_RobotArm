public static class ExperimentChecks
{
    // Perception coordinates fluctuate between consecutive frames.  Keep the
    // selected object identity by nearest-neighbour association, but refuse to
    // guess when two same-kind objects are similarly close.
    const double SourceTrackingRadiusM = 0.035;
    const double SourceUniquenessMarginM = 0.010;
    const double SourceHeightToleranceM = 0.025;
    public static bool Matches(IReadOnlyList<SceneObject> expected, IReadOnlyList<SceneObject> actual)
    {
        if (expected.Count == 0 || expected.Count != actual.Count) return false;
        var owners = Enumerable.Repeat(-1, actual.Count).ToArray();
        bool Match(int i, bool[] visited)
        {
            for (int j = 0; j < actual.Count; j++)
            {
                var a = actual[j]; var e = expected[i];
                if (visited[j] || a.Name != e.Name || a.Shape != e.Shape || Distance(a, e) > 0.015 || Math.Abs(a.Z - e.Z) > 0.01 ||
                    (e.Shape == "domino" && (a.Orientation != e.Orientation || Math.Abs(a.SkewDeg - e.SkewDeg) > 10))) continue;
                visited[j] = true;
                if (owners[j] == -1 || Match(owners[j], visited)) { owners[j] = i; return true; }
            }
            return false;
        }
        return Enumerable.Range(0, expected.Count).All(i => Match(i, new bool[actual.Count]));
    }
    public static bool Resolve(TranslatedStep step, IReadOnlyList<SceneObject> plannedScene, IReadOnlyList<SceneObject> current, out Assignment assignment, out string error)
    {
        assignment = new Assignment(); error = "";
        if (step.SourceIndex < 0 || step.SourceIndex >= plannedScene.Count || step.Target == null || step.Actions == null || step.Actions.Count == 0)
        { error = "轉譯資料缺少來源、目標或操作。"; return false; }
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
            error = "目前觀測不到原先選定的來源；可能是感知暫時遺漏或場景已改變。";
            return false;
        }
        if (candidates.Count > 1 &&
            candidates[1].Distance - candidates[0].Distance < SourceUniquenessMarginM)
        {
            error = "目前有多個同類來源與原位置同樣接近，無法安全判定原先選定的物件。";
            return false;
        }
        var target = step.Target;
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(target.Z) || target.Z <= 0 || target.Shape != source.Shape || target.Name != source.Name)
        { error = "轉譯目標座標或物件身分不合法。"; return false; }
        assignment.Source = candidates[0].Object;
        assignment.Target = new TargetCell { WorldX = target.X, WorldY = target.Y, WorldZ = target.Z, ExpectedShape = source.Shape,
            ExpectedColor = source.Name.Split('_')[0], ExpectedOrientation = target.Orientation };
        return true;
    }
    static double Distance(SceneObject a, SceneObject b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
