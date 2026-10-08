using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

// -----------------------------------------------------------------
// Isaac Sim 3D 疊放驗證（isaac_sim_server.py，常駐 HTTP 服務，位址用 ISAAC_SIM_URL
// 環境變數指定，預設 http://localhost:6000/）。場景座標一律用 QR 座標系
// （跟 perception /scene 相同），換到 UR 基座 / Isaac 世界由 isaac_sim_server.py 負責。
//
// 3D 疊放的流程（2D 平面移動不經過這裡，照舊由 Unity 模擬驗證）：
//   BeginVerifyAsync  把真實場景投影到 Isaac、記錄起始狀態；Isaac 手臂即時跟隨 URSim。
//   （Program 把整輪步驟以 robot_target = "ursim" 交給 Unity，在 URSim 執行）
//   EndVerifyAsync    URSim 跑完後，Isaac 等積木靜止做幾何檢查，回傳結果與模擬截圖；
//                     呼叫端再讓 LLM 看截圖，都通過才送真實手臂。
//   SyncRealScene     嘗試開始 / 結束時把真實場景投影到 Isaac（背景，只更新畫面）。
// 同一時間只有一組呼叫在 Isaac 上跑（依序排隊）。
// -----------------------------------------------------------------
public static class IsaacSimExecutor
{
    const double CubeSizeM = 0.025;
    // 目標壓在另一塊積木上（中心 XY 在這之內）就算疊放
    const double StackFootprintM = 0.020;

