using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Demo: one snapshot, ONE LLM intent call, one fixed batch; no LLM motion/vision.
string assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../unity_project/Assets/StreamingAssets"));
if (!Directory.Exists(assets)) assets = Path.GetFullPath("../unity_project/Assets/StreamingAssets");
if (!Directory.Exists(assets)) throw new DirectoryNotFoundException("Run dotnet from csharp_server.");
string input = Path.Combine(assets, "user_input.txt"), current = Path.Combine(assets, "current_step.json"),
    report = Path.Combine(assets, "step_done.json");
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true, IncludeFields = true };
var executionJournal = new DemoExecutionJournal(DemoExecutionJournal.DirectoryForAssets(assets),
    r => JsonSerializer.Serialize(r, json), s => JsonSerializer.Deserialize<DemoExecutionReceipt>(s, json)!);
using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5000/"), Timeout = TimeSpan.FromSeconds(8) };
var router = new CommandRouter();
var workspace = new WorkspaceBounds();
int nextId = Math.Max(checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
    executionJournal.ReadAll().Select(r => r.batch_id).DefaultIfEmpty(0).Max());
Directory.CreateDirectory("outputs");
Console.WriteLine("=== DEMO: fixed 3x3 L / 5 cubes / 12 URScript programs per cube ===");
Console.WriteLine($"Input: {input}");
Console.WriteLine("Target: 120–180 seconds. Snapshot once; no camera verification/replanning.");
Console.WriteLine($"Cell size: {workspace.CellSize * 100:F1} cm; origin: ({workspace.TargetOriginX:F3}, {workspace.TargetOriginY:F3}).");
AtomicWrite(current, new { step_id = ++nextId, done = true, control_event = "startup_reset", related_batch_id = 0 });
Console.WriteLine("[Demo] 服務初始化／清理舊任務；這不是批次完成回報。");
File.WriteAllText(input, "");

