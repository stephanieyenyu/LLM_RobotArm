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
// 銝?orchestrator嚗?撅斗瑽?
// 瘥蝙?刻?隞方?銝甈?5-layer ?嚗?
//   Layer 1 (PatternDesigner) ??CanonicalPattern
//   Layer 2 (LayoutRealizer)  ??List<TargetCell>
//   Layer 3 (TaskAssigner)    ??瘥郊 1 ??Assignment
//   Layer 4A (MotionPlanner)  ??LLM 蝯? robot functions
//   Layer 4B (Validator/Unity)??摰撽??? URScript?銵甇?//   Layer 5 (Verifier)        ??瑼Ｘ?捱摰?retry / replan / abort
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

// ??嚗Ⅱ隤?perception_server 撌脣頝?
try
{
    var health = await httpClient.GetFromJsonAsync<JsonElement>("health", jsonOptions);
    string status = health.TryGetProperty("status", out var s) ? s.GetString() ?? "?" : "?";
    Console.WriteLine($"[perception_server] 撌脤?? (status={status})");
}
catch (Exception ex)
{
    Console.WriteLine($"[perception_server] ?⊥???? ??{ex.Message}");
    return;
}

// 瑼?頝臬?
string unityStreamingAssets = "../unity_project/Assets/StreamingAssets";
string inputPath = Path.Combine(unityStreamingAssets, "user_input.txt");
string currentStepPath = Path.Combine(unityStreamingAssets, "current_step.json");
string stepDonePath = Path.Combine(unityStreamingAssets, "step_done.json");
string localOutputDir = "outputs";
Directory.CreateDirectory(localOutputDir);

Console.WriteLine();
Console.WriteLine("=== LLM Planner (?惜?嗆?) 撌脣???===");
Console.WriteLine($"??嚗Path.GetFullPath(inputPath)}");
Console.WriteLine($"瘥郊?誘嚗Path.GetFullPath(currentStepPath)}");
Console.WriteLine($"?瑁??嚗Path.GetFullPath(stepDonePath)}");
Console.WriteLine("蝑? Unity 頛詨?誘...");
Console.WriteLine();

// 撱?layer instances
var workspace = new WorkspaceBounds();
var patternDesigner = new PatternDesigner(workspace.MaxRows, workspace.MaxCols);
var motionPlanner = new MotionPlanner();

// 皜征 input + old files
if (File.Exists(inputPath)) File.WriteAllText(inputPath, "");
if (File.Exists(currentStepPath)) File.Delete(currentStepPath);
if (File.Exists(stepDonePath)) File.Delete(stepDonePath);

int globalStepId = 0;

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
        Console.WriteLine($"?嗅?誘嚗userCommand}");

        await RunTaskAsync(userCommand);

        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"?航炊嚗ex.Message}");
        await Task.Delay(500);
    }
}