    static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("ISAAC_SIM_URL") ?? "http://localhost:6000/"),
        Timeout = TimeSpan.FromMinutes(5),
    };
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    static readonly SemaphoreSlim IsaacLock = new(1, 1);
    static int unavailableReported;

    /// <summary>
    /// 是否為 3D 疊放：任何一步的目標壓在另一塊積木上，或比來源積木高出半層以上。
    /// 不用目標 Z 的絕對值判斷，因為 perception 的頂面高度系統性偏低約 2 cm
    /// （Unity 用 Z_CORRECTION 補償），疊放目標常常只有 0.024 m。
    /// </summary>
    public static bool RequiresCheck(IEnumerable<TranslatedStep> steps, IReadOnlyList<SceneObject> scene)
        => steps.Any(s =>
        {
            if (s.Target == null) return false;
            var source = s.SourceIndex >= 0 && s.SourceIndex < scene.Count ? scene[s.SourceIndex] : null;
            if (source != null && s.Target.Z > source.Z + CubeSizeM / 2) return true;
            return scene.Where(o => o != source)
                .Any(o => Math.Sqrt(Math.Pow(o.X - s.Target.X, 2) + Math.Pow(o.Y - s.Target.Y, 2)) < StackFootprintM);
        });

    /// <summary>
    /// 投影真實場景並開始驗證；Isaac 或 URSim 不可用時丟 SimulationUnavailableException。
    /// useCurrentWorld（純模擬）：Isaac 本身就是世界，不重新投影，直接從目前的物理狀態開始驗證。
    /// expectedCells：計畫的高度圖（每一格每一層一個），Isaac 在 verify/end 算畫面重疊率（整體對齊 ±5 mm 後 &gt; 90%）。
    /// </summary>
    public static async Task BeginVerifyAsync(List<SceneObject> scene, List<TranslatedStep> steps,
        JsonElement? camera, byte[]? realFrame, string dir, bool useCurrentWorld = false,
        IReadOnlyList<ExpectedCell>? expectedCells = null, double cellXM = 0, double cellYM = 0)
    {
        await IsaacLock.WaitAsync();
        try
        {
            var stepBodies = steps.Select(s => new { source_index = s.SourceIndex, target = s.Target, actions = s.Actions });
            var expected = (expectedCells ?? new List<ExpectedCell>())
                .Select(c => new { x = c.X, y = c.Y, z = c.Z, shape = c.Shape, orientation = c.Orientation }).ToList();
            object body = new { scene, camera, steps = stepBodies, use_current_world = useCurrentWorld,
                expected_cells = expected, cell_size_x_m = cellXM, cell_size_y_m = cellYM };
            File.WriteAllText(Path.Combine(dir, "isaac_verify_begin.json"), await Post("verify/begin", body));
            await SaveFrame(dir, "isaac_before.jpg");
            // 純模擬的「真實畫面」就是 Isaac 自己的畫面，不做疊合
            if (realFrame != null && !useCurrentWorld) await SaveOverlay(dir, realFrame);
        }
        finally { IsaacLock.Release(); }
    }

    /// <summary>純模擬時場景與相機畫面由 Isaac 提供，端點格式同 perception_server。</summary>
    public static Uri PerceptionBaseUri => new(Http.BaseAddress!, "perception/");

    /// <summary>純模擬：用虛擬場景重建 Isaac 世界（中止還沒結束的驗證）。</summary>
    public static async Task<string> LoadSimSceneAsync(List<SceneObject> scene, JsonElement? camera)
    {
        await IsaacLock.WaitAsync();
        try { return await Post("sim/load", new { scene, camera }); }
        finally { IsaacLock.Release(); }
    }

    /// <summary>純模擬：記下目前積木位姿（3D 驗證前）。</summary>
    public static async Task SnapshotWorldAsync()
    {
        await IsaacLock.WaitAsync();
        try { await Post("sim/snapshot", new { }); }
        finally { IsaacLock.Release(); }
    }

    /// <summary>純模擬：積木放回快照的位姿（3D 驗證後，正式執行從驗證前的狀態開始）。</summary>
    public static async Task RestoreWorldAsync()
    {
        await IsaacLock.WaitAsync();
        try { await Post("sim/restore", new { }); }
        finally { IsaacLock.Release(); }
    }

    /// <summary>URSim 跑完後取得 Isaac 幾何檢查結果、模擬後場景與截圖。</summary>
    public static async Task<VerifyReport> EndVerifyAsync(string dir)
    {
        await IsaacLock.WaitAsync();
        try
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
            // 畫面重疊率（有給預期格才有）與比對圖：模擬結果 / 預期 / 疊合
            if (root.TryGetProperty("overlap", out var overlap) && overlap.ValueKind == JsonValueKind.Object)
            {
                report.OverlapRatio = overlap.GetProperty("ratio").GetDouble();
                if (overlap.TryGetProperty("image_png_base64", out var png) && png.ValueKind == JsonValueKind.String)
                    File.WriteAllBytes(Path.Combine(dir, "isaac_overlap.png"), Convert.FromBase64String(png.GetString()!));
            }
            report.Frame = await SaveFrame(dir, "isaac_after.jpg");
            return report;
        }
        finally { IsaacLock.Release(); }
    }

    /// <summary>背景把真實場景投影到 Isaac（積木、相機；手臂由 Isaac 跟隨 URSim）。立即返回。</summary>
    public static void SyncRealScene(List<SceneObject> scene, JsonElement? camera)
    {
        _ = Task.Run(async () =>
        {
            await IsaacLock.WaitAsync();
            try
            {
                await Post("reset", new { scene, camera });
                ReportAvailable();
            }
            catch (Exception ex) { ReportUnavailable(ex); }
            finally { IsaacLock.Release(); }
        });
    }

    static async Task SaveOverlay(string dir, byte[] realFrame)
    {
        // 疊合檢查只用來診斷對位，失敗不影響驗證流程。
        try
        {
            var content = new ByteArrayContent(realFrame);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var resp = await Http.PostAsync("overlay", content);
            resp.EnsureSuccessStatusCode();
            File.WriteAllBytes(Path.Combine(dir, "isaac_overlay_before.jpg"), await resp.Content.ReadAsByteArrayAsync());
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "isaac_overlay_before.jpg.error.txt"), ex.Message);
        }
    }

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

    // 沒開 Isaac Sim（只跑 Unity 2D）時背景投影會一直失敗，只提示一次，恢復連線後再重新提示。
    static void ReportUnavailable(Exception ex)
    {
        if (Interlocked.Exchange(ref unavailableReported, 1) == 0)
            Console.WriteLine($"[Isaac Sim] 畫面更新失敗（沒開 isaac_sim_server 可忽略）：{ex.Message}");
    }

    static void ReportAvailable()
    {
        if (Interlocked.Exchange(ref unavailableReported, 0) == 1)
            Console.WriteLine("[Isaac Sim] 已恢復連線。");
    }
}

public sealed class VerifyReport
{
    public bool Pass { get; set; }
    public List<string> Reasons { get; set; } = new();
    public List<SceneObject> Scene { get; set; } = new();
    public byte[]? Frame { get; set; }
    // 畫面重疊率（整體對齊後）；server 沒給預期格時是 null
    public double? OverlapRatio { get; set; }
}

/// <summary>Isaac Sim / URSim 不可用：屬於基礎設施錯誤，不是規劃失敗，不進 Reflection、不計成功率。</summary>
public sealed class SimulationUnavailableException : Exception
{
    public SimulationUnavailableException(string message, Exception? inner) : base(message, inner) { }
}
