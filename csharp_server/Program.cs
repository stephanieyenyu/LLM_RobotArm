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
// 純模擬（Unity「模式：純模擬」）：場景改由 Isaac Sim 提供，端點與格式同 perception_server
using HttpClient simPerceptionClient = new()
{
    BaseAddress = IsaacSimVerifier.PerceptionBaseUri,
    Timeout = TimeSpan.FromSeconds(30),
};

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
};

// 啟動：確認 perception_server 已在執行。只有實機模式需要它；純模擬的場景來自 Isaac Sim，所以連不上也不結束，
// 實機任務開始時讀不到場景才會停下
try
{
    var health = await httpClient.GetFromJsonAsync<JsonElement>("health", jsonOptions);
    string status = health.TryGetProperty("status", out var s) ? s.GetString() ?? "?" : "?";
    Console.WriteLine($"[perception_server] 已連線 (status={status})");
}
catch (Exception ex)
{
    Console.WriteLine($"[perception_server] 無法連線 → {ex.Message}");
    Console.WriteLine("[perception_server] 實機模式需要 perception_server；純模擬（Unity 按「模式」切成純模擬）不需要，場景改由 Isaac Sim 提供。");
}

// 檔案路徑
string unityStreamingAssets = "../unity_project/Assets/StreamingAssets";
string inputPath = Path.Combine(unityStreamingAssets, "user_input.txt");
string currentStepPath = Path.Combine(unityStreamingAssets, "current_step.json");
string stepDonePath = Path.Combine(unityStreamingAssets, "step_done.json");
// Unity 模擬結束比對 bitmap 的結果，印在這個 terminal
string simCheckPath = Path.Combine(unityStreamingAssets, "sim_check.json");
string localOutputDir = "outputs";
Directory.CreateDirectory(localOutputDir);

Console.WriteLine();
Console.WriteLine("=== LLM Planner（分層架構）已啟動 ===");
Console.WriteLine($"監聽：{Path.GetFullPath(inputPath)}");
Console.WriteLine($"每步指令：{Path.GetFullPath(currentStepPath)}");
Console.WriteLine($"執行回報：{Path.GetFullPath(stepDonePath)}");
Console.WriteLine("純模擬 / 實機用 Unity 的「模式」按鈕切換（StreamingAssets/run_mode.json），每個指令開始時讀一次。");
Console.WriteLine("等待 Unity 輸入指令...");
Console.WriteLine();

// 建立各 layer instance
var workspace = new WorkspaceBounds();
var patternDesigner = new PatternDesigner(workspace.MaxRows, workspace.MaxCols);
// 3D 跟 2D 一樣走雙模型（共用 PatternDesigner 的 Gemini 連線、投票 prompt 與 pattern審查開關）
var spatialPatternDesigner = new SpatialPatternDesigner(
    workspace.SpatialRows, workspace.SpatialCols, workspace.SpatialLayers, patternDesigner);
var motionPlanner = new MotionPlanner();
// 3D 模擬驗證最後一關：LLM 看 Isaac 的模擬畫面判定
var simulationImageJudge = new SimulationImageJudge();
var commandRouter = new CommandRouter();

// 清空 input 與舊檔案
if (File.Exists(inputPath)) File.WriteAllText(inputPath, "");
if (File.Exists(currentStepPath)) File.Delete(currentStepPath);
if (File.Exists(stepDonePath)) File.Delete(stepDonePath);
if (File.Exists(simCheckPath)) File.Delete(simCheckPath);

// Keep step IDs unique when dotnet is restarted while Unity remains in Play
// Mode; otherwise Unity can mistake a new Step 1/2/... for an old command.
int globalStepId = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
const double UNITY_STEP_TIMEOUT_SEC = 600;

// 一鍵開關（實驗組 / 對照組），只控制 Unity「模擬結束比對 bitmap」。
// 每個指令開頭從 Unity 的旗標檔讀一次，蓋在整批任務上，中途切換不會一半開、一半關。
// 動作規劃檢查（MotionPlanValidator）一律開著，不受這個開關影響。
bool verificationEnabled = true;

// 純模擬 / 實機（RunModeConfig）：每個指令開頭讀一次，整個任務用同一個模式。
// 純模擬時場景來自 Isaac Sim（沒開時 2D 用內建的虛擬世界）；2D 只在 Unity 模擬，3D 的批次標 robot_target = "ursim"，只有 URSim 會動
bool simMode = false;
HttpClient perceptionClient = httpClient;
string? loadedSimScene = null;
// 純模擬沒開 Isaac Sim 時，2D 改用內建的虛擬世界（VirtualSimWorld，同 main）；3D 一定要 Isaac
VirtualSimWorld? virtualWorld = null;
VirtualSimWorld.Delete(Path.GetFullPath(unityStreamingAssets));
// 收到指令時讀一次的相機內參與位姿（Isaac 用來擺模擬相機；純模擬是 null，用場景檔的相機）
JsonElement? taskCamera = null;
// 最近一次 Unity 模擬的比對報告（sim_check.json），2D 驗證流程用
SimulationCheckReport? lastSimCheck = null;

// 驗證流程（三個版本相同，2026-10-08）：相機只在收到指令時用一次（場景與相機位姿），之後的驗證都看模擬畫面。
//   2D（排平面圖形、相對移動）：Unity 模擬整批（只預覽）→ Unity 畫面重疊率 > 90% ＋ LLM 看 Unity 主相機畫面判 PASS → 才送實體手臂。
//       純模擬：Unity 模擬通過就算執行完成，不需要 Isaac。
//   3D（排立體圖形、疊放）：Unity 只把軌跡轉送 URSim（不預覽），Isaac 跟隨 URSim 做物理 → Isaac 物理檢查 ＋ 畫面重疊率
//       （整體對齊 ±5 mm 後 > 90%）＋ LLM 看 Isaac 畫面判 PASS → 才送實體手臂（純模擬送 URSim）。
//   手臂做完就結束，不再用相機驗證。「Unity驗證」開關關掉（對照組）時，2D 的重疊率與畫面判定只記錄、照常執行；
//   3D 的 Isaac 驗證不受開關影響。

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
    RunModeConfig mode;
    try { mode = RunModeConfig.Load(Path.GetFullPath(unityStreamingAssets)); }
    catch (InvalidDataException ex)
    {
        // 不猜模式：猜錯成實機會讓本來只想模擬的指令動到實體手臂
        Console.WriteLine($"[模式] {ex.Message}；不確定是模擬還是實機，這個指令不執行。");
        return;
    }
    simMode = mode.IsSim;
    perceptionClient = simMode ? simPerceptionClient : httpClient;
    if (simMode && !await PrepareSimWorldAsync(mode))
        return;
    if (!simMode)
        Console.WriteLine("[模式] 實機：場景來自相機（perception_server），動作送實體手臂。");

    verificationEnabled = VerificationSwitch.ReadEnabled();
    Console.WriteLine(verificationEnabled
        ? $"[Verification] 模擬結束比對開啟（實驗組）：比對不通過就不送{ArmName()}"
        : $"[Verification] 模擬結束比對關閉（對照組）：比對結果只記錄，{ArmName()}照常執行");

    // 相機只在收到指令時用一次：這一份場景與相機內參位姿，之後的驗證都看模擬畫面
    var initialScene = await FetchSceneAsync();
    if (initialScene.Count == 0)
    {
        Console.WriteLine("[CommandRouter] Scene contains no objects with valid coordinates.");
        return;
    }
    taskCamera = simMode ? null : await FetchCameraAsync();

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
            await RunPatternTaskBatchAsync(userCommand, initialScene);
            break;
        case "arrange_3d_pattern":
            await RunSpatialPatternTaskBatchAsync(userCommand, initialScene);
            break;
        case "move_relative":
            await RunSingleObjectTaskBatchAsync(userCommand, routed, initialScene);
            break;
        case "stack":
            if (routed.StackSequence.Count > 2 || (routed.ObjectCount ?? 2) > 2)
                await RunMultiStackTaskBatchAsync(userCommand, routed, initialScene);
            else
                await RunSingleObjectTaskBatchAsync(userCommand, routed, initialScene);
            break;
        default:
            Console.WriteLine($"[CommandRouter] Unsupported action: {routed.Action}");
            break;
    }
}

