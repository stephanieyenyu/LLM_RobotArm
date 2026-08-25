using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

// -----------------------------------------------------------------
// 主 orchestrator（分層架構）
// 每個使用者指令跑一次 5-layer 閉環：
//   Layer 1 (PatternDesigner)  → CanonicalPattern
//   Layer 2 (LayoutRealizer)   → List<TargetCell>
//   Layer 3 (TaskAssigner)     → 每步 1 個 Assignment
//   Layer 4A (MotionPlanner)   → LLM 組合 robot functions
//   Layer 4B (Validator/Unity) → 安全驗證後由 Unity 轉成 URScript 並執行
//   Layer 5 (Verifier)         → 檢查、決定 retry / replan / abort
// -----------------------------------------------------------------

using HttpClient httpClient = new()
{
    BaseAddress = new Uri("http://localhost:5000/"),
    Timeout = TimeSpan.FromSeconds(5),
};

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
};

// 啟動：確認 perception_server 已在執行
try
{
    var health = await httpClient.GetFromJsonAsync<JsonElement>("health", jsonOptions);
    string status = health.TryGetProperty("status", out var s) ? s.GetString() ?? "?" : "?";
    Console.WriteLine($"[perception_server] 已連線 (status={status})");
}
catch (Exception ex)
{
    Console.WriteLine($"[perception_server] 無法連線 → {ex.Message}");
    return;
}

// 檔案路徑
string unityStreamingAssets = "../unity_project/Assets/StreamingAssets";
string inputPath = Path.Combine(unityStreamingAssets, "user_input.txt");
string currentStepPath = Path.Combine(unityStreamingAssets, "current_step.json");
string stepDonePath = Path.Combine(unityStreamingAssets, "step_done.json");
string localOutputDir = "outputs";
Directory.CreateDirectory(localOutputDir);

Console.WriteLine();
Console.WriteLine("=== LLM Planner（分層架構）已啟動 ===");
Console.WriteLine($"監聽：{Path.GetFullPath(inputPath)}");
Console.WriteLine($"每步指令：{Path.GetFullPath(currentStepPath)}");
Console.WriteLine($"執行回報：{Path.GetFullPath(stepDonePath)}");
Console.WriteLine("等待 Unity 輸入指令...");
Console.WriteLine();

// 建立各 layer instance
var workspace = new WorkspaceBounds();
var patternDesigner = new PatternDesigner(workspace.MaxRows, workspace.MaxCols);
var motionPlanner = new MotionPlanner();
var commandRouter = new CommandRouter();

// 清空 input 與舊檔案
if (File.Exists(inputPath)) File.WriteAllText(inputPath, "");
if (File.Exists(currentStepPath)) File.Delete(currentStepPath);
if (File.Exists(stepDonePath)) File.Delete(stepDonePath);

int globalStepId = 0;
const double UNITY_STEP_TIMEOUT_SEC = 600;

while (true)
{
    try
    {
        if (!File.Exists(inputPath))
        {
            await Task.Delay(500);
            continue;
        }

        string userCommand = File.ReadAllText(inputPath).Trim();
        if (string.IsNullOrWhiteSpace(userCommand))
        {
            await Task.Delay(500);
            continue;
        }

        File.WriteAllText(inputPath, "");
        Console.WriteLine($"收到指令：{userCommand}");

        await RunTaskAsync(userCommand);

        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"錯誤：{ex.Message}");
        await Task.Delay(500);
    }
}

// --- 主任務閉環 ---
async Task RunTaskAsync(string userCommand)
{
    var initialScene = await FetchSceneAsync();
    if (initialScene.Count == 0)
    {
        Console.WriteLine("[CommandRouter] Scene contains no objects with valid coordinates.");
        return;
    }

    RoutedCommand routed;
    try
    {
        routed = await commandRouter.RouteAsync(userCommand, initialScene);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[CommandRouter] Failed: {ex.Message}");
        return;
    }

    Console.WriteLine($"[CommandRouter] action={routed.Action} — {routed.Reasoning}");
    switch (routed.Action)
    {
        case "arrange_pattern":
            await RunPatternTaskAsync(userCommand);
            break;
        case "move_relative":
        case "stack":
            await RunSingleObjectTaskAsync(routed, initialScene);
            break;
        default:
            Console.WriteLine($"[CommandRouter] Unsupported action: {routed.Action}");
            break;
    }
}

