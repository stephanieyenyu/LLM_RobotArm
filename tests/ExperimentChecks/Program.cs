using System.Text.Json;
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
SceneObject Piece(double x) => new() { Name = "yellow_cube", Shape = "cube", X = x, Y = 0.1, Z = 0.025 };
var baseline = new List<SceneObject> { Piece(0.1), Piece(0.15) };
Check(ExperimentChecks.Matches(baseline, new[] { Piece(0.15), Piece(0.1) }), "reset matching ignores detection order");
Check(!ExperimentChecks.Matches(baseline, new[] { Piece(0.1), Piece(0.1) }), "duplicate detection cannot satisfy two baseline pieces");
Check(!ExperimentChecks.Matches(baseline, new[] { Piece(0.1), Piece(0.2) }), "changed state fails reset gate");
Check(!ExperimentChecks.Matches(baseline, new[] { Piece(0.1) }), "missing piece fails reset gate");
Check(!ExperimentChecks.Matches(Array.Empty<SceneObject>(), Array.Empty<SceneObject>()), "empty table cannot establish baseline");
var actions = new List<RobotFunctionCall> {
    new() { Function = "move_above", Location = "source", HeightM = 0.1 }, new() { Function = "descend", Location = "source" }, new() { Function = "grasp" },
    new() { Function = "lift", Location = "source", HeightM = 0.1 }, new() { Function = "move_above", Location = "target", HeightM = 0.1 },
    new() { Function = "descend", Location = "target" }, new() { Function = "release" }, new() { Function = "lift", Location = "target", HeightM = 0.1 }
};
var step = new TranslatedStep { SourceIndex = 0, Target = Piece(0.4), Actions = actions };
Check(ExperimentChecks.Resolve(step, baseline, baseline, out var assignment, out _), "explicit source resolves from latest scene");
var drifted = new[] { Piece(0.123), Piece(0.15) };
Check(ExperimentChecks.Resolve(step, baseline, drifted, out var driftedAssignment, out _) &&
      Math.Abs(driftedAssignment.Source!.X - 0.123) < 0.0001,
      "source tracking tolerates perception drift and selects nearest object");
var ambiguous = new[] { Piece(0.090), Piece(0.111) };
Check(!ExperimentChecks.Resolve(step, baseline, ambiguous, out _, out _),
      "source tracking refuses an ambiguous nearest neighbour");
Check(!ExperimentChecks.Resolve(step, baseline, new[] { Piece(0.4), Piece(0.15) }, out _, out _), "moved source is not silently reassigned");
var nearTargetAssignment = new Assignment
{
    StepId = 99,
    Source = new SceneObject { Name = "yellow_cube", Shape = "cube", X = 0.495, Y = 0.121, Z = 0.018 },
    Target = new TargetCell { WorldX = 0.500, WorldY = 0.124, WorldZ = 0.010,
        ExpectedShape = "cube", ExpectedColor = "yellow" }
};
Check(ExperimentChecks.IsAlreadyAtTarget(nearTargetAssignment),
      "source within 20 mm of target is already satisfied");
nearTargetAssignment.Target.WorldX = 0.530;
Check(!ExperimentChecks.IsAlreadyAtTarget(nearTargetAssignment),
      "source beyond 20 mm still requires motion");
nearTargetAssignment.Target.WorldX = 0.500;
var nearTargetAfter = new List<SceneObject>
{
    new() { Name = "yellow_cube", Shape = "cube", X = 0.496, Y = 0.123, Z = 0.019 }
};
Check(Verifier.CheckSingleObjectStep(nearTargetAssignment,
          new List<SceneObject> { nearTargetAssignment.Source }, nearTargetAfter, false).OverallStatus == "ok",
      "overlapping source and target regions do not report unexpected scene change");
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = actions }, assignment, baseline, out _), "explicit safe plan accepted");
var direct = new List<RobotFunctionCall> { new() { Function = "grasp" } };
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = direct }, assignment, baseline, out _), "validator does not prescribe a grasp recipe");
var holdingActions = actions.Take(4).ToList();
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = holdingActions }, assignment, baseline, out _), "plan may finish while holding without a category");
var unsafeHome = new List<RobotFunctionCall> { new() { Function = "grasp" }, new() { Function = "go_home" } };
Check(!MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = unsafeHome }, assignment, baseline, out _), "unsafe home motion while holding rejected");
Check(Verifier.CheckExecutionOnly(assignment).OverallStatus == "ok", "arm-only batch does not claim source movement failure");
var naturalScene = new List<SceneObject> { new() {
    Name = "yellow_cube", Shape = "cube", X = 0.146329, Y = 0.117252, Z = 0.019623
} };
var naturalPlan = """
來源：index 0
目標位置：(0.246329, 0.117252)
執行路徑開始
- move_above(source, 0.12)
- descend(source)
- grasp()
- lift(source, 0.12)
- move_above(target, 0.12)
- descend(target)
- release()
執行路徑結束
""";
var natural = NaturalLanguagePlanAdapter.Translate(naturalPlan, naturalScene);
Check(string.IsNullOrEmpty(natural.Error) && natural.Steps.Count == 1 &&
      Math.Abs(natural.Steps[0].Target!.X - 0.246329) < 0.000001 &&
      Math.Abs(natural.Steps[0].Target!.Z - 0.019623) < 0.000001,
      "natural language adapter preserves source z for tabletop target");
Check(natural.Steps[0].Actions.Select(a => a.Function).SequenceEqual(
      new[] { "move_above", "descend", "grasp", "lift", "move_above", "descend", "release" }),
      "natural language adapter preserves explicit action order");