async Task RunPatternTaskBatchAsync(string userCommand, List<SceneObject> initialSnap)
{
    string blockColor = GuessBlockColor(userCommand, initialSnap);
    var colorSupplies = initialSnap
        .Where(s => TaskAssigner.IsInSupplyZone(s) &&
                    (s.Name == $"{blockColor}_cube" ||
                     s.Name == $"{blockColor}_domino"))
        .ToList();
    var safeColorSupplies = colorSupplies
        .Where(TaskAssigner.IsSourceReachSafe)
        .ToList();
    int cubeBudget = safeColorSupplies.Count(s => s.Name == $"{blockColor}_cube");
    int dominoBudget = safeColorSupplies.Count(s => s.Name == $"{blockColor}_domino");

    Console.WriteLine($"[Batch] 安全可用庫存：{cubeBudget} cube + {dominoBudget} domino; " +
                      $"排除 {colorSupplies.Count - safeColorSupplies.Count} 顆不可安全到達的積木。");

    Console.WriteLine($"[Batch] 使用任務開始時的單一 scene snapshot 規劃全部步驟。");
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
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }

    var realize = LayoutRealizer.Realize(pattern, workspace, cubeBudget, dominoBudget);
    for (int compactAttempt = 1; realize.Error != null && compactAttempt <= 2; compactAttempt++)
    {
        int span = compactAttempt == 1 ? 4 : 3;
        string feedback = $"Physical placement failed: {realize.Error}. Keep the original target identity and " +
            $"the same {workspace.MaxRows}x{workspace.MaxCols} canvas. Do not enlarge the canvas or change cell spacing " +
            $"({workspace.CellSize:F3} m). Redraw a compact recognizable candidate whose occupied bounding box " +
            $"is at most {span} rows by {span} columns; leave remaining cells empty. " +
            "If exact recognizable identity cannot fit these constraints, report infeasible instead of forcing it.";
        Console.WriteLine($"[Layer 2 compact retry] {compactAttempt}/2: 維持畫布與格距，佔用範圍最多 {span}x{span}；重新生成與評審。");
        try
        {
            pattern = await patternDesigner.DesignAsync(userCommand, blockColor, cubeBudget, dominoBudget,
                feedback, span);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Layer 1 compact] 無可接受候選：{ex.Message}");
            continue;
        }
        realize = LayoutRealizer.Realize(pattern, workspace, cubeBudget, dominoBudget);
    }

    int br = pattern.Bitmap!.GetLength(0), bc = pattern.Bitmap.GetLength(1);
    var rows = new List<string>();
    Console.WriteLine($"[Layer 1] pattern={pattern.PatternId}, bitmap={br}x{bc}");
    for (int r = 0; r < br; r++)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < bc; c++) sb.Append(pattern.Bitmap[r, c] == 1 ? "■" : "□");
        rows.Add(sb.ToString());
        Console.WriteLine("           " + sb);
    }

    File.WriteAllText(
        Path.Combine(localOutputDir, $"pattern_{pattern.PatternId}.json"),
        JsonSerializer.Serialize(new
        {
            pattern_id = pattern.PatternId,
            block_color = pattern.BlockColor,
            bitmap = rows,
            rows = br,
            cols = bc,
            timestamp = DateTime.Now.ToString("s"),
        }, jsonOptions));

    if (realize.Error != null || realize.Targets == null)
    {
        Console.WriteLine($"[Layer 2] {realize.Error}");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }

    Console.WriteLine($"[Layer 2] 展開成 {realize.Targets.Count} 個 target cells "
                      + $"({realize.Targets.Count(t => t.ExpectedShape == "domino")} domino + "
                      + $"{realize.Targets.Count(t => t.ExpectedShape == "cube")} cube)");
    Console.WriteLine($"[Layer 2] Placement shift: X={realize.PlacementShiftX:+0.000;-0.000;0.000} m, " +
                      $"Y={realize.PlacementShiftY:+0.000;-0.000;0.000} m (CellSize={workspace.CellSize:F3} m)");

    var virtualScene = CloneScene(initialSnap);
    var remainingTargets = new List<TargetCell>(realize.Targets);
    var plannedTargets = new List<TargetCell>();
    var steps = new List<StepEnvelope>();

    while (remainingTargets.Count > 0)
    {
        int stepId = ++globalStepId;
        var assignment = TaskAssigner.Assign(
            remainingTargets, virtualScene, stepId,
            recoveryMode: false, protectedTargets: plannedTargets);
        if (assignment == null)
        {
            Console.WriteLine("[Batch Layer 3] 沒有可執行的 assignment（supply 用完或不足）");
            Console.WriteLine("[Batch] 未排完全部 target，取消送出，避免只執行半成品。");
            foreach (var t in remainingTargets)
            {
                Console.WriteLine(
                    $"        missing r{t.Row}c{t.Col} {t.ExpectedColor}_{t.ExpectedShape} " +
                    $"at ({t.WorldX:F3},{t.WorldY:F3})");
            }
            WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
            return;
        }

        Console.WriteLine($"[Batch Layer 3] {assignment.Reasoning}");
        var envelope = await BuildStepEnvelopeAsync(assignment, virtualScene);
        if (envelope == null)
        {
            Console.WriteLine("[Batch] 規劃中止；尚未送給 Unity 執行。");
            WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
            return;
        }

        steps.Add(envelope);
        UpdateVirtualSceneAfterPlannedStep(virtualScene, assignment);
        plannedTargets.Add(assignment.Target!);
        remainingTargets.RemoveAll(t => t.Row == assignment.Target!.Row && t.Col == assignment.Target.Col);
    }

    await Run2DVerifiedAsync(userCommand, $"arrange pattern {pattern.PatternId}", steps, initialSnap,
        ExpectedFromTargets(realize.Targets), rows, workspace.CellSize, workspace.CellSize);
}

