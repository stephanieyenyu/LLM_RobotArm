using System.Text.Json;

var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../unity_project/Assets/StreamingAssets"));
if (!Directory.Exists(assets)) assets = Path.GetFullPath("../unity_project/Assets/StreamingAssets");
Directory.CreateDirectory(assets);
var output = Path.GetFullPath("outputs/experiments");
Directory.CreateDirectory(output);
using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5000/"), Timeout = TimeSpan.FromSeconds(5) };
// 純模擬（Unity「模擬模式」按鈕）：場景與照片改由 Isaac Sim 提供，格式同 perception_server；紀錄另存，不跟實機成功率混在一起
using var simPerception = new HttpClient { BaseAddress = IsaacSimExecutor.PerceptionBaseUri, Timeout = TimeSpan.FromSeconds(30) };
var simOutput = Path.GetFullPath("outputs/experiments_sim");
var perception = http;
bool sim = false;
string? loadedSimScene = null;
// 純模擬沒開 Isaac Sim 時，2D 改用 csharp_server 內建的虛擬世界（VirtualSimWorld）；上次留下的世界檔先清掉
VirtualSimWorld? virtualWorld = null;
VirtualSimWorld.Delete(assets);
// 3D 正式執行沿用驗證過的整批軌跡；驗證後來源積木位置變動超過這個距離（相機抖動以外）就不執行
const double VerifiedSourceDriftM = 0.010;
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var model = Environment.GetEnvironmentVariable("ROBOT_MODEL") ?? "gpt-5";
var llm = new ExperimentLlm(model);
// release 260917 的 PatternDesigner（prompt 原封不動）：每個任務一開始都先畫目標 bitmap
// （最多 5×5，OpenAI 與 Gemini 各畫一張、交叉審查、加權投票）；不分類指令
var patternDesigner = new PatternDesigner(5, 5, model);
var baselinePath = Path.Combine(output, "initial_scene.json");
// 預設每個任務以收到指令時的桌面為起點；FIXED_BASELINE=1 才要求每個任務先恢復成同一個固定配置
bool fixedBaseline = Environment.GetEnvironmentVariable("FIXED_BASELINE") == "1";
var baseline = fixedBaseline && File.Exists(baselinePath)
    ? JsonSerializer.Deserialize<List<SceneObject>>(File.ReadAllText(baselinePath), json) : null;
