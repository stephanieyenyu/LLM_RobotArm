using System.Text;
using System.Text.Json;

// -----------------------------------------------------------------
// 3D 模擬驗證：Isaac Sim（isaac_sim/isaac_sim_server.py，常駐 HTTP 服務，位址用 ISAAC_SIM_URL 環境變數指定，
// 預設 http://localhost:6000/）。2026-10-07 從 main 的 IsaacSimExecutor 精簡搬來，只留 3D 批次用到的兩步：
//   BeginVerifyAsync  把任務開始時的場景投影到 Isaac、記錄起始狀態；之後 Isaac 的手臂即時跟隨 URSim
//   EndVerifyAsync    URSim 跑完後，Isaac 等積木靜止做幾何檢查（位置、層高、傾斜、撞動其他積木、穩定、
//                     指尖撞桌、手臂自撞），回傳結果與模擬截圖
// 場景座標一律用 QR 座標系（跟 perception /scene 相同），換到 UR 基座 / Isaac 世界由 isaac_sim_server.py 負責。
// 純模擬（run_mode.json 的 mode = sim）另外用到：LoadSimSceneAsync（用虛擬場景檔重建 Isaac 世界）、
// SnapshotWorldAsync / RestoreWorldAsync（3D 驗證前後記下並還原積木位姿）、PerceptionBaseUri（場景改由 Isaac 提供）。
// -----------------------------------------------------------------
public static class IsaacSimVerifier
{
    static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("ISAAC_SIM_URL") ?? "http://localhost:6000/"),
        Timeout = TimeSpan.FromMinutes(5),
    };
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// 投影場景並開始驗證。steps 的每一筆是 { source_index（scene 的 index）, target（SceneObject）, actions }。
    /// Isaac 或 URSim 不可用時丟 SimulationUnavailableException。
    /// </summary>
    /// camera = perception_server /camera 的內參與相機位姿；Isaac 用它把模擬相機擺到跟實體相機同一個位置（null = 沿用上次）。
    /// useCurrentWorld（純模擬）：Isaac 本身就是世界，不重新投影，直接從目前的物理狀態開始驗證。
    public static async Task BeginVerifyAsync(List<SceneObject> scene, JsonElement? camera, IEnumerable<object> steps, string dir,
        bool useCurrentWorld = false)
    {
        object body = useCurrentWorld
            ? new { scene, camera, steps, use_current_world = true }
            : new { scene, camera, steps };
        File.WriteAllText(Path.Combine(dir, "isaac_verify_begin_request.json"), JsonSerializer.Serialize(body, Indented));
        File.WriteAllText(Path.Combine(dir, "isaac_verify_begin.json"), await Post("verify/begin", body));
        await SaveFrame(dir, "isaac_before.jpg");
    }

    /// <summary>URSim 跑完後取得 Isaac 幾何檢查結果、模擬後場景與截圖。</summary>
    public static async Task<VerifyReport> EndVerifyAsync(string dir)
    {
        var text = await Post("verify/end", new { });
        File.WriteAllText(Path.Combine(dir, "isaac_verify.json"), text);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var report = new VerifyReport
        {
            Pass = root.GetProperty("pass").GetBoolean(),
            Reasons = root.GetProperty("reasons").EnumerateArray().Select(r => r.GetString() ?? "").ToList(),
            Scene = root.GetProperty("objects").Deserialize<List<SceneObject>>(Json) ?? new(),
        };
        File.WriteAllText(Path.Combine(dir, "isaac_after_scene.json"), JsonSerializer.Serialize(report.Scene, Indented));
        report.Frame = await SaveFrame(dir, "isaac_after.jpg");
        return report;
    }

    /// <summary>純模擬時場景由 Isaac 提供，端點格式同 perception_server（/perception/scene、/perception/camera）。</summary>
    public static Uri PerceptionBaseUri => new(Http.BaseAddress!, "perception/");

    /// <summary>純模擬：用虛擬場景重建 Isaac 世界（中止還沒結束的驗證）。camera = null 時沿用 Isaac 目前的相機。</summary>
    public static Task<string> LoadSimSceneAsync(List<SceneObject> scene, JsonElement? camera) =>
        Post("sim/load", new { scene, camera });

    /// <summary>純模擬：記下目前積木位姿（3D 驗證前）。</summary>
    public static Task SnapshotWorldAsync() => Post("sim/snapshot", new { });

    /// <summary>純模擬：積木放回快照的位姿（3D 驗證後，正式執行從驗證前的狀態開始）。</summary>
    public static Task RestoreWorldAsync() => Post("sim/restore", new { });

    static async Task<byte[]?> SaveFrame(string dir, string name)
    {
        try
        {
            var frame = await Http.GetByteArrayAsync("frame");
            File.WriteAllBytes(Path.Combine(dir, name), frame);
            return frame;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, name + ".error.txt"), ex.Message);
            return null;
        }
    }

    static async Task<string> Post(string path, object body)
    {
        HttpResponseMessage resp;
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            resp = await Http.PostAsync(path, content);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new SimulationUnavailableException($"連不到 Isaac Sim（{Http.BaseAddress}{path}）：{ex.Message}", ex);
        }
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new SimulationUnavailableException($"Isaac Sim {path} 回應 {(int)resp.StatusCode}：{text}", null);
        return text;
    }
}

public sealed class VerifyReport
{
    public bool Pass { get; set; }
    public List<string> Reasons { get; set; } = new();
    public List<SceneObject> Scene { get; set; } = new();
    public byte[]? Frame { get; set; }
}

/// <summary>Isaac Sim / URSim 不可用（沒開、連不上、讀不到 URSim 關節角）：實體手臂不動。</summary>
public sealed class SimulationUnavailableException : Exception
{
    public SimulationUnavailableException(string message, Exception? inner) : base(message, inner) { }
}