async Task RunPatternTaskAsync(string userCommand)
{
    // 先掃一次，取得 supplies 與 block color 的決策依據
    var initialSnap = await FetchSceneAsync();
    string blockColor = GuessBlockColor(userCommand, initialSnap);
    
    int cubeBudget = initialSnap.Count(s =>
    s.Name == $"{blockColor}_cube" && s.X < 0.30);

    int dominoBudget = initialSnap.Count(s =>
    s.Name == $"{blockColor}_domino" && s.X < 0.30);

    int maxCoveredCells = cubeBudget + dominoBudget * 2;
    Console.WriteLine($"[Layer 1] 呼叫 LLM 設計 pattern (color={blockColor})...");

    CanonicalPattern pattern;
    try
    {
        pattern = await patternDesigner.DesignAsync(userCommand, blockColor, cubeBudget, dominoBudget);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Layer 1] pattern 設計失敗：{ex.Message}");
        return;
    }
    Console.WriteLine($"[Layer 1] pattern={pattern.PatternId}, bitmap={pattern.Bitmap!.GetLength(0)}x{pattern.Bitmap.GetLength(1)}");
    // 印出 ASCII 圖，並存到 outputs/pattern_XX.json 方便 debug
    int br = pattern.Bitmap.GetLength(0), bc = pattern.Bitmap.GetLength(1);
    var rows = new List<string>();
    for (int r = 0; r < br; r++)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < bc; c++) sb.Append(pattern.Bitmap[r, c] == 1 ? "■" : "□");
        rows.Add(sb.ToString());
        Console.WriteLine("           " + sb.ToString());
    }
    var patternDump = new
    {
        pattern_id = pattern.PatternId,
        block_color = pattern.BlockColor,
        bitmap = rows,
        rows = br,
        cols = bc,
        timestamp = DateTime.Now.ToString("s"),
    };
    File.WriteAllText(
        Path.Combine(localOutputDir, $"pattern_{pattern.PatternId}.json"),
        JsonSerializer.Serialize(patternDump, jsonOptions)
    );

    // Layer 2：計算所有 target（cubeBudget / dominoBudget 已在上方算好）
    var realize = LayoutRealizer.Realize(pattern, workspace, cubeBudget, dominoBudget);
    if (realize.Error != null || realize.Targets == null)
    {
        Console.WriteLine($"[Layer 2] {realize.Error}");
        return;
    }
    Console.WriteLine($"[Layer 2] 展開成 {realize.Targets.Count} 個 target cells "
                      + $"({realize.Targets.Count(t => t.ExpectedShape == "domino")} domino + "
                      + $"{realize.Targets.Count(t => t.ExpectedShape == "cube")} cube)");

    var remainingTargets = new List<TargetCell>(realize.Targets);
    var placedTargets = new List<TargetCell>();
    int retryCountThisStep = 0;
    string? motionFeedback = null;
    const int MAX_RETRY = 2;

    // Layer 3/4/5 閉環
    while (remainingTargets.Count > 0)
    {
        globalStepId++;
        Console.WriteLine();
        Console.WriteLine($"─── Step {globalStepId} ───");

        // 每步重新掃描一次（Layer 3 需要最新 supply 狀況）
        var beforeSnap = await FetchSceneAsync();

        var assignment = TaskAssigner.Assign(remainingTargets, beforeSnap, globalStepId);
        if (assignment == null)
        {
            Console.WriteLine("[Layer 3] 沒有可執行的 assignment（supply 用完或不足）");
            break;
        }
        Console.WriteLine($"[Layer 3] {assignment.Reasoning}");

        // Layer 4A：由 LLM 使用白名單 robot functions 規劃動作。
        // 最多要求 LLM 修正三次；通過 deterministic validator 後才交給 Unity。
        MotionPlan? motionPlan = null;
        string validationError = "";
        for (int planAttempt = 1; planAttempt <= 3; planAttempt++)
        {
            string feedback = string.Join("; ", new[] { motionFeedback, validationError }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            try
            {
                motionPlan = await motionPlanner.PlanAsync(assignment, beforeSnap, feedback);
            }
            catch (Exception ex)
            {
                validationError = "Motion Planner call failed: " + ex.Message;
                Console.WriteLine($"[Layer 4A] 第 {planAttempt} 次規劃呼叫失敗：{ex.Message}");
                motionPlan = null;
                continue;
            }
            if (MotionPlanValidator.TryValidate(motionPlan, assignment, beforeSnap, out validationError)) break;
            Console.WriteLine($"[Layer 4A] 第 {planAttempt} 次規劃未通過安全驗證：{validationError}");
            motionPlan = null;
        }
        if (motionPlan == null)
        {
            Console.WriteLine("[Layer 4A] 無法取得安全的動作規劃，停止目前任務");
            break;
        }
        Console.WriteLine($"[Layer 4A] LLM motion plan：{motionPlan.ActionSequence.Count} functions — {motionPlan.Reasoning}");

        // Layer 4B：將已驗證的 function sequence 交給 Unity，不再固定展開成 12 步。
        var envelope = new StepEnvelope
        {
            StepId = assignment.StepId,
            Done = false,
            SourcePosition = assignment.Source,
            TargetPosition = new SceneObject
            {
                Name = $"grid_{assignment.Target!.ExpectedShape}_r{assignment.Target.Row}_c{assignment.Target.Col}",
                X = assignment.Target.WorldX,
                Y = assignment.Target.WorldY,
                Z = assignment.Target.WorldZ,
                Shape = assignment.Target.ExpectedShape,
                Orientation = assignment.Target.ExpectedOrientation,
            },
            Comment = assignment.Reasoning + " | Motion: " + motionPlan.Reasoning,
            ActionSequence = motionPlan.ActionSequence,
        };
        WriteStepFile(envelope);

        Console.WriteLine($"[Layer 4] 送出 step {assignment.StepId}，等待 Unity 執行...");
        var execResult = await WaitForStepDoneAsync(
            assignment.StepId, timeoutSec: UNITY_STEP_TIMEOUT_SEC);
        if (execResult == null || !execResult.Completed)
        {
            Console.WriteLine($"[Layer 4] 執行 timeout 或失敗：{execResult?.Error}");
            break;
        }
        Console.WriteLine($"[Layer 4] 執行完成 ({execResult.DurationSec:F1}s)");

        // Layer 5：驗證
        var afterSnap = await FetchSceneAsync();
        var verify = Verifier.CheckStep(assignment, beforeSnap, afterSnap);
        Console.WriteLine($"[Layer 5] {verify.OverallStatus} — {verify.Note}");

        int keyRow = assignment.Target!.Row;
        int keyCol = assignment.Target.Col;

        switch (verify.OverallStatus)
        {
            case "ok":
                placedTargets.Add(assignment.Target);
                remainingTargets.RemoveAll(t => t.Row == keyRow && t.Col == keyCol);
                retryCountThisStep = 0;
                motionFeedback = null;
                break;
            case "retry":
                motionFeedback = verify.Note;
                retryCountThisStep++;
                if (retryCountThisStep > MAX_RETRY)
                {
                    Console.WriteLine($"       同一步重試 {MAX_RETRY} 次仍失敗，跳過");
                    remainingTargets.RemoveAll(t => t.Row == keyRow && t.Col == keyCol);
                    retryCountThisStep = 0;
                }
                break;
            case "replan":
                // 保留 remaining，下一輪由 Layer 3 重新配對。
                motionFeedback = verify.Note;
                break;
            case "abort":
                Console.WriteLine("       abort：終止目前任務");
                goto TaskDone;
        }

        Console.WriteLine($"       剩餘 targets: {remainingTargets.Count}");
    }

    TaskDone:
    // 寫入 done，讓 Unity 停止 polling
    WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });

    // 最終驗證
    var finalSnap = await FetchSceneAsync();
    var overallResults = Verifier.CheckOverall(realize.Targets, finalSnap);
    int matched = overallResults.Count(r => r.matched);
    Console.WriteLine();
    Console.WriteLine("=== 任務結束 ===");
    Console.WriteLine($"最終驗證：{matched}/{overallResults.Count} 個位置正確");
    foreach (var (t, ok) in overallResults.Where(x => !x.matched))
    {
        Console.WriteLine($"  × r{t.Row}c{t.Col} ({t.ExpectedShape}) 未匹配");
    }
}