async Task RunSingleObjectTaskBatchAsync(string userCommand, RoutedCommand routed, List<SceneObject> initialScene)
{
    Assignment assignment;
    try
    {
        assignment = SingleObjectTaskBuilder.Build(routed, initialScene, ++globalStepId);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SingleObject Batch] Cannot build task: {ex.Message}");
        SendDone();
        return;
    }

    Console.WriteLine($"[SingleObject Batch] {assignment.Reasoning}");
    var envelope = await BuildStepEnvelopeAsync(assignment, initialScene);
    if (envelope == null)
    {
        SendDone();
        return;
    }
    var source = assignment.Source!;
    var target = assignment.Target!;
    if (routed.Action != "stack")
    {
        // 相對移動是 2D：預期格 = 這塊積木放到目標的位置（桌上一層）
        var expected2d = new List<ExpectedCell> { new() { Row = 0, Col = 0, X = target.WorldX, Y = target.WorldY,
            Z = LayeredGraspGeometry.BlockLayerM, Shape = source.Shape ?? "cube", Orientation = target.ExpectedOrientation ?? source.Orientation } };
        await Run2DVerifiedAsync(userCommand, routed.Action, new List<StepEnvelope> { envelope }, initialScene, expected2d,
            new List<string> { "■" }, LayeredGraspGeometry.BlockLayerM, LayeredGraspGeometry.BlockLayerM);
        return;
    }

    // 疊放是 3D：3D 分層夾取的頂面（來源對齊層高；放好後 = 下方積木頂面 + 一層），預期格 = 下方積木 + 疊上去的這塊
    double lowerTopPerceived = target.WorldZ - source.Z - SingleObjectTaskBuilder.StackReleaseClearanceM;
    double lowerTop = LayeredGraspGeometry.SnapTopToLayer(lowerTopPerceived);
    envelope.SourceTopM = LayeredGraspGeometry.SnapTopToLayer(source.Z);
    envelope.TargetTopM = lowerTop + LayeredGraspGeometry.BlockLayerM;
    var lower = initialScene.Where(o => !ReferenceEquals(o, source))
        .OrderBy(o => Math.Pow(o.X - target.WorldX, 2) + Math.Pow(o.Y - target.WorldY, 2)).FirstOrDefault();
    var expected3d = new List<ExpectedCell>
    {
        new() { X = target.WorldX, Y = target.WorldY, Z = lowerTop, Shape = lower?.Shape ?? "cube", Orientation = lower?.Orientation },
        new() { X = target.WorldX, Y = target.WorldY, Z = envelope.TargetTopM, Shape = source.Shape ?? "cube", Orientation = source.Orientation },
    };
    await Run3DVerifiedAsync(userCommand, "stack", new List<StepEnvelope> { envelope },
        new List<int> { SourceIndexIn(initialScene, source, new List<int>()) }, initialScene, expected3d,
        LayeredGraspGeometry.BlockLayerM, LayeredGraspGeometry.BlockLayerM);
}

async Task RunMultiStackTaskBatchAsync(string userCommand, RoutedCommand routed, List<SceneObject> initialScene)
{
    List<string> sequence = routed.StackSequence.Count >= 2
        ? routed.StackSequence
        : Enumerable.Repeat(routed.ObjectName ?? "", routed.ObjectCount ?? 2).ToList();
    if (sequence.Count < 2 || sequence.Any(string.IsNullOrWhiteSpace))
    {
        Console.WriteLine("[MultiStack Batch] Invalid stack sequence.");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }
    if (sequence.Any(name => !name.EndsWith("_cube", StringComparison.Ordinal)))
    {
        Console.WriteLine("[MultiStack Batch] Multi-layer stacking currently supports cubes only.");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }

    var virtualScene = CloneScene(initialScene);
    string baseName = sequence[0];
    SceneObject towerBase = virtualScene
        .Where(o => o.Name == baseName)
        .OrderByDescending(o => o.X >= 0.30)
        .ThenByDescending(o => o.X)
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"Base object '{baseName}' was not found.");

    double towerX = towerBase.X;
    double towerY = towerBase.Y;
    double towerTopZ = towerBase.Z;
    var failedStackSources = new List<SceneObject>();
    var steps = new List<StepEnvelope>();
    // 3D 分層夾取與 Isaac 驗證用：塔底的真實頂面（對齊層高）、每一步來源在任務開始場景的 index、預期格（塔底 + 每一層）
    double baseTop = LayeredGraspGeometry.SnapTopToLayer(towerBase.Z);
    var sourceIndices = new List<int>();
    var expected = new List<ExpectedCell> { new() { X = towerX, Y = towerY, Z = baseTop, Shape = towerBase.Shape ?? "cube",
        Orientation = towerBase.Orientation } };

    Console.WriteLine(
        $"[MultiStack Batch] Planning {sequence.Count}-cube tower at " +
        $"({towerX:F3}, {towerY:F3}); sequence={string.Join(" -> ", sequence)}.");

    for (int layer = 2; layer <= sequence.Count; layer++)
    {
        Assignment assignment;
        try
        {
            assignment = SingleObjectTaskBuilder.BuildStackOntoLocation(
                sequence[layer - 1], virtualScene, towerX, towerY, towerTopZ,
                ++globalStepId, failedStackSources);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiStack Batch] Cannot build layer {layer}: {ex.Message}");
            break;
        }

        Console.WriteLine($"[MultiStack Batch] Layer {layer}/{sequence.Count}: {assignment.Reasoning}");
        var envelope = await BuildStepEnvelopeAsync(assignment, virtualScene);
        if (envelope == null)
        {
            SendDone();
            return;
        }
        envelope.SourceTopM = LayeredGraspGeometry.SnapTopToLayer(assignment.Source!.Z);
        envelope.TargetTopM = baseTop + (layer - 1) * LayeredGraspGeometry.BlockLayerM;
        steps.Add(envelope);
        sourceIndices.Add(SourceIndexIn(initialScene, assignment.Source, sourceIndices));
        expected.Add(new ExpectedCell { X = towerX, Y = towerY, Z = envelope.TargetTopM, Shape = "cube" });
        UpdateVirtualSceneAfterPlannedStep(virtualScene, assignment);
        towerTopZ += assignment.Source!.Z;
    }
    if (steps.Count == 0)
    {
        Console.WriteLine("[MultiStack Batch] 沒有排出任何一層，取消。");
        SendDone();
        return;
    }

    await Run3DVerifiedAsync(userCommand, "multi-stack", steps, sourceIndices, initialScene, expected,
        LayeredGraspGeometry.BlockLayerM, LayeredGraspGeometry.BlockLayerM);
}

