using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Threading.Tasks;

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
    RedirectStandardOutput = false,
    RedirectStandardError = false,
    UseShellExecute = false,
    CreateNoWindow = true
};

using var process = Process.Start(processInfo);

if (process == null)
{
    Console.WriteLine("Failed to start Part B Python process.");
    return;
}

process.WaitForExit();

if (process.ExitCode != 0)
{
    Console.WriteLine($"Part B failed. ExitCode = {process.ExitCode}");
    return;
}

Console.WriteLine("=== Part B Finished ===");

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

// ==========================================
// Step 4: 監聽 user_input.txt，呼叫 LLM 寫 robot_plan.json
// 不再從終端機讀，改成監聽 Unity 寫入的指令檔
// ==========================================
string unityStreamingAssets = "../unity_project/Assets/StreamingAssets";
string inputPath = Path.Combine(unityStreamingAssets, "user_input.txt");
string outputPath = Path.Combine(unityStreamingAssets, "robot_plan.json");
string localOutputDir = "outputs";

Directory.CreateDirectory(localOutputDir);

Console.WriteLine($"\n=== LLM Planner 已啟動 ===");
Console.WriteLine($"監聽：{Path.GetFullPath(inputPath)}");
Console.WriteLine($"輸出：{Path.GetFullPath(outputPath)}");
Console.WriteLine("等待 Unity 輸入指令...\n");

LlmPlanner planner = new();
string lastContent = "";
// 啟動時清空 user_input.txt
if (File.Exists(inputPath))
{
    File.WriteAllText(inputPath, "");
}

while (true)
{
    try
    {
        if (File.Exists(inputPath))
        {
            string userCommand = File.ReadAllText(inputPath).Trim();

            if (!string.IsNullOrWhiteSpace(userCommand) && userCommand != lastContent)
            {
                lastContent = userCommand;
                Console.WriteLine($"收到指令：{userCommand}");

                RobotPlan plan = await planner.GeneratePlanAsync(userCommand, sceneObjects);

                string outputJson = JsonSerializer.Serialize(
                    plan,
                    new JsonSerializerOptions { WriteIndented = true }
                );

                File.WriteAllText(Path.Combine(localOutputDir, "robot_plan.json"), outputJson);
                File.WriteAllText(outputPath, outputJson);

                Console.WriteLine($"已寫入 robot_plan.json");
                File.WriteAllText(inputPath, "");
                Console.WriteLine($"--- robot_plan.json ---");
                Console.WriteLine(outputJson);
                Console.WriteLine();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"錯誤：{ex.Message}");
    }

    await Task.Delay(500);
}

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

    [JsonPropertyName("position")]
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
