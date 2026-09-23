using System.Diagnostics;
using System.Text.Json;

// -----------------------------------------------------------------
// 送真實手臂執行前，先在 Isaac Sim 裡用物理引擎確認這次規劃疊放的積木
// 會不會倒／滑掉。只有偵測到有目標不是貼著桌面（也就是疊在別的積木上）
// 才會跑，平面單層任務不會多花這個時間。
//
// 呼叫外部 headless Isaac Sim process（isaac_sim/stack_verifier.py），只
// 負責「把積木照規劃位置放好、跑物理、回報穩不穩」，不模擬手臂抓放動作
// ——手臂實際抓放動作已經有 MotionPlanValidator/Unity 那邊在檢查，這裡
// 只加「疊出來的結構本身站不站得住」這一關。
//
// python 執行檔路徑用 ISAAC_PYTHON 環境變數指定（跟現有 ROBOT_MODEL 一樣
// 的設定方式）；沒設就退回 "python"，在沒裝 Isaac Sim 的機器上會直接跑
// 不動，回報失敗，不會誤判成 STABLE。
//
// 這整個檔案完全沒辦法在這個環境測試過（沒有 Isaac Sim 可以跑），邏輯
// 是照 isaac_sim/stack_verifier.py 既有的輸入輸出格式寫的，實際能不能接
// 起來要在有裝 Isaac Sim 的機器上跑過才能確認。
// -----------------------------------------------------------------
public static class IsaacSimGate
{
    const double CubeSizeM = 0.025;
    // 貼桌面的允許誤差；目標 Z 超過這個高度才視為「疊在別的積木上」。
    const double RestingZToleranceM = 0.004;
    const int TimeoutSec = 180;

    /// <summary>
    /// 這批步驟裡只要有一個目標明顯不是貼著桌面，就代表這次規劃有疊放，
    /// 需要先跑 Isaac Sim 確認穩不穩。物件 Z 是頂面高度（見 ExperimentLlm.cs
    /// 的環境說明），貼桌面的單層物件頂面高度就是一顆積木的高度
    /// （CubeSizeM），超過這個高度太多才算疊在別的積木上。
    /// </summary>
    public static bool RequiresCheck(IEnumerable<TranslatedStep> steps)
        => steps.Any(s => s.Target != null && s.Target.Z > CubeSizeM + RestingZToleranceM);

    /// <summary>
    /// 跑一次 headless Isaac Sim 物理模擬，回報這批目標位置疊起來穩不穩。
    /// Stable=false 時 Detail 會說明原因（模擬本身失敗、或模擬判定 UNSTABLE）；
    /// 呼叫端把這個直接當成這次 attempt 失敗處理即可，不需要另外分辨原因。
    /// </summary>
    public static (bool Stable, string Detail) CheckStability(List<TranslatedStep> steps, string runDir)
    {
        string isaacDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../isaac_sim"));
        if (!Directory.Exists(isaacDir)) isaacDir = Path.GetFullPath("../isaac_sim");
        if (!Directory.Exists(isaacDir))
            return (false, $"找不到 isaac_sim 資料夾（試過 {isaacDir}）");

        string patternPath = Path.Combine(runDir, "isaac_pattern.json");
        string reportPath = Path.Combine(runDir, "isaac_pattern.report.json");

        var blocks = steps.Where(s => s.Target != null).Select(BuildBlockSpec).ToList();
        if (blocks.Count == 0) return (true, "沒有需要疊放檢查的目標");

        File.WriteAllText(patternPath, JsonSerializer.Serialize(
            new { blocks, settle_seconds = 3.0 },
            new JsonSerializerOptions { WriteIndented = true }));

        string pythonExe = Environment.GetEnvironmentVariable("ISAAC_PYTHON") ?? "python";
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = isaacDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("stack_verifier.py");
        psi.ArgumentList.Add(patternPath);

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return (false, $"啟動 Isaac Sim process 失敗：{ex.Message}"); }
        if (proc == null) return (false, "Isaac Sim process 啟動失敗");

        string stdout, stderr;
        int exitCode;
        using (proc)
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            bool exited = proc.WaitForExit(TimeoutSec * 1000);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, $"Isaac Sim 模擬超過 {TimeoutSec} 秒未完成，已中止");
            }
            stdout = stdoutTask.Result;
            stderr = stderrTask.Result;
            // ExitCode 要在 Dispose 前讀出來，避免依賴 Process 物件釋放後
            // 還能讀取屬性這種沒有保證的行為。
            exitCode = proc.ExitCode;
        }

        File.WriteAllText(Path.Combine(runDir, "isaac_stdout.txt"), stdout);
        if (!string.IsNullOrWhiteSpace(stderr))
            File.WriteAllText(Path.Combine(runDir, "isaac_stderr.txt"), stderr);

        if (exitCode != 0 || !File.Exists(reportPath))
            return (false, $"Isaac Sim 模擬未正常完成（exit={exitCode}），無法確認穩定性；詳見 isaac_stderr.txt");

        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        string verdict = doc.RootElement.TryGetProperty("verdict", out var v) ? v.GetString() ?? "UNKNOWN" : "UNKNOWN";
        return (verdict == "STABLE", $"Isaac Sim 判定：{verdict}（詳見 {reportPath}）");
    }

    static object BuildBlockSpec(TranslatedStep step)
    {
        var t = step.Target!;
        double[] size = t.Shape == "domino"
            ? (t.Orientation == "vertical"
                ? new[] { CubeSizeM, CubeSizeM * 2, CubeSizeM }
                : new[] { CubeSizeM * 2, CubeSizeM, CubeSizeM })
            : new[] { CubeSizeM, CubeSizeM, CubeSizeM };
        // t.Z 沿用全專案既有的「頂面高度」慣例，換成 stack_verifier.py 需要的
        // 方塊中心點，才會跟它自己畫的 cube 尺寸對得上。
        double centerZ = t.Z - CubeSizeM / 2;
        return new { pos = new[] { t.X, t.Y, centerZ }, size, color = "gray", mass = 0.05 };
    }
}
