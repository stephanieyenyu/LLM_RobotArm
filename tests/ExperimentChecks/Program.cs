using System.Text.Json;
if (args.Contains("--ursim-ik-smoke"))
{
    using var diagnosticStop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var q = UrSimIkClient.Solve("192.168.50.221", new[] { 0.1, -0.3, 0.25, 0.0, Math.PI, 0.0 },
        new[] { 0.0, -1.57, 1.57, -1.57, -1.57, 0.0 }, 0.123, diagnosticStop.Token);
    Console.WriteLine("URSim IK (no motion): " + string.Join(", ", q));
    var rotated = UrSimIkClient.Solve("192.168.50.221", new[] { 0.1, -0.3, 0.25, 0.2, 3.0, 0.1 },
        q, 0.123, diagnosticStop.Token);
    Console.WriteLine("URSim arbitrary TCP direction (no motion): " + string.Join(", ", rotated));
    try
    {
        UrSimIkClient.Solve("192.168.50.221", new[] { 9.0, 9.0, 9.0, 0.0, Math.PI, 0.0 },
            q, 0.123, diagnosticStop.Token);
        throw new Exception("Unreachable TCP unexpectedly accepted.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("無 IK 解"))
    { Console.WriteLine("URSim unreachable TCP rejected (no local IK fallback)."); }
    return;
}
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
SceneObject Piece(double x) => new() { Name = "yellow_cube", Shape = "cube", X = x, Y = 0.1, Z = 0.025 };
var baseline = new List<SceneObject> { Piece(0.1), Piece(0.15) };
var tcpAction = JsonSerializer.Deserialize<RobotFunctionCall>("""
    {"function":"descend","location":"target","tcp_pose":{"x":0.55,"y":0.3,"z":0.045,"rx":-1.2,"ry":2.1,"rz":0.7}}
    """);
var tcpWire = JsonSerializer.Serialize(tcpAction);
var tcpRoundtrip = JsonSerializer.Deserialize<RobotFunctionCall>(tcpWire)!;
Check(tcpRoundtrip.TcpPose!.Z == 0.045 && tcpRoundtrip.TcpPose.Rx == -1.2 &&
    tcpRoundtrip.TcpPose.Ry == 2.1 && tcpRoundtrip.TcpPose.Rz == 0.7,
    "LLM TCP position and arbitrary rotation vector survive the execution JSON transport");
bool missingTcpComponentRejected = false;
try { JsonSerializer.Deserialize<TcpPose>("{\"x\":0.1,\"y\":0.2,\"z\":0.045,\"rx\":0,\"ry\":3.14}"); }
catch (JsonException) { missingTcpComponentRejected = true; }
Check(missingTcpComponentRejected, "missing TCP direction component is rejected instead of becoming a programmed default");
var canonicalPlan = new DualBitmapPlan { Rows = new() { "■□", "■■" } };
var freeLayout = canonicalPlan.ForUnity("bitmap_grid(0.60, 0.15, 0.045, 0.035, 0.025)");
Check(freeLayout.Cells.Count == 3 && freeLayout.Cells[0].X == 0.60 && freeLayout.Cells[0].Y == 0.15,
    "bitmap placement uses the LLM-selected origin without a fixed placement zone");
Check(Math.Abs(freeLayout.Cells[2].X - 0.645) < 1e-9 && Math.Abs(freeLayout.Cells[2].Y - 0.115) < 1e-9 &&
    freeLayout.CellXM == 0.045 && freeLayout.CellYM == 0.035,
    "LLM independently chooses X and Y spacing while canonical row orientation is preserved");
Check(freeLayout.Rows.SequenceEqual(canonicalPlan.Rows) && !freeLayout.Cells.Any(c => c.Row == 0 && c.Col == 1),
    "Unity receives the original bitmap including its empty cell, independently of operation targets");
var otherLayout = canonicalPlan.ForUnity("bitmap_grid(0.1, 0.3, 0.06, 0.05, 0.025)");
Check(otherLayout.Cells[0].X == 0.1 && otherLayout.Cells[0].Y == 0.3 && otherLayout.Rows.SequenceEqual(freeLayout.Rows),
    "free placement accepts another location without changing selected bitmap");
bool missingFrameRejected = false;
try { canonicalPlan.ForUnity("no coordinate frame"); } catch (TranslationContractException) { missingFrameRejected = true; }
Check(missingFrameRejected, "missing coordinate frame is reported instead of inventing placement");
bool badSpacingRejected = false;
try { canonicalPlan.ForUnity("bitmap_grid(0.1, 0.3, 0, 0.05, 0.025)"); } catch (TranslationContractException) { badSpacingRejected = true; }
Check(badSpacingRejected, "invalid zero grid spacing is rejected as an interface error");
Check(!canonicalPlan.PlanningConstraint.Contains("X <") && !canonicalPlan.PlanningConstraint.Contains("Targets:"),
    "placement prompt does not impose supply zoning or fixed target coordinates");
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
// 一鍵切換純模擬 / 實機（Unity 寫 run_mode.json，csharp_server 每個任務讀一次）
var modeDir = Path.Combine(Path.GetTempPath(), "robot_mode_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(modeDir);
Check(!RunModeConfig.Load(modeDir).IsSim, "no run_mode.json means real hardware, exactly as before");
File.WriteAllText(Path.Combine(modeDir, RunModeConfig.FileName),
    "{\"mode\": \"sim\", \"scene\": \"sim_scenes/letter_blocks.json\", \"reset_each_task\": false, \"sim_perception_url\": \"http://localhost:6000/perception/\"}");
var simMode = RunModeConfig.Load(modeDir);
Check(simMode.IsSim && simMode.Scene == "sim_scenes/letter_blocks.json" && !simMode.ResetEachTask,
      "the file Unity writes (including its extra url field) selects simulation and the scene");
File.WriteAllText(Path.Combine(modeDir, RunModeConfig.FileName), "{\"mode\": \"simulation\"}");
bool rejected = false;
try { RunModeConfig.Load(modeDir); } catch (InvalidDataException) { rejected = true; }
Check(rejected, "an unknown mode is refused instead of guessed (a wrong guess could move the real arm)");
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var streamingAssets = Path.Combine(repoRoot, "unity_project", "Assets", "StreamingAssets");
foreach (var sceneFile in new[] { "two_cubes", "letter_blocks", "domino_cubes" })
{
    var config = new RunModeConfig { Mode = "sim", Scene = $"sim_scenes/{sceneFile}.json" };
    var simScene = SimScene.Load(config.ScenePath(streamingAssets));
    Check(simScene.Objects.Count > 0 && simScene.Objects.All(LayeredHeights.IsBlock),
          $"virtual scene {sceneFile}.json resolves from StreamingAssets to the repo and loads as blocks");
    Check(simScene.Camera is { ValueKind: JsonValueKind.Object } cameraJson &&
          cameraJson.TryGetProperty("intrinsics", out _) && cameraJson.TryGetProperty("pose_in_qr", out _),
          $"{sceneFile}.json without a camera gets the lab camera from sim_scenes/camera/default.json");
}
var badScene = new List<SceneObject> {
    new() { Name = "cup", Shape = "cube", X = 0.2, Y = 0.1, Z = 0.025 },
    new() { Name = "yellow_cube", Shape = "cube", X = 0.2, Y = 0.1, Z = 0 },
    new() { Name = "black_domino", Shape = "domino", X = 0.3, Y = 0.1, Z = 0.025 } };
Check(SimScene.Validate(badScene).Count == 3, "virtual scene validation names a non-block, a non-positive top and a domino without orientation");
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
    // 只刪這一行的文字、留下空行：raw string 的換行跟原始檔一樣，Windows checkout 成 CRLF 時結尾的 \n 會對不到
    multiPlan.Replace("第 2 顆：source index 2 → target (0.604, 0.184, 0.025)", ""), multiScene);
Check(!string.IsNullOrEmpty(missingPair.Error),
      "natural language adapter refuses unmatched multi-step actions");
// 2D 整批：計畫放下的物件畫成 bitmap（跟相機畫面同方向：上 = +Y、右 = +X），也是 Unity 比對的預期格
SceneObject Figure(double x, double y, string shape = "cube", string? orientation = null) =>
    new() { Name = shape == "domino" ? "black_domino" : "yellow_cube", Shape = shape, Orientation = orientation, X = x, Y = y, Z = 0.025 };
var lShape = FigureBitmap.Build(new[] { Figure(0.50, 0.20), Figure(0.50, 0.17), Figure(0.50, 0.14), Figure(0.53, 0.14) });
Check(Near(lShape.CellXM, 0.03) && Near(lShape.CellYM, 0.03) && lShape.Rows.SequenceEqual(new[] { "■□", "■□", "■■" }),
      "an L planned with 3 cm spacing draws as an upright L in camera orientation");
Check(lShape.Cells.Count == 4 && lShape.Cells.All(c => c.SecondRow == -1) && lShape.Cells[3] is { Row: 2, Col: 1 },
      "each planned cube becomes one expected cell at its grid position");
var withDomino = FigureBitmap.Build(new[] { Figure(0.40, 0.20), Figure(0.43, 0.20), Figure(0.475, 0.20, "domino", "horizontal") });
Check(withDomino.Rows.SequenceEqual(new[] { "■■■■" }) && withDomino.Cells[2] is { Row: 0, Col: 2, SecondRow: 0, SecondCol: 3 },
      "a horizontal domino fills two cells along X, the left one first");
var vertical = FigureBitmap.Build(new[] { Figure(0.40, 0.20, "domino", "vertical") });
Check(vertical.Rows.SequenceEqual(new[] { "■", "■" }) && vertical.Cells[0] is { Row: 0, SecondRow: 1 },
      "a vertical domino fills two cells along Y, the far (+Y) one first");
// 手指沿 X 開合，X 方向排得比 Y 疏時，X、Y 格距分開算：5 格寬的 T 照樣是 5 格寬
var wideT = FigureBitmap.Build(new[] { Figure(0.40, 0.20), Figure(0.45, 0.20), Figure(0.50, 0.20), Figure(0.55, 0.20), Figure(0.60, 0.20),
                                       Figure(0.50, 0.17), Figure(0.50, 0.14), Figure(0.50, 0.11), Figure(0.50, 0.08) });
Check(Near(wideT.CellXM, 0.05) && Near(wideT.CellYM, 0.03) &&
      wideT.Rows.SequenceEqual(new[] { "■■■■■", "□□■□□", "□□■□□", "□□■□□", "□□■□□" }),
      "a T spaced 5 cm along X and 3 cm along Y draws as a 5x5 T");
Check(FigureBitmap.LetterCount("用黃色積木排一個L") == 1 && FigureBitmap.LetterCount("排出CAT") == 3 &&
      FigureBitmap.LetterCount("排一個字母") == 1 && FigureBitmap.LetterCount("把 yellow_cube 移到 QR1 旁邊") == 0 &&
      FigureBitmap.LetterCount("用方塊排一個 3x3 的正方形") == 0 && FigureBitmap.LetterCount("把方塊疊成 3D 的塔") == 0,
      "letter goals are recognised from standalone capital letters or the word 字母, not object names, QR1 or 3x3");
Check(FigureBitmap.LetterSizeProblem(1, wideT.Rows) == null && FigureBitmap.LetterSizeProblem(0, new[] { "■■■■■■■" }) == null,
      "a 5x5 letter passes and non-letter figures are not limited");
Check(FigureBitmap.LetterSizeProblem(1, new[] { "■", "■", "■", "■", "■", "■" }) is string tall && tall.Contains("高 6 格") &&
      FigureBitmap.LetterSizeProblem(2, new[] { "■■■■■□□■■■■■" }) == null && FigureBitmap.LetterSizeProblem(2, new[] { "■■■■■■■■■■■■■" }) != null,
      "letters taller than 5 cells, or wider than 5 cells each plus 2 gap cells, are rejected");
// 執行前檢查：放置目標跟同一層的積木部分重疊（含同一批前面步驟剛放的）
var spread = new List<SceneObject> { Block("yellow_cube", 0.20, 0.10, 0.025), Block("yellow_cube", 0.30, 0.10, 0.025),
                                     Block("black_cube", 0.50, 0.20, 0.025) };
Check(LayeredHeights.SameLayerOverlaps(new[] { Move(0, 0.515, 0.20, 0.025) }, spread) is [var partial] &&
      partial.Contains("black_cube（場景 index 2）") && partial.Contains("10×25 mm"),
      "a flat placement 15 mm from a block on the same layer is reported as a partial overlap");
Check(LayeredHeights.SameLayerOverlaps(new[] { Move(0, 0.525, 0.20, 0.025) }, spread).Count == 0,
      "blocks placed flush side by side do not overlap");
Check(LayeredHeights.SameLayerOverlaps(new[] { Move(0, 0.50, 0.20, 0.05) }, spread).Count == 0,
      "stacking on top of a block is a different layer, not an overlap");
Check(LayeredHeights.SameLayerOverlaps(new[] { Move(0, 0.502, 0.199, 0.0265) }, spread).Count == 0,
      "a full-footprint overlap with z read from a low perceived top is still treated as stacking");
var twoTargets = LayeredHeights.SameLayerOverlaps(new[] { Move(0, 0.60, 0.10, 0.025), Move(1, 0.62, 0.10, 0.025) }, spread);
Check(twoTargets is [var earlier] && earlier.StartsWith("第 2 個操作") && earlier.Contains("第 1 個操作放下的 yellow_cube"),
      "a target overlapping a block placed earlier in the same batch is reported");
Check(LayeredHeights.SameLayerOverlaps(new[] { Move(0, 0.21, 0.10, 0.025) }, spread).Count == 0,
      "moving a block a little does not collide with its own old position");
// 純模擬沒開 Isaac Sim：內建虛擬世界照計畫移動放下的物件，寫成 Unity SceneSyncer 讀得懂的 /scene 格式
var world = new VirtualSimWorld(new[] { Block("yellow_cube", 0.20, 0.10, 0.025), Block("black_domino", 0.30, 0.10, 0.025, "horizontal") });
var firstView = world.Snapshot();
firstView[0].X = 9;
Check(Near(world.Snapshot()[0].X, 0.20), "virtual world snapshots are copies, not the world itself");
world.Place(0, new SceneObject { X = 0.50, Y = 0.15, Z = 0.019 }, 0.025);
world.Place(1, new SceneObject { X = 0.55, Y = 0.15, Z = 0.025, Orientation = "horizontal" }, 0.025);
var placedWorld = world.Snapshot();
Check(Near(placedWorld[0].X, 0.50) && Near(placedWorld[0].Y, 0.15) && Near(placedWorld[0].Z, 0.025) &&
      placedWorld[0].Name == "yellow_cube" && placedWorld[1].Orientation == "horizontal" && placedWorld.Count == 2,
      "a placement moves that scene index to the planned target with the layer top height");
var worldDir = Path.Combine(Path.GetTempPath(), "robot_world_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(worldDir);
world.Write(worldDir);
using (var worldJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(worldDir, VirtualSimWorld.FileName))))
{
    var objects = worldJson.RootElement.GetProperty("objects");
    var position = objects[0].GetProperty("position");
    Check(objects.GetArrayLength() == 2 && Near(position.GetProperty("x").GetDouble(), 0.50) &&
          position.GetProperty("source").GetString() == VirtualSimWorld.Source &&
          objects[1].GetProperty("shape").GetString() == "domino",
          "sim_world.json uses the perception /scene format with a non-empty position source");
}
VirtualSimWorld.Delete(worldDir);
Check(!File.Exists(Path.Combine(worldDir, VirtualSimWorld.FileName)), "the world file is removed when Isaac is used");
Directory.Delete(worldDir, true);
var root = Path.Combine(Path.GetTempPath(), "robot_metrics_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Outcome(string id, bool success, string status, int attempts, string[]? outcomes = null) {
    var dir = Path.Combine(root, id); Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "result.json"), outcomes == null
        ? JsonSerializer.Serialize(new { success, status, attempts })
        : JsonSerializer.Serialize(new { success, status, attempts, attempt_outcomes = outcomes }));
}
Outcome("a", true, "success", 2, new[] { "translation_format", "success" });
Outcome("b", false, "failed", 10, Enumerable.Repeat("execution", 9).Append("translation_rejected").ToArray());
Outcome("c", false, "infrastructure_error", 1); Outcome("d", false, "adapter_error", 0);
ExperimentMetrics.Write(root);
using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "metrics.json")));
Check(metrics.RootElement.GetProperty("success_rate_within_10").GetDouble() == 0.5, "failure remains in success denominator");
Check(metrics.RootElement.GetProperty("infrastructure_errors").GetInt32() == 1, "infrastructure error separately counted");
Check(metrics.RootElement.GetProperty("adapter_errors").GetInt32() == 1, "adapter error separately counted");
Check(metrics.RootElement.GetProperty("first_attempt_success_rate").GetDouble() == 0, "first-attempt success measured independently");
var kinds = metrics.RootElement.GetProperty("attempt_outcome_counts");
Check(kinds.GetProperty("success").GetInt32() == 1 && kinds.GetProperty("translation_format").GetInt32() == 1 &&
      kinds.GetProperty("translation_rejected").GetInt32() == 1 && kinds.GetProperty("execution").GetInt32() == 9 &&
      kinds.EnumerateObject().Count() == 4,
      "per-attempt outcomes are counted by kind; results without attempt_outcomes are skipped");
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

public sealed class TranslationContractException : Exception { public TranslationContractException(string message, Exception inner) : base(message, inner) {} }
