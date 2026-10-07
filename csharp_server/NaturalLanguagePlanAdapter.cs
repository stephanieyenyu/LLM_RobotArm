using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Deterministic adapter from the planner's natural-language execution block to
/// the bounded internal contract used by Unity.  It never chooses an action,
/// object, or destination; missing decisions are reported as adapter errors.
/// </summary>
public static class NaturalLanguagePlanAdapter
{
    static readonly Regex Action = new(
        @"\b(move_above|descend|grasp|release|lift|wait)\s*\(([^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ExplicitIndex = new(
        @"(?:source|來源)\s*(?::=|=|:|：)?\s*(?:index\s*(?:=|:|：)?\s*)?(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex NamedIndex = new(
        @"index\s*(?:=|:|：)?\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    const string Number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)";

    public static TranslatedPlan Translate(string plan, IReadOnlyList<SceneObject> scene)
    {
        if (string.IsNullOrWhiteSpace(plan)) return Error("自然語言計畫是空的。");
        if (scene.Count == 0) return Error("目前場景沒有可選來源。");

        string execution = ExecutionBlock(plan);
        var matches = Action.Matches(execution);
        if (matches.Count == 0)
            return Error("找不到執行函式。請在執行路徑區塊逐行寫出函式呼叫。");
        var groups = SplitActionGroups(matches);
        var pairs = ExplicitPairs(plan).ToList();
        if (groups.Count > 1 && pairs.Count != groups.Count)
            return Error($"執行路徑含 {groups.Count} 組抓放，但只找到 {pairs.Count} 組明確的 source index 與 target 座標配對。");

        var translated = new TranslatedPlan();
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            // 上限不寫在訊息裡（2026-10-07 起規劃與反思都不給函式數上限）
            if (group.Count > 20) return Error($"第 {groupIndex + 1} 個步驟的函式數超過介面上限。");

            int sourceIndex;
            SceneObject target;
            if (groups.Count > 1)
            {
                var pair = pairs[groupIndex];
                sourceIndex = pair.SourceIndex;
                if (sourceIndex < 0 || sourceIndex >= scene.Count)
                    return Error($"第 {groupIndex + 1} 組來源 index {sourceIndex} 超出目前場景範圍。");
                target = Copy(scene[sourceIndex]);
                target.X = pair.X;
                target.Y = pair.Y;
                target.Z = pair.Z ?? scene[sourceIndex].Z;
            }
            else
            {
                if (!TrySourceIndex(plan, scene, out sourceIndex, out string sourceError))
                    return Error(sourceError);
                var source = scene[sourceIndex];
                bool usesTarget = group.Any(match =>
                    Regex.IsMatch(match.Groups[2].Value, @"\btarget\b", RegexOptions.IgnoreCase));
                if (usesTarget)
                {
                    if (!TryTarget(plan, source, out target, out string targetError)) return Error(targetError);
                }
                else target = Copy(source);
            }

            if (!TryActions(group, out var actions, out string actionError)) return Error(actionError);
            translated.Steps.Add(new TranslatedStep
            {
                SourceIndex = sourceIndex,
                Target = target,
                Actions = actions
            });
        }
        return translated;
    }

    static List<List<Match>> SplitActionGroups(MatchCollection matches)
    {
        var groups = new List<List<Match>> { new() };
        bool completedPlacement = false;
        foreach (Match match in matches)
        {
            string function = match.Groups[1].Value.ToLowerInvariant();
            string arguments = match.Groups[2].Value;
            bool beginsNextPick = function == "move_above" &&
                Regex.IsMatch(arguments, @"^\s*source\b", RegexOptions.IgnoreCase) &&
                completedPlacement;
            if (beginsNextPick)
            {
                groups.Add(new List<Match>());
                completedPlacement = false;
            }
            groups[^1].Add(match);
            if (function == "release") completedPlacement = true;
        }
        return groups;
    }

