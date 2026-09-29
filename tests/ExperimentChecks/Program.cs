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
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = actions }, assignment, baseline, out _), "explicit safe plan accepted");
var direct = new List<RobotFunctionCall> { new() { Function = "grasp" } };
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = direct }, assignment, baseline, out _), "validator does not prescribe a grasp recipe");
var holdingActions = actions.Take(4).ToList();
Check(MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = holdingActions }, assignment, baseline, out _), "plan may finish while holding without a category");
var unsafeHome = new List<RobotFunctionCall> { new() { Function = "grasp" }, new() { Function = "go_home" } };
Check(!MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = unsafeHome }, assignment, baseline, out _), "unsafe home motion while holding rejected");
Check(Verifier.CheckExecutionOnly(assignment).OverallStatus == "ok", "arm-only batch does not claim source movement failure");
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
