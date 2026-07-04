using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Threading.Tasks;

// ==========================================
// 偵測改由 Python detection_server.py（ultralytics）提供。
// 啟動順序：先在 csharp_server 資料夾執行
//     python detection_server.py
// 再執行本程式（dotnet run）。
// ==========================================

const string DetectionServerUrl = "http://127.0.0.1:8765";
const int RescanIntervalMs = 5000;

HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

// ==========================================
// 輔助 local functions（啟動流程與背景偵測迴圈共用）
// ==========================================

// 向 Python server 要一次偵測結果（detected_objects.json 格式）
// 失敗回傳 null（連不上、逾時、伺服器錯誤）
async Task<string?> FetchDetectionJsonAsync(bool verbose = true)
{
    try
    {
        HttpResponseMessage response = await http.GetAsync($"{DetectionServerUrl}/detect");

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"[偵測] detection server 回應 {(int)response.StatusCode}。");
            return null;
        }

        string json = await response.Content.ReadAsStringAsync();

        if (verbose)
            Console.WriteLine("[偵測] 已從 detection server 取得偵測結果。");

        return json;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"[偵測] 無法連線 detection server（{DetectionServerUrl}）：{ex.Message}");
        return null;
    }
}

// 把偵測結果寫到 Part B 讀取的位置（與原本 Part A 相同的兩個路徑）
void SaveDetectionJson(string json)
{
    Directory.CreateDirectory("outputs");
    Directory.CreateDirectory("../sample_json");

    File.WriteAllText("outputs/detection_result.json", json);
    File.WriteAllText("../sample_json/detected_objects.json", json);
}

// Part B：呼叫 Python 做 3D 座標轉換
// verbose = false 時吃掉 Python 輸出，避免背景迴圈洗版
bool RunPartB(bool verbose = true)
{
    if (verbose)
        Console.WriteLine("Running Part B Python coordinate mapper...");

    var processInfo = new ProcessStartInfo
    {
        FileName = "python",
        Arguments = "coordinate_mapper_3d.py",
        WorkingDirectory = Directory.GetCurrentDirectory(),
        RedirectStandardOutput = !verbose,
        RedirectStandardError = !verbose,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(processInfo);

    if (process == null)
    {
        Console.WriteLine("Failed to start Part B Python process.");
        return false;
    }

    if (!verbose)
    {
        // 非同步讀掉輸出，避免 buffer 塞滿造成 deadlock
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
    }

    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        Console.WriteLine($"Part B failed. ExitCode = {process.ExitCode}");
        return false;
    }

    if (verbose)
        Console.WriteLine("=== Part B Finished ===");

    return true;
}

// 讀 Part B 輸出的 objects_world.json，轉成 SceneObject 清單
List<SceneObject>? LoadSceneObjectsFromWorldJson()
{
    string sceneJsonPath = "../sample_json/objects_world.json";

    if (!File.Exists(sceneJsonPath))
    {
        Console.WriteLine($"找不到 Part B 輸出：{sceneJsonPath}");
        return null;
    }

    string sceneJson = File.ReadAllText(sceneJsonPath);

    ObjectsWorld? objectsWorld = JsonSerializer.Deserialize<ObjectsWorld>(
        sceneJson,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
    );

    if (objectsWorld?.Objects == null)
        return null;

    return objectsWorld.Objects
        .Where(obj => obj.Position != null)
        .Select(obj => new SceneObject
        {
            Name = obj.Name,
            X = obj.Position!.X,
            Y = obj.Position!.Y,
            Z = obj.Position!.Z
        })
        .ToList();
}

// ==========================================
// Step 1: Part A — 從 Python detection server 取得初始偵測
// （伺服器可能還在載入模型，最多等 10 次、每次 3 秒）
// ==========================================
string? detectionJson = null;

for (int attempt = 1; attempt <= 10; attempt++)
{
    detectionJson = await FetchDetectionJsonAsync();

    if (detectionJson != null)
        break;

    Console.WriteLine($"等待 detection server 啟動中...（{attempt}/10）" +
                      "請確認已在 csharp_server 資料夾執行 python detection_server.py");
    await Task.Delay(3000);
}