// --- 輔助函式 ---
async Task RunSingleObjectTaskAsync(RoutedCommand routed, List<SceneObject> initialScene)
{
    globalStepId++;
    Assignment assignment;
    try
    {
        assignment = SingleObjectTaskBuilder.Build(routed, initialScene, globalStepId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SingleObject] Cannot build task: {ex.Message}");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }

    Console.WriteLine($"[SingleObject] {assignment.Reasoning}");
    string? feedback = null;
    const int maxRetries = 2;

    for (int retry = 0; retry <= maxRetries; retry++)
    {
        var beforeSnap = await FetchSceneAsync();
        MotionPlan? motionPlan = null;
        string validationError = "";

        for (int planAttempt = 1; planAttempt <= 3; planAttempt++)
        {
            string plannerFeedback = string.Join("; ", new[] { feedback, validationError }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            try
            {
                motionPlan = await motionPlanner.PlanAsync(assignment, beforeSnap, plannerFeedback);
            }
            catch (Exception ex)
            {
                validationError = "Motion Planner call failed: " + ex.Message;
                motionPlan = null;
                continue;
            }

            if (MotionPlanValidator.TryValidate(motionPlan, assignment, beforeSnap, out validationError))
                break;
            Console.WriteLine($"[MotionPlanner] Attempt {planAttempt} rejected: {validationError}");
            motionPlan = null;
        }

        if (motionPlan == null)
        {
            Console.WriteLine("[MotionPlanner] Could not produce a safe plan.");
            break;
        }

        var envelope = new StepEnvelope
        {
            StepId = assignment.StepId,
            Done = false,
            SourcePosition = assignment.Source,
            TargetPosition = new SceneObject
            {
                Name = routed.Action == "stack" ? "stack_target" : "relative_target",
                X = assignment.Target!.WorldX,
                Y = assignment.Target.WorldY,
                Z = assignment.Target.WorldZ,
                Shape = assignment.Target.ExpectedShape,
                Orientation = assignment.Target.ExpectedOrientation,
            },
            Comment = assignment.Reasoning + " | Motion: " + motionPlan.Reasoning,
            ActionSequence = motionPlan.ActionSequence,
        };

        // A retry must have a fresh step id so Unity will execute it again.
        if (retry > 0)
        {
            assignment.StepId = ++globalStepId;
            envelope.StepId = assignment.StepId;
        }

        WriteStepFile(envelope);
        Console.WriteLine($"[Executor] Sent step {assignment.StepId}; waiting for Unity...");
        var execResult = await WaitForStepDoneAsync(
            assignment.StepId, timeoutSec: UNITY_STEP_TIMEOUT_SEC);
        if (execResult == null || !execResult.Completed)
        {
            feedback = execResult?.Error ?? "Unity execution timeout";
            Console.WriteLine($"[Executor] Failed: {feedback}");
            // Execution state is unknown; do not blindly return to the old source coordinate.
            break;
        }

        var afterSnap = await FetchSceneAsync();
        var verify = Verifier.CheckSingleObjectStep(
            assignment, beforeSnap, afterSnap, requireStackHeight: routed.Action == "stack");
        Console.WriteLine($"[Verifier] {verify.OverallStatus} — {verify.Note}");
        if (verify.OverallStatus == "ok")
            break;
        if (verify.OverallStatus != "retry")
            break;
        feedback = verify.Note;
    }

    WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
}

async Task<List<SceneObject>> FetchSceneAsync()
{
    try
    {
        var world = await httpClient.GetFromJsonAsync<ObjectsWorld>("scene", jsonOptions);
        if (world?.Objects == null) return new List<SceneObject>();
        return world.Objects
            .Where(o => o.Position != null)
            .Select(o => new SceneObject
            {
                Name = o.Name,
                X = o.Position!.X,
                Y = o.Position!.Y,
                Z = o.Position!.Z,
                Shape = string.IsNullOrEmpty(o.Shape) ? "cube" : o.Shape,
                Orientation = o.Orientation,
                SkewDeg = o.SkewDeg,
            })
            .ToList();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[perception] fetch scene 失敗：{ex.Message}");
        return new List<SceneObject>();
    }
}

string GuessBlockColor(string userCommand, List<SceneObject> snap)
{
    if (userCommand.Contains("black") || userCommand.Contains("黑")) return "black";
    if (userCommand.Contains("yellow") || userCommand.Contains("黃")) return "yellow";
    // 未指定時，選擇 supply 較多的顏色
    int y = snap.Count(s => s.Name.StartsWith("yellow_"));
    int b = snap.Count(s => s.Name.StartsWith("black_"));
    return y >= b ? "yellow" : "black";
}

void WriteStepFile(StepEnvelope env)
{
    string json = JsonSerializer.Serialize(env, jsonOptions);
    File.WriteAllText(currentStepPath, json);
    File.WriteAllText(Path.Combine(localOutputDir, $"step_{env.StepId}.json"), json);
}

async Task<ExecutionResult?> WaitForStepDoneAsync(int stepId, double timeoutSec)
{
    var start = DateTime.UtcNow;
    while ((DateTime.UtcNow - start).TotalSeconds < timeoutSec)
    {
        if (File.Exists(stepDonePath))
        {
            try
            {
                string json = File.ReadAllText(stepDonePath);
                var result = JsonSerializer.Deserialize<ExecutionResult>(json, jsonOptions);
                if (result != null && result.StepId == stepId)
                {
                    File.Delete(stepDonePath);   // 避免重讀
                    return result;
                }
            }
            catch { /* 檔案可能仍在寫入，下一輪重試 */ }
        }
        await Task.Delay(200);
    }
    return null;
}

// 對應 perception 回傳格式
public class ObjectsWorld
{
    [JsonPropertyName("objects")] public List<WorldObject> Objects { get; set; } = new();
}
public class WorldObject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("position")] public WorldPos? Position { get; set; }
    [JsonPropertyName("shape")] public string? Shape { get; set; }
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }
    [JsonPropertyName("skew_deg")] public double SkewDeg { get; set; }
}
public class WorldPos
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
}

