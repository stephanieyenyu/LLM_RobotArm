using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
await PartABPipeline.RunAsync();
Console.WriteLine("請輸入機械手臂指令：");
string? userCommand = Console.ReadLine();

if (string.IsNullOrWhiteSpace(userCommand))
{
    Console.WriteLine("指令不可為空。");
    return;
}

// 測試階段先模擬 Part B 的輸出。
// 未來這裡改成接收 Part B 根據 YOLO + 三個 QR code 算出的物件名稱與座標。

//string sceneJson = File.ReadAllText("scene_objects.json");
//List<SceneObject>? sceneObjects =
//    JsonSerializer.Deserialize<List<SceneObject>>(sceneJson);

//if (sceneObjects == null || sceneObjects.Count == 0)
//{
//    Console.WriteLine("Part B 沒有提供有效的物件座標資料。");
//    return;
//}

//假資料(之後用上面代換)
//List<SceneObject> sceneObjects = new()
//{
//   new SceneObject { Name = "bottle", X = 120.5, Y = 45.2, Z = 30.0 },
// new SceneObject { Name = "cup", X = 80.0, Y = 20.0, Z = 30.0 },
// new SceneObject { Name = "scissors", X = 140.0, Y = 50.0, Z = 25.0 },
// new SceneObject { Name = "box", X = 220.0, Y = 90.0, Z = 20.0 }
//};
string sceneJsonPath = "../sample_json/objects_world.json";

if (!File.Exists(sceneJsonPath))
{
    Console.WriteLine($"找不到 Part B 輸出檔案：{sceneJsonPath}");
    return;
}

string sceneJson = File.ReadAllText(sceneJsonPath);

ObjectsWorld? objectsWorld = JsonSerializer.Deserialize<ObjectsWorld>(
    sceneJson,
    new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }
);

if (objectsWorld == null || objectsWorld.Objects == null || objectsWorld.Objects.Count == 0)
{
    Console.WriteLine("Part B 沒有提供有效的物件座標資料。");
    return;
}

List<SceneObject> sceneObjects = objectsWorld.Objects
    .Select(obj => new SceneObject
    {
        Name = obj.Name,
        X = obj.Position.X,
        Y = obj.Position.Y,
        Z = obj.Position.Z
    })
    .ToList();
LlmPlanner planner = new();

RobotPlan plan = await planner.GeneratePlanAsync(userCommand, sceneObjects);

string outputJson = JsonSerializer.Serialize(
    plan,
    new JsonSerializerOptions
    {
        WriteIndented = true
    }
);

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
    public string Name { get; set; } = "";
    public double Confidence { get; set; }
    public Vec3 Position { get; set; } = new();
}

public class Vec3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}
Console.WriteLine(outputJson);
Directory.CreateDirectory("../sample_json");
File.WriteAllText("../sample_json/robot_plan.json", outputJson);
Console.WriteLine("Saved to ../sample_json/robot_plan.json");