using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

// ==========================================
// Step 1: Part A — YOLO + QRCode detection
// 輸出 ../sample_json/detected_objects.json
// ==========================================
bool partASuccess = PartAExporter.Run();

if (!partASuccess)
{
    Console.WriteLine("Part A failed.");
    return;
}

// ==========================================
// Step 2: Part B — Python 3D coordinate mapping
// 讀 ../sample_json/detected_objects.json
// 輸出 ../sample_json/objects_world.json
// ==========================================
Console.WriteLine("Running Part B Python coordinate mapper...");

var processInfo = new ProcessStartInfo
{
    FileName = "python",
    Arguments = "coordinate_mapper_3d.py",
    WorkingDirectory = Directory.GetCurrentDirectory(),
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true
};

using var process = Process.Start(processInfo);

if (process == null)
{
    Console.WriteLine("Failed to start Part B Python process.");
    return;
}

string partBOutput = process.StandardOutput.ReadToEnd();
string partBError = process.StandardError.ReadToEnd();

process.WaitForExit();

Console.WriteLine(partBOutput);

if (!string.IsNullOrWhiteSpace(partBError))
{
    Console.WriteLine("Part B error:");
    Console.WriteLine(partBError);
}

if (process.ExitCode != 0)
{
    Console.WriteLine("Part B failed.");
    return;
}

// ==========================================
// Step 3: Part C — LLM 指令規劃
// 讀 Part B 輸出的 objects_world.json
// ==========================================
string sceneJsonPath = "../sample_json/objects_world.json";

if (!File.Exists(sceneJsonPath))
{
    Console.WriteLine($"找不到 Part B 輸出：{sceneJsonPath}");
    return;
}

string sceneJson = File.ReadAllText(sceneJsonPath);

ObjectsWorld? objectsWorld = JsonSerializer.Deserialize<ObjectsWorld>(
    sceneJson,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
);

if (objectsWorld?.Objects == null || objectsWorld.Objects.Count == 0)
{
    Console.WriteLine("Part B 沒有輸出任何有效物件，無法繼續。");
    return;
}

// 轉成 LlmPlanner 需要的 SceneObject 格式
List<SceneObject> sceneObjects = objectsWorld.Objects
    .Where(obj => obj.Position != null)
    .Select(obj => new SceneObject
    {
        Name = obj.Name,
        X = obj.Position!.X,
        Y = obj.Position!.Y,
        Z = obj.Position!.Z
    })
    .ToList();

Console.WriteLine($"\n載入 {sceneObjects.Count} 個物件（世界座標，單位 m）：");
foreach (var o in sceneObjects)
    Console.WriteLine($"  {o.Name}  x={o.X:F3}  y={o.Y:F3}  z={o.Z:F3}");

Console.WriteLine("\n請輸入機械手臂指令：");
string? userCommand = Console.ReadLine();

if (string.IsNullOrWhiteSpace(userCommand))
{
    Console.WriteLine("指令不可為空。");
    return;
}

// ==========================================
// Step 3: LLM → robot_plan.json
// ==========================================
LlmPlanner planner = new();
RobotPlan plan = await planner.GeneratePlanAsync(userCommand, sceneObjects);

string outputJson = JsonSerializer.Serialize(
    plan,
    new JsonSerializerOptions { WriteIndented = true }
);

Console.WriteLine("\n=== robot_plan.json ===");
Console.WriteLine(outputJson);

// 同時存到兩個地方：本機 outputs/ 和 Unity 讀取的 StreamingAssets/
Directory.CreateDirectory("outputs");
File.WriteAllText("outputs/robot_plan.json", outputJson);
Console.WriteLine("Saved to outputs/robot_plan.json");

string unityPath = "../unity_project/Assets/StreamingAssets/robot_plan.json";
if (Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(unityPath))!))
{
    File.WriteAllText(unityPath, outputJson);
    Console.WriteLine($"Saved to {unityPath}");
}

// ==========================================
// 輔助 class：對應 Part A 輸出的 JSON 格式
// ==========================================
// ==========================================
// 輔助 class：對應 Part B 輸出的 objects_world.json 格式
// ==========================================
public class ObjectsWorld
{
    [JsonPropertyName("objects")]
    public List<WorldObject> Objects { get; set; } = new();
}

public class WorldObject
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("world_position")]  
    public WorldPos? Position { get; set; }
}

public class WorldPos
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }
}