using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

// ==========================================
// Step 1: Part A — YOLO + QRCode + 座標對應
// ==========================================
PartAExporter.Run();

// ==========================================
// Step 2: Part C — LLM 指令規劃
// 讀 Part A 直接輸出的 detection_result.json
// ==========================================
string sceneJsonPath = "outputs/detection_result.json";

if (!File.Exists(sceneJsonPath))
{
    Console.WriteLine($"找不到 Part A 輸出：{sceneJsonPath}");
    return;
}

string sceneJson = File.ReadAllText(sceneJsonPath);

DetectionOutput? detectionOutput = JsonSerializer.Deserialize<DetectionOutput>(
    sceneJson,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
);

if (detectionOutput?.Objects == null || detectionOutput.Objects.Count == 0)
{
    Console.WriteLine("Part A 沒有偵測到任何物件，無法繼續。");
    return;
}

// 轉成 LlmPlanner 需要的 SceneObject 格式
List<SceneObject> sceneObjects = detectionOutput.Objects
    .Where(obj => obj.WorldPosition != null)
    .Select(obj => new SceneObject
    {
        Name = obj.Name,
        X    = obj.WorldPosition!.X,
        Y    = obj.WorldPosition!.Y,
        Z    = obj.WorldPosition!.Z
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
public class DetectionOutput
{
    public int ImageWidth  { get; set; }
    public int ImageHeight { get; set; }

    [JsonPropertyName("objects")]
    public List<DetectedObject> Objects { get; set; } = new();
}

public class DetectedObject
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("world_position")]
    public WorldPos? WorldPosition { get; set; }

    [JsonPropertyName("bbox")]
    public double[] Bbox { get; set; } = Array.Empty<double>();

    [JsonPropertyName("center_pixel")]
    public double[] CenterPixel { get; set; } = Array.Empty<double>();

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";
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
