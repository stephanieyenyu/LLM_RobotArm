using System.IO;

// -----------------------------------------------------------------
// 一鍵驗證開關（實驗組 / 對照組）
//
// 跟 Unity UIManager 共用 StreamingAssets/verification_enabled.txt，內容 "1"/"0"。
// 檔案不存在、讀不到、或內容不是 "0" 都算開啟 —— 預設永遠是實驗組，
// 只有使用者在 Unity 明確按成關閉才會變成對照組。
//
// 開關關閉時略過的驗證：
//   - MotionPlanValidator 與重新規劃（LLM 第一次產出的規劃直接採用）
//   - 整批平移重試（它靠驗證器挑候選位置）
//   - Unity 的軌跡奇點 / 碰撞檢查、可達性預檢（由 BatchEnvelope.VerificationDisabled 帶過去）
// 不受影響、永遠生效的硬體安全：Unity 的底座排除半徑、最大伸展、保護性停止處理。
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
