using System.Text.Json;
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
SceneObject Piece(double x) => new() { Name = "yellow_cube", Shape = "cube", X = x, Y = 0.1, Z = 0.025 };
var baseline = new List<SceneObject> { Piece(0.1), Piece(0.15) };
Check(ExperimentChecks.Matches(baseline, new[] { Piece(0.15), Piece(0.1) }), "reset matching ignores detection order");
Check(!ExperimentChecks.Matches(baseline, new[] { Piece(0.1), Piece(0.1) }), "duplicate detection cannot satisfy two baseline pieces");
Check(!ExperimentChecks.Matches(baseline, new[] { Piece(0.1), Piece(0.2) }), "changed state fails reset gate");
var moved = ExperimentChecks.DescribeMismatch(baseline, new[] { Piece(0.1), Piece(0.25) });
Check(moved.Count == 2 && moved.Count(l => l.StartsWith("符合")) == 1 &&
      moved.Any(l => l.StartsWith("不符") && l.Contains("XY 差 100 mm") && l.Contains("往左（QR1 側） 10.0 cm")),
      "reset mismatch reports the moved piece, its distance and which way to move it");
Check(ExperimentChecks.DescribeMismatch(baseline, new[] { Piece(0.15), Piece(0.1) }).All(l => l.StartsWith("符合")),
      "reset mismatch reports every piece as matching when the gate would pass");
var stackScene = new List<SceneObject> { Piece(0.1), new() { Name = "black_domino", Shape = "domino", X = 0.3, Y = 0.1, Z = 0.001, Orientation = "horizontal" } };
var stackStep = new TranslatedStep { SourceIndex = 0, Actions = new() { new() { Function = "grasp" } },
    Target = new SceneObject { Name = "black_domino", Shape = "domino", X = 0.3, Y = 0.1, Z = 0.026 } };
ExperimentChecks.FillTargetIdentity(new[] { stackStep }, stackScene);
Check(stackStep.Target!.Name == "yellow_cube" && stackStep.Target.Shape == "cube",
      "target identity is filled from the source even when the translator names the object underneath");
var lowStep = new TranslatedStep { SourceIndex = 0, Actions = stackStep.Actions, Target = new SceneObject { X = 0.3, Y = 0.1, Z = -0.004 } };
ExperimentChecks.FillTargetIdentity(new[] { lowStep }, stackScene);
Check(!ExperimentChecks.Resolve(lowStep, stackScene, stackScene, out _, out var lowError) &&
      lowError.Contains("target.z=-0.0040") && !lowError.Contains("身分") && !lowError.Contains("0.02"),
      "non-positive target z is reported as a fact without suggesting a value");
var badIndex = new TranslatedStep { SourceIndex = 5, Actions = stackStep.Actions, Target = new SceneObject { X = 0.3, Y = 0.1, Z = 0.026 } };
Check(!ExperimentChecks.Resolve(badIndex, stackScene, stackScene, out _, out var indexError) && indexError.Contains("source_index=5"),
      "out-of-range source index names the offending value");
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
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = actions }, assignment, baseline, out _), "explicit safe plan accepted");
var direct = new List<RobotFunctionCall> { new() { Function = "grasp" } };
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = direct }, assignment, baseline, out _), "validator does not prescribe a grasp recipe");
var holdingActions = actions.Take(4).ToList();
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = holdingActions }, assignment, baseline, out _), "plan may finish while holding without a category");
var unsafeHome = new List<RobotFunctionCall> { new() { Function = "grasp" }, new() { Function = "go_home" } };
Check(!MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = unsafeHome }, assignment, baseline, out _), "unsafe home motion while holding rejected");
Check(Verifier.CheckExecutionOnly(assignment).OverallStatus == "ok", "arm-only batch does not claim source movement failure");
// Program.cs 寫 current_step.json 用的同一組選項
var batchJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
Check(!JsonSerializer.Serialize(new BatchEnvelope { BatchId = 7 }, batchJson).Contains("layered_grasp"),
      "2D batch JSON has no layered_grasp field, so Unity receives the same text as before");
Check(JsonSerializer.Serialize(new BatchEnvelope { BatchId = 7, LayeredGrasp = true }, batchJson).Contains("\"layered_grasp\": true"),
      "3D batch JSON carries layered_grasp");
bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
Check(new[] { -0.0075, 0.0015, 0.013, 0.0179, 0.0277, 0.029 }.All(z => Near(LayeredGraspGeometry.SnapTopToLayer(z), 0.025)),
      "every table-level top seen so far (black -32..+3 mm, yellow -7..+3 mm) snaps to layer 1");
