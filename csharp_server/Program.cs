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
string batchPlanPath = Path.Combine(unityStreamingAssets, "batch_plan.json");
string batchDonePath = Path.Combine(unityStreamingAssets, "batch_done.json");
string localOutputDir = "outputs";
Directory.CreateDirectory(localOutputDir);

Console.WriteLine();
Console.WriteLine("=== LLM Planner（分層架構）已啟動 ===");
Console.WriteLine($"監聽：{Path.GetFullPath(inputPath)}");
Console.WriteLine($"每步指令：{Path.GetFullPath(currentStepPath)}");
Console.WriteLine($"執行回報：{Path.GetFullPath(stepDonePath)}");
Console.WriteLine($"完整 Batch Plan：{Path.GetFullPath(batchPlanPath)}");
Console.WriteLine("等待 Unity 輸入指令...");
Console.WriteLine();

// 建立各 layer instance
var workspace = new WorkspaceBounds();
var patternDesigner = new PatternDesigner(workspace.MaxRows, workspace.MaxCols);
var spatialPatternDesigner = new SpatialPatternDesigner(
    workspace.SpatialRows, workspace.SpatialCols, workspace.SpatialLayers);
var motionPlanner = new MotionPlanner();
var batchMotionPlanner = new BatchMotionPlanner();
var commandRouter = new CommandRouter();

// 清空 input 與舊檔案
if (File.Exists(inputPath)) File.WriteAllText(inputPath, "");
if (File.Exists(currentStepPath)) File.Delete(currentStepPath);
if (File.Exists(stepDonePath)) File.Delete(stepDonePath);
if (File.Exists(batchPlanPath)) File.Delete(batchPlanPath);
if (File.Exists(batchDonePath)) File.Delete(batchDonePath);

// Keep step IDs unique when dotnet is restarted while Unity remains in Play
// Mode; otherwise Unity can mistake a new Step 1/2/... for an old command.
int globalStepId = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
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
            await RunBatchPatternTaskAsync(userCommand, initialScene);
            break;
        case "arrange_3d_pattern":
            await RunSpatialPatternTaskAsync(userCommand, initialScene);
            break;
        case "move_relative":
            await RunSingleObjectTaskAsync(routed, initialScene);
            break;
        case "stack":
            if (routed.StackSequence.Count > 2 || (routed.ObjectCount ?? 2) > 2)
                await RunMultiStackTaskAsync(routed, initialScene);
            else
                await RunSingleObjectTaskAsync(routed, initialScene);
            break;
        default:
            Console.WriteLine($"[CommandRouter] Unsupported action: {routed.Action}");
            break;
    }
}

