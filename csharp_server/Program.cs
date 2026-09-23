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
var baseline = File.Exists(baselinePath) ? JsonSerializer.Deserialize<List<SceneObject>>(File.ReadAllText(baselinePath), json) : null;
int stepId = checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
Console.WriteLine("自由規劃實驗：每任務最多 10 次；任務間恢復初始桌面，任務內不重置。");
Console.WriteLine($"初始配置：{baselinePath}；第一次任務建立。更換配置需停止服務後移除此檔。");
while (true)
{
    var input = Path.Combine(assets, "user_input.txt");
    if (!File.Exists(input) || string.IsNullOrWhiteSpace(File.ReadAllText(input))) { await Task.Delay(500); continue; }
    var goal = File.ReadAllText(input).Trim();
    File.WriteAllText(input, "");
    var run = Path.Combine(output, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
    Directory.CreateDirectory(run);
    File.WriteAllText(Path.Combine(run, "task.txt"), goal);
    Save(run, "config.json", new { model, max_attempts = 10, reset_between_tasks = true,
        reset_within_task = false, reset_xy_m = 0.015, reset_z_m = 0.01,
        rule_scope = "task", evaluation = "independent_visual_model", started_utc = DateTime.UtcNow });
    bool success = false;
    bool executionPending = false;
    string status = "failed";
    int attempts = 0;
    var rules = new List<string>();
    var feedback = "無前次結果。";
    try
    {
        List<SceneObject> initial;
        int stable = 0;
        List<SceneObject>? previous = null;
        int resetWaitSeconds = 0;
        Console.WriteLine("[任務重置] 將實體積木恢復初始配置後，相機確認桌面即開始；不會自動搬回積木。");
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
                string expectedSummary = SceneInventory(baseline ?? previous ?? new List<SceneObject>());
                string actualSummary = SceneInventory(initial);
                Console.WriteLine($"[任務重置] 仍在等待初始桌面，已等待 {resetWaitSeconds} 秒（無逾時限制）。");
                Console.WriteLine($"             基準：{expectedSummary}");
                Console.WriteLine($"             目前：{actualSummary}");
            }
        }
        if (baseline == null) { baseline = initial; Save(output, "initial_scene.json", baseline); }
        Save(run, "initial_scene.json", initial);
        Console.WriteLine("[任務重置] 初始桌面已確認；本任務內不再重置。");
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            var dir = Path.Combine(run, $"attempt_{attempt:00}");
            Directory.CreateDirectory(dir);
            Console.WriteLine($"[實驗] 第 {attempt}/10 次規劃，保留目前場景。");
            string failure = "", plan = "";
            var local = new List<VerifyResult>();
            try
            {
                var before = await Scene();
                if (before.Count == 0) throw new SceneUnavailableException("沒有有效場景觀測。");
                Save(dir, "before_scene.json", before);
                var image = await Frame(dir, "before.jpg");
                var hierarchy = await llm.Decompose(goal, before, rules, feedback, image, dir);
                plan = await llm.Plan(goal, before, hierarchy, rules, feedback, image, dir);
                var translated = await llm.Translate(plan, before, dir);
                Save(dir, "translated_plan.json", translated);
                // A task attempt starts only after the internal adapter has
                // produced a readable contract. Adapter failures are not task
                // reasoning failures and never enter reflection.
                attempts = attempt;
                if (!string.IsNullOrWhiteSpace(translated.Error)) throw new InvalidOperationException("轉譯失敗：" + translated.Error);
                if (translated.Steps == null || translated.Steps.Count > 50) throw new InvalidOperationException("執行資料無效或超過單次 50 步上限。");
                // 疊放檢查：規劃裡有目標不是貼桌面（疊在別的積木上）才跑，先在
                // Isaac Sim 用物理引擎確認穩不穩，不穩就直接算這次 attempt 失敗、
                // 不送真實手臂，走跟其他失敗一樣的路徑進 Reflect 產生下一輪教訓。
                if (IsaacSimGate.RequiresCheck(translated.Steps))
                {
                    var (stable, detail) = IsaacSimGate.CheckStability(translated.Steps, dir);
                    Save(dir, "isaac_gate.json", new { stable, detail });
                    if (!stable) throw new InvalidOperationException("Isaac Sim 疊放模擬：" + detail);
                }
                foreach (var step in translated.Steps)
                {
                    var current = await Scene();
                    if (!ExperimentChecks.Resolve(step, before, current, out var assignment, out var error)) throw new InvalidOperationException(error);
                    assignment.StepId = ++stepId;
                    var motion = new MotionPlan { ActionSequence = step.Actions, Reasoning = plan };
                    if (!MotionPlanValidator.TryValidate(motion, assignment, current, out error)) throw new InvalidOperationException("執行前檢查：" + error);
                    var env = new StepEnvelope { StepId = assignment.StepId, SourcePosition = assignment.Source,
                        TargetPosition = step.Target, ActionSequence = step.Actions, Comment = "自由規劃實驗" };
                    Save(dir, $"step_{stepId}.json", env);
                    int batchId = ++stepId;
                    // Use the existing batch entry point even for one operation:
                    // it prepares shared trajectories and honors preview-only.
                    AtomicWrite(Path.Combine(assets, "current_step.json"), new BatchEnvelope {
                        BatchId = batchId, Steps = new List<StepEnvelope> { env }, Comment = env.Comment
                    });
                    executionPending = true;
                    var execution = await Wait(batchId);
                    if (execution != null) executionPending = false;
                    Save(dir, $"execution_{stepId}.json", execution);
                    if (execution == null) { status = "execution_unknown"; throw new ExecutionUnknownException(); }
                    if (!execution.Completed) throw new InvalidOperationException(execution.Error ?? "執行失敗");
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
                    if (check.OverallStatus != "ok") throw new InvalidOperationException("局部驗證：" + check.Note);
                }
            }
            catch (ExecutionUnknownException) { throw; }
            catch (InvalidOperationException ex) { failure = ex.Message; }
            var after = await Scene();
            Save(dir, "after_scene.json", after);
            var afterImage = await Frame(dir, "after.jpg");
            var verdict = await llm.Validate(goal, initial, after, afterImage, dir);
            feedback = $"執行／局部觀察：{failure}\n整體觀察：{verdict}";
            File.WriteAllText(Path.Combine(dir, "feedback.txt"), feedback);
            success = string.IsNullOrEmpty(failure) && after.Count > 0 && afterImage != null && verdict.Split('\n')[0].Trim() == "PASS";
            if (success) { status = "success"; Console.WriteLine($"[實驗] 第 {attempt} 次達標。"); break; }
            var reflection = await llm.Reflect(goal, plan, feedback, rules, dir);
            rules = new List<string> { reflection };
            File.WriteAllText(Path.Combine(dir, "rules_for_next_attempt.txt"), reflection);
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
    Save(run, "result.json", new { success, status, attempts,
        counts_toward_success_rate = status is "success" or "failed",
        final_rules = rules, finished_utc = DateTime.UtcNow });
    ExperimentMetrics.Write(output);
    if (status == "execution_unknown") { Console.WriteLine("執行狀態未知，服務停止。確認手臂停止後再重新啟動。"); break; }
    AtomicWrite(Path.Combine(assets, "current_step.json"), new StepEnvelope { StepId = ++stepId, Done = true });
    Console.WriteLine($"[實驗] {status}；紀錄：{run}。下個任務需恢復初始桌面。");
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
async Task<ExecutionResult?> Wait(int id)
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
            Console.WriteLine($"[Unity/UR3] batch {id} 仍在執行，已等待 {elapsedSeconds} 秒（無逾時限制）...");
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