Check(new[] { 0.038, 0.042, 0.047, 0.050, 0.0301 }.All(z => Near(LayeredGraspGeometry.SnapTopToLayer(z), 0.050)),
      "second-layer tops and LLM targets built from perceived z snap to layer 2");
Check(Near(LayeredGraspGeometry.SnapTopToLayer(0.0299), 0.025) && Near(LayeredGraspGeometry.SnapTopToLayer(0.0551), 0.075),
      "layer window is true top -20 mm to +5 mm");
Check(!JsonSerializer.Serialize(new StepEnvelope { StepId = 3 }, batchJson).Contains("_top_m") &&
      JsonSerializer.Serialize(new StepEnvelope { StepId = 3, SourceTopM = 0.025, TargetTopM = 0.05 }, batchJson).Contains("\"target_top_m\": 0.05"),
      "block top heights are only written for 3D steps");
SceneObject Block(string name, double x, double y, double z, string? orientation = null) =>
    new() { Name = name, Shape = name.EndsWith("domino") ? "domino" : "cube", X = x, Y = y, Z = z, Orientation = orientation };
var pickPlace = actions;   // move_above / descend / grasp / lift / move_above / descend / release / lift
TranslatedStep Move(int source, double x, double y, double z, List<RobotFunctionCall>? acts = null, string? orientation = null) =>
    new() { SourceIndex = source, Target = new SceneObject { X = x, Y = y, Z = z, Orientation = orientation }, Actions = acts ?? pickPlace };
// 上次失敗的任務：黑色 domino（感知頂面 17 mm）放到黃色 cube（22 mm）上，LLM 給 target z = 0.047
var lastRun = new List<SceneObject> { Block("black_domino", 0.1679, 0.0342, 0.0173, "horizontal"), Block("yellow_cube", 0.1513, 0.1194, 0.0223) };
var h = LayeredHeights.ForSteps(new[] { Move(0, 0.1513, 0.1194, 0.0473, orientation: "horizontal") }, lastRun)[0];
Check(Near(h.SourceTopM, 0.025) && Near(h.TargetTopM, 0.050), "failed task: pick the domino at 25 mm, place it with its top at 50 mm");
// 黑色支撐感知頂面只有 1.5 mm：LLM 照感知值給 0.0265，單看 z 會對到第 1 層、壓進支撐
var blackSupport = new List<SceneObject> { Block("yellow_cube", 0.30, 0.10, 0.022), Block("black_cube", 0.20, 0.20, 0.0015) };
Check(Near(LayeredHeights.ForSteps(new[] { Move(0, 0.201, 0.198, 0.0265) }, blackSupport)[0].TargetTopM, 0.050),
      "a support under the target lifts the placement one layer even when the LLM z points at the table");
Check(Near(LayeredHeights.ForSteps(new[] { Move(0, 0.225, 0.20, 0.022) }, blackSupport)[0].TargetTopM, 0.025),
      "a block placed flush beside another block stays on the table");
Check(Near(LayeredHeights.ForSteps(new[] { Move(0, 0.20, 0.20, 0.075) }, blackSupport)[0].TargetTopM, 0.075),
      "the LLM z may raise the release height but never lowers it into a support");
var twoSteps = LayeredHeights.ForSteps(new[] { Move(0, 0.40, 0.25, 0.022), Move(1, 0.40, 0.25, 0.05) }, blackSupport);
Check(Near(twoSteps[0].TargetTopM, 0.025) && Near(twoSteps[1].SourceTopM, 0.025) && Near(twoSteps[1].TargetTopM, 0.050),
      "a block placed by an earlier step in the same batch supports a later step");
var holdThenPlace = LayeredHeights.ForSteps(new[] { Move(0, 0.40, 0.25, 0.022, actions.Take(4).ToList()), Move(1, 0.40, 0.25, 0.022) }, blackSupport);
Check(Near(holdThenPlace[1].TargetTopM, 0.025), "a block still held at the end of a step does not support later steps");
var overlapping = LayeredHeights.SceneTops(new List<SceneObject> { Block("yellow_cube", 0.30, 0.10, 0.022), Block("black_domino", 0.305, 0.10, 0.018, "horizontal") });
Check(Near(overlapping.Values.Max(), 0.050) && Near(overlapping.Values.Min(), 0.025),
      "two overlapping blocks are stacked (never interpenetrating) even when the upper one reads lower");
var fingerRoot = LayeredGraspGeometry.FingerRoot(new[] { 0.1, 0.2, 0.3 }, new[] { 0.1, 0.2, 0.006 });
Check(Near(fingerRoot[0], 0.1) && Near(fingerRoot[1], 0.2) && Near(fingerRoot[2], 0.036), "finger root is 30 mm up the tool axis from the fingertip");
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