// Complete batch path: one perception snapshot -> one LLM batch request -> one
// immutable JSON file -> sequential physical execution. There is deliberately no
// per-step re-perception or replanning inside this path.
async Task RunBatchPatternTaskAsync(string userCommand, List<SceneObject> sceneSnapshot)
{
    string blockColor = GuessBlockColor(userCommand, sceneSnapshot);
    int cubeBudget = sceneSnapshot.Count(s =>
        s.Name == $"{blockColor}_cube" && s.X < workspace.SupplyZoneXMax);
    int dominoBudget = sceneSnapshot.Count(s =>
        s.Name == $"{blockColor}_domino" && s.X < workspace.SupplyZoneXMax);

    Console.WriteLine($"[Batch Layer 1] Designing pattern from one scene snapshot (color={blockColor})...");
    CanonicalPattern pattern;
    try
    {
        pattern = await patternDesigner.DesignAsync(
            userCommand, blockColor, cubeBudget, dominoBudget);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Batch Layer 1] Pattern design failed: {ex.Message}");
        return;
    }

    var realized = LayoutRealizer.Realize(pattern, workspace, cubeBudget, dominoBudget);
    if (realized.Error != null || realized.Targets == null)
    {
        Console.WriteLine($"[Batch Layer 2] {realized.Error}");
        return;
    }

    List<Assignment> candidates;
    try
    {
        candidates = TaskAssigner.AssignBatch(realized.Targets, sceneSnapshot, ref globalStepId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Batch Layer 3] {ex.Message}");
        return;
    }
    Console.WriteLine($"[Batch Layer 3] Frozen {candidates.Count} unique source/target assignments.");

    BatchMotionPlan llmBatch;
    try
    {
        Console.WriteLine("[Batch Layer 4A] LLM is planning all moves and their order in one request...");
        llmBatch = await batchMotionPlanner.PlanAsync(candidates, sceneSnapshot);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Batch Layer 4A] Batch planning failed: {ex.Message}");
        return;
    }

    var byId = candidates.ToDictionary(a => a.StepId);
    var returnedIds = llmBatch.Steps.Select(s => s.StepId).ToList();
    if (returnedIds.Count != candidates.Count ||
        returnedIds.Distinct().Count() != candidates.Count ||
        returnedIds.Any(id => !byId.ContainsKey(id)))
    {
        Console.WriteLine("[Batch Validator] Rejected: LLM must return every candidate step_id exactly once.");
        return;
    }

    var envelopes = new List<StepEnvelope>();
    foreach (var planned in llmBatch.Steps)
    {
        Assignment assignment = byId[planned.StepId];
        var motion = new MotionPlan { ActionSequence = planned.ActionSequence };
        if (!MotionPlanValidator.TryValidate(motion, assignment, sceneSnapshot, out string error))
        {
            Console.WriteLine($"[Batch Validator] Step {planned.StepId} rejected: {error}");
            return;
        }
        envelopes.Add(new StepEnvelope
        {
            StepId = assignment.StepId,
            SourcePosition = assignment.Source,
            TargetPosition = new SceneObject
            {
                Name = $"grid_{assignment.Target!.ExpectedShape}_r{assignment.Target.Row}_c{assignment.Target.Col}",
                X = assignment.Target.WorldX, Y = assignment.Target.WorldY, Z = assignment.Target.WorldZ,
                Shape = assignment.Target.ExpectedShape,
                Orientation = assignment.Target.ExpectedOrientation,
            },
            Comment = assignment.Reasoning,
            ActionSequence = planned.ActionSequence,
        });
    }

    int batchId = checked(++globalStepId);
    var batch = new BatchPlan
    {
        BatchId = batchId,
        CreatedAt = DateTimeOffset.Now.ToString("O"),
        SceneCapturedAt = DateTimeOffset.Now.ToString("O"),
        Comment = $"{pattern.PatternId}: {llmBatch.Reasoning}",
        Steps = envelopes,
    };
    WriteBatchFile(batch);
    Console.WriteLine($"[Batch Layer 4B] Sent batch {batchId}: {envelopes.Count} physical steps.");

    BatchExecutionResult? result = await WaitForBatchDoneAsync(
        batchId, UNITY_STEP_TIMEOUT_SEC * Math.Max(1, envelopes.Count));
    if (result == null)
    {
        Console.WriteLine("[Batch Executor] Timed out waiting for Unity.");
        return;
    }
    Console.WriteLine(result.Completed
        ? $"[Batch Executor] Completed all {result.TotalSteps} steps in {result.DurationSec:F1}s."
        : $"[Batch Executor] Stopped at step {result.FailedStepId}: {result.Error}");

    var finalSnap = await FetchSceneAsync();
    var overall = Verifier.CheckOverall(realized.Targets, finalSnap);
    Console.WriteLine($"[Batch Verifier] {overall.Count(x => x.matched)}/{overall.Count} targets matched.");
}