if (detectionJson == null)
{
    Console.WriteLine("無法取得偵測結果。請先啟動 detection_server.py 後再執行本程式。");
    return;
}

SaveDetectionJson(detectionJson);
Console.WriteLine("已寫入 ../sample_json/detected_objects.json");

// ==========================================
// Step 2: Part B — Python 3D coordinate mapping
// 讀 ../sample_json/detected_objects.json
// 輸出 ../sample_json/objects_world.json
// ==========================================
if (!RunPartB())
{
    return;
}

// ==========================================
// Step 3: 載入初始場景物件
// ==========================================
List<SceneObject>? initialSceneObjects = LoadSceneObjectsFromWorldJson();

if (initialSceneObjects == null || initialSceneObjects.Count == 0)
{
    Console.WriteLine("Part B 沒有輸出任何有效物件，無法繼續。");
    return;
}

// 共用場景狀態：背景偵測迴圈更新、LLM planner 讀取，讀寫都要包 lock
object sceneLock = new object();
List<SceneObject> sceneObjects = initialSceneObjects;

Console.WriteLine($"\n載入 {sceneObjects.Count} 個物件（世界座標，單位 m）：");
foreach (var o in sceneObjects)
    Console.WriteLine($"  {o.Name}  x={o.X:F3}  y={o.Y:F3}  z={o.Z:F3}");

// ==========================================
// Step 3.5: 背景即時偵測迴圈（鏡頭固定 + 即時動態辨識新物件）
// 依 Notion comment：偵測交給 ultralytics 的 Python server，
// 這裡每隔幾秒向 server 要最新偵測 → Part B 座標轉換 →
// 更新記憶體中的 sceneObjects，LLM 每次收到指令都抓最新版本，
// 使用者不必為了新增物件重跑 csharp_server。
// ==========================================
_ = Task.Run(async () =>
{
    Console.WriteLine($"[背景偵測] 已啟動，每 {RescanIntervalMs / 1000} 秒向 detection server 重新掃描場景。");

    while (true)
    {
        await Task.Delay(RescanIntervalMs);

        try
        {
            string? latestJson = await FetchDetectionJsonAsync(verbose: false);

            if (latestJson == null)
            {
                Console.WriteLine("[背景偵測] 取得偵測結果失敗，沿用上一輪場景。");
                continue;
            }

            SaveDetectionJson(latestJson);

            bool okB = RunPartB(verbose: false);

            if (!okB)
            {
                Console.WriteLine("[背景偵測] Part B 失敗，沿用上一輪場景。");
                continue;
            }

            List<SceneObject>? latest = LoadSceneObjectsFromWorldJson();

            if (latest == null || latest.Count == 0)
            {
                Console.WriteLine("[背景偵測] 本輪沒有偵測到物件，沿用上一輪場景。");
                continue;
            }

            lock (sceneLock)
            {
                sceneObjects = latest;
            }

            Console.WriteLine(
                $"[背景偵測] 場景已更新：{latest.Count} 個物件（{string.Join(", ", latest.Select(o => o.Name))}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[背景偵測] 錯誤：{ex.Message}");
        }
    }
});

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

                // 抓最新場景快照（背景偵測迴圈可能剛更新過）
                List<SceneObject> currentScene;
                lock (sceneLock)
                {
                    currentScene = new List<SceneObject>(sceneObjects);
                }

                RobotPlan plan = await planner.GeneratePlanAsync(userCommand, currentScene);

                string outputJson = JsonSerializer.Serialize(
                    plan,
                    new JsonSerializerOptions { WriteIndented = true }
                );

                File.WriteAllText(Path.Combine(localOutputDir, "robot_plan.json"), outputJson);
                File.WriteAllText(outputPath, outputJson);

                Console.WriteLine($"已寫入 robot_plan.json");
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