    static IEnumerable<(int SourceIndex, double X, double Y, double? Z)> ExplicitPairs(string plan)
    {
        var lines = plan.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            var source = Regex.Match(line,
                @"(?:source[_ ]?index|source\s+index|來源\s*index)\s*(?:=|:|：)?\s*(\d+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!source.Success) continue;
            var target = Regex.Match(line,
                $@"(?:target|目標)[^\r\n]*?[\[(（]\s*({Number})\s*[,，]\s*({Number})(?:\s*[,，]\s*({Number}))?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!target.Success) continue;
            if (!int.TryParse(source.Groups[1].Value, out int sourceIndex) ||
                !TryNumber(target.Groups[1].Value, out double x) ||
                !TryNumber(target.Groups[2].Value, out double y)) continue;
            double? z = target.Groups[3].Success && TryNumber(target.Groups[3].Value, out double parsedZ)
                ? parsedZ : null;
            string key = $"{sourceIndex}:{x:R}:{y:R}:{z:R}";
            if (seen.Add(key)) yield return (sourceIndex, x, y, z);
        }
    }

    static bool TryActions(IEnumerable<Match> matches, out List<RobotFunctionCall> actions, out string error)
    {
        actions = new List<RobotFunctionCall>();
        foreach (Match match in matches)
        {
            string function = match.Groups[1].Value.ToLowerInvariant();
            string[] args = match.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var call = new RobotFunctionCall { Function = function };
            switch (function)
            {
                case "move_above":
                case "lift":
                    if (args.Length != 2 || !TryLocation(args[0], out string? elevatedLocation) ||
                        !TryNumber(args[1], out double height))
                    { error = $"{function} 必須明確寫成 {function}(source或target, 數值高度)。"; return false; }
                    call.Location = elevatedLocation;
                    call.HeightM = height;
                    break;
                case "descend":
                    if (args.Length != 1 || !TryLocation(args[0], out string? descendLocation))
                    { error = "descend 必須明確寫成 descend(source) 或 descend(target)。"; return false; }
                    call.Location = descendLocation;
                    break;
                case "wait":
                    if (args.Length != 1 || !TryNumber(args[0], out double seconds))
                    { error = "wait 必須包含數值秒數。"; return false; }
                    call.Seconds = seconds;
                    break;
                default:
                    if (args.Length != 0) { error = $"{function} 不接受參數。"; return false; }
                    break;
            }
            actions.Add(call);
        }
        error = "";
        return true;
    }

    // 所有「執行路徑開始…執行路徑結束」區塊依序接起來：每組抓放各寫一個區塊、或全部寫在同一個區塊都照實轉譯
    // （2026-10-07 之前只讀最後一個區塊，每組各寫一個區塊時其他組會被默默丟掉）。每個區塊從「結束」前面最近的
    // 「開始」算起，文字裡提到「執行路徑開始」的說明不會被當成區塊；最後一個「開始」沒有「結束」時讀到結尾，都沒有就讀全文
    static string ExecutionBlock(string plan)
    {
        const string Begin = "執行路徑開始", End = "執行路徑結束";
        var blocks = new List<string>();
        int position = 0;
        while (true)
        {
            int end = plan.IndexOf(End, position, StringComparison.Ordinal);
            if (end < 0) break;
            int start = plan.LastIndexOf(Begin, end, end - position + 1, StringComparison.Ordinal);
            if (start >= position) blocks.Add(plan[(start + Begin.Length)..end]);
            position = end + End.Length;
        }
        int open = plan.IndexOf(Begin, position, StringComparison.Ordinal);
        if (open >= 0) blocks.Add(plan[(plan.LastIndexOf(Begin, StringComparison.Ordinal) + Begin.Length)..]);
        return blocks.Count > 0 ? string.Join("\n", blocks) : plan;
    }

    static bool TrySourceIndex(string plan, IReadOnlyList<SceneObject> scene,
        out int index, out string error)
    {
        index = -1;
        Match m = ExplicitIndex.Match(plan);
        if (!m.Success) m = NamedIndex.Match(plan);
        if (m.Success && int.TryParse(m.Groups[1].Value, out index))
        {
            if (index >= 0 && index < scene.Count) { error = ""; return true; }
            error = $"來源 index {index} 超出目前場景範圍。";
            return false;
        }

        foreach (var candidate in scene.Select((item, i) => (item, i)))
        {
            string name = Regex.Escape(candidate.item.Name);
            bool explicitlySource = Regex.IsMatch(plan,
                $@"(?:\b{name}\b\s*[（(]\s*source\s*[)）]|(?:source|來源)\s*(?::=|=|:|：)\s*\b{name}\b)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!explicitlySource) continue;
            var sameName = scene.Select((item, i) => (item, i))
                .Where(x => x.item.Name == candidate.item.Name).ToList();
            if (sameName.Count == 1) { index = sameName[0].i; error = ""; return true; }
            error = $"來源名稱 {candidate.item.Name} 對應多個物件，請明確寫出當輪 index。";
            return false;
        }

        var named = scene.Select((item, i) => (item, i))
            .Where(x => Regex.IsMatch(plan, $@"\b{Regex.Escape(x.item.Name)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        if (named.Count == 1) { index = named[0].i; error = ""; return true; }

        error = "無法唯一判定來源。請在自然語言計畫中明確寫出當輪來源 index。";
        return false;
    }

    static bool TryTarget(string plan, SceneObject source, out SceneObject target, out string error)
    {
        target = Copy(source);
        bool hasX = TryNamedCoordinate(plan, "x", out double x);
        bool hasY = TryNamedCoordinate(plan, "y", out double y);
        bool hasZ = TryNamedCoordinate(plan, "z", out double z);

        if (!hasX || !hasY)
        {
            // Natural prose such as:
            //   目標 Ptgt: x=0.246, y=0.118, z=0.022
            // The target label may contain a human-readable alias before the
            // numeric coordinates.
            var labeledTarget = Regex.Match(plan,
                $@"(?:target|目標)[^\r\n]{{0,48}}?\bx\s*(?:=|:|：)\s*({Number})[^\r\n]{{0,48}}?\by\s*(?:=|:|：)\s*({Number})(?:[^\r\n]{{0,48}}?\bz\s*(?:=|:|：)\s*({Number}))?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (labeledTarget.Success)
            {
                hasX = TryNumber(labeledTarget.Groups[1].Value, out x);
                hasY = TryNumber(labeledTarget.Groups[2].Value, out y);
                if (labeledTarget.Groups[3].Success)
                    hasZ = TryNumber(labeledTarget.Groups[3].Value, out z);
            }
        }

        if (!hasX || !hasY)
        {
            var targetTuple = Regex.Match(plan,
                $@"(?:target|目標(?:位置|位姿|座標|點)?)\s*(?::=|=|:|：)?\s*[\[(（]\s*({Number})\s*[,，]\s*({Number})(?:\s*[,，]\s*({Number}))?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (targetTuple.Success)
            {
                hasX = TryNumber(targetTuple.Groups[1].Value, out x);
                hasY = TryNumber(targetTuple.Groups[2].Value, out y);
                if (targetTuple.Groups[3].Success) hasZ = TryNumber(targetTuple.Groups[3].Value, out z);
            }
        }

        if (!hasX || !hasY)
        {
            error = "計畫使用 target，但沒有明確提供數值 x 與 y；本地轉譯器不會猜測目的地。";
            return false;
        }

        target.X = x;
        target.Y = y;
        // A tabletop translation preserves the observed object's top height
        // unless the planner explicitly chose another numeric target z.
        target.Z = hasZ ? z : source.Z;
        error = "";
        return true;
    }

    static bool TryNamedCoordinate(string plan, string axis, out double value)
    {
        var matches = Regex.Matches(plan,
            $@"(?:\b{axis}_target\b|\btarget[._]{axis}\b|目標\s*{axis})\s*(?:=|:|：)\s*({Number})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0) { value = 0; return false; }
        return TryNumber(matches[^1].Groups[1].Value, out value);
    }

    static bool TryLocation(string text, out string? location)
    {
        string value = text.Trim().ToLowerInvariant();
        if (value is "source" or "target") { location = value; return true; }
        location = null;
        return false;
    }

    static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    static SceneObject Copy(SceneObject source) => new()
    {
        Name = source.Name, X = source.X, Y = source.Y, Z = source.Z,
        Shape = source.Shape, Orientation = source.Orientation, SkewDeg = source.SkewDeg
    };

    static TranslatedPlan Error(string message) => new() { Error = message };
}