// 3D 排立體圖形：流程跟 2D 的 RunPatternTaskBatchAsync 一樣，規則寫死
//   Layer 1  雙 LLM 設計俯視高度圖（SpatialPatternDesigner，畫布 SpatialRows × SpatialCols × SpatialLayers）
//   Layer 2  SpatialLayoutRealizer：固定原點與格距，每一格每一層一個 target，檢查庫存與可達範圍
//   Layer 3  第 1 層照 2D 的 TaskAssigner（遠端優先、補貨區最近的同色 cube）；第 2 層起逐層往上疊，
//            同一層也是遠端優先，來源與放開高度照多層疊放的規則（SingleObjectTaskBuilder.BuildStackOntoLocation）
//   Layer 4  每一步由 MotionPlanner 組動作、MotionPlanValidator 檢查
//   驗證     Run3DVerifiedAsync：Unity 只轉送 URSim，Isaac 物理檢查＋畫面重疊率＋LLM 看 Isaac 畫面，都通過才送實體手臂
// 任何一格排不出來就整批取消，不送半成品
async Task RunSpatialPatternTaskBatchAsync(string userCommand, List<SceneObject> initialScene)
{
    string color = GuessBlockColor(userCommand, initialScene);
    string cubeName = $"{color}_cube";
    var colorSupplies = initialScene
        .Where(o => o.Name == cubeName && TaskAssigner.IsInSupplyZone(o))
        .ToList();
    var safeColorSupplies = colorSupplies.Where(TaskAssigner.IsSourceReachSafe).ToList();
    int cubeBudget = safeColorSupplies.Count;
    Console.WriteLine($"[3D Batch] 安全可用庫存：{cubeBudget} cube；" +
                      $"排除 {colorSupplies.Count - safeColorSupplies.Count} 顆不可安全到達的積木。");
    Console.WriteLine("[3D Batch] 使用任務開始時的單一 scene snapshot 規劃全部步驟。");
    Console.WriteLine(
        $"[3D Batch Layer 1] 呼叫 LLM 設計立體圖形 (color={color}, cubes={cubeBudget}, volume=" +
        $"{workspace.SpatialRows}x{workspace.SpatialCols}x{workspace.SpatialLayers})...");

    SpatialPattern pattern;
    try
    {
        pattern = await spatialPatternDesigner.DesignAsync(userCommand, color, cubeBudget);
    }
    catch (SpatialPatternInfeasibleException ex)
    {
        Console.WriteLine($"[3D Batch Layer 1] 不可執行：{ex.Message}");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[3D Batch Layer 1] pattern 設計失敗：{ex.Message}");
        Console.WriteLine(ex.ToString());
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }

    int[,] heights = pattern.ColumnHeights!;
    int hr = heights.GetLength(0), hc = heights.GetLength(1);
    int maxLayer = 0;
    var heightRows = new List<string>();
    for (int r = 0; r < hr; r++)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < hc; c++)
        {
            sb.Append(heights[r, c]);
            maxLayer = Math.Max(maxLayer, heights[r, c]);
        }
        heightRows.Add(sb.ToString());
    }
    Console.WriteLine($"[3D Batch Layer 1] pattern={pattern.PatternId}，俯視高度圖（數字 = 那格疊幾層）：");
    foreach (var row in heightRows) Console.WriteLine("           " + string.Join(" ", row.ToCharArray()));
    Console.WriteLine("[3D Batch Layer 1] 正面圖（上 = 最高層，從 -Y 那側看，往右 = +X）：");
    for (int z = maxLayer; z >= 1; z--)
    {
        var line = new System.Text.StringBuilder();
        for (int c = 0; c < hc; c++)
        {
            bool filled = false;
            for (int r = 0; r < hr; r++) filled |= heights[r, c] >= z;
            line.Append(filled ? "■" : "□");
        }
        Console.WriteLine("           " + line);
    }

    File.WriteAllText(
        Path.Combine(localOutputDir, $"pattern3d_{pattern.PatternId}.json"),
        JsonSerializer.Serialize(new
        {
            pattern_id = pattern.PatternId,
            block_color = pattern.BlockColor,
            column_heights = heightRows,
            rows = hr,
            cols = hc,
            timestamp = DateTime.Now.ToString("s"),
        }, jsonOptions));

    var realize = SpatialLayoutRealizer.Realize(pattern, workspace, cubeBudget);
    if (realize.Error != null || realize.Targets == null)
    {
        Console.WriteLine($"[3D Batch Layer 2] {realize.Error}");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
        return;
    }
    Console.WriteLine($"[3D Batch Layer 2] 展開成 {realize.Targets.Count} 個 target（" +
                      string.Join("、", realize.Targets.GroupBy(t => t.Layer).OrderBy(g => g.Key)
                          .Select(g => $"第 {g.Key} 層 {g.Count()} 個")) +
                      $"），原點 X={workspace.TargetOriginX:F3} Y={workspace.SpatialTargetOriginY:F3}，" +
                      $"格距 {workspace.SpatialCellSize:F3} m");

    var virtualScene = CloneScene(initialScene);
    var steps = new List<StepEnvelope>();
    var plannedTargets = new List<TargetCell>();
    var towerTopZ = new Dictionary<(int Row, int Col), double>();
    // 每一步的來源在任務開始場景裡的 index（Isaac 用它對到要搬的積木）
    var sourceIndices = new List<int>();
    int SourceIndexOf(SceneObject source)
    {
        int best = -1;
        double bestD = 0.02;
        for (int i = 0; i < initialScene.Count; i++)
        {
            if (initialScene[i].Name != source.Name || sourceIndices.Contains(i)) continue;
            double d = Math.Sqrt(Math.Pow(initialScene[i].X - source.X, 2) + Math.Pow(initialScene[i].Y - source.Y, 2));
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }
    // 3D 分層夾取的頂面：來源是桌上的積木，對齊層高；放好後的頂面 = 第幾層 × 積木高（固定布局寫死）
    void AddLayeredStep(StepEnvelope envelope, Assignment assignment, int layer)
    {
        envelope.SourceTopM = LayeredGraspGeometry.SnapTopToLayer(assignment.Source!.Z);
        envelope.TargetTopM = layer * LayeredGraspGeometry.BlockLayerM;
        steps.Add(envelope);
        sourceIndices.Add(SourceIndexOf(assignment.Source));
    }

    void CancelBatch(string why, IEnumerable<TargetCell> missing)
    {
        Console.WriteLine($"[3D Batch Layer 3] {why}");
        Console.WriteLine("[3D Batch] 未排完全部 target，取消送出，避免只執行半成品。");
        foreach (var t in missing)
            Console.WriteLine($"        missing r{t.Row}c{t.Col} 第 {t.Layer} 層 {t.ExpectedColor}_{t.ExpectedShape} " +
                              $"at ({t.WorldX:F3},{t.WorldY:F3})");
        WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
    }

    // 第 1 層：跟 2D 一樣，遠端優先、從補貨區挑最近的同色 cube、不拿已經規劃好的格子
    var remainingBase = realize.Targets.Where(t => t.Layer == 1).ToList();
    while (remainingBase.Count > 0)
    {
        var assignment = TaskAssigner.Assign(
            remainingBase, virtualScene, ++globalStepId,
            recoveryMode: false, protectedTargets: plannedTargets);
        if (assignment == null)
        {
            CancelBatch("沒有可執行的 assignment（supply 用完或不足）",
                remainingBase.Concat(realize.Targets.Where(t => t.Layer > 1)));
            return;
        }
        Console.WriteLine($"[3D Batch Layer 3] 第 1 層 {assignment.Reasoning}");
        var envelope = await BuildStepEnvelopeAsync(assignment, virtualScene);
        if (envelope == null)
        {
            Console.WriteLine("[3D Batch] 規劃中止；尚未送給 Unity 執行。");
            WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
            return;
        }
        AddLayeredStep(envelope, assignment, 1);
        UpdateVirtualSceneAfterPlannedStep(virtualScene, assignment);
        plannedTargets.Add(assignment.Target!);
        towerTopZ[(assignment.Target!.Row, assignment.Target.Col)] = assignment.Source!.Z;
        remainingBase.RemoveAll(t => t.Row == assignment.Target.Row && t.Col == assignment.Target.Col);
    }

    // 第 2 層起：一層一層往上疊（整層疊完才疊下一層），同一層也是遠端優先（Y 大、X 大）
    for (int layer = 2; layer <= maxLayer; layer++)
    {
        var layerTargets = realize.Targets.Where(t => t.Layer == layer)
            .OrderByDescending(t => t.WorldY).ThenByDescending(t => t.WorldX).ToList();
        foreach (var target in layerTargets)
        {
            var key = (target.Row, target.Col);
            Assignment assignment;
            try
            {
                assignment = SingleObjectTaskBuilder.BuildStackOntoLocation(
                    cubeName, virtualScene, target.WorldX, target.WorldY, towerTopZ[key],
                    ++globalStepId, sourceZoneXMax: workspace.SupplyZoneXMax);
            }
            catch (Exception ex)
            {
                CancelBatch($"第 {layer} 層 r{target.Row}c{target.Col} 無法安排：{ex.Message}",
                    realize.Targets.Where(t => t.Layer > layer || (t.Layer == layer && !plannedTargets.Contains(t))));
                return;
            }
            assignment.Target!.Row = target.Row;
            assignment.Target.Col = target.Col;
            assignment.Target.Layer = layer;
            Console.WriteLine($"[3D Batch Layer 3] 第 {layer} 層 r{target.Row}c{target.Col} {assignment.Reasoning}");
            var envelope = await BuildStepEnvelopeAsync(assignment, virtualScene);
            if (envelope == null)
            {
                Console.WriteLine("[3D Batch] 規劃中止；尚未送給 Unity 執行。");
                WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });
                return;
            }
            AddLayeredStep(envelope, assignment, layer);
            UpdateVirtualSceneAfterPlannedStep(virtualScene, assignment);
            plannedTargets.Add(target);
            towerTopZ[key] += assignment.Source!.Z;
        }
    }

    await Run3DVerifiedAsync(userCommand, $"3D pattern {pattern.PatternId}", steps, sourceIndices, initialScene,
        ExpectedFromTargets(realize.Targets), workspace.SpatialCellSize, workspace.SpatialCellSize);
}