// --- 銝颱遙????---
async Task RunTaskAsync(string userCommand)
{
    // ??銝甈⊥??supplies + block color 瘙箇?靘?
    var initialSnap = await FetchSceneAsync();
    string blockColor = GuessBlockColor(userCommand, initialSnap);
    
    int cubeBudget = initialSnap.Count(s =>
    s.Name == $"{blockColor}_cube" && s.X < 0.30);

    int dominoBudget = initialSnap.Count(s =>
    s.Name == $"{blockColor}_domino" && s.X < 0.30);

    int maxCoveredCells = cubeBudget + dominoBudget * 2;
    Console.WriteLine($"[Layer 1] ??LLM 閮剛? pattern (color={blockColor})...");

    CanonicalPattern pattern;
    try
    {
        pattern = await patternDesigner.DesignAsync(userCommand, blockColor, cubeBudget, dominoBudget);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Layer 1] pattern 閮剛?憭望?嚗ex.Message}");
        return;
    }
    Console.WriteLine($"[Layer 1] pattern={pattern.PatternId}, bitmap={pattern.Bitmap!.GetLength(0)}x{pattern.Bitmap.GetLength(1)}");
    // ??ASCII ??+ 摮 outputs/pattern_XX.json ?嫣噶 debug
    int br = pattern.Bitmap.GetLength(0), bc = pattern.Bitmap.GetLength(1);
    var rows = new List<string>();
    for (int r = 0; r < br; r++)
    {
        var sb = new System.Text.StringBuilder();
        for (int c = 0; c < bc; c++) sb.Append(pattern.Bitmap[r, c] == 1 ? "?? : "??);
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

    // Layer 2嚗????target嚗ubeBudget / dominoBudget 撌脣銝蝞末嚗?
    var realize = LayoutRealizer.Realize(pattern, workspace, cubeBudget, dominoBudget);
    if (realize.Error != null || realize.Targets == null)
    {
        Console.WriteLine($"[Layer 2] {realize.Error}");
        return;
    }
    Console.WriteLine($"[Layer 2] 撅???{realize.Targets.Count} ??target cells "
                      + $"({realize.Targets.Count(t => t.ExpectedShape == "domino")} domino + "
                      + $"{realize.Targets.Count(t => t.ExpectedShape == "cube")} cube)");

    var remainingTargets = new List<TargetCell>(realize.Targets);
    var placedTargets = new List<TargetCell>();
    int retryCountThisStep = 0;
    string? motionFeedback = null;
    const int MAX_RETRY = 2;

    // Layer 3/4/5 ?
    while (remainingTargets.Count > 0)
    {
        globalStepId++;
        Console.WriteLine();
        Console.WriteLine($"??? Step {globalStepId} ???");

        // 瘥郊?賡???甈?(Layer 3 ?閬???supply ?瘜?
        var beforeSnap = await FetchSceneAsync();

        var assignment = TaskAssigner.Assign(remainingTargets, beforeSnap, globalStepId);
        if (assignment == null)
        {
            Console.WriteLine($"[Layer 3] 瘝??臬銵? assignment嚗upply ?典???頞喉?");
            break;
        }
        Console.WriteLine($"[Layer 3] {assignment.Reasoning}");

        // Layer 4A嚗LM 雿輻?賢???robot functions ?芾?閬?????        // ?憭?瘙?LLM 靽格迤?拇活嚗遙雿?? deterministic validator ???恍銝? Unity??        MotionPlan? motionPlan = null;
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
                Console.WriteLine($"[Layer 4A] 蝚?{planAttempt} 甈∪?怠仃??{ex.Message}");
                motionPlan = null;
                continue;
            }
            if (MotionPlanValidator.TryValidate(motionPlan, out validationError)) break;
            Console.WriteLine($"[Layer 4A] ??閮蝚?{planAttempt} 甈⊥??摰瑼Ｘ嚗validationError}");
            motionPlan = null;
        }
        if (motionPlan == null)
        {
            Console.WriteLine("[Layer 4A] ?⊥??Ｙ?摰??閮嚗甈∩遙?葉甇?);
            break;
        }
        Console.WriteLine($"[Layer 4A] LLM motion plan嚗motionPlan.ActionSequence.Count} functions ??{motionPlan.Reasoning}");

        // Layer 4B嚗?撌脤?霅? function sequence 撖怎策 Unity嚗nity 銝??箏?撅? 12 甇乓?        var envelope = new StepEnvelope
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

        Console.WriteLine($"[Layer 4] ? step {assignment.StepId}嚗? Unity ?瑁?...");
        var execResult = await WaitForStepDoneAsync(assignment.StepId, timeoutSec: 90);
        if (execResult == null || !execResult.Completed)
        {
            Console.WriteLine($"[Layer 4] ?瑁? timeout ?仃??{execResult?.Error}");
            break;
        }
        Console.WriteLine($"[Layer 4] ?瑁?摰? ({execResult.DurationSec:F1}s)");

        // Layer 5嚗?霅?
        var afterSnap = await FetchSceneAsync();
        var verify = Verifier.CheckStep(assignment, beforeSnap, afterSnap);
        Console.WriteLine($"[Layer 5] {verify.OverallStatus} ??{verify.Note}");

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
                    Console.WriteLine($"       ??甇仿?閰?{MAX_RETRY} 甈∩?憭望?嚗歲??);
                    remainingTargets.RemoveAll(t => t.Row == keyRow && t.Col == keyCol);
                    retryCountThisStep = 0;
                }
                break;
            case "replan":
                // 銝? remaining嚗?銝頛?Layer 3 ???圈?撠?                motionFeedback = verify.Note;
                break;
            case "abort":
                Console.WriteLine($"       abort嚗?甇Ｘ甈∩遙??);
                goto TaskDone;
        }

        Console.WriteLine($"       ?拚? targets: {remainingTargets.Count}");
    }

    TaskDone:
    // ?喲?done 霈?Unity ??polling
    WriteStepFile(new StepEnvelope { StepId = ++globalStepId, Done = true });

    // ?湧?撽?
    var finalSnap = await FetchSceneAsync();
    var overallResults = Verifier.CheckOverall(realize.Targets, finalSnap);
    int matched = overallResults.Count(r => r.matched);
    Console.WriteLine();
    Console.WriteLine($"=== 隞餃?蝯? ===");
    Console.WriteLine($"?湧?撽?嚗matched}/{overallResults.Count} ?潭迤蝣?);
    foreach (var (t, ok) in overallResults.Where(x => !x.matched))
    {
        Console.WriteLine($"  ??r{t.Row}c{t.Col} ({t.ExpectedShape}) 瘝撠?);
    }
}

// --- 頛 ---
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
        Console.WriteLine($"[perception] fetch scene 憭望?嚗ex.Message}");
        return new List<SceneObject>();
    }
}

string GuessBlockColor(string userCommand, List<SceneObject> snap)
{
    if (userCommand.Contains("black") || userCommand.Contains("暺?)) return "black";
    if (userCommand.Contains("yellow") || userCommand.Contains("暺?)) return "yellow";
    // 瘝?摰停??supply 憭?
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
                    File.Delete(stepDonePath);   // ?典?皜?
                    return result;
                }
            }
            catch { /* 敹賜霈撖怎１??*/ }
        }
        await Task.Delay(200);
    }
    return null;
}

// ???? perception ??
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