int stepId = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
if (fixedBaseline)
{
    Console.WriteLine("自由規劃實驗：每任務最多 10 次；任務間恢復初始桌面，任務內不重置。");
    Console.WriteLine($"初始配置：{baselinePath}；第一次任務建立。更換配置需停止服務後移除此檔。");
}
else
{
    Console.WriteLine("自由規劃實驗：每任務最多 10 次；每個任務以收到指令時的桌面為起點，任務內不重置。");
    Console.WriteLine("（要求每個任務先恢復同一個固定配置：setx FIXED_BASELINE 1 後重開 terminal）");
}
Console.WriteLine("純模擬 / 實機用 Unity 的「模擬模式」按鈕切換（StreamingAssets/run_mode.json），每個任務開始時讀一次。");
while (true)
{
    var input = Path.Combine(assets, "user_input.txt");
    if (!File.Exists(input) || string.IsNullOrWhiteSpace(File.ReadAllText(input))) { await Task.Delay(500); continue; }
    var goal = File.ReadAllText(input).Trim();
    File.WriteAllText(input, "");
    RunModeConfig mode;
    try { mode = RunModeConfig.Load(assets); }
    catch (InvalidDataException ex)
    {
        Console.WriteLine($"[模式] {ex.Message}；不確定是模擬還是實機，這個指令不執行。");
        continue;
    }
    sim = mode.IsSim;
    perception = sim ? simPerception : http;
    var root = sim ? simOutput : output;
    Directory.CreateDirectory(root);
    var run = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
    Directory.CreateDirectory(run);
    File.WriteAllText(Path.Combine(run, "task.txt"), goal);
    if (sim) Console.WriteLine($"[純模擬] 2D 由 Unity 預覽與 bitmap 比對完成就算執行（不連 URSim）；3D 疊放送 URSim 由 Isaac Sim 驗證。" +
                               $"場景與畫面來自 Isaac Sim，沒開 Isaac 時 2D 改用內建的虛擬世界；紀錄在 {simOutput}。");
    Save(run, "config.json", new { model, max_attempts = 10, reset_between_tasks = !sim && fixedBaseline,
        initial_scene_source = sim ? (mode.ResetEachTask ? "sim_scene_file" : "sim_continued")
            : fixedBaseline ? "fixed_baseline" : "table_at_command",
        run_mode = sim ? "sim" : "real", sim_scene = sim ? mode.Scene : null,
        reset_within_task = false, reset_xy_m = ExperimentChecks.ResetXYToleranceM, reset_z_m = ExperimentChecks.ResetZToleranceM,
        rule_scope = "task", evaluation = "independent_visual_model", started_utc = DateTime.UtcNow });
    bool success = false;
    bool executionPending = false;
    string status = "failed";
    int attempts = 0;
    // 每一輪的結果：success 或失敗類型（見下方 stageKind），寫進 result.json 供分開統計轉譯與規劃／執行失敗
    var attemptOutcomes = new List<string>();
    var rules = new List<string>();
    var feedback = "無前次結果。";
    PatternDesign? design = null;
    string? designError = null;
    llm.TargetBitmap = null;
    try
    {
        if (sim)
        {
            var scenePath = mode.ScenePath(assets);
            if (mode.ResetEachTask || loadedSimScene != scenePath)
            {
                var simScene = SimScene.Load(scenePath);
                try
                {
                    await IsaacSimExecutor.LoadSimSceneAsync(simScene.Objects, simScene.Camera);
                    virtualWorld = null;
                    VirtualSimWorld.Delete(assets);
                    Console.WriteLine($"[純模擬] 用虛擬場景 {mode.Scene} 重建 Isaac Sim 世界（{SceneInventory(simScene.Objects)}）。");
                }
                catch (SimulationUnavailableException ex) when (ex.InnerException is HttpRequestException or TaskCanceledException)
                {
                    // 連不到 Isaac Sim（不是 Isaac 回報錯誤）：2D 用內建的虛擬世界，遇到 3D 疊放才回報需要 Isaac
                    virtualWorld = new VirtualSimWorld(simScene.Objects);
                    virtualWorld.Write(assets);
                    Console.WriteLine($"[純模擬] 連不到 Isaac Sim，用內建的虛擬世界載入 {mode.Scene}（{SceneInventory(simScene.Objects)}）：" +
                                      "只支援 2D，沒有物理，畫面是依座標畫的俯視示意圖；3D 疊放需要 Isaac Sim。");
                }
                loadedSimScene = scenePath;
                File.Copy(scenePath, Path.Combine(run, "sim_scene.json"), true);
            }
            else
            {
                Console.WriteLine("[純模擬] 接續目前的模擬世界（reset_each_task = false）。");
                // 內建虛擬世界：重寫一次世界檔，Unity 重開或換過場景後畫面才跟接續的世界一致
                virtualWorld?.Write(assets);
            }
            File.WriteAllText(Path.Combine(run, "sim_backend.txt"), virtualWorld != null ? VirtualSimWorld.Source : "isaac_sim");
        }
        // 純模擬沒開 Isaac 時，附圖是程式畫的俯視示意圖，提示 LLM 不要當成照片
        llm.SchematicImage = sim && virtualWorld != null;
        // 固定基準只用在實機；純模擬的起點是虛擬場景檔（或接續的模擬結果）
        var fixedStart = !sim ? baseline : null;
        bool waitForBaseline = fixedBaseline && !sim;
        List<SceneObject> initial;
        int stable = 0;
        List<SceneObject>? previous = null;
        int resetWaitSeconds = 0;
        Console.WriteLine(waitForBaseline
            ? "[任務重置] 將實體積木恢復初始配置後，相機確認桌面即開始；不會自動搬回積木。"
            : "[任務開始] 以目前桌面為起點，相機連續 3 次看到桌面穩定就開始。");
        while (true)
        {
            initial = await Scene();
            var expected = fixedStart ?? previous;
            if (initial.Count > 0 && expected != null && ExperimentChecks.Matches(expected, initial)) stable++;
            else stable = 0;
            previous = initial;
            if (stable >= 3) break;
            await Task.Delay(1000);
            resetWaitSeconds++;
            if (resetWaitSeconds % 15 == 0)
            {
                Console.WriteLine(waitForBaseline
                    ? $"[任務重置] 仍在等待初始桌面，已等待 {resetWaitSeconds} 秒（無逾時限制）。"
                    : $"[任務開始] 桌面還沒穩定（可能有東西在動或偵測不穩），已等待 {resetWaitSeconds} 秒。");
                if (expected == null || initial.Count == 0)
                {
                    Console.WriteLine($"             目前：{SceneInventory(initial)}");
                    continue;
                }
                // 還沒有基準（或不用固定基準）時是在等連續幾幀穩定，比的是上一幀
                Console.WriteLine(fixedStart != null
                    ? $"             基準：{SceneInventory(fixedStart)}；目前：{SceneInventory(initial)}"
                    : "             等待桌面穩定，跟上一幀比：");
                foreach (var line in ExperimentChecks.DescribeMismatch(expected, initial))
                    Console.WriteLine($"             {line}");
            }
        }
        if (waitForBaseline && baseline == null) { baseline = initial; Save(output, "initial_scene.json", baseline); }
        Save(run, "initial_scene.json", initial);
        Console.WriteLine($"[任務開始] 初始桌面已確認（{SceneInventory(initial)}）；本任務內不再重置。");
        // 目標 bitmap 設計（release 260917）：選出後每一輪都要把積木排成這張圖。
        // 指令不是排圖形時沒有目標，照常自由規劃；設計不出可用的圖，任務記為失敗、不進入規劃
        int maxAttempts = 10;
        try
        {
            design = await DesignTarget(goal, initial, Path.Combine(run, "design"));
            Save(run, "design.json", design);
            llm.TargetBitmap = design.Bitmap;
        }
        catch (PatternDesignException ex)
        {
            designError = ex.Message;
            maxAttempts = 0;
            Console.WriteLine($"[Layer 1] pattern 設計失敗：{ex.Message}；這個任務記為失敗，不進入規劃。");
        }
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var dir = Path.Combine(run, $"attempt_{attempt:00}");
            Directory.CreateDirectory(dir);
            Console.WriteLine($"[實驗] 第 {attempt}/10 次規劃，保留目前場景。");
            attempts = attempt;
            string failure = "", failureKind = "", plan = "", translationText = "（沒有產生轉譯結果）";
            var local = new List<VerifyResult>();
            var isaacDir = Path.Combine(dir, "isaac_sim");
            JsonElement? camera = null;
            // 系統實際走過的流程，給下一輪規劃與反思當觀測事實；沒有記在這裡的步驟都沒有發生。
            var trace = new List<string>();
            string stage = "觀測場景";
            // 在這個階段停止時記錄的失敗類型：planning（拆解或規劃階段）、translation_format（轉譯輸出讀不懂）、
            // translation_rejected（轉譯器回報計畫無法忠實轉譯）、precheck、simulation（3D 的 URSim / Isaac Sim，
            // 2D 的 Unity 預覽 bitmap 比對）、execution；流程跑完但整體判定未達標則是 global_validation。
            // local_validation 只出現在 2026-10-01 之前逐步執行的 2D 紀錄
            string stageKind = "planning";
            int robotOperations = 0;
            try
            {
                var before = await Scene();
                if (before.Count == 0) throw new SceneUnavailableException("沒有有效場景觀測。");
                Save(dir, "before_scene.json", before);
                var image = await Frame(dir, "before.jpg");
                camera = await CameraInfo();
                // 收到指令就先把目前真實場景投影到 Isaac Sim（背景），不必等 LLM 規劃完才看得到積木。
                // 純模擬時 Isaac 本身就是世界，重新投影會把歪掉的積木擺正，不投影。
                if (!sim) IsaacSimExecutor.SyncRealScene(before, camera);
                trace.Add($"觀測到 {before.Count} 個物件");
                stage = "拆解子任務";
                var hierarchy = await llm.Decompose(goal, before, rules, feedback, image, dir);
                stage = "操作規劃";
                plan = await llm.Plan(goal, before, hierarchy, rules, feedback, image, dir);
                stage = "轉譯成執行資料";
                stageKind = "translation_format";
                var translated = await llm.Translate(plan, before, dir);
                Save(dir, "translated_plan.json", translated);
                translationText = JsonSerializer.Serialize(translated,
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                // 轉譯失敗也算一輪，照常進 Reflection
                if (!string.IsNullOrWhiteSpace(translated.Error))
                {
                    stageKind = "translation_rejected";
                    throw new TranslationContractException("轉譯失敗：" + translated.Error,
                        new InvalidOperationException(translated.Error));
                }
                if (translated.Steps == null || translated.Steps.Count > 50)
                    throw new TranslationContractException("執行資料無效或超過單次 50 步上限。",
                        new InvalidOperationException("Invalid translated step count."));
                trace.Add($"轉譯出 {translated.Steps.Count} 個操作");
                PrintPlannedFigure(attempt, translated.Steps, before);
                // 有雙模型設計的目標時，計畫排出的圖形要跟它完全相同才往下走
                if (llm.TargetBitmap != null && DesignMismatch(llm.TargetBitmap, translated.Steps, before) is string mismatch)
                {
                    stage = "執行前檢查（跟雙模型設計的 bitmap 比對）";
                    stageKind = "precheck";
                    throw new InvalidOperationException(mismatch);
                }
                // 依步驟順序檢查放置目標有沒有跟同一層的積木部分重疊（含同一批前面步驟剛放的），2D、3D 都檢查
                stage = "執行前檢查（積木重疊）";
                stageKind = "precheck";
                var overlaps = LayeredHeights.SameLayerOverlaps(translated.Steps, before);
                if (overlaps.Count > 0)
                    throw new InvalidOperationException("執行前檢查：放置位置會跟其他積木重疊：" + string.Join("；", overlaps));
                // 3D 疊放：整輪步驟先交給 Unity 在 URSim 執行（robot_target = "ursim"），Isaac Sim 的手臂
                // 即時跟隨 URSim、積木用物理模擬；URSim 跑完由 Isaac 做幾何檢查，再讓 LLM 看模擬畫面，
                // 都通過才把同一批整批送實體手臂。不通過就算這次 attempt 失敗、實機完全不動，照常進 Reflect。
                // 2D 平面移動不經過這裡（見下方 2D 整批）。存檔另開子資料夾，避免跟實機結果撞名。
                // 3D 的每一批（URSim 與之後的實機）都帶 layered_grasp：Unity 用分層夾取深度，實機執行的就是
                // Isaac 驗證過的同一套深度；2D 批次不帶這個欄位，Unity 照舊。
                bool stacked3d = IsaacSimExecutor.RequiresCheck(translated.Steps, before);
                if (stacked3d && sim && virtualWorld != null)
                    throw new SimulationUnavailableException("純模擬目前沒開 Isaac Sim：這個計畫是 3D 疊放，需要 Isaac 做物理驗證，" +
                        "內建的虛擬世界只支援 2D。開好 isaac_sim_server 後再下指令。", null);
                var ursimSteps = new List<StepEnvelope>();
                if (stacked3d)
                {
                    trace.Add("判定為 3D 疊放，先在 URSim 執行並由 Isaac Sim 驗證");
                    Directory.CreateDirectory(isaacDir);
                    var heights = LayeredHeights.ForSteps(translated.Steps, before);
                    for (int k = 0; k < translated.Steps.Count; k++)
                    {
                        var step = translated.Steps[k];
                        stage = $"送 URSim 前第 {k + 1} 個操作的執行前檢查";
                        stageKind = "precheck";
                        if (!ExperimentChecks.Resolve(step, before, before, out var planned, out var error))
                            throw new InvalidOperationException($"第 {k + 1} 個操作：{error}");
                        planned.StepId = ++stepId;
                        if (!MotionPlanValidator.TryValidate(new MotionPlan { ActionSequence = step.Actions, Reasoning = plan }, planned, before, out error))
                            throw new InvalidOperationException($"第 {k + 1} 個操作執行前檢查：{error}");
                        ursimSteps.Add(new StepEnvelope { StepId = planned.StepId, SourcePosition = planned.Source,
                            TargetPosition = step.Target, ActionSequence = step.Actions, Comment = "3D 疊放 URSim 驗證",
                            SourceTopM = heights[k].SourceTopM, TargetTopM = heights[k].TargetTopM });
                    }
                    Console.WriteLine($"[Isaac Sim] 3D 疊放：{ursimSteps.Count} 步先在 URSim 執行，Isaac Sim 跟隨驗證。");
                    stage = "URSim 執行";
                    stageKind = "simulation";
                    // 純模擬：Isaac 就是世界，驗證會真的移動積木。先記下位姿，驗證結束（不論通過與否）放回去，
                    // 正式執行才從驗證前的狀態開始，跟實機模式「驗證不動到真實積木」一致。
                    if (sim) await IsaacSimExecutor.SnapshotWorldAsync();
                    try
                    {
                        await IsaacSimExecutor.BeginVerifyAsync(before, translated.Steps, camera, image, isaacDir, useCurrentWorld: sim);
                        int ursimBatchId = ++stepId;
                        // 3D 也用 Unity 畫面比對：預覽結束逐層切開拍俯視畫面得到每一點的高度，跟計畫的高度圖
                        // （含壓在底下的支撐）比，畫面重疊率大於門檻才讓 URSim 動；不通過 URSim、實機都不動
                        var figure3d = PlannedFigure(translated.Steps, before, withSupports: true).Values.ToList();
                        var (heightRows, heightCells, cell3dX, cell3dY) = FigureBitmap.HeightMap(figure3d, figure3d.Select(LayerOf).ToList());
                        var ursimBatch = new BatchEnvelope { BatchId = ursimBatchId, Steps = ursimSteps,
                            Comment = "3D 疊放 URSim 驗證", RobotTarget = "ursim", LayeredGrasp = true,
                            Bitmap = heightRows, ExpectedCells = heightCells, CellSizeM = cell3dX, CellSizeXM = cell3dX, CellSizeYM = cell3dY };
                        Save(isaacDir, "ursim_batch.json", ursimBatch);
                        var simCheckPath3d = Path.Combine(assets, "sim_check.json");
                        ClearSimulationCheckFiles();
                        AtomicWrite(Path.Combine(assets, "current_step.json"), ursimBatch);
                        var ursimExecution = await Wait(attempt, ursimBatchId);
                        Save(isaacDir, "ursim_execution.json", ursimExecution);
                        var simCheck3d = ReadSimulationCheck(simCheckPath3d, ursimBatchId, dir, heightCells);
                        if (simCheck3d is { Performed: true })
                            trace.Add($"Unity 預覽畫面跟 3D 高度圖的比對{(simCheck3d.Passed ? "通過" : "未通過")}（畫面重疊率 {simCheck3d.OverlapRatio * 100:F0}%）");
                        if (ursimExecution == null || !ursimExecution.Completed)
                        {
                            if (simCheck3d is { Performed: true, Passed: false, VerificationEnabled: true })
                            {
                                stage = "3D 疊放 Unity 預覽畫面比對";
                                throw new InvalidOperationException(
                                    $"Unity 預覽的畫面跟計畫的 3D 高度圖不吻合（URSim、實機都未動）：畫面重疊率 {simCheck3d.OverlapRatio * 100:F0}%，" +
                                    $"要大於 {simCheck3d.OverlapThreshold * 100:F0}%；" + string.Join("；", simCheck3d.Errors));
                            }
                            string reason = ursimExecution?.Error ?? "沒有回報原因";
                            if (reason.Contains("未連線") || reason.Contains("未設定")) throw new SimulationUnavailableException("URSim 無法使用：" + reason, null);
                            throw new InvalidOperationException("URSim 執行失敗（實機未動）：" + reason);
                        }
                        trace.Add("URSim 執行完成");
                        stage = "Isaac Sim 幾何驗證";
                        var report = await IsaacSimExecutor.EndVerifyAsync(isaacDir);
                        if (!report.Pass)
                            throw new InvalidOperationException("Isaac Sim 驗證未通過（實機未動）：" + string.Join("；", report.Reasons));
                        trace.Add("Isaac Sim 幾何驗證通過");
                        stage = "Isaac Sim 模擬畫面判定";
                        var isaacVerdict = await llm.Validate(goal, before, report.Scene, report.Frame, isaacDir);
                        if (isaacVerdict.Split('\n')[0].Trim() != "PASS")
                            throw new InvalidOperationException("Isaac Sim 模擬畫面判定未通過（實機未動）：" + isaacVerdict);
                        trace.Add("Isaac Sim 模擬畫面判定通過");
                    }
                    finally
                    {
                        // 還原失敗就不能從正確的狀態繼續，例外往外丟，這個任務記為基礎設施錯誤
                        if (sim) await IsaacSimExecutor.RestoreWorldAsync();
                    }
                    Console.WriteLine(sim ? "[Isaac Sim] 3D 驗證通過，模擬世界已還原，開始正式執行（URSim）。"
                                          : "[Isaac Sim] 3D 驗證通過，開始送實體手臂。");

                    // 正式執行整批一次送：步驟、來源位置、積木高度都跟 URSim 驗證的那一批相同，實機跑的就是
                    // Isaac 驗證過的同一條關節軌跡。逐步送的話每一步結束都要回 Ready，那些收尾路徑驗證時沒有，
                    // 有些位置回不去（肘關節奇異點）。步驟之間不再重新觀測與局部驗證，最後由整體驗證判定。
                    stage = "3D 疊放整批執行前檢查";
                    stageKind = "precheck";
                    var current = await Scene();
                    for (int k = 0; k < translated.Steps.Count; k++)
                    {
                        if (!ExperimentChecks.Resolve(translated.Steps[k], before, current, out var now, out var error))
                            throw new InvalidOperationException($"第 {k + 1} 個操作：{error}");
                        var verified = ursimSteps[k].SourcePosition!;
                        double driftM = Math.Sqrt(Math.Pow(now.Source!.X - verified.X, 2) + Math.Pow(now.Source.Y - verified.Y, 2));
                        if (driftM > VerifiedSourceDriftM)
                            throw new InvalidOperationException(
                                $"第 {k + 1} 個操作的來源 {now.Source.Name} 在驗證後移動了 {driftM * 1000:F0} mm，不能沿用驗證過的軌跡。");
                    }
                    stage = "3D 疊放整批在實體手臂執行";
                    stageKind = "execution";
                    var executionSteps = ursimSteps.Select(s => new StepEnvelope { StepId = ++stepId, SourcePosition = s.SourcePosition,
                        TargetPosition = s.TargetPosition, ActionSequence = s.ActionSequence, Comment = "自由規劃實驗",
                        SourceTopM = s.SourceTopM, TargetTopM = s.TargetTopM }).ToList();
                    int batchId3d = ++stepId;
                    // 純模擬時「實機」就是 URSim：標 robot_target = "ursim"，Unity 不會連實體手臂。
                    // 同一條軌跡在 URSim 驗證那批已經在 Unity 預覽過，這批不再預覽（SkipPreview）
                    var batch3d = new BatchEnvelope { BatchId = batchId3d, Steps = executionSteps,
                        Comment = "3D 疊放正式執行（整批，同驗證軌跡）", LayeredGrasp = true, RobotTarget = sim ? "ursim" : "",
                        SkipPreview = true };
                    Save(dir, $"batch_{batchId3d}.json", batch3d);
                    AtomicWrite(Path.Combine(assets, "current_step.json"), batch3d);
                    Console.WriteLine($"[實驗] 第 {attempt}/10 輪已送出 3D 整批（{executionSteps.Count} 個操作）：batch {batchId3d}。");
                    executionPending = true;
                    var execution3d = await Wait(attempt, batchId3d);
                    if (execution3d != null) executionPending = false;
                    Save(dir, $"execution_{batchId3d}.json", execution3d);
                    if (execution3d == null) { status = "execution_unknown"; throw new ExecutionUnknownException(); }
                    if (!execution3d.Completed)
                    {
                        var executionError = $"3D 疊放整批執行失敗：{execution3d.Error ?? "沒有回報原因"}";
                        Console.WriteLine($"[實驗] 第 {attempt}/10 輪 Unity/UR3 退回：{executionError}");
                        throw new InvalidOperationException(executionError);
                    }
                    Console.WriteLine($"[實驗] 第 {attempt}/10 輪 Unity/UR3 已完成 3D 整批 batch {batchId3d}。");
                    robotOperations = executionSteps.Count;
                    trace.Add($"3D 疊放 {executionSteps.Count} 個操作整批由實體手臂執行完成");
                }
                // 2D：整批一次送。Unity 先預覽整批，模擬結束拍 Unity 畫面跟計畫畫出的 bitmap 比對（畫面重疊率要大於門檻），
                // 通過才整批送實機，不通過實機完全不動。步驟之間不重新觀測、不做局部驗證（跟 3D 相同），
                // 最後由整體驗證判定。2D 的夾取深度與參數不變（不帶 layered_grasp）。
                if (!stacked3d)
                {
                    trace.Add("判定為 2D 平面擺放，整批先在 Unity 預覽並比對 bitmap，通過才整批執行");
                    var batchSteps = new List<StepEnvelope>();
                    var operationOf = new Dictionary<int, string>();
                    // 圖形裡的每個物件（含已經在目標上、不用搬的），畫 bitmap 與 Unity 比對用；
                    // 同一塊被搬兩次時只算最後放下的位置（key = 場景 index）
                    var figure = new Dictionary<int, SceneObject>();
                    // 已在目標 2 公分內、不搬的物件實際停在哪裡（key = 場景 index）：Unity 畫面比對時預期佔地放在這裡，
                    // 不放在 target，否則沒動的方塊會被算成偏移。bitmap 的格子仍照 target 畫
                    var keptInPlace = new Dictionary<int, (double X, double Y)>();
                    var heights2d = LayeredHeights.ForSteps(translated.Steps, before);
                    for (int k = 0; k < translated.Steps.Count; k++)
                    {
                        var step = translated.Steps[k];
                        stage = $"第 {k + 1} 個操作的執行前檢查";
                        stageKind = "precheck";
                        if (!ExperimentChecks.Resolve(step, before, before, out var assignment, out var error))
                            throw new InvalidOperationException($"第 {k + 1} 個操作：{error}");
                        assignment.StepId = ++stepId;
                        var motion = new MotionPlan { ActionSequence = step.Actions, Reasoning = plan };
                        if (!MotionPlanValidator.TryValidate(motion, assignment, before, out error))
                            throw new InvalidOperationException($"第 {k + 1} 個操作執行前檢查：{error}");
                        bool places = ClassifyOutcome(step.Actions) == ActionOutcome.Placed;
                        if (places && ExperimentChecks.IsAlreadyAtTarget(assignment))
                        {
                            var satisfied = new VerifyResult
                            {
                                StepId = assignment.StepId,
                                SourceRemoved = false,
                                TargetOccupied = true,
                                ShapeMatch = true,
                                ColorMatch = true,
                                PositionErrorMm = Math.Sqrt(
                                    Math.Pow(assignment.Source!.X - assignment.Target!.WorldX, 2) +
                                    Math.Pow(assignment.Source.Y - assignment.Target.WorldY, 2)) * 1000.0,
                                OverallStatus = "ok",
                                Note = "Selected source is already within the accepted 20 mm target tolerance; robot motion skipped."
                            };
                            local.Add(satisfied);
                            Save(dir, "local_validation.json", local);
                            Console.WriteLine($"[實驗] step {assignment.StepId} 來源已在目標 2 公分容差內，略過重複抓放。");
                            trace.Add($"第 {k + 1} 個操作的來源已在目標 2 公分內，略過抓放（手臂未動）");
                            // 不用搬的物件也是圖形的一部分；Unity 預覽裡它停在觀測到的高度
                            figure[step.SourceIndex] = FigureObject(step.Target!, assignment.Source!.Z);
                            keptInPlace[step.SourceIndex] = (assignment.Source.X, assignment.Source.Y);
                            continue;
                        }
                        keptInPlace.Remove(step.SourceIndex);
                        batchSteps.Add(new StepEnvelope { StepId = assignment.StepId, SourcePosition = assignment.Source,
                            TargetPosition = step.Target, ActionSequence = step.Actions, Comment = "自由規劃實驗" });
                        operationOf[assignment.StepId] = $"第 {k + 1} 個操作（{assignment.Source!.Name} " +
                            $"({assignment.Source.X:F3}, {assignment.Source.Y:F3}) → target ({step.Target!.X:F3}, {step.Target.Y:F3})）";
                        if (places) figure[step.SourceIndex] = FigureObject(step.Target!, heights2d[k].TargetTopM);
                        else figure.Remove(step.SourceIndex);
                    }
                    if (batchSteps.Count == 0) trace.Add("所有操作的來源都已在目標 2 公分內，手臂不用動");
                    else
                    {
                        int batchId2d = ++stepId;
                        // 純模擬（PreviewOnly）：Unity 預覽與 bitmap 比對通過就是執行完成，不連 URSim、也不連實體手臂
                        var batch2d = new BatchEnvelope { BatchId = batchId2d, Steps = batchSteps,
                            Comment = "2D 整批（Unity 預覽比對 bitmap 通過才執行）", RobotTarget = sim ? "ursim" : "", PreviewOnly = sim };
                        if (figure.Count > 0)
                        {
                            var (rows, cells, cellX, cellY) = FigureBitmap.Build(figure.Values.ToList());
                            // Keys 跟 Values 的順序相同，cells[i] 對應 figure 的第 i 個物件
                            var sources = figure.Keys.ToList();
                            for (int i = 0; i < cells.Count; i++)
                                if (keptInPlace.TryGetValue(sources[i], out var at)) { cells[i].X = at.X; cells[i].Y = at.Y; }
                            batch2d.Bitmap = rows;
                            batch2d.ExpectedCells = cells;
                            batch2d.CellSizeM = cellX;
                            batch2d.CellSizeXM = cellX;
                            batch2d.CellSizeYM = cellY;
                            // 圖形在轉譯完就印過了（PrintPlannedFigure，同一組物件畫出來相同），這裡不再印
                            // 排字母時每個字母最多 5×5 格：超過就不送，算執行前檢查失敗
                            var sizeProblem = FigureBitmap.LetterSizeProblem(FigureBitmap.LetterCount(goal), rows);
                            if (sizeProblem != null)
                            {
                                stage = "執行前檢查（字母 bitmap 大小）";
                                stageKind = "precheck";
                                throw new InvalidOperationException("執行前檢查：" + sizeProblem);
                            }
                        }
                        Save(dir, $"batch_{batchId2d}.json", batch2d);
                        stage = sim ? "2D 整批：Unity 預覽與 bitmap 比對（純模擬，不接手臂）" : "2D 整批：Unity 預覽、bitmap 比對與實體手臂執行";
                        stageKind = "execution";
                        var simCheckPath = Path.Combine(assets, "sim_check.json");
                        ClearSimulationCheckFiles();
                        AtomicWrite(Path.Combine(assets, "current_step.json"), batch2d);
                        Console.WriteLine($"[實驗] 第 {attempt}/10 輪已送出 2D 整批（{batchSteps.Count} 個操作）：batch {batchId2d}。");
                        executionPending = true;
                        var execution2d = await Wait(attempt, batchId2d);
                        if (execution2d != null) executionPending = false;
                        Save(dir, $"execution_{batchId2d}.json", execution2d);
                        var simCheck = ReadSimulationCheck(simCheckPath, batchId2d, dir);
                        if (execution2d == null) { status = "execution_unknown"; throw new ExecutionUnknownException(); }
                        if (simCheck is { Performed: true })
                            trace.Add($"Unity 預覽畫面跟 bitmap 的比對{(simCheck.Passed ? "通過" : "未通過")}（畫面重疊率 {simCheck.OverlapRatio * 100:F0}%）");
                        if (!execution2d.Completed)
                        {
                            if (simCheck is { Performed: true, Passed: false, VerificationEnabled: true })
                            {
                                stage = "2D 整批 Unity 預覽的 bitmap 比對";
                                stageKind = "simulation";
                                throw new InvalidOperationException(
                                    $"Unity 預覽的畫面跟計畫畫出的 bitmap 不吻合（實機未動）：畫面重疊率 {simCheck.OverlapRatio * 100:F0}%，" +
                                    $"要大於 {simCheck.OverlapThreshold * 100:F0}%；" + string.Join("；", simCheck.Errors));
                            }
                            // Unity 的錯誤用內部 step_id，換成第幾個操作與它的來源、目標，反思才對得上計畫
                            var executionError = $"2D 整批執行失敗：{execution2d.Error ?? "沒有回報原因"}" +
                                $"\n（Unity 的 step_id：{string.Join("；", operationOf.Select(p => $"{p.Key} = {p.Value}"))}；座標只適用本輪觀測）";
                            Console.WriteLine($"[實驗] 第 {attempt}/10 輪 Unity/UR3 退回：{executionError}");
                            throw new InvalidOperationException(executionError);
                        }
                        if (sim)
                        {
                            // 純模擬：Unity 預覽與比對通過就是執行完成；模擬世界照預覽的實際落點更新（不是計畫的座標）
                            int moved = await ApplyPreviewResult(execution2d.FinalBlocks, before);
                            Console.WriteLine($"[實驗] 第 {attempt}/10 輪純模擬：Unity 預覽完成 2D 整批 batch {batchId2d}，" +
                                              $"模擬世界照預覽落點更新了 {moved} 個物件（沒有連 URSim 或實體手臂）。");
                            trace.Add($"純模擬：Unity 預覽完成 {batchSteps.Count} 個操作就當作執行完成，模擬世界照預覽落點更新了 {moved} 個物件" +
                                      "（沒有連 URSim 或實體手臂，沒有物理模擬）");
                        }
                        else
                        {
                            Console.WriteLine($"[實驗] 第 {attempt}/10 輪 Unity/UR3 已完成 2D 整批 batch {batchId2d}。");
                            robotOperations = batchSteps.Count;
                            trace.Add($"2D {batchSteps.Count} 個操作整批由實體手臂執行完成");
                        }
                    }
                }
            }
            catch (ExecutionUnknownException) { throw; }
            catch (TranslationContractException ex)
            {
                failure = ex.Message;
                failureKind = stageKind;
                trace.Add($"在「{stage}」停止");
                Console.WriteLine($"[實驗] 第 {attempt}/10 輪轉譯失敗，將進入 Reflection：{failure}");
            }
            catch (InvalidOperationException ex) { failure = ex.Message; failureKind = stageKind; trace.Add($"在「{stage}」停止"); }
            trace.Add(sim ? $"純模擬沒有實體手臂；URSim 執行了 {robotOperations} 個操作" : $"實體手臂完整執行了 {robotOperations} 個操作");
            var after = await Scene();
            Save(dir, "after_scene.json", after);
            // 跟 Unity SceneSyncer 執行後刷新場景對應：把這次嘗試的真實結果投影回 Isaac Sim（背景）。純模擬不投影。
            if (!sim) IsaacSimExecutor.SyncRealScene(after, camera);
            var afterImage = await Frame(dir, "after.jpg");
            var verdict = await llm.Validate(goal, initial, after, afterImage, dir);
            feedback = $"實際流程（系統紀錄）：{string.Join(" → ", trace)}\n執行／局部觀察：{failure}\n整體觀察：{verdict}";
            File.WriteAllText(Path.Combine(dir, "feedback.txt"), feedback);
            success = string.IsNullOrEmpty(failure) && after.Count > 0 && afterImage != null && verdict.Split('\n')[0].Trim() == "PASS";
            if (!success && string.IsNullOrEmpty(failureKind)) failureKind = "global_validation";
            attemptOutcomes.Add(success ? "success" : failureKind);
            Save(dir, "attempt_result.json", new { attempt, success, failure_kind = success ? null : failureKind,
                failure_stage = string.IsNullOrEmpty(failure) ? null : stage, failure = string.IsNullOrEmpty(failure) ? null : failure });
            if (success) { status = "success"; Console.WriteLine($"[實驗] 第 {attempt} 次達標。"); break; }
            var reflection = await llm.Reflect(goal, plan, translationText, feedback, rules, after, dir);
            File.WriteAllText(Path.Combine(dir, "rules_for_next_attempt.txt"), reflection);
            var reflectionLines = reflection.Split('\n');
            if (reflectionLines[0].Trim() == "GIVE_UP")
            {
                Console.WriteLine($"[實驗] LLM 判斷本任務無法達成，第 {attempt} 次後結束嘗試。");
                break;
            }
            // 規則逐輪累積（由舊到新）；第一行的 CONTINUE 是判定，不是規則內容
            rules.Add(reflectionLines[0].Trim() == "CONTINUE" ? string.Join('\n', reflectionLines.Skip(1)).Trim() : reflection);
        }
    }
    catch (Exception ex)
    {
        if (executionPending) status = "execution_unknown";
        else if (ex is TranslationContractException) status = "adapter_error";
        else if (status != "execution_unknown") status = "infrastructure_error";
        File.WriteAllText(Path.Combine(run, "error.txt"), ex.ToString());
        Console.WriteLine("[實驗] " + ex.Message);
    }
    Save(run, "result.json", new { success, status, attempts, attempt_outcomes = attemptOutcomes,
        counts_toward_success_rate = status is "success" or "failed",
        design, design_error = designError,
        final_rules = rules, finished_utc = DateTime.UtcNow });
    ExperimentMetrics.Write(root);
    if (status == "execution_unknown") { Console.WriteLine("執行狀態未知，服務停止。確認手臂停止後再重新啟動。"); break; }
    AtomicWrite(Path.Combine(assets, "current_step.json"), new StepEnvelope { StepId = ++stepId, Done = true });
    Console.WriteLine($"[實驗] {status}；紀錄：{run}。" +
        (sim ? (mode.ResetEachTask ? "下個純模擬任務會重建虛擬場景。" : "下個純模擬任務接續目前的模擬世界。")
             : fixedBaseline ? "下個任務需恢復初始桌面。" : "下個任務以當時的桌面為起點。"));
}
void Save(string dir, string name, object? value) => File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(value, json));
void AtomicWrite(string path, object value)
{
    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, json));
    File.Move(path + ".tmp", path, true);
}
// Unity 預覽結束比對 bitmap 的報告（StreamingAssets/sim_check.json，Unity 在送實機之前寫）：印在 terminal，存進這一輪
// 純模擬的 2D：把 Unity 預覽結束時方塊的落點寫回模擬世界，回傳更新了幾個物件。Unity 回報每塊被放下的方塊
// 預覽前、後的位置，依預覽前的位置對回場景 index（2 cm 內最近的那塊）。內建虛擬世界直接改位置；
// 開著 Isaac 時 Isaac 就是世界，用同樣的物件清單重新載入（方塊直接放到落點，沒有物理過程）
async Task<int> ApplyPreviewResult(List<PreviewBlock>? blocks, List<SceneObject> scene)
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
    if (changed.Count == 0) return 0;
    if (virtualWorld != null)
    {
        foreach (int i in changed) virtualWorld.Place(i, updated[i], updated[i].Z);
        virtualWorld.Write(assets);
    }
    else await IsaacSimExecutor.LoadSimSceneAsync(updated, null);
    return changed.Count;
}
SimulationCheckReport? ReadSimulationCheck(string path, int batchId, string dir, IReadOnlyList<ExpectedCell>? cells = null)
{
    if (!File.Exists(path)) return null;
    SimulationCheckReport? report;
    try { report = JsonSerializer.Deserialize<SimulationCheckReport>(File.ReadAllText(path), json); }
    catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    if (report == null || report.BatchId != batchId) return null;
    File.Copy(path, Path.Combine(dir, "sim_check.json"), true);
    // 比對圖跟 sim_check.json 放在同一個 StreamingAssets；加上重疊率與說明後存進這一輪的資料夾，加註失敗就存原圖
    string? image = null;
    var imageSource = string.IsNullOrEmpty(report.ImageFile) ? null : Path.Combine(Path.GetDirectoryName(path)!, report.ImageFile);
    if (imageSource != null && File.Exists(imageSource))
    {
        image = Path.Combine(dir, report.ImageFile!);
        try { SimCheckImage.Annotate(imageSource, image, report, cells); }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bitmap 比對] 比對圖加註失敗，存原圖：{ex.Message}");
            File.Copy(imageSource, image, true);
        }
    }
    // Unity 拍的照片原樣存進這一輪的資料夾
    var photos = new List<string>();
    foreach (var name in new[] { report.PhotoFile, report.ViewFile })
    {
        if (string.IsNullOrEmpty(name)) continue;
        var source = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (!File.Exists(source)) continue;
        File.Copy(source, Path.Combine(dir, name), true);
        photos.Add(Path.Combine(dir, name));
    }
    PrintSimulationCheck(report, image);
    if (photos.Count > 0) Console.WriteLine($"           Unity 照片（正上方 / 主相機）：{string.Join("、", photos)}");
    return report;
}
// 送出要比對的批次前，刪掉上一批留下的比對結果與照片，免得讀到舊的
void ClearSimulationCheckFiles()
{
    foreach (var name in new[] { "sim_check.json", "sim_check.png", "unity_top.png", "unity_view.png" })
    {
        var file = Path.Combine(assets, name);
        if (File.Exists(file)) File.Delete(file);
    }
}
// 場景、照片：實機來自相機（perception_server），純模擬來自 Isaac Sim（同格式）
async Task<List<SceneObject>> Scene()
{
    if (sim && virtualWorld != null) return virtualWorld.Snapshot();
    using var doc = JsonDocument.Parse(await perception.GetStringAsync("scene"));
    if (!doc.RootElement.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.Number ||
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - ts.GetDouble() > 5)
        throw new HttpRequestException("相機觀測未更新或已超過 5 秒，不能使用舊場景。");
    return doc.RootElement.GetProperty("objects").EnumerateArray()
        .Where(o => o.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Object)
        .Select(o => {
            var p = o.GetProperty("position");
            return new SceneObject { Name = o.GetProperty("name").GetString() ?? "",
                X = p.GetProperty("x").GetDouble(), Y = p.GetProperty("y").GetDouble(), Z = p.GetProperty("z").GetDouble(),
                Shape = o.TryGetProperty("shape", out var s) ? s.GetString() ?? "cube" : "cube",
                Orientation = o.TryGetProperty("orientation", out var r) ? r.GetString() : null,
                SkewDeg = o.TryGetProperty("skew_deg", out var k) ? k.GetDouble() : 0 };
        }).Where(o => double.IsFinite(o.X) && double.IsFinite(o.Y) && double.IsFinite(o.Z)).ToList();
}
async Task<byte[]?> Frame(string dir, string name)
{
    if (sim && virtualWorld != null)
    {
        var schematic = TopViewRenderer.Render(virtualWorld.Snapshot());
        File.WriteAllBytes(Path.Combine(dir, name), schematic);
        return schematic;
    }
    try { var bytes = await perception.GetByteArrayAsync("debug/frame"); File.WriteAllBytes(Path.Combine(dir, name), bytes); return bytes; }
    catch (Exception ex)
    {
        File.WriteAllText(Path.Combine(dir, name + ".error.txt"), ex.Message);
        throw new SceneUnavailableException("相機影像不可取得，不能作為任務失敗進行 Reflection。");
    }
}
// 相機內參與位姿只用來對齊 Isaac Sim 的模擬相機；取不到時模擬端沿用上次的相機，不中斷實驗。
// 純模擬沒有真實相機，模擬相機由虛擬場景檔決定。
async Task<JsonElement?> CameraInfo()
{
    if (sim) return null;
    try
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync("camera"));
        return doc.RootElement.Clone();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Isaac Sim] 取不到 perception /camera，模擬相機沿用上次位置：{ex.Message}");
        return null;
    }
}
async Task<ExecutionResult?> Wait(int attempt, int id)
{
    var path = Path.Combine(assets, "step_done.json");
    var started = DateTime.UtcNow;
    int lastReportedSeconds = 0;
    // No experiment-level timeout: a slow Unity/robot execution remains pending
    // until Unity returns this exact batch id or the process is stopped manually.
    while (true)
    {
        if (File.Exists(path))
        {
            try { var result = JsonSerializer.Deserialize<ExecutionResult>(File.ReadAllText(path), json);
                if (result?.StepId == id) { File.Delete(path); return result; } }
            catch (IOException) { }
            catch (JsonException) { }
        }
        int elapsedSeconds = (int)(DateTime.UtcNow - started).TotalSeconds;
        if (elapsedSeconds >= lastReportedSeconds + 15)
        {
            lastReportedSeconds = elapsedSeconds;
            Console.WriteLine($"[Unity/UR3] 第 {attempt}/10 輪，batch {id} 仍在執行，已等待 {elapsedSeconds} 秒（無逾時限制）...");
        }
        await Task.Delay(200);
    }
}
// 圖形裡的一個物件：位置與方向照計畫的 target，頂面高度給 Unity 比對用
static SceneObject FigureObject(SceneObject target, double topM) => new()
{
    Name = target.Name, Shape = target.Shape, Orientation = target.Orientation,
    X = target.X, Y = target.Y, Z = topM, SkewDeg = target.SkewDeg
};
// 這一輪計畫最後擺出來的圖形（key = 場景 index）：每塊只算最後放下的位置（同一塊搬兩次只算最後一次，最後還夾著的不算），
// Z 是放好後的頂面（LayeredHeights.ForSteps）。withSupports：再加上沒被搬、但壓在放下的積木底下的場景積木
// （3D 疊放時支撐也是結構的一部分，位置是觀測到的、Z 是它的頂面）。3D 的高度圖與給 Unity 比對的預期格都用這一組。
static Dictionary<int, SceneObject> PlannedFigure(List<TranslatedStep> steps, List<SceneObject> scene, bool withSupports)
{
    var tops = LayeredHeights.ForSteps(steps, scene);
    var placed = new Dictionary<int, SceneObject>();
    for (int k = 0; k < steps.Count; k++)
    {
        var step = steps[k];
        if (step.Target == null || step.SourceIndex < 0 || step.SourceIndex >= scene.Count) continue;
        if (ClassifyOutcome(step.Actions) == ActionOutcome.Placed) placed[step.SourceIndex] = FigureObject(step.Target, tops[k].TargetTopM);
        else placed.Remove(step.SourceIndex);
    }
    if (withSupports)
        foreach (var (i, top) in LayeredHeights.UnmovedSupports(steps, scene, placed.Values))
            placed[i] = FigureObject(scene[i], top);
    return placed;
}
// 目標 bitmap 設計：每個任務都先用 release 260917 的 PatternDesigner 畫一張（prompt 原封不動，不分類指令）。
// 顏色照舊版 GuessBlockColor；庫存是桌上那個顏色的所有 cube / domino（現在沒有補貨區）
async Task<PatternDesign> DesignTarget(string goal, List<SceneObject> scene, string logDir)
{
    DesignLog.Start(logDir);
    string color = GuessBlockColor(goal, scene);
    int cubes = scene.Count(o => o.Name == $"{color}_cube"), dominoes = scene.Count(o => o.Name == $"{color}_domino");
    Console.WriteLine($"[Layer 1] 呼叫 LLM 設計 pattern (color={color}, {cubes} cube + {dominoes} domino)...");
    try
    {
        int[,] grid = (await patternDesigner.DesignAsync(goal, color, cubes, dominoes)).Bitmap!;
        var rows = Enumerable.Range(0, grid.GetLength(0)).Select(r => string.Concat(
            Enumerable.Range(0, grid.GetLength(1)).Select(c => grid[r, c] == 1 ? '1' : '0'))).ToList();
        FigureBitmap.Print("[Layer 1] 目標 bitmap（第一列 = 相機畫面最上方）：", rows.Select(r => r.Replace('0', '□').Replace('1', '■')));
        return new PatternDesign { BlockColor = color, Bitmap = rows, Reviewed = !PatternDesigner.SkipReview };
    }
    // 舊版設計器畫不出可用的圖時丟 InvalidOperationException；Gemini 的 HTTP 錯誤、缺 key 照基礎設施錯誤處理
    catch (InvalidOperationException ex) when (!ex.Message.StartsWith("Gemini API"))
    {
        throw new PatternDesignException(ex.Message);
    }
}
// release 260917 的 GuessBlockColor：指令有提到顏色就用它，沒有就用桌上比較多的顏色
static string GuessBlockColor(string userCommand, List<SceneObject> snap)
{
    if (userCommand.Contains("black") || userCommand.Contains("黑")) return "black";
    if (userCommand.Contains("yellow") || userCommand.Contains("黃")) return "yellow";
    int y = snap.Count(s => s.Name.StartsWith("yellow_"));
    int b = snap.Count(s => s.Name.StartsWith("black_"));
    return y >= b ? "yellow" : "black";
}
static int LayerOf(SceneObject o) => Math.Max(1, (int)Math.Round(o.Z / LayeredGraspGeometry.BlockLayerM));
// 計畫排出的圖形跟雙模型設計的目標 bitmap 比：兩邊都去掉四周的空列、空行後逐格相同才算一樣。
// 2D 的 ■ 當 1；3D 比高度圖的層數（含壓在底下沒被搬的支撐）。回傳不同的說明，null = 相同
static string? DesignMismatch(IReadOnlyList<string> target, List<TranslatedStep> steps, List<SceneObject> scene)
{
    bool stacked = IsaacSimExecutor.RequiresCheck(steps, scene);
    var figure = PlannedFigure(steps, scene, withSupports: stacked).Values.ToList();
    var planned = figure.Count == 0 ? new List<string>()
        : stacked ? FigureBitmap.HeightMap(figure, figure.Select(LayerOf).ToList()).Rows
        : FigureBitmap.Build(figure).Rows.Select(r => r.Replace('■', '1').Replace('□', '0')).ToList();
    var want = FigureBitmap.Trim(target);
    var got = FigureBitmap.Trim(planned);
    if (want.SequenceEqual(got)) return null;
    return "執行前檢查：計畫排出的圖形跟雙模型設計的目標 bitmap 不同（0 = 空，數字 = 疊幾層；第一列 = 相機畫面最上方）。" +
           $"目標：{string.Join(" / ", want)}；計畫：{(got.Count == 0 ? "沒有放下任何物件" : string.Join(" / ", got))}";
}
// 規劃收到回覆、轉譯完就把這一輪計畫排出的圖形印在 LLM 進度後面，後面的檢查沒過也看得到 LLM 想排成什麼樣子。
// 2D 印 ■□ bitmap，跟之後送 Unity 比對的相同；3D 疊放印俯視高度圖（數字 = 那格疊到第幾層，例如站起來的 L 是 3 1 1），
// 含壓在底下沒被搬的支撐，也跟送 Unity 比對的相同。
static void PrintPlannedFigure(int attempt, List<TranslatedStep> steps, List<SceneObject> scene)
{
    bool stacked = IsaacSimExecutor.RequiresCheck(steps, scene);
    var figure = PlannedFigure(steps, scene, withSupports: stacked).Values.ToList();
    if (figure.Count == 0)
    {
        Console.WriteLine($"[LLM] 第 {attempt}/10 輪計畫沒有放下任何物件，沒有 bitmap。");
        return;
    }
    if (!stacked)
    {
        var (rows, _, cellX, cellY) = FigureBitmap.Build(figure);
        FigureBitmap.Print($"[LLM] 第 {attempt}/10 輪計畫排出的 bitmap（{figure.Count} 個物件，格距 X {cellX * 1000:F0} mm、" +
                           $"Y {cellY * 1000:F0} mm，跟相機畫面同方向：上 = +Y、右 = +X）：", rows);
        return;
    }
    var (heights, _, cell3dX, cell3dY) = FigureBitmap.HeightMap(figure, figure.Select(LayerOf).ToList());
    FigureBitmap.PrintHeightMap($"[LLM] 第 {attempt}/10 輪計畫是 3D 疊放，俯視高度圖（數字 = 那格疊到第幾層，· = 空；{figure.Count} 個物件，" +
                                $"含壓在底下沒被搬的支撐；格距 X {cell3dX * 1000:F0} mm、Y {cell3dY * 1000:F0} mm，上 = +Y、右 = +X）：", heights);
}
static void PrintSimulationCheck(SimulationCheckReport report, string? image)
{
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
    if (image != null) Console.WriteLine($"           比對圖（左：Unity 俯視畫面；右：綠 重疊、紅 該有沒有、藍 多出來）：{image}");
    if (!report.Passed)
        Console.WriteLine(report.VerificationEnabled
            ? "           實體手臂不會動作"
            : "           驗證關閉（對照組）→ 只記錄，實體手臂照常執行");
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
static ActionOutcome ClassifyOutcome(IEnumerable<RobotFunctionCall> actions)
{
    bool holding = false;
    bool grasped = false;
    bool placed = false;
    foreach (var action in actions)
    {
        if (action.Function == "grasp")
        {
            holding = true;
            grasped = true;
        }
        else if (action.Function == "release")
        {
            if (holding && grasped) placed = true;
            holding = false;
        }
    }
    if (holding) return ActionOutcome.Holding;
    return placed ? ActionOutcome.Placed : ActionOutcome.NoObjectStateChange;
}

static string SceneInventory(IEnumerable<SceneObject> scene)
{
    var groups = scene.GroupBy(o => o.Name).OrderBy(g => g.Key)
        .Select(g => $"{g.Key}×{g.Count()}");
    string summary = string.Join("、", groups);
    return string.IsNullOrWhiteSpace(summary) ? "沒有偵測到物件" : summary;
}

public sealed class ExecutionUnknownException : Exception
{
    public ExecutionUnknownException() : base("Unity 執行逾時；不可確認手臂是否停止。") { }
}

public sealed class SceneUnavailableException : Exception
{
    public SceneUnavailableException(string message) : base(message) { }
}

enum ActionOutcome { NoObjectStateChange, Holding, Placed }
