using System.Text.Json;

var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../unity_project/Assets/StreamingAssets"));
if (!Directory.Exists(assets)) assets = Path.GetFullPath("../unity_project/Assets/StreamingAssets");
Directory.CreateDirectory(assets);
var output = Path.GetFullPath("outputs/experiments");
Directory.CreateDirectory(output);
using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5000/"), Timeout = TimeSpan.FromSeconds(5) };
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var model = Environment.GetEnvironmentVariable("ROBOT_MODEL") ?? "gpt-5";
var llm = new ExperimentLlm(model);
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
while (true)
{
    var input = Path.Combine(assets, "user_input.txt");
    if (!File.Exists(input) || string.IsNullOrWhiteSpace(File.ReadAllText(input))) { await Task.Delay(500); continue; }
    var goal = File.ReadAllText(input).Trim();
    File.WriteAllText(input, "");
    var run = Path.Combine(output, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
    Directory.CreateDirectory(run);
    File.WriteAllText(Path.Combine(run, "task.txt"), goal);
    Save(run, "config.json", new { model, max_attempts = 10, reset_between_tasks = fixedBaseline,
        initial_scene_source = fixedBaseline ? "fixed_baseline" : "table_at_command",
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
    try
    {
        List<SceneObject> initial;
        int stable = 0;
        List<SceneObject>? previous = null;
        int resetWaitSeconds = 0;
        Console.WriteLine(fixedBaseline
            ? "[任務重置] 將實體積木恢復初始配置後，相機確認桌面即開始；不會自動搬回積木。"
            : "[任務開始] 以目前桌面為起點，相機連續 3 次看到桌面穩定就開始。");
        while (true)
        {
            initial = await Scene();
            var expected = baseline ?? previous;
            if (initial.Count > 0 && expected != null && ExperimentChecks.Matches(expected, initial)) stable++;
            else stable = 0;
            previous = initial;
            if (stable >= 3) break;
            await Task.Delay(1000);
            resetWaitSeconds++;
            if (resetWaitSeconds % 15 == 0)
            {
                Console.WriteLine(fixedBaseline
                    ? $"[任務重置] 仍在等待初始桌面，已等待 {resetWaitSeconds} 秒（無逾時限制）。"
                    : $"[任務開始] 桌面還沒穩定（可能有東西在動或偵測不穩），已等待 {resetWaitSeconds} 秒。");
                if (expected == null || initial.Count == 0)
                {
                    Console.WriteLine($"             目前：{SceneInventory(initial)}");
                    continue;
                }
                // 還沒有基準（或不用固定基準）時是在等連續幾幀穩定，比的是上一幀
                Console.WriteLine(baseline != null
                    ? $"             基準：{SceneInventory(baseline)}；目前：{SceneInventory(initial)}"
                    : "             等待桌面穩定，跟上一幀比：");
                foreach (var line in ExperimentChecks.DescribeMismatch(expected, initial))
                    Console.WriteLine($"             {line}");
            }
        }
        if (fixedBaseline && baseline == null) { baseline = initial; Save(output, "initial_scene.json", baseline); }
        Save(run, "initial_scene.json", initial);
        Console.WriteLine($"[任務開始] 初始桌面已確認（{SceneInventory(initial)}）；本任務內不再重置。");
        for (int attempt = 1; attempt <= 10; attempt++)
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
            // translation_rejected（轉譯器回報計畫無法忠實轉譯）、precheck、simulation（URSim / Isaac Sim）、execution、
            // local_validation；流程跑完但整體判定未達標則是 global_validation
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
                IsaacSimExecutor.SyncRealScene(before, camera);
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
                // 3D 疊放：整輪步驟先交給 Unity 在 URSim 執行（robot_target = "ursim"），Isaac Sim 的手臂
                // 即時跟隨 URSim、積木用物理模擬；URSim 跑完由 Isaac 做幾何檢查，再讓 LLM 看模擬畫面，
                // 都通過才進下面的逐步實機執行。不通過就算這次 attempt 失敗、實機完全不動，照常進 Reflect。
                // 2D 平面移動不經過這裡，照舊由 Unity 的模擬預覽驗證。存檔另開子資料夾，避免跟實機結果撞名。
                // 3D 的每一批（URSim 與之後的實機）都帶 layered_grasp：Unity 用分層夾取深度，實機執行的就是
                // Isaac 驗證過的同一套深度；2D 批次不帶這個欄位，Unity 照舊。
                bool stacked3d = IsaacSimExecutor.RequiresCheck(translated.Steps, before);
                if (stacked3d)
                {
                    trace.Add("判定為 3D 疊放，先在 URSim 執行並由 Isaac Sim 驗證");
                    Directory.CreateDirectory(isaacDir);
                    var ursimSteps = new List<StepEnvelope>();
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
                    await IsaacSimExecutor.BeginVerifyAsync(before, translated.Steps, camera, image, isaacDir);
                    int ursimBatchId = ++stepId;
                    var ursimBatch = new BatchEnvelope { BatchId = ursimBatchId, Steps = ursimSteps,
                        Comment = "3D 疊放 URSim 驗證", RobotTarget = "ursim", LayeredGrasp = true };
                    Save(isaacDir, "ursim_batch.json", ursimBatch);
                    AtomicWrite(Path.Combine(assets, "current_step.json"), ursimBatch);
                    var ursimExecution = await Wait(attempt, ursimBatchId);
                    Save(isaacDir, "ursim_execution.json", ursimExecution);
                    if (ursimExecution == null || !ursimExecution.Completed)
                    {
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
                    Console.WriteLine("[Isaac Sim] 3D 驗證通過，開始送實體手臂。");
                }
                for (int k = 0; k < translated.Steps.Count; k++)
                {
                    var step = translated.Steps[k];
                    stage = $"第 {k + 1} 個操作的執行前檢查";
                    stageKind = "precheck";
                    var current = await Scene();
                    if (!ExperimentChecks.Resolve(step, before, current, out var assignment, out var error))
                        throw new InvalidOperationException($"第 {k + 1} 個操作：{error}");
                    assignment.StepId = ++stepId;
                    var motion = new MotionPlan { ActionSequence = step.Actions, Reasoning = plan };
                    if (!MotionPlanValidator.TryValidate(motion, assignment, current, out error))
                        throw new InvalidOperationException($"第 {k + 1} 個操作執行前檢查：{error}");
                    stage = $"第 {k + 1} 個操作在實體手臂執行";
                    stageKind = "execution";
                    var env = new StepEnvelope { StepId = assignment.StepId, SourcePosition = assignment.Source,
                        TargetPosition = step.Target, ActionSequence = step.Actions, Comment = "自由規劃實驗" };
                    if (stacked3d)
                    {
                        // 用這一步開始前重新觀測的場景算高度（前面的實機步驟已經改變了場景）
                        var observed = new TranslatedStep { SourceIndex = current.IndexOf(assignment.Source!),
                            Target = step.Target, Actions = step.Actions };
                        (env.SourceTopM, env.TargetTopM) = LayeredHeights.ForSteps(new[] { observed }, current)[0];
                    }
                    Save(dir, $"step_{stepId}.json", env);
                    int batchId = ++stepId;
                    // Use the existing batch entry point even for one operation:
                    // it prepares shared trajectories and honors preview-only.
                    AtomicWrite(Path.Combine(assets, "current_step.json"), new BatchEnvelope {
                        BatchId = batchId, Steps = new List<StepEnvelope> { env }, Comment = env.Comment,
                        LayeredGrasp = stacked3d
                    });
                    Console.WriteLine($"[實驗] 第 {attempt}/10 輪已送出 Unity/UR3：step {assignment.StepId}、batch {batchId}。");
                    executionPending = true;
                    var execution = await Wait(attempt, batchId);
                    if (execution != null) executionPending = false;
                    Save(dir, $"execution_{stepId}.json", execution);
                    if (execution == null) { status = "execution_unknown"; throw new ExecutionUnknownException(); }
                    if (!execution.Completed)
                    {
                        string sourceContext = assignment.Source == null
                            ? ""
                            : $"\n失敗步驟來源（當輪 QR 座標）：{assignment.Source.Name} " +
                              $"x={assignment.Source.X:F3}, y={assignment.Source.Y:F3}, " +
                              $"z={assignment.Source.Z:F3} m；source_index={step.SourceIndex} 只適用本輪觀測。";
                        var executionError = $"第 {k + 1} 個操作執行失敗：{execution.Error ?? "沒有回報原因"}" + sourceContext;
                        Console.WriteLine($"[實驗] 第 {attempt}/10 輪 Unity/UR3 退回：{executionError}");
                        throw new InvalidOperationException(executionError);
                    }
                    Console.WriteLine($"[實驗] 第 {attempt}/10 輪 Unity/UR3 已完成 batch {batchId}。");
                    robotOperations++;
                    trace.Add($"第 {k + 1} 個操作實體手臂執行完成");
                    stage = $"第 {k + 1} 個操作的局部驗證";
                    stageKind = "local_validation";
                    await Task.Delay(1200);
                    var afterStep = await Scene();
                    var outcome = ClassifyOutcome(step.Actions);
                    var check = outcome switch
                    {
                        ActionOutcome.Holding => Verifier.CheckHoldingStep(assignment, current, afterStep),
                        ActionOutcome.Placed => Verifier.CheckSingleObjectStep(assignment, current, afterStep,
                            assignment.Target!.WorldZ > assignment.Source!.Z + 0.005),
                        _ => Verifier.CheckExecutionOnly(assignment)
                    };
                    local.Add(check);
                    Save(dir, $"after_step_{stepId}.json", afterStep);
                    Save(dir, "local_validation.json", local);
                    if (check.OverallStatus != "ok") throw new InvalidOperationException($"第 {k + 1} 個操作局部驗證：{check.Note}");
                    trace.Add($"第 {k + 1} 個操作局部驗證通過");
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
            trace.Add($"實體手臂完整執行了 {robotOperations} 個操作");
            var after = await Scene();
            Save(dir, "after_scene.json", after);
            // 跟 Unity SceneSyncer 執行後刷新場景對應：把這次嘗試的真實結果投影回 Isaac Sim（背景）。
            IsaacSimExecutor.SyncRealScene(after, camera);
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
        final_rules = rules, finished_utc = DateTime.UtcNow });
    ExperimentMetrics.Write(output);
    if (status == "execution_unknown") { Console.WriteLine("執行狀態未知，服務停止。確認手臂停止後再重新啟動。"); break; }
    AtomicWrite(Path.Combine(assets, "current_step.json"), new StepEnvelope { StepId = ++stepId, Done = true });
    Console.WriteLine($"[實驗] {status}；紀錄：{run}。" +
        (fixedBaseline ? "下個任務需恢復初始桌面。" : "下個任務以當時的桌面為起點。"));
}
void Save(string dir, string name, object? value) => File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(value, json));
void AtomicWrite(string path, object value)
{
    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, json));
    File.Move(path + ".tmp", path, true);
}
async Task<List<SceneObject>> Scene()
{
    using var doc = JsonDocument.Parse(await http.GetStringAsync("scene"));
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
    try { var bytes = await http.GetByteArrayAsync("debug/frame"); File.WriteAllBytes(Path.Combine(dir, name), bytes); return bytes; }
    catch (Exception ex)
    {
        File.WriteAllText(Path.Combine(dir, name + ".error.txt"), ex.Message);
        throw new SceneUnavailableException("相機影像不可取得，不能作為任務失敗進行 Reflection。");
    }
}
// 相機內參與位姿只用來對齊 Isaac Sim 的模擬相機；取不到時模擬端沿用上次的相機，不中斷實驗。
async Task<JsonElement?> CameraInfo()
{
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
