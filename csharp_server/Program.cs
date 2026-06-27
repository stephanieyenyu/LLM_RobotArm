using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

// ==========================================
// Step 1: Part A — YOLO + QRCode + 座標對應
// ==========================================
PartAExporter.Run();

// ==========================================
// Step 2: Part C — LLM 指令規劃
// ==========================================
string sceneJsonPath = "../sample_json/objects_world.json";

if (!File.Exists(sceneJsonPath))
{
    Console.WriteLine($"找不到 Part B 輸出檔案：{sceneJsonPath}");
    Console.WriteLine("請確認 Part A 是否成功偵測到 QR1, QR2, QR3, QR4。");
    return;
}

string sceneJson = File.ReadAllText(sceneJsonPath);

ObjectsWorld? objectsWorld = JsonSerializer.Deserialize<ObjectsWorld>(
    sceneJson,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
);

if (objectsWorld?.Objects == null || objectsWorld.Objects.Count == 0)
{
    Console.WriteLine("Part A/B 沒有提供有效的物件座標資料。");
    return;
}

List<SceneObject> sceneObjects = objectsWorld.Objects
    .Select(obj => new SceneObject
    {
        Name = obj.Name,
        X    = obj.Position.X,
        Y    = obj.Position.Y,
        Z    = obj.Position.Z
    })
    .ToList();

Console.WriteLine($"載入 {sceneObjects.Count} 個物件：");
foreach (var o in sceneObjects)
    Console.WriteLine($"  {o.Name}  x={o.X:F3} y={o.Y:F3} z={o.Z:F3}");

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

Directory.CreateDirectory("../sample_json");
File.WriteAllText("../sample_json/robot_plan.json", outputJson);
Console.WriteLine("Saved to ../sample_json/robot_plan.json");

// ==========================================
// 輔助 class（Program.cs 頂層宣告）
// ==========================================
public class ObjectsWorld
{
    public WorkspaceInfo? Workspace { get; set; }
    public List<WorldObject> Objects { get; set; } = new();
}

public class WorkspaceInfo
{
    public string? Type { get; set; }
}

public class WorldObject
{
    public string Name     { get; set; } = "";
    public double Confidence { get; set; }
    public Vec3   Position { get; set; } = new();
}

public class Vec3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
