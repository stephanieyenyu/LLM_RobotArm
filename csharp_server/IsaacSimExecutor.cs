using System.Text;
using System.Text.Json;

// -----------------------------------------------------------------
// 送真實手臂執行前，先在 Isaac Sim 裡完整模擬這輪要做的每一步操作
// （真的模擬 UR3e 抓放動作，不是只把積木瞬間擺好），確認抓放過程跟
// 疊放結果都沒問題，才送真實手臂。取代原本 IsaacSimGate.cs 的簡化版
// （那個只瞬間擺放積木檢查穩不穩，不模擬抓放過程）。
//
// Isaac Sim 常駐在另一台電腦，那邊跑一個常駐 HTTP 服務
// （isaac_sim_server.py），這裡用 HttpClient 呼叫。服務位址用
// ISAAC_SIM_URL 環境變數指定（跟現有 perception_server.py 走
// http://localhost:5000 是同一種模式，只是 Isaac Sim 在不同機器上，
// 所以位址要指到那台機器的 IP，例如 http://192.168.x.x:6000/）。
//
// 這整個檔案沒辦法在這個環境測試過，isaac_sim_server.py 那邊的 API
// 對不對要在 Isaac Sim 機器上實際跑過才能確認。
// -----------------------------------------------------------------
public static class IsaacSimExecutor
{
    const double CubeSizeM = 0.025;
    // 貼桌面的允許誤差；目標 Z 超過這個高度才視為「疊在別的積木上」。
    const double RestingZToleranceM = 0.004;

    static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("ISAAC_SIM_URL") ?? "http://localhost:6000/"),
        Timeout = TimeSpan.FromMinutes(5),
    };
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 這批步驟裡只要有一個目標明顯不是貼著桌面，就代表這次規劃有疊放，
    /// 需要先在 Isaac Sim 完整模擬。邏輯跟舊版 IsaacSimGate.RequiresCheck
    /// 一樣，維持不變。
    /// </summary>
    public static bool RequiresCheck(IEnumerable<TranslatedStep> steps)
        => steps.Any(s => s.Target != null && s.Target.Z > CubeSizeM + RestingZToleranceM);

    /// <summary>
    /// 在 Isaac Sim 裡完整模擬這輪所有步驟：先依 initial 場景擺好積木，
    /// 逐步驅動 UR3e 執行每個 step 的 actions，最後回報模擬後的場景跟
    /// 一張模擬相機截圖。呼叫端拿這兩樣東西跟現有 llm.Validate 一樣判斷
    /// PASS/FAIL，不通過就直接讓這次 attempt 失敗、不送真實手臂。
    /// </summary>
    public static async Task<(List<SceneObject> Scene, byte[]? Frame)> SimulateAsync(
        List<SceneObject> initial, List<TranslatedStep> steps, string dir)
    {
        await Post("reset", new { scene = initial });

        foreach (var step in steps)
        {
            try
            {
                await Post("execute_step", new { source_index = step.SourceIndex, target = step.Target, actions = step.Actions });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Isaac Sim 執行 step（source_index={step.SourceIndex}）失敗：{ex.Message}");
            }
        }

        List<SceneObject> scene;
        try
        {
            var sceneJson = await Http.GetStringAsync("scene");
            using var doc = JsonDocument.Parse(sceneJson);
            scene = doc.RootElement.GetProperty("objects").Deserialize<List<SceneObject>>(Json) ?? new();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Isaac Sim 讀取模擬結果場景失敗：" + ex.Message);
        }
        File.WriteAllText(Path.Combine(dir, "isaac_after_scene.json"), JsonSerializer.Serialize(scene, new JsonSerializerOptions { WriteIndented = true }));

        byte[]? frame = null;
        try
        {
            frame = await Http.GetByteArrayAsync("frame");
            File.WriteAllBytes(Path.Combine(dir, "isaac_after.jpg"), frame);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "isaac_after.jpg.error.txt"), ex.Message);
        }

        return (scene, frame);
    }

    static async Task Post(string path, object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(path, content);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{path} 回應 {(int)resp.StatusCode}：{await resp.Content.ReadAsStringAsync()}");
    }
}