var prosePlan = """
目標物：yellow_cube（source）= (0.146329, 0.117252, 0.019623) m
目標位置 target = (0.246329, 0.117252) m；安全高度 z=0.12 m
操作序列
- move_above(source, 0.12)
- descend(source)
- grasp()
- lift(source, 0.12)
- move_above(target, 0.12)
- descend(target)
- release()
""";
var prose = NaturalLanguagePlanAdapter.Translate(prosePlan,
    new List<SceneObject> { naturalScene[0], new() { Name = "black_domino", Shape = "domino", X = 0.15, Y = 0.03, Z = 0.02 } });
Check(string.IsNullOrEmpty(prose.Error) &&
      Math.Abs(prose.Steps[0].Target!.Z - naturalScene[0].Z) < 0.000001,
      "natural language adapter does not confuse safe height with target z");
var labeledTargetPlan = """
來源 index: 0（名稱：yellow_cube）
目標 Ptgt: x=0.246515, y=0.118027, z=0.022079
執行路徑開始
move_above(source, 0.10)
descend(source)
grasp()
lift(source, 0.10)
move_above(target, 0.10)
descend(target)
release()
執行路徑結束
""";
var labeledTarget = NaturalLanguagePlanAdapter.Translate(labeledTargetPlan, naturalScene);
Check(string.IsNullOrEmpty(labeledTarget.Error) &&
      Math.Abs(labeledTarget.Steps[0].Target!.X - 0.246515) < 0.000001 &&
      Math.Abs(labeledTarget.Steps[0].Target!.Y - 0.118027) < 0.000001 &&
      Math.Abs(labeledTarget.Steps[0].Target!.Z - 0.022079) < 0.000001,
      "natural language adapter accepts labeled target coordinates");
var missingTarget = NaturalLanguagePlanAdapter.Translate(
    "來源：index 0\n執行路徑開始\nmove_above(target, 0.12)\n執行路徑結束", naturalScene);
Check(!string.IsNullOrEmpty(missingTarget.Error), "natural language adapter refuses missing target coordinates");
var ambiguousScene = new List<SceneObject> { Piece(0.1), Piece(0.2) };
var ambiguousPlan = NaturalLanguagePlanAdapter.Translate(
    "yellow_cube\n執行路徑開始\ngrasp()\n執行路徑結束", ambiguousScene);
Check(!string.IsNullOrEmpty(ambiguousPlan.Error), "natural language adapter refuses ambiguous source");
var multiScene = new List<SceneObject> { Piece(0.10), Piece(0.20), Piece(0.30) };
var multiPlan = """
第 1 顆：source index 1 → target (0.604, 0.154, 0.025)
第 2 顆：source index 2 → target (0.604, 0.184, 0.025)
執行路徑開始
move_above(source, 0.08)
descend(source)
grasp()
lift(source, 0.08)
move_above(target, 0.08)
descend(target)
release()
lift(target, 0.08)
move_above(source, 0.08)
descend(source)
grasp()
lift(source, 0.08)
move_above(target, 0.08)
descend(target)
release()
lift(target, 0.08)
執行路徑結束
""";
var multi = NaturalLanguagePlanAdapter.Translate(multiPlan, multiScene);
Check(string.IsNullOrEmpty(multi.Error) && multi.Steps.Count == 2 &&
      multi.Steps[0].SourceIndex == 1 && multi.Steps[1].SourceIndex == 2 &&
      Math.Abs(multi.Steps[0].Target!.Y - 0.154) < 0.000001 &&
      Math.Abs(multi.Steps[1].Target!.Y - 0.184) < 0.000001 &&
      multi.Steps.All(x => x.Actions.Count == 8),
      "natural language adapter preserves multiple source-target pairs");
var missingPair = NaturalLanguagePlanAdapter.Translate(
    multiPlan.Replace("第 2 顆：source index 2 → target (0.604, 0.184, 0.025)\n", ""), multiScene);
Check(!string.IsNullOrEmpty(missingPair.Error),
      "natural language adapter refuses unmatched multi-step actions");
var root = Path.Combine(Path.GetTempPath(), "robot_metrics_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Outcome(string id, bool success, string status, int attempts) {
    var dir = Path.Combine(root, id); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "result.json"), JsonSerializer.Serialize(new { success, status, attempts }));
}
Outcome("a", true, "success", 2); Outcome("b", false, "failed", 10); Outcome("c", false, "infrastructure_error", 1); Outcome("d", false, "adapter_error", 0);
ExperimentMetrics.Write(root);
using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "metrics.json")));
Check(metrics.RootElement.GetProperty("success_rate_within_10").GetDouble() == 0.5, "failure remains in success denominator");
Check(metrics.RootElement.GetProperty("infrastructure_errors").GetInt32() == 1, "infrastructure error separately counted");
Check(metrics.RootElement.GetProperty("adapter_errors").GetInt32() == 1, "adapter error separately counted");
Check(metrics.RootElement.GetProperty("first_attempt_success_rate").GetDouble() == 0, "first-attempt success measured independently");
Console.WriteLine($"{passed} checks passed; metrics fixture: {root}");
public sealed class TranslatedStep
{
    public int SourceIndex { get; set; }
    public SceneObject? Target { get; set; }
    public List<RobotFunctionCall> Actions { get; set; } = new();
}
public sealed class TranslatedPlan
{
    public string Error { get; set; } = "";
    public List<TranslatedStep> Steps { get; set; } = new();
}