#pragma warning disable CS8321 // Legacy closed-loop mode kept as a fallback while batch mode is active.
async Task RunPatternTaskAsync(string userCommand)
{
    // 先掃一次，取得 supplies 與 block color 的決策依據
    var initialSnap = await FetchSceneAsync();
    string blockColor = GuessBlockColor(userCommand, initialSnap);
    
    var safeColorSupplies = initialSnap.Where(s =>
        TaskAssigner.IsInSupplyZone(s) &&
        (s.Name == $"{blockColor}_cube" || s.Name == $"{blockColor}_domino") &&
        TaskAssigner.IsSourceReachSafe(s)).ToList();
    int cubeBudget = safeColorSupplies.Count(s => s.Name == $"{blockColor}_cube");
    int dominoBudget = safeColorSupplies.Count(s => s.Name == $"{blockColor}_domino");

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
    Console.WriteLine($"[Layer 2] Placement shift: X={realize.PlacementShiftX:+0.000;-0.000;0.000} m, " +
                      $"Y={realize.PlacementShiftY:+0.000;-0.000;0.000} m (CellSize={workspace.CellSize:F3} m)");

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
        o.Name == cubeName && TaskAssigner.IsInSupplyZone(o));
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
            double targetX = workspace.TargetOriginX + c * workspace.SpatialCellSize;
            double targetY = workspace.TargetOriginY + (rows - 1 - r) * workspace.SpatialCellSize;
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
                .Where(o => o.Name == cubeName && TaskAssigner.IsInSupplyZone(o))
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

#pragma warning restore CS8321

async Task<List<SceneObject>> FetchSceneAsync()
{
    if (simMode && virtualWorld != null) return virtualWorld.Snapshot();
    try
    {
        var world = await perceptionClient.GetFromJsonAsync<ObjectsWorld>("scene", jsonOptions);
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
        Console.WriteLine(simMode
            ? $"[Isaac Sim 場景] fetch scene 失敗（純模擬的場景來自 Isaac Sim /perception/scene）：{ex.Message}"
            : $"[perception] fetch scene 失敗：{ex.Message}");
        return new List<SceneObject>();
    }
}

async Task<StepEnvelope?> BuildStepEnvelopeAsync(
    Assignment assignment,
    IReadOnlyList<SceneObject> planningScene)
{
    MotionPlan? motionPlan = null;
    string validationError = "";
    for (int planAttempt = 1; planAttempt <= 3; planAttempt++)
    {
        try
        {
            motionPlan = await motionPlanner.PlanAsync(
                assignment, planningScene, validationError);
        }
        catch (Exception ex)
        {
            validationError = "Motion Planner call failed: " + ex.Message;
            Console.WriteLine($"[Batch Layer 4A] 第 {planAttempt} 次規劃呼叫失敗：{ex.Message}");
            motionPlan = null;
            continue;
        }

        if (MotionPlanValidator.TryValidate(
                motionPlan, assignment, planningScene, out validationError))
            break;

        Console.WriteLine($"[Batch Layer 4A] 第 {planAttempt} 次規劃未通過安全驗證：{validationError}");
        motionPlan = null;
    }

    if (motionPlan == null)
    {
        Console.WriteLine("[Batch Layer 4A] 無法取得安全的動作規劃");
        return null;
    }

    Console.WriteLine(
        $"[Batch Layer 4A] step {assignment.StepId}: " +
        $"{motionPlan.ActionSequence.Count} functions — {motionPlan.Reasoning}");

    return new StepEnvelope
    {
        StepId = assignment.StepId,
        Done = false,
        SourcePosition = assignment.Source,
        TargetPosition = new SceneObject
        {
            Name = $"target_{assignment.Target!.ExpectedShape}_r{assignment.Target.Row}_c{assignment.Target.Col}",
            X = assignment.Target.WorldX,
            Y = assignment.Target.WorldY,
            Z = assignment.Target.WorldZ,
            Shape = assignment.Target.ExpectedShape,
            Orientation = assignment.Target.ExpectedOrientation,
        },
        Comment = assignment.Reasoning + " | Motion: " + motionPlan.Reasoning,
        ActionSequence = motionPlan.ActionSequence,
    };
}

// 整批送給 Unity 並等回報：步驟換新的 step_id，configure 設定這一批的旗標（preview_only、skip_preview、robot_target…）。
// 純模擬時沒指定 robot_target 的批次一律只送 URSim。回傳 Unity 的執行結果（逾時是 null）
async Task<ExecutionResult?> SendBatchAsync(string comment, List<StepEnvelope> steps, Action<BatchEnvelope>? configure = null)
{
    var candidateSteps = JsonSerializer.Deserialize<List<StepEnvelope>>(
        JsonSerializer.Serialize(steps, jsonOptions), jsonOptions)!;
    foreach (var step in candidateSteps) step.StepId = ++globalStepId;
    int batchId = ++globalStepId;
    var batch = new BatchEnvelope
    {
        BatchId = batchId,
        Done = false,
        Comment = comment,
        Steps = candidateSteps,
        VerificationDisabled = !verificationEnabled,
        CellSizeM = workspace.CellSize,
    };
    configure?.Invoke(batch);
    if (simMode && batch.RobotTarget == null) batch.RobotTarget = "ursim";
    WriteBatchFile(batch);

    Console.WriteLine($"[Batch] 已一次送出 {steps.Count} steps 給 Unity（batch {batchId}，{comment}），等待整批完成...");
    var execResult = await WaitForStepDoneAsync(
        batchId, timeoutSec: UNITY_STEP_TIMEOUT_SEC * Math.Max(1, steps.Count));
    if (execResult == null || !execResult.Completed)
        Console.WriteLine($"[Batch] Unity batch 逾時或失敗：{execResult?.Error}");
    else
        Console.WriteLine($"[Batch] Unity 回報 batch {batchId} 全部完成。");
    return execResult;
}

void SendDone() => WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });

