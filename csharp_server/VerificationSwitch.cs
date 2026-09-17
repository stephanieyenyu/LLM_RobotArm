using System.IO;

// -----------------------------------------------------------------
// 一鍵開關（實驗組 / 對照組）：Unity 指令列的「Unity驗證」按鈕
//
// 跟 Unity UIManager 共用 StreamingAssets/verification_enabled.txt，內容 "1"/"0"。
// 檔案不存在、讀不到、或內容不是 "0" 都算開啟 —— 預設永遠是實驗組，
// 只有使用者在 Unity 明確按成關閉才會變成對照組。
//
// 只控制 Unity「模擬結束比對 bitmap」：開 = 不通過就不送實體手臂；關 = 只記錄。
// 一律開著、不受這個開關影響的：MotionPlanValidator 與重新規劃、Unity 軌跡的
// 奇點 / 碰撞 / 底座朝向檢查、硬體安全範圍（底座排除半徑、最大伸展、保護性停止）。
// -----------------------------------------------------------------
public static class VerificationSwitch
{
    const string FlagPath = "../unity_project/Assets/StreamingAssets/verification_enabled.txt";

    public static bool ReadEnabled()
    {
        try
        {
            return !File.Exists(FlagPath) || File.ReadAllText(FlagPath).Trim() != "0";
        }
        catch (IOException)
        {
            return true;
        }
    }
}
