using System.Text.Json;
using System.Text.Json.Serialization;

// -----------------------------------------------------------------
// 一鍵切換純模擬 / 實機。Unity 的「模擬模式」按鈕寫 StreamingAssets/run_mode.json，
// csharp_server 每個任務開始時讀一次，整個任務都用同一個模式：
//   real（預設；沒有這個檔也是）：場景與照片來自相機（perception_server），動作送實體手臂。
//   sim：場景與照片來自 Isaac Sim（/perception/*），任務開始時用虛擬場景檔重建 Isaac 世界，
//        每一批都標 robot_target = "ursim"，只有 URSim 會動，Isaac 跟隨 URSim 做物理。
// -----------------------------------------------------------------
public sealed class RunModeConfig
{
    public const string FileName = "run_mode.json";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "real";

    // 虛擬場景檔，相對 repo 根目錄
    // 三個版本相同的兩個場景：yellow_cubes_15.json（預設）、domino_cube.json
    public const string DefaultScene = "sim_scenes/yellow_cubes_15.json";

    [JsonPropertyName("scene")]
    public string Scene { get; set; } = DefaultScene;

    // true：每個任務開始時都重建成場景檔的配置；false：只在換場景檔或服務剛啟動時重建，之後接續模擬結果
    [JsonPropertyName("reset_each_task")]
    public bool ResetEachTask { get; set; } = true;

    [JsonIgnore]
    public bool IsSim => string.Equals(Mode, "sim", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 讀 StreamingAssets/run_mode.json。檔案不存在就是實機。檔案存在卻讀不懂時丟例外（這個任務記為基礎設施錯誤），
    /// 不猜模式：猜錯成實機會讓本來只想模擬的指令動到實體手臂。
    /// </summary>
    public static RunModeConfig Load(string streamingAssetsDir)
    {
        var path = Path.Combine(streamingAssetsDir, FileName);
        if (!File.Exists(path)) return new RunModeConfig();
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var config = JsonSerializer.Deserialize<RunModeConfig>(File.ReadAllText(path)) ?? new RunModeConfig();
                if (!config.IsSim && !string.Equals(config.Mode, "real", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"mode 只能是 sim 或 real，讀到 \"{config.Mode}\"");
                // 場景檔換過名字或被刪掉（2026-10-08 起只剩兩個場景）：改用預設場景，不讓任務因為舊設定停住
                if (!File.Exists(config.ScenePath(streamingAssetsDir)))
                {
                    Console.WriteLine($"[模式] 找不到虛擬場景檔 {config.Scene}，改用預設的 {DefaultScene}。");
                    config.Scene = DefaultScene;
                }
                return config;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                last = ex;              // Unity 可能正在寫檔，稍等再讀
                Thread.Sleep(100);
            }
        }
        throw new InvalidDataException($"讀不懂 {path}：{last?.Message}", last);
    }

    /// <summary>場景檔的完整路徑（repo 根目錄 = StreamingAssets 往上三層）。</summary>
    public string ScenePath(string streamingAssetsDir) =>
        Path.GetFullPath(Path.Combine(streamingAssetsDir, "..", "..", "..", Scene));
}

/// <summary>
/// 虛擬場景檔（repo 的 sim_scenes/*.json）：純模擬的初始桌面。座標跟相機場景相同（QR 座標系、公尺，
/// z 是頂面高度：桌上一層 0.025、第二層 0.05）。camera 格式同 perception_server /camera；
/// 省略時用同資料夾的 camera/default.json（實機相機的位姿，模擬畫面跟實拍同一個視角），
/// 那個檔也沒有才沿用 Isaac 目前的相機。
/// </summary>
public sealed class SimScene
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("objects")]
    public List<SceneObject> Objects { get; set; } = new();

    [JsonPropertyName("camera")]
    public JsonElement? Camera { get; set; }

    public static SimScene Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到虛擬場景檔 {path}", path);
        var scene = JsonSerializer.Deserialize<SimScene>(File.ReadAllText(path))
                    ?? throw new InvalidDataException($"{path} 是空的");
        var problems = Validate(scene.Objects);
        if (problems.Count > 0)
            throw new InvalidDataException($"虛擬場景檔 {path} 有問題：{string.Join("；", problems)}");
        if (scene.Camera is not { ValueKind: JsonValueKind.Object })
        {
            var defaultCamera = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "camera", "default.json");
            scene.Camera = File.Exists(defaultCamera)
                ? JsonDocument.Parse(File.ReadAllText(defaultCamera)).RootElement.Clone()
                : null;
        }
        return scene;
    }

    /// <summary>只收 Isaac 會建成剛體的積木（名稱 顏色_cube / 顏色_domino），座標要是有限數字、頂面高於桌面。</summary>
    public static List<string> Validate(IReadOnlyList<SceneObject> objects)
    {
        var problems = new List<string>();
        if (objects.Count == 0) problems.Add("objects 是空的");
        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (!LayeredHeights.IsBlock(o))
                problems.Add($"objects[{i}] {o.Name}/{o.Shape} 不是積木（名稱要是 顏色_cube 或 顏色_domino，shape 同名稱）");
            if (!double.IsFinite(o.X) || !double.IsFinite(o.Y) || !double.IsFinite(o.Z) || o.Z <= 0)
                problems.Add($"objects[{i}] {o.Name} 座標要是有限數字且 z > 0（z 是頂面高度）");
            if (o.Shape == "domino" && o.Orientation is not ("horizontal" or "vertical"))
                problems.Add($"objects[{i}] {o.Name} 是 domino，orientation 要是 horizontal 或 vertical");
        }
        return problems;
    }
}