while (true)
{
    if (!File.Exists(input) || string.IsNullOrWhiteSpace(File.ReadAllText(input)))
    { await Task.Delay(200); continue; }
    string command = File.ReadAllText(input).Trim();
    File.WriteAllText(input, "");
    int batchId = nextId += 100;
    var timer = Stopwatch.StartNew();
    string run = Path.Combine("outputs", $"demo_{DateTime.Now:yyyyMMdd_HHmmss}_{batchId}");
    Directory.CreateDirectory(run);
    bool sent = false, unknown = false, completed = false;
    try
    {
        var unfinished = executionJournal.ReadAll().Where(r => r.status == "executing").ToList();
        if (unfinished.Count > 0)
        {
            Save(run, "result.json", new { status = "blocked_by_active_batch", completed = false,
                dispatched = false, execution_unknown = true,
                error = "An earlier Unity batch has no terminal receipt", batch_ids = unfinished.Select(r => r.batch_id) });
            Console.WriteLine("[Demo] 上一批尚無完成／中斷回報，沒有派發新動作：" + string.Join(", ", unfinished.Select(r => r.batch_id)));
            continue;
        }
        if (File.Exists(Path.Combine(assets, "demo_motion_blocked.txt")))
        {
            Save(run, "result.json", new { status = "blocked", completed = false,
                dispatched = false, execution_unknown = false,
                error = "Hardware motion locked after self-collision", elapsed_seconds = timer.Elapsed.TotalSeconds });
            Console.WriteLine("[Demo] 未執行：自撞後的實機動作鎖仍啟用。本次未拍照、未規劃、未送出動作。");
            Console.WriteLine("[Demo] 路徑與工具外型尚待驗證；目前不能重跑，也不會自動回 Home。");
            continue;
        }
        using var response = await http.PostAsync("demo/snapshot", null);
        response.EnsureSuccessStatusCode();
        var snapshot = await response.Content.ReadFromJsonAsync<DemoSnapshot>(json)
            ?? throw new InvalidDataException("Empty snapshot.");
        File.WriteAllBytes(Path.Combine(run, "initial.jpg"), Convert.FromBase64String(snapshot.Frame));
        var scene = snapshot.Objects.Where(o => o.Position != null).Select(o => new SceneObject {
            Name = o.Name, X = o.Position!.X, Y = o.Position.Y, Z = o.Position.Z,
            Shape = o.Shape ?? "cube", Orientation = o.Orientation, SkewDeg = o.SkewDeg
        }).ToList();
        Save(run, "initial_scene.json", scene);
        if (scene.Count == 0) throw new InvalidOperationException("No objects with QR coordinates in initial snapshot.");
        var routed = await router.RouteAsync(command, scene).WaitAsync(TimeSpan.FromSeconds(30));
        Save(run, "intent.json", routed);
        Console.WriteLine($"[Intent only] {routed.Action}: {routed.Reasoning}");
        DemoBatch batch = DemoPlanner.Build(routed, command, scene, workspace, batchId);
        Save(run, "batch.json", batch);
        var text = new StringBuilder("執行路徑開始\n");
        foreach (var s in batch.Steps)
        {
            text.AppendLine(FormattableString.Invariant($"source index {s.SourceIndex} -> target ({s.Target.X:F4}, {s.Target.Y:F4}, {s.Target.Z:F4})"));
            for (int i = 0; i < s.Commands.Length; i++) text.AppendLine($"{i + 1:00}. {s.Commands[i]}");
        }
        text.AppendLine("執行路徑結束");
        File.WriteAllText(Path.Combine(run, "plan.txt"), text.ToString());
        Console.WriteLine(text);
        if (File.Exists(report)) File.Delete(report);
        AtomicWrite(current, batch);
        sent = true;
        ExecutionResult? result = null;
        var waiting = Stopwatch.StartNew();
        string lastProgress = "";
        while (waiting.Elapsed.TotalSeconds < 360)
        {
            if (File.Exists(report))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<ExecutionResult>(File.ReadAllText(report), json);
                    if (parsed?.StepId == batchId) { result = parsed; break; }
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            try
            {
                var receipt = executionJournal.Read(batchId);
                if (receipt != null)
                {
                    string progress = $"{receipt.step_id}/{receipt.action_index} {receipt.last_action}";
                    if (receipt.status == "executing" && progress != lastProgress)
                    {
                        Console.WriteLine($"[Demo 進度] {progress}" +
                            (receipt.last_action == "go_home" ? "；等待控制器確認 Home 到位，尚未完成回報。" : ""));
                        lastProgress = progress;
                    }
                    if (receipt.status != "executing")
                    {
                        result = new ExecutionResult { StepId = batchId, Status = receipt.status,
                            Completed = receipt.completed, ExecutionUnknown = receipt.execution_unknown,
                            Error = receipt.error, DurationSec = receipt.duration_sec };
                        break;
                    }
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            await Task.Delay(100);
        }
        unknown = result == null || result.ExecutionUnknown;
        completed = result?.Completed == true && !unknown;
        Save(run, "result.json", new {
            status = result?.Status ?? "execution_unknown", completed, execution_unknown = unknown,
            error = result?.Error ?? "Unity batch report timed out",
            elapsed_seconds = Math.Round(timer.Elapsed.TotalSeconds, 1),
            robot_seconds = result?.DurationSec, target_seconds = new[] { 120, 180 }, camera_verification = false
        });
        Console.WriteLine($"[Demo] completed={result?.Completed ?? false}, total={timer.Elapsed.TotalSeconds:F1}s, robot={result?.DurationSec:F1}s; {result?.Error}");
    }
    catch (Exception ex)
    {
        unknown = sent;
        Save(run, "result.json", new { completed = false, execution_unknown = unknown,
            error = ex.Message, elapsed_seconds = timer.Elapsed.TotalSeconds });
        Console.WriteLine($"[Demo] {ex.Message}");
    }
    if (unknown)
    {
        Console.WriteLine("[Demo] 執行已中斷或狀態未確認（例如 Unity 重載／退出）。不是完成；服務停止，不會重播舊批次。");
        break;
    }
    AtomicWrite(current, new { step_id = ++nextId, done = true,
        control_event = completed ? "batch_completed" : "batch_failed", related_batch_id = batchId });
    Console.WriteLine(completed
        ? "[Demo] 動作批次執行完成（未做視覺驗證）；下一批前請恢復供料與目標區。"
        : sent ? "[Demo] 動作批次已中止／失敗，沒有完成 demo。請先檢查失敗原因。"
               : "[Demo] 任務未啟動，沒有送出動作。請檢查上方錯誤。");
}
void Save(string dir, string name, object value) =>
    File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(value, json));
void AtomicWrite(string path, object value)
{
    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, json));
    File.Move(path + ".tmp", path, true);
}
public sealed class DemoSnapshot
{
    [JsonPropertyName("frame_jpeg_base64")] public string Frame { get; set; } = "";
    [JsonPropertyName("objects")] public List<WorldObject> Objects { get; set; } = new();
}
public sealed class WorldObject
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("position")] public WorldPos? Position { get; set; }
    [JsonPropertyName("shape")] public string? Shape { get; set; }
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }
    [JsonPropertyName("skew_deg")] public double SkewDeg { get; set; }
}
public sealed class WorldPos
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
}