// 2D 驗證（三個版本相同）：Unity 模擬整批（preview_only，手臂不動）→ Unity 畫面重疊率 > 90% ＋ LLM 看 Unity 主相機畫面判 PASS
// → 同一批送實體手臂（SkipPreview）。純模擬：模擬通過就算執行完成，落點寫回模擬世界，不需要 Isaac。
// 「Unity驗證」關閉（對照組）時，重疊率與畫面判定只記錄，照常執行。回傳是否執行完成
async Task<bool> Run2DVerifiedAsync(string userCommand, string label, List<StepEnvelope> steps, List<SceneObject> scene,
    List<ExpectedCell> expected, List<string> bitmapRows, double cellX, double cellY)
{
    string dir = Path.Combine(localOutputDir, $"unity_{DateTime.Now:yyyyMMdd_HHmmss}_{FileSafe(label)}");
    Directory.CreateDirectory(dir);
    string halt = $"{ArmName()}不動";
    Console.WriteLine($"[2D 模擬驗證] {steps.Count} 步先在 Unity 模擬（手臂不動），畫面重疊率與 LLM 畫面判定都通過才送{ArmName()}" +
                      $"{(simMode ? "（純模擬：通過就算執行完成）" : "")}；紀錄：{dir}");
    lastSimCheck = null;
    ClearUnityImages();
    var preview = await SendBatchAsync($"{label}（Unity 模擬驗證）", steps, b =>
    {
        b.PreviewOnly = true;
        b.RobotTarget = simMode ? "ursim" : null;
        b.Bitmap = bitmapRows;
        b.ExpectedCells = expected;
        b.CellSizeM = cellX;
        b.CellSizeXM = cellX;
        b.CellSizeYM = cellY;
    });
    var report = lastSimCheck;
    CopyUnityImages(report, dir, expected);
    File.WriteAllText(Path.Combine(dir, "unity_preview.json"), JsonSerializer.Serialize(preview, jsonOptions));
    if (preview == null || !preview.Completed)
    {
        Console.WriteLine(report is { Performed: true, Passed: false }
            ? $"[2D 模擬驗證] Unity 畫面重疊率 {report.OverlapRatio * 100:F0}% 沒有大於 {report.OverlapThreshold * 100:F0}%；{halt}。"
            : $"[2D 模擬驗證] Unity 模擬沒有完成：{preview?.Error ?? "逾時"}；{halt}。");
        SendDone();
        return false;
    }
    if (report is not { Performed: true })
    {
        Console.WriteLine($"[2D 模擬驗證] Unity 沒有回報畫面重疊率；{halt}。");
        SendDone();
        return false;
    }
    // LLM 視覺驗證：看 Unity 模擬結束時主相機的畫面；目前場景 = 預覽落點更新後的位置
    var previewScene = PreviewScene(preview.FinalBlocks, scene).Scene;
    File.WriteAllText(Path.Combine(dir, "preview_scene.json"), JsonSerializer.Serialize(previewScene, jsonOptions));
    var viewPath = string.IsNullOrEmpty(report.ViewFile) ? null : Path.Combine(dir, report.ViewFile);
    byte[]? view = viewPath != null && File.Exists(viewPath) ? File.ReadAllBytes(viewPath) : null;
    Console.WriteLine("[2D 模擬驗證] 請 LLM 看 Unity 模擬畫面判定...");
    string verdict;
    try
    {
        verdict = await simulationImageJudge.JudgeAsync(userCommand, scene, previewScene, view, dir,
            SimulationImageJudge.UnityImageNote, "unity_judge");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[2D 模擬驗證] 模擬畫面判定呼叫失敗：{ex.Message}；{halt}。");
        SendDone();
        return false;
    }
    bool passed = report.Passed && verdict.Split('\n')[0].Trim() == "PASS";
    if (verdict.Split('\n')[0].Trim() != "PASS")
    {
        Console.WriteLine("[2D 模擬驗證] Unity 模擬畫面判定未通過：");
        Console.WriteLine("        " + verdict.Replace("\n", "\n        "));
    }
    if (!passed)
    {
        if (verificationEnabled)
        {
            Console.WriteLine($"[2D 模擬驗證] 驗證沒有全部通過；{halt}。");
            SendDone();
            return false;
        }
        Console.WriteLine($"[2D 模擬驗證] Unity驗證關閉（對照組）→ 只記錄，{ArmName()}照常執行。");
    }
    if (simMode)
    {
        int moved = await ApplyPreviewResult(preview.FinalBlocks, scene);
        Console.WriteLine($"[2D 模擬驗證] 純模擬：Unity 模擬{(passed ? "驗證通過" : "完成（對照組）")}就算執行完成，" +
                          $"模擬世界照預覽落點更新了 {moved} 個物件（沒有連 URSim 或實體手臂）。");
        SendDone();
        return true;
    }
    Console.WriteLine("[2D 模擬驗證] 同一批送實體手臂（不再預覽）。");
    var execution = await SendBatchAsync(label, steps, b => b.SkipPreview = true);
    SendDone();
    return execution is { Completed: true };
}

// 3D 驗證（三個版本相同）：Unity 只把軌跡轉送 URSim（SkipPreview，不預覽、不比對），Isaac 跟隨 URSim 做物理 →
// Isaac 物理檢查（位置、層高、傾斜、撞動、穩定、指尖撞桌、手臂自撞、URSim 資料）＋ 畫面重疊率（整體對齊 ±5 mm 後 > 90%）
// ＋ LLM 看 Isaac 畫面判 PASS → 同一批送實體手臂（純模擬送 URSim）。任何一關沒過、或 Isaac / URSim 不可用，手臂都不動。
// 純模擬：Isaac 本身就是世界，驗證前記下積木位姿、驗證後放回去。不受「Unity驗證」開關影響。回傳是否執行完成
async Task<bool> Run3DVerifiedAsync(string userCommand, string label, List<StepEnvelope> steps, List<int> sourceIndices,
    List<SceneObject> scene, List<ExpectedCell> expected, double cellX, double cellY)
{
    string halt = simMode ? "正式執行（URSim）不跑" : "實體手臂不動";
    if (simMode && virtualWorld != null)
    {
        Console.WriteLine("[3D 模擬驗證] 純模擬目前沒開 Isaac Sim：3D（疊放）需要 Isaac 做物理驗證，內建的虛擬世界只支援 2D。" +
                          "開好 isaac_sim_server（--ursim_ip）後再下指令。");
        SendDone();
        return false;
    }
    if (sourceIndices.Contains(-1))
    {
        Console.WriteLine($"[3D 模擬驗證] 有來源積木對不到任務開始時的場景，無法交給 Isaac Sim 驗證；{halt}。");
        SendDone();
        return false;
    }
    string verifyDir = Path.Combine(localOutputDir, $"isaac_{DateTime.Now:yyyyMMdd_HHmmss}_{FileSafe(label)}");
    Directory.CreateDirectory(verifyDir);
    var stepBodies = steps.Select((s, k) => (object)new
    {
        source_index = sourceIndices[k],
        target = s.TargetPosition,
        actions = s.ActionSequence,
    }).ToList();
    if (taskCamera != null)
        File.WriteAllText(Path.Combine(verifyDir, "camera.json"), JsonSerializer.Serialize(taskCamera, jsonOptions));
    if (simMode)
    {
        try
        {
            await IsaacSimVerifier.SnapshotWorldAsync();
        }
        catch (SimulationUnavailableException ex)
        {
            Console.WriteLine($"[3D 模擬驗證] 記不下模擬世界的積木位姿：{ex.Message}；{halt}。");
            SendDone();
            return false;
        }
    }
    try
    {
        try
        {
            await IsaacSimVerifier.BeginVerifyAsync(scene, taskCamera, stepBodies, verifyDir, useCurrentWorld: simMode,
                expectedCells: expected, cellXM: cellX, cellYM: cellY);
        }
        catch (SimulationUnavailableException ex)
        {
            Console.WriteLine($"[3D 模擬驗證] {ex.Message}；{halt}。");
            SendDone();
            return false;
        }
        Console.WriteLine($"[3D 模擬驗證] {steps.Count} 步先在 URSim 執行（Unity 只轉送、不預覽），Isaac Sim 跟隨模擬並驗證（紀錄：{verifyDir}）。");
        var ursim = await SendBatchAsync($"{label}（URSim 驗證）", steps, b =>
        {
            b.RobotTarget = "ursim";
            b.LayeredGrasp = true;
            b.SkipPreview = true;
        });
        if (ursim is not { Completed: true })
        {
            Console.WriteLine($"[3D 模擬驗證] URSim 那批沒有完成（路徑檢查或 URSim 執行沒過）：{ursim?.Error ?? "逾時"}；{halt}。");
            SendDone();
            return false;
        }
        VerifyReport report;
        try
        {
            report = await IsaacSimVerifier.EndVerifyAsync(verifyDir);
        }
        catch (SimulationUnavailableException ex)
        {
            Console.WriteLine($"[3D 模擬驗證] {ex.Message}；{halt}。");
            SendDone();
            return false;
        }
        if (report.OverlapRatio is double overlap)
            Console.WriteLine($"[3D 模擬驗證] Isaac 畫面重疊率 {overlap * 100:F0}%（整體對齊後，要大於 90%）；比對圖 {Path.Combine(verifyDir, "isaac_overlap.png")}");
        if (!report.Pass)
        {
            Console.WriteLine($"[3D 模擬驗證] Isaac Sim 驗證未通過，{halt}：");
            foreach (var reason in report.Reasons) Console.WriteLine("        - " + reason);
            SendDone();
            return false;
        }
        Console.WriteLine("[3D 模擬驗證] Isaac Sim 物理檢查與畫面重疊率通過，請 LLM 看 Isaac 的模擬畫面判定...");
        string verdict;
        try
        {
            verdict = await simulationImageJudge.JudgeAsync(userCommand, scene, report.Scene, report.Frame, verifyDir,
                SimulationImageJudge.IsaacImageNote, "isaac_judge");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[3D 模擬驗證] 模擬畫面判定呼叫失敗：{ex.Message}；{halt}。");
            SendDone();
            return false;
        }
        if (verdict.Split('\n')[0].Trim() != "PASS")
        {
            Console.WriteLine($"[3D 模擬驗證] Isaac Sim 模擬畫面判定未通過，{halt}：");
            Console.WriteLine("        " + verdict.Replace("\n", "\n        "));
            SendDone();
            return false;
        }
    }
    finally
    {
        // 還原失敗就不能從正確的狀態繼續：例外往外丟，這個指令停在這裡
        if (simMode)
        {
            await IsaacSimVerifier.RestoreWorldAsync();
            Console.WriteLine("[3D 模擬驗證] 模擬世界已還原成驗證前的狀態。");
        }
    }
    Console.WriteLine(simMode
        ? "[3D 模擬驗證] Isaac Sim 驗證通過，同一批正式執行（純模擬：URSim，不預覽）。"
        : "[3D 模擬驗證] Isaac Sim 驗證通過，同一批送實體手臂（不預覽）。");
    var execution = await SendBatchAsync(label, steps, b =>
    {
        b.LayeredGrasp = true;
        b.SkipPreview = true;
    });
    SendDone();
    return execution is { Completed: true };
}

