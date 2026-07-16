using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

// ==========================================
// 啟動：確認 perception_server 已在跑
// ==========================================
using HttpClient httpClient = new()
{
    BaseAddress = new Uri("http://localhost:5000/"),
    Timeout = TimeSpan.FromSeconds(5),
};

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

try
{
    var health = await httpClient.GetFromJsonAsync<JsonElement>("health", jsonOptions);
    int framesProcessed = health.TryGetProperty("frames_processed", out var f) ? f.GetInt32() : 0;
    string status = health.TryGetProperty("status", out var s) ? s.GetString() ?? "?" : "?";
    Console.WriteLine($"[perception_server] 已連線 (status={status}, frames_processed={framesProcessed})");
}
catch (Exception ex)
{
    Console.WriteLine($"[perception_server] 無法連線 http://localhost:5000 → {ex.Message}");
    Console.WriteLine("請先開一個新的 PowerShell 跑：");
    Console.WriteLine("  cd csharp_server");
    Console.WriteLine("  yolo11_env\\Scripts\\python.exe perception_server.py");
    return;
}

// ==========================================
// LLM Planner 啟動
// ==========================================
string unityStreamingAssets = "../unity_project/Assets/StreamingAssets";
string inputPath = Path.Combine(unityStreamingAssets, "user_input.txt");
string outputPath = Path.Combine(unityStreamingAssets, "robot_plan.json");
string localOutputDir = "outputs";

Directory.CreateDirectory(localOutputDir);

Console.WriteLine();
Console.WriteLine($"=== LLM Planner 已啟動 ===");
Console.WriteLine($"感知服務：http://localhost:5000/scene");
Console.WriteLine($"監聽：{Path.GetFullPath(inputPath)}");
Console.WriteLine($"輸出：{Path.GetFullPath(outputPath)}");
Console.WriteLine("等待 Unity 輸入指令...");
Console.WriteLine();

LlmPlanner planner = new();

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

            if (!string.IsNullOrWhiteSpace(userCommand))
            {
                // 先清空，避免下一輪 tick 重讀同一句
                File.WriteAllText(inputPath, "");

                Console.WriteLine($"收到指令：{userCommand}");

                // 每次收到新指令都去 perception_server 拉當下場景（不是啟動時的舊快照）
                ObjectsWorld? objectsWorld;
                try
                {
                    objectsWorld = await httpClient.GetFromJsonAsync<ObjectsWorld>("scene", jsonOptions);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[perception] 取場景失敗：{ex.Message}");
                    continue;
                }

                if (objectsWorld?.Objects == null)
                {
                    Console.WriteLine("[perception] 回傳沒有 objects 欄位");
                    continue;
                }

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

                if (sceneObjects.Count == 0)
                {
                    Console.WriteLine("[perception] 場景中沒有帶有效座標的物件（QR1-3 沒偵測到？）");
                    continue;
                }

                Console.WriteLine($"當下場景：{sceneObjects.Count} 個物件");
                foreach (var o in sceneObjects)
                    Console.WriteLine($"  {o.Name}  x={o.X:F3}  y={o.Y:F3}  z={o.Z:F3}");

                RobotPlan plan = await planner.GeneratePlanAsync(userCommand, sceneObjects);

                string outputJson = JsonSerializer.Serialize(
                    plan,
                    new JsonSerializerOptions { WriteIndented = true }
                );

                File.WriteAllText(Path.Combine(localOutputDir, "robot_plan.json"), outputJson);
                File.WriteAllText(outputPath, outputJson);

                if (plan.Action == "error")
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine();
                    Console.WriteLine("!!! 指令無法執行 !!!");
                    Console.WriteLine($"原因：{plan.ErrorMessage}");
                    Console.WriteLine();
                    Console.ForegroundColor = prev;
                }
                else if (plan.Action == "arrange_pattern")
                {
                    int stepCount = plan.PlacementSteps?.Count ?? 0;
                    Console.WriteLine($"已寫入 robot_plan.json (arrange_pattern: pattern={plan.Pattern}, color={plan.BlockColor}, {stepCount} 步)");
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine($"已寫入 robot_plan.json");
                    Console.WriteLine($"--- robot_plan.json ---");
                    Console.WriteLine(outputJson);
                    Console.WriteLine();
                }
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
// 反序列化 perception_server /scene 回應的資料類別
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