async Task RunPatternTaskAsync(string userCommand)
{
    // 先掃一次，取得 supplies 與 block color 的決策依據
    var initialSnap = await FetchSceneAsync();
    string blockColor = GuessBlockColor(userCommand, initialSnap);
    
    int cubeBudget = initialSnap.Count(s =>
    s.Name == $"{blockColor}_cube" && s.X < workspace.SupplyZoneXMax);

    int dominoBudget = initialSnap.Count(s =>
    s.Name == $"{blockColor}_domino" && s.X < workspace.SupplyZoneXMax);

    int maxCoveredCells = cubeBudget + dominoBudget * 2;
    Console.WriteLine($"[Layer 1] 呼叫 LLM 設計 pattern (color={blockColor})...");

    CanonicalPattern pattern;
    try
    {
        pattern = await patternDesigner.DesignAsync(
            userCommand, blockColor, cubeBudget, dominoBudget);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Layer 1] pattern 設計失敗：{ex.Message}");
        Console.WriteLine(ex.ToString());
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
    var failureCounts = new Dictionary<(int Row, int Col), int>();
    var skippedTargets = new HashSet<(int Row, int Col)>();
    var failedSources = new List<SceneObject>();
    string? motionFeedback = null;
    const int MAX_RETRY = 1;
    const int MAX_NO_PROGRESS_ROUNDS = 5;
    int noProgressRounds = 0;
    int previousMatchedCount = -1;
    int recoveryRound = 0;
    bool recoveryMode = false;

    bool RegisterStepFailure(
        Assignment failedAssignment,
        string reason,
        bool blacklistSource,
        IReadOnlyList<SceneObject>? latestScene = null)
    {
        var key = (failedAssignment.Target!.Row, failedAssignment.Target.Col);
        int failures = failureCounts.GetValueOrDefault(key) + 1;
        failureCounts[key] = failures;
        motionFeedback = reason;

        if (blacklistSource && failedAssignment.Source != null &&
            !failedSources.Any(s => s.Name == failedAssignment.Source.Name &&
                Math.Pow(s.X - failedAssignment.Source.X, 2) +
                Math.Pow(s.Y - failedAssignment.Source.Y, 2) <= Math.Pow(0.035, 2)))
        {
            failedSources.Add(failedAssignment.Source);
            Console.WriteLine(
                $"       記錄失敗積木：{failedAssignment.Source.Name} " +
                $"({failedAssignment.Source.X:F3}, {failedAssignment.Source.Y:F3})");
        }

        if (failures <= MAX_RETRY)
        {
            Console.WriteLine($"       同一目標將重試第 {failures}/{MAX_RETRY} 次");
            return false;
        }

        // A retry limit applies to the current source choice, not to every block
        // that could satisfy this target. Before skipping, look for a same-type
        // piece that has never failed. Search the full QR workspace so a valid
        // spare outside the normal supply-zone cutoff is not overlooked.
        string expectedName = $"{failedAssignment.Target.ExpectedColor}_" +
                              failedAssignment.Target.ExpectedShape;
        bool HasFailedBefore(SceneObject candidate) => failedSources.Any(f =>
            candidate.Name == f.Name &&
            Math.Pow(candidate.X - f.X, 2) + Math.Pow(candidate.Y - f.Y, 2)
                <= Math.Pow(0.035, 2));
        bool OccupiesPlacedTarget(SceneObject candidate) => placedTargets.Any(t =>
            Math.Pow(candidate.X - t.WorldX, 2) + Math.Pow(candidate.Y - t.WorldY, 2)
                <= Math.Pow(0.025, 2));
        var untriedAlternatives = blacklistSource && latestScene != null
            ? latestScene
                .Where(o => o.Name == expectedName)
                .Where(o => !HasFailedBefore(o))
                .Where(o => !OccupiesPlacedTarget(o))
                .ToList()
            : new List<SceneObject>();

        if (untriedAlternatives.Count > 0)
        {
            failureCounts[key] = 0;
            recoveryMode = true; // allow TaskAssigner to use the full QR workspace
            motionFeedback = reason + "；改用尚未嘗試的同色同形積木。";
            Console.WriteLine(
                $"       已達目前積木的重試上限，但仍有 " +
                $"{untriedAlternatives.Count} 顆未嘗試的 {expectedName}，改抓其他積木");
            return false;
        }

        Console.WriteLine($"       同一目標重試 {MAX_RETRY} 次仍失敗，跳過 r{key.Row}c{key.Col}");
        skippedTargets.Add(key);
        remainingTargets.RemoveAll(t => t.Row == key.Row && t.Col == key.Col);
        failureCounts.Remove(key);
        motionFeedback = null;
        return true;
    }

    // Layer 3/4/5 閉環。每一輪執行完都做全局驗證；若仍有未匹配
    // target，就用最新場景重建待辦並進入 recovery。
    while (true)
    {
      while (remainingTargets.Count > 0)
      {
        globalStepId++;
        Console.WriteLine();
        Console.WriteLine($"─── Step {globalStepId} ───");

        // 每步重新掃描一次（Layer 3 需要最新 supply 狀況）
        var beforeSnap = await FetchSceneAsync();

        var assignment = TaskAssigner.Assign(
            remainingTargets,
            beforeSnap,
            globalStepId,
            recoveryMode,
            placedTargets,
            failedSources);
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
            Console.WriteLine("[Layer 4A] 無法取得安全的動作規劃");
            RegisterStepFailure(
                assignment, validationError, blacklistSource: false, latestScene: beforeSnap);
            continue;
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
            RegisterStepFailure(
                assignment,
                execResult?.Error ?? "Unity execution timeout",
                blacklistSource: true,
                latestScene: beforeSnap);
            continue;
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
                failureCounts.Remove((keyRow, keyCol));
                motionFeedback = null;
                break;
            case "retry":
                RegisterStepFailure(
                    assignment, verify.Note, blacklistSource: true, latestScene: afterSnap);
                break;
            case "replan":
                RegisterStepFailure(
                    assignment, verify.Note, blacklistSource: true, latestScene: afterSnap);
                break;
            case "abort":
                Console.WriteLine("       abort：終止目前任務");
                goto TaskDone;
        }

        Console.WriteLine($"       剩餘 targets: {remainingTargets.Count}");
      }

      // 一輪結束後不直接宣告任務完成；重新掃描整個場景，僅保留未匹配目標。
      var roundSnap = await FetchSceneAsync();
      var roundResults = Verifier.CheckOverall(realize.Targets, roundSnap);
      int roundMatched = roundResults.Count(r => r.matched);

      Console.WriteLine();
      Console.WriteLine(recoveryMode
          ? $"=== Recovery {recoveryRound} 驗證：{roundMatched}/{roundResults.Count} ==="
          : $"=== 第一輪全局驗證：{roundMatched}/{roundResults.Count} ===");

      if (roundMatched == roundResults.Count)
      {
          Console.WriteLine("所有目標位置均已匹配。");
          break;
      }

      if (roundMatched > previousMatchedCount)
          noProgressRounds = 0;
      else
          noProgressRounds++;
      previousMatchedCount = roundMatched;

      if (noProgressRounds >= MAX_NO_PROGRESS_ROUNDS)
      {
          Console.WriteLine(
              $"連續 {MAX_NO_PROGRESS_ROUNDS} 輪沒有進展，停止自動恢復；" +
              "請檢查積木是否掉出視野、辨識錯誤或供應不足。");
          break;
      }

      placedTargets.Clear();
      placedTargets.AddRange(roundResults.Where(r => r.matched).Select(r => r.target));
      remainingTargets = roundResults
          .Where(r => !r.matched && !skippedTargets.Contains((r.target.Row, r.target.Col)))
          .Select(r => r.target)
          .ToList();

      if (remainingTargets.Count == 0)
      {
          Console.WriteLine("所有未匹配目標都已達重試上限並跳過，不再重新加入 recovery。");
          break;
      }

      recoveryMode = true;
      recoveryRound++;
      motionFeedback = "全局驗證未匹配；重新掃描並回收放偏或掉落的同色同形積木。";

      Console.WriteLine(
          $"[Recovery {recoveryRound}] 將重新處理 {remainingTargets.Count} 個未匹配位置；" +
          $"連續無進展 {noProgressRounds}/{MAX_NO_PROGRESS_ROUNDS} 輪。");
      await Task.Delay(1000);
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

async Task RunSpatialPatternTaskAsync(string userCommand, List<SceneObject> initialScene)
{
    string color = GuessBlockColor(userCommand, initialScene);
    string cubeName = $"{color}_cube";
    int cubeBudget = initialScene.Count(o =>
        o.Name == cubeName && o.X < workspace.SupplyZoneXMax);
    Console.WriteLine(
        $"[3D Layer 1] Asking LLM for a self-supporting voxel glyph " +
        $"(color={color}, cubes={cubeBudget}, volume=" +
        $"{workspace.SpatialRows}x{workspace.SpatialCols}x{workspace.SpatialLayers})...");

    SpatialPattern pattern;
    try
    {
        pattern = await spatialPatternDesigner.DesignAsync(
            userCommand, color, cubeBudget);
    }
    catch (SpatialPatternInfeasibleException ex)
    {
        Console.WriteLine($"[3D Layer 1] 不可執行：{ex.Message}");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[3D Layer 1] 設計服務失敗：{ex.Message}");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }

    int[,] heights = pattern.ColumnHeights!;
    int rows = heights.GetLength(0), cols = heights.GetLength(1);
    int total = 0;
    Console.WriteLine($"[3D Layer 1] pattern={pattern.PatternId}, column heights={rows}x{cols}");
    for (int r = 0; r < rows; r++)
    {
        var line = new System.Text.StringBuilder();
        for (int c = 0; c < cols; c++)
        {
            line.Append(heights[r, c]);
            if (c + 1 < cols) line.Append(' ');
            total += heights[r, c];
        }
        Console.WriteLine("             " + line);
    }
    Console.WriteLine($"[3D deterministic] support=pass (contiguous columns), cubes={total}/{cubeBudget}");

    var columns = new List<(int Row, int Col, int Height, double X, double Y)>();
    for (int r = 0; r < rows; r++)
    for (int c = 0; c < cols; c++)
        if (heights[r, c] > 0)
        {
            double targetX = workspace.TargetOriginX + c * workspace.CellSize;
            double targetY = workspace.TargetOriginY + (rows - 1 - r) * workspace.CellSize;
            if (targetX < workspace.TargetZoneXMin)
                throw new InvalidOperationException(
                    $"3D target r{r}c{c} X={targetX:F3} is outside the target zone " +
                    $"(X >= {workspace.TargetZoneXMin:F2} m).");
            columns.Add((r, c, heights[r, c], targetX, targetY));
        }
    columns = columns.OrderByDescending(x => x.Y).ThenByDescending(x => x.X).ToList();

    var failedSources = new List<SceneObject>();
    var placedBases = new List<(int Row, int Col, int Height, double X, double Y, double TopZ)>();
    var baseRoute = new RoutedCommand { Action = "move_relative" };
    var stackRoute = new RoutedCommand { Action = "stack" };

    // Build every table-supported base before adding upper layers.
    foreach (var column in columns)
    {
        var scene = await FetchSceneAsync();
        Assignment BuildBase(List<SceneObject> snap, int id)
        {
            SceneObject source = snap
                .Where(o => o.Name == cubeName && o.X < workspace.SupplyZoneXMax)
                .Where(o => !failedSources.Any(f => f.Name == o.Name &&
                    Math.Pow(f.X - o.X, 2) + Math.Pow(f.Y - o.Y, 2) < Math.Pow(0.035, 2)))
                .OrderBy(o => Math.Pow(o.X - column.X, 2) + Math.Pow(o.Y - column.Y, 2))
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"No untried {cubeName} remains for 3D base.");
            double z = Math.Max(source.Z, workspace.DefaultBlockZ);
            return new Assignment
            {
                StepId = id,
                Source = source,
                Target = new TargetCell
                {
                    Row = column.Row, Col = column.Col,
                    WorldX = column.X, WorldY = column.Y, WorldZ = z,
                    ExpectedShape = "cube", ExpectedColor = color,
                },
                Reasoning = $"3D base r{column.Row}c{column.Col} at ({column.X:F3},{column.Y:F3})",
            };
        }

        Assignment assignment;
        try { assignment = BuildBase(scene, ++globalStepId); }
        catch (Exception ex)
        {
            Console.WriteLine("[3D base] " + ex.Message);
            goto SpatialDone;
        }
        bool ok = await RunSingleObjectTaskAsync(
            baseRoute, scene, assignment, writeDoneWhenFinished: false,
            rebuildForRetry: BuildBase);
        if (!ok)
        {
            failedSources.Add(assignment.Source!);
            Console.WriteLine($"[3D base] Failed r{column.Row}c{column.Col}; stopping.");
            goto SpatialDone;
        }
        placedBases.Add((column.Row, column.Col, column.Height,
            column.X, column.Y, assignment.Source!.Z));
    }

    // Add upper cubes bottom-up. Every target is supported by its own column.
    for (int layer = 2; layer <= workspace.SpatialLayers; layer++)
    {
        foreach (var column in placedBases.Where(c => c.Height >= layer).ToList())
        {
            int index = placedBases.FindIndex(c => c.Row == column.Row && c.Col == column.Col);
            var scene = await FetchSceneAsync();
            Assignment assignment;
            try
            {
                assignment = SingleObjectTaskBuilder.BuildStackOntoLocation(
                    cubeName, scene, column.X, column.Y, column.TopZ,
                    ++globalStepId, failedSources, workspace.SupplyZoneXMax);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[3D layer {layer}] {ex.Message}");
                goto SpatialDone;
            }
            bool ok = await RunSingleObjectTaskAsync(
                stackRoute, scene, assignment, writeDoneWhenFinished: false,
                rebuildForRetry: (latest, retryId) =>
                    SingleObjectTaskBuilder.BuildStackOntoLocation(
                        cubeName, latest, column.X, column.Y, column.TopZ,
                        retryId, failedSources, workspace.SupplyZoneXMax),
                failedStackSources: failedSources);
            if (!ok)
            {
                Console.WriteLine($"[3D layer {layer}] Failed r{column.Row}c{column.Col}; stopping.");
                goto SpatialDone;
            }
            placedBases[index] = (column.Row, column.Col, column.Height,
                column.X, column.Y, column.TopZ + assignment.Source!.Z);
        }
    }

    Console.WriteLine("[3D] 所有立體字柱已完成。");

    SpatialDone:
    WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
}

// --- 輔助函式 ---
async Task<bool> RunSingleObjectTaskAsync(
    RoutedCommand routed,
    List<SceneObject> initialScene,
    Assignment? preparedAssignment = null,
    bool writeDoneWhenFinished = true,
    Func<List<SceneObject>, int, Assignment>? rebuildForRetry = null,
    List<SceneObject>? failedStackSources = null)
{
    if (preparedAssignment == null)
        globalStepId++;
    Assignment assignment;
    try
    {
        assignment = preparedAssignment ??
            SingleObjectTaskBuilder.Build(routed, initialScene, globalStepId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SingleObject] Cannot build task: {ex.Message}");
        if (writeDoneWhenFinished)
            WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return false;
    }

    Console.WriteLine($"[SingleObject] {assignment.Reasoning}");
    string? feedback = null;
    const int maxRetries = 1;
    bool succeeded = false;

    void RememberFailedStackSource(Assignment failedAssignment)
    {
        if (routed.Action != "stack" || failedStackSources == null ||
            failedAssignment.Source == null)
            return;
        SceneObject source = failedAssignment.Source;
        bool alreadyRecorded = failedStackSources.Any(f =>
            f.Name == source.Name &&
            Math.Sqrt(Math.Pow(f.X - source.X, 2) + Math.Pow(f.Y - source.Y, 2)) < 0.035);
        if (alreadyRecorded) return;
        failedStackSources.Add(source);
        Console.WriteLine(
            $"[MultiStack] Blacklisted failed source {source.Name} " +
            $"({source.X:F3}, {source.Y:F3}); retry will choose another block.");
    }

    for (int retry = 0; retry <= maxRetries; retry++)
    {
        var beforeSnap = await FetchSceneAsync();
        if (retry > 0)
        {
            int retryStepId = ++globalStepId;
            try
            {
                if (rebuildForRetry != null)
                {
                    assignment = rebuildForRetry(beforeSnap, retryStepId);
                }
                else if (routed.Action == "stack")
                {
                    assignment = SingleObjectTaskBuilder.Build(
                        routed, beforeSnap, retryStepId);
                }
                else
                {
                    assignment.StepId = retryStepId;
                }
                Console.WriteLine(
                    $"[Retry] Recomputed source and stack target from latest scene: " +
                    assignment.Reasoning);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Retry] Cannot rebuild assignment: {ex.Message}");
                break;
            }
        }
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

        // Give the multi-frame perception stabilizer time to replace the
        // pre-motion detections, especially when one block occludes another.
        if (routed.Action == "stack")
            await Task.Delay(1200);
        var afterSnap = await FetchSceneAsync();
        var verify = Verifier.CheckSingleObjectStep(
            assignment, beforeSnap, afterSnap, requireStackHeight: routed.Action == "stack");
        Console.WriteLine($"[Verifier] {verify.OverallStatus} — {verify.Note}");
        if (verify.OverallStatus == "ok")
        {
            succeeded = true;
            break;
        }
        RememberFailedStackSource(assignment);
        if (retry >= maxRetries)
            break;
        if (verify.OverallStatus is not ("retry" or "replan" or "abort"))
            break;
        feedback = verify.Note;
    }

    if (writeDoneWhenFinished)
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
    return succeeded;
}