// 依固定布局的 target 產生預期格（2D、3D 共用；3D 每一格每一層一個，Z = 那一層放好後的頂面）
static List<ExpectedCell> ExpectedFromTargets(IEnumerable<TargetCell> targets) => targets.Select(t => new ExpectedCell
{
    Row = t.Row,
    Col = t.Col,
    SecondRow = t.SecondRow ?? -1,
    SecondCol = t.SecondCol ?? -1,
    X = t.WorldX,
    Y = t.WorldY,
    Z = t.WorldZ,
    Shape = t.ExpectedShape,
    Orientation = t.ExpectedOrientation,
}).ToList();

// 來源在任務開始場景裡的 index（Isaac 用它對到要搬的積木）：同名、XY 2 cm 內最近、還沒被用過的那塊；對不到是 -1
static int SourceIndexIn(List<SceneObject> scene, SceneObject source, ICollection<int> used)
{
    int best = -1;
    double bestD = 0.02;
    for (int i = 0; i < scene.Count; i++)
    {
        if (scene[i].Name != source.Name || used.Contains(i)) continue;
        double d = Math.Sqrt(Math.Pow(scene[i].X - source.X, 2) + Math.Pow(scene[i].Y - source.Y, 2));
        if (d <= bestD) { bestD = d; best = i; }
    }
    return best;
}

static string FileSafe(string text) =>
    string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));

// 收到指令時讀一次相機內參與相機在 QR 座標系的位姿（perception_server /camera），Isaac 把模擬相機擺到同一個位置
async Task<JsonElement?> FetchCameraAsync()
{
    try
    {
        using var cameraDoc = JsonDocument.Parse(await httpClient.GetStringAsync("camera"));
        return cameraDoc.RootElement.Clone();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[相機] 取不到 perception /camera，Isaac 的模擬相機沿用上次的位置：{ex.Message}");
        return null;
    }
}

// 送出 2D 模擬驗證批次前，刪掉上一批留下的比對圖與 Unity 照片，免得讀到舊的
void ClearUnityImages()
{
    foreach (var name in new[] { "sim_check.png", "unity_top.png", "unity_view.png" })
    {
        var file = Path.Combine(unityStreamingAssets, name);
        if (File.Exists(file)) File.Delete(file);
    }
}

// 比對圖（加註重疊率）與 Unity 照片（正上方 / 主相機）存進這次驗證的資料夾
void CopyUnityImages(SimulationCheckReport? report, string dir, IReadOnlyList<ExpectedCell> cells)
{
    if (report == null) return;
    File.WriteAllText(Path.Combine(dir, "sim_check.json"), JsonSerializer.Serialize(report, jsonOptions));
    if (!string.IsNullOrEmpty(report.ImageFile))
    {
        var source = Path.Combine(unityStreamingAssets, report.ImageFile);
        var target = Path.Combine(dir, report.ImageFile);
        if (File.Exists(source))
        {
            try { SimCheckImage.Annotate(source, target, report, cells); }
            catch (Exception ex)
            {
                Console.WriteLine($"[2D 模擬驗證] 比對圖加註失敗，存原圖：{ex.Message}");
                File.Copy(source, target, true);
            }
        }
    }
    foreach (var name in new[] { report.PhotoFile, report.ViewFile })
    {
        if (string.IsNullOrEmpty(name)) continue;
        var source = Path.Combine(unityStreamingAssets, name);
        if (File.Exists(source)) File.Copy(source, Path.Combine(dir, name), true);
    }
}

// 純模擬的 2D：把 Unity 預覽結束時方塊的落點寫回模擬世界，回傳更新了幾個物件。Unity 回報每塊被放下的方塊
// 預覽前、後的位置，依預覽前的位置對回場景 index（2 cm 內最近的那塊）。內建虛擬世界直接改位置；
// 開著 Isaac 時 Isaac 就是世界，用同樣的物件清單重新載入（方塊直接放到落點，沒有物理過程）
async Task<int> ApplyPreviewResult(List<PreviewBlock>? blocks, List<SceneObject> scene)
{
    var (updated, changed) = PreviewScene(blocks, scene);
    if (changed.Count == 0) return 0;
    if (virtualWorld != null)
    {
        foreach (int i in changed) virtualWorld.Place(i, updated[i], updated[i].Z);
        virtualWorld.Write(Path.GetFullPath(unityStreamingAssets));
    }
    else await IsaacSimVerifier.LoadSimSceneAsync(updated, null);
    return changed.Count;
}
// 預覽結束時的場景（2D 的 LLM 視覺驗證與更新模擬世界共用）：被放下的方塊換成預覽落點，回傳更新後的場景與改到的 index
static (List<SceneObject> Scene, HashSet<int> Changed) PreviewScene(List<PreviewBlock>? blocks, List<SceneObject> scene)
{
    var updated = scene.Select(o => new SceneObject { Name = o.Name, Shape = o.Shape, Orientation = o.Orientation,
        X = o.X, Y = o.Y, Z = o.Z, SkewDeg = o.SkewDeg }).ToList();
    var changed = new HashSet<int>();
    foreach (var b in blocks ?? new List<PreviewBlock>())
    {
        double Distance(int i) => Math.Sqrt(Math.Pow(scene[i].X - b.FromX, 2) + Math.Pow(scene[i].Y - b.FromY, 2));
        int index = Enumerable.Range(0, scene.Count).Where(i => !changed.Contains(i) && Distance(i) <= 0.02)
            .OrderBy(Distance).DefaultIfEmpty(-1).First();
        if (index < 0) continue;
        updated[index].X = b.X;
        updated[index].Y = b.Y;
        updated[index].Z = b.Z;
        updated[index].SkewDeg = 0;
        if (!string.IsNullOrEmpty(b.Orientation)) updated[index].Orientation = b.Orientation;
        changed.Add(index);
    }
    return (updated, changed);
}

List<SceneObject> CloneScene(IEnumerable<SceneObject> scene) =>
    scene.Select(o => new SceneObject
    {
        Name = o.Name,
        X = o.X,
        Y = o.Y,
        Z = o.Z,
        Shape = o.Shape,
        Orientation = o.Orientation,
        SkewDeg = o.SkewDeg,
    }).ToList();

void UpdateVirtualSceneAfterPlannedStep(List<SceneObject> scene, Assignment assignment)
{
    if (assignment.Source == null || assignment.Target == null) return;

    const double samePieceRadiusM = 0.035;
    scene.RemoveAll(o =>
        o.Name == assignment.Source.Name &&
        Math.Sqrt(Math.Pow(o.X - assignment.Source.X, 2) +
                  Math.Pow(o.Y - assignment.Source.Y, 2)) < samePieceRadiusM);

    scene.Add(new SceneObject
    {
        Name = $"{assignment.Target.ExpectedColor}_{assignment.Target.ExpectedShape}",
        X = assignment.Target.WorldX,
        Y = assignment.Target.WorldY,
        Z = assignment.Target.WorldZ,
        Shape = assignment.Target.ExpectedShape,
        Orientation = assignment.Target.ExpectedOrientation,
        SkewDeg = 0,
    });
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
    // 純模擬：逐步送的 step 也只送 URSim（done 訊號不需要）
    if (simMode && !env.Done) env.RobotTarget = "ursim";
    string json = JsonSerializer.Serialize(env, jsonOptions);
    File.WriteAllText(currentStepPath, json);
    File.WriteAllText(Path.Combine(localOutputDir, $"step_{env.StepId}.json"), json);
}

// 純模擬：任務開始時用虛擬場景檔重建 Isaac 世界（reset_each_task = false 時只在換場景檔或第一次才重建，之後接續模擬結果）。
// 連不到 Isaac 時 2D 改用內建的虛擬世界（同 main），3D 才回報需要 Isaac；場景檔有問題就不執行這個指令
async Task<bool> PrepareSimWorldAsync(RunModeConfig mode)
{
    string streaming = Path.GetFullPath(unityStreamingAssets);
    string scenePath = mode.ScenePath(streaming);
    if (!mode.ResetEachTask && loadedSimScene == scenePath)
    {
        Console.WriteLine($"[模式] 純模擬：接續目前的模擬世界（{mode.Scene}，reset_each_task = false）。");
        // 內建虛擬世界：重寫一次世界檔，Unity 重開或換過場景後畫面才跟接續的世界一致
        virtualWorld?.Write(streaming);
        return true;
    }
    try
    {
        var simScene = SimScene.Load(scenePath);
        string inventory = string.Join("、", simScene.Objects.GroupBy(o => o.Name).Select(g => $"{g.Key} ×{g.Count()}"));
        try
        {
            await IsaacSimVerifier.LoadSimSceneAsync(simScene.Objects, simScene.Camera);
            virtualWorld = null;
            VirtualSimWorld.Delete(streaming);
            Console.WriteLine($"[模式] 純模擬：用虛擬場景 {mode.Scene} 重建 Isaac Sim 世界（{inventory}）；" +
                              "2D 由 Unity 模擬驗證，3D 由 Isaac 驗證後送 URSim；實體手臂不動。");
        }
        catch (SimulationUnavailableException ex) when (ex.InnerException is HttpRequestException or TaskCanceledException)
        {
            // 連不到 Isaac Sim（不是 Isaac 回報錯誤）：2D 用內建的虛擬世界，遇到 3D 才回報需要 Isaac
            virtualWorld = new VirtualSimWorld(simScene.Objects);
            virtualWorld.Write(streaming);
            Console.WriteLine($"[模式] 純模擬：連不到 Isaac Sim，用內建的虛擬世界載入 {mode.Scene}（{inventory}）：" +
                              "2D 只用 Unity 模擬；3D（疊放）需要 Isaac Sim 與 URSim。實體手臂不動。");
        }
        loadedSimScene = scenePath;
        return true;
    }
    catch (Exception ex) when (ex is SimulationUnavailableException or IOException or InvalidDataException or JsonException)
    {
        Console.WriteLine($"[模式] 純模擬：無法建立模擬世界 → {ex.Message}；這個指令不執行。");
        return false;
    }
}

string ArmName() => simMode ? "URSim" : "實體手臂";

void WriteBatchFile(BatchEnvelope env)
{
    string json = JsonSerializer.Serialize(env, jsonOptions);
    File.WriteAllText(currentStepPath, json);
    File.WriteAllText(Path.Combine(localOutputDir, $"batch_{env.BatchId}.json"), json);
}

async Task<ExecutionResult?> WaitForStepDoneAsync(int stepId, double timeoutSec)
{
    var start = DateTime.UtcNow;
    while ((DateTime.UtcNow - start).TotalSeconds < timeoutSec)
    {
        // 模擬結束比對一做完就印，不必等實體手臂整批跑完
        TryPrintSimulationCheckReport(stepId);
        if (File.Exists(stepDonePath))
        {
            try
            {
                string json = File.ReadAllText(stepDonePath);
                var result = JsonSerializer.Deserialize<ExecutionResult>(json, jsonOptions);
                if (result != null && result.StepId == stepId)
                {
                    File.Delete(stepDonePath);   // 避免重讀
                    TryPrintSimulationCheckReport(stepId);   // 比對不通過時兩個檔案幾乎同時寫出
                    return result;
                }
            }
            catch { /* 檔案可能仍在寫入，下一輪重試 */ }
        }
        await Task.Delay(200);
    }
    return null;
}

void TryPrintSimulationCheckReport(int batchId)
{
    if (!File.Exists(simCheckPath)) return;
    SimulationCheckReport? report;
    try
    {
        report = JsonSerializer.Deserialize<SimulationCheckReport>(File.ReadAllText(simCheckPath), jsonOptions);
    }
    catch
    {
        return;   // Unity 可能還在寫，下一輪再讀
    }
    if (report == null || report.BatchId != batchId) return;
    try { File.Delete(simCheckPath); } catch (IOException) { }
    lastSimCheck = report;

    var previousColor = Console.ForegroundColor;
    if (!report.Performed)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Bitmap 比對] batch {report.BatchId}：沒有進行 — {report.SkippedReason}");
        Console.ForegroundColor = previousColor;
        return;
    }
    Console.ForegroundColor = report.Passed ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"[Bitmap 比對] batch {report.BatchId}：{(report.Passed ? "✓ 吻合" : "✗ 不吻合")} — " +
                      $"Unity 畫面重疊率 {report.OverlapRatio * 100:F0}%（要大於 {report.OverlapThreshold * 100:F0}%）；" +
                      $"座標比對 {report.CorrectCount}/{report.ExpectedCount} 個物件放對");
    Console.ForegroundColor = previousColor;
    if (!report.Passed)
        Console.WriteLine(report.VerificationEnabled
            ? $"           驗證開啟（實驗組）→ 已停止，{ArmName()}不會動作"
            : $"           驗證關閉（對照組）→ 只記錄，{ArmName()}照常執行");
    int rows = Math.Max(report.ExpectedRows.Count, report.ResultRows.Count);
    if (rows > 0)
    {
        int width = report.ExpectedRows.Concat(report.ResultRows).Max(r => r.Length);
        Console.WriteLine("           預期 bitmap / 座標比對結果（■ 正確  ✗ 少放或錯誤  ● 多放或放錯  □ 空）");
        for (int r = 0; r < rows; r++)
        {
            string want = r < report.ExpectedRows.Count ? report.ExpectedRows[r] : "";
            string got = r < report.ResultRows.Count ? report.ResultRows[r] : "";
            Console.WriteLine($"           {want.PadRight(width)}    {got}");
        }
    }
    foreach (var error in report.Errors) Console.WriteLine("           - " + error);
    foreach (var note in report.Notes) Console.WriteLine("           · " + note);
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