async Task RunMultiStackTaskAsync(RoutedCommand routed, List<SceneObject> initialScene)
{
    List<string> sequence = routed.StackSequence.Count >= 2
        ? routed.StackSequence
        : Enumerable.Repeat(routed.ObjectName ?? "", routed.ObjectCount ?? 2).ToList();
    int requestedCount = sequence.Count;
    if (requestedCount < 2 || sequence.Any(string.IsNullOrWhiteSpace))
    {
        Console.WriteLine("[MultiStack] Invalid stack sequence.");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }
    if (sequence.Any(name => !name.EndsWith("_cube", StringComparison.Ordinal)))
    {
        Console.WriteLine("[MultiStack] Multi-layer stacking currently supports cubes only.");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }

    foreach (var requirement in sequence.GroupBy(name => name))
    {
        int visible = initialScene.Count(o => o.Name == requirement.Key);
        if (visible < requirement.Count())
        {
            Console.WriteLine(
                $"[MultiStack] Sequence needs {requirement.Count()} {requirement.Key}, " +
                $"but only {visible} are visible.");
            WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
            return;
        }
    }

    // Prefer a base outside the supply zone; otherwise use the farthest-X cube.
    string baseName = sequence[0];
    SceneObject towerBase = initialScene
        .Where(o => o.Name == baseName)
        .OrderByDescending(o => o.X >= 0.30)
        .ThenByDescending(o => o.X)
        .First();
    double towerX = towerBase.X;
    double towerY = towerBase.Y;
    double towerTopZ = towerBase.Z;
    var failedStackSources = new List<SceneObject>();
    Console.WriteLine(
        $"[MultiStack] Building {requestedCount}-cube tower at " +
        $"({towerX:F3}, {towerY:F3}); sequence=" +
        $"{string.Join(" -> ", sequence)}.");

    for (int layer = 2; layer <= requestedCount; layer++)
    {
        await Task.Delay(1200);
        var scene = await FetchSceneAsync();
        Assignment assignment;
        try
        {
            assignment = SingleObjectTaskBuilder.BuildStackOntoLocation(
                sequence[layer - 1], scene, towerX, towerY, towerTopZ,
                ++globalStepId, failedStackSources);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiStack] Cannot build layer {layer}: {ex.Message}");
            break;
        }

        Console.WriteLine($"[MultiStack] Layer {layer}/{requestedCount}: {assignment.Reasoning}");
        bool ok = await RunSingleObjectTaskAsync(
            routed,
            scene,
            assignment,
            writeDoneWhenFinished: false,
            rebuildForRetry: (latestScene, retryStepId) =>
                SingleObjectTaskBuilder.BuildStackOntoLocation(
                    sequence[layer - 1], latestScene, towerX, towerY,
                    towerTopZ, retryStepId, failedStackSources),
            failedStackSources: failedStackSources);
        if (!ok)
        {
            Console.WriteLine($"[MultiStack] Layer {layer} failed; stopping tower construction.");
            break;
        }
        towerTopZ += assignment.Source!.Z;
        Console.WriteLine(
            $"[MultiStack] Accumulated tower top Z after layer {layer}: " +
            $"{towerTopZ:F3} m");
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

void WriteBatchFile(BatchPlan batch)
{
    string json = JsonSerializer.Serialize(batch, jsonOptions);
    string tempPath = batchPlanPath + ".tmp";
    File.WriteAllText(tempPath, json);
    File.Move(tempPath, batchPlanPath, true);
    File.WriteAllText(Path.Combine(localOutputDir, $"batch_{batch.BatchId}.json"), json);
}

async Task<BatchExecutionResult?> WaitForBatchDoneAsync(int batchId, double timeoutSec)
{
    var start = DateTime.UtcNow;
    while ((DateTime.UtcNow - start).TotalSeconds < timeoutSec)
    {
        if (File.Exists(batchDonePath))
        {
            try
            {
                string json = File.ReadAllText(batchDonePath);
                var result = JsonSerializer.Deserialize<BatchExecutionResult>(json, jsonOptions);
                if (result != null && result.BatchId == batchId)
                {
                    File.Delete(batchDonePath);
                    return result;
                }
            }
            catch { /* writer may still be replacing the report */ }
        }
        await Task.Delay(200);
    }
    return null;
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

