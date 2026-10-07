using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
using System.Collections.Generic;
using System.IO;

public class UIManager : MonoBehaviour
{
    public UIDocument uiDocument;
    public JsonExecutor executor;

    // 兩邊共用的資料夾（Unity / csharp_server 都指到這裡）
    // 如果之後換電腦或換路徑，只要改這一行
    private string SHARED_DIR => Application.streamingAssetsPath;

    private TextField inputField;
    private Button sendButton;
    private Button verificationButton;
    private Button patternReviewButton;
    private Label statusLabel;

    void OnEnable()
    {
        var root = uiDocument.rootVisualElement;

        var container = new VisualElement();
        container.style.position = UnityEngine.UIElements.Position.Absolute;
        container.style.bottom = 10;
        container.style.left = 10;
        container.style.right = 10;
        container.style.flexDirection = FlexDirection.Row;
        container.style.backgroundColor = new Color(0, 0, 0, 0.7f);
        container.style.paddingTop = 5;
        container.style.paddingBottom = 5;
        container.style.paddingLeft = 5;
        container.style.paddingRight = 5;
        container.style.height = 50;

        // 一鍵開關（實驗組 / 對照組）：只控制模擬動畫結束後的 bitmap 比對。放在指令列最左邊。
        // 寫進跟 csharp_server 共用的旗標檔；server 每收到一個指令就重讀一次，
        // 所以切換後「下一個」指令生效，已經在跑的那一批不受影響。
        verificationButton = new Button(() => SetVerificationEnabled(!ReadVerificationEnabled()));
        StyleToggleButton(verificationButton);
        RefreshVerificationButton();

        // pattern 審查開關：LLM 產生 bitmap 後要不要做雙模型交叉審查與投票。
        // 關閉 = 只呼叫一次 OpenAI 就直接採用。跟驗證開關各自獨立。
        patternReviewButton = new Button(() => SetPatternReviewEnabled(!ReadPatternReviewEnabled()));
        StyleToggleButton(patternReviewButton);
        RefreshPatternReviewButton();

        inputField = new TextField("");
        inputField.style.flexGrow = 1;
        inputField.style.marginRight = 5;
        inputField.style.height = 40;
        inputField.focusable = true;

        sendButton = new Button(() => OnSendCommand());
        sendButton.text = "執行";
        sendButton.style.height = 40;
        sendButton.style.width = 80;

        container.Add(verificationButton);
        container.Add(patternReviewButton);
        container.Add(inputField);
        container.Add(sendButton);
        root.Add(container);

        statusLabel = new Label("");
        statusLabel.style.position = UnityEngine.UIElements.Position.Absolute;
        statusLabel.style.bottom = 65;
        statusLabel.style.left = 10;
        statusLabel.style.right = 10;
        statusLabel.style.color = new Color(1f, 0.4f, 0.4f);
        statusLabel.style.backgroundColor = new Color(0, 0, 0, 0.6f);
        statusLabel.style.paddingTop = 4;
        statusLabel.style.paddingBottom = 4;
        statusLabel.style.paddingLeft = 8;
        statusLabel.style.display = DisplayStyle.None;
        root.Add(statusLabel);

        // 確保共享資料夾存在
        if (!Directory.Exists(SHARED_DIR))
        {
            Directory.CreateDirectory(SHARED_DIR);
            Debug.Log("已建立共享資料夾：" + SHARED_DIR);
        }

        // ---------------------------------------------------------
        // 右上角三個手動控制按鈕：鬆開 / 夾緊 / 回 Home
        // ---------------------------------------------------------
        var controlPanel = new VisualElement();
        controlPanel.style.position = UnityEngine.UIElements.Position.Absolute;
        controlPanel.style.top = 10;
        controlPanel.style.right = 10;
        controlPanel.style.flexDirection = FlexDirection.Column;
        controlPanel.style.backgroundColor = new Color(0, 0, 0, 0.7f);
        controlPanel.style.paddingTop = 6;
        controlPanel.style.paddingBottom = 6;
        controlPanel.style.paddingLeft = 6;
        controlPanel.style.paddingRight = 6;

        var openBtn = new Button(() => { if (executor != null) executor.ReleaseGripper(); });
        openBtn.text = "鬆開夾爪";
        openBtn.style.height = 36;
        openBtn.style.width = 120;
        openBtn.style.marginBottom = 4;

        var gripBtn = new Button(() => { if (executor != null) executor.GripGripper(); });
        gripBtn.text = "夾緊夾爪";
        gripBtn.style.height = 36;
        gripBtn.style.width = 120;
        gripBtn.style.marginBottom = 4;

        var homeBtn = new Button(() => { if (executor != null) executor.GoHome(); });
        homeBtn.text = "回 Home";
        homeBtn.style.height = 36;
        homeBtn.style.width = 120;

        controlPanel.Add(openBtn);
        controlPanel.Add(gripBtn);
        controlPanel.Add(homeBtn);
        root.Add(controlPanel);
    }

    // ---------------------------------------------------------
    // 一鍵開關（模擬結束比對 bitmap）：跟 csharp_server/VerificationSwitch.cs 共用
    // StreamingAssets/verification_enabled.txt，寫 "1"/"0"。
    // 檔案不存在或讀不到都算開啟 —— 預設永遠是實驗組，只有明確按成關閉才是對照組。
    // ---------------------------------------------------------
    string VerificationFlagPath => Path.Combine(SHARED_DIR, "verification_enabled.txt");

    bool ReadVerificationEnabled()
    {
        try
        {
            return !File.Exists(VerificationFlagPath) ||
                   File.ReadAllText(VerificationFlagPath).Trim() != "0";
        }
        catch (IOException)
        {
            return true;
        }
    }

    void SetVerificationEnabled(bool enabled)
    {
        try
        {
            File.WriteAllText(VerificationFlagPath, enabled ? "1" : "0");
            Debug.Log(enabled
                ? "[UI] Unity驗證開啟（實驗組）：模擬結束比對不通過就不送實體手臂，下一個指令生效"
                : "[UI] Unity驗證關閉（對照組）：模擬結束比對只記錄，下一個指令生效");
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[UI] 寫入 verification_enabled.txt 失敗：{e.Message}");
        }
        RefreshVerificationButton();
    }

    // 按鈕狀態一律從檔案讀回來，寫入失敗時畫面不會顯示成已切換
    void RefreshVerificationButton()
    {
        bool enabled = ReadVerificationEnabled();
        verificationButton.text = enabled ? "Unity驗證：開" : "Unity驗證：關";
        SetToggleColor(verificationButton, enabled);
    }

    // ---------------------------------------------------------
    // pattern 審查開關：跟 csharp_server/PatternDesigner.cs 共用
    // StreamingAssets/skip_pattern_review.txt。注意檔案記的是「跳過」：
    // "1" = 跳過審查（按鈕顯示關），其他或檔案不存在 = 照常審查（按鈕顯示開）。
    // server 每次設計 pattern 前都重讀，切換後下一個指令生效。
    // ---------------------------------------------------------
    string SkipPatternReviewFlagPath => Path.Combine(SHARED_DIR, "skip_pattern_review.txt");

    bool ReadPatternReviewEnabled()
    {
        try
        {
            return !(File.Exists(SkipPatternReviewFlagPath) &&
                     File.ReadAllText(SkipPatternReviewFlagPath).Trim() == "1");
        }
        catch (IOException)
        {
            return true;
        }
    }

    void SetPatternReviewEnabled(bool enabled)
    {
        try
        {
            File.WriteAllText(SkipPatternReviewFlagPath, enabled ? "0" : "1");
            Debug.Log(enabled
                ? "[UI] pattern 審查開啟，下一個指令生效"
                : "[UI] pattern 審查關閉（只呼叫一次 OpenAI 直接採用），下一個指令生效");
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[UI] 寫入 skip_pattern_review.txt 失敗：{e.Message}");
        }
        RefreshPatternReviewButton();
    }

    void RefreshPatternReviewButton()
    {
        bool enabled = ReadPatternReviewEnabled();
        patternReviewButton.text = enabled ? "pattern審查：開" : "pattern審查：關";
        SetToggleColor(patternReviewButton, enabled);
    }

    static void StyleToggleButton(Button button)
    {
        button.style.height = 40;
        button.style.width = 150;
        button.style.marginRight = 5;
        button.style.color = Color.white;
    }

    static void SetToggleColor(Button button, bool enabled)
    {
        button.style.backgroundColor = enabled
            ? new Color(0.15f, 0.5f, 0.25f)
            : new Color(0.7f, 0.2f, 0.2f);
    }

    public void ShowMessage(string message)
    {
        if (statusLabel == null) return;

        statusLabel.text = message;
        statusLabel.style.display = DisplayStyle.Flex;
    }

    void OnSendCommand()
    {
        string command = inputField.value;
        Debug.Log("按鈕被按下");
        Debug.Log("輸入內容：" + command);

        if (statusLabel != null)
            statusLabel.style.display = DisplayStyle.None;

        if (string.IsNullOrWhiteSpace(command))
        {
            Debug.LogWarning("輸入是空的，所以沒有寫入");
            return;
        }

        try
        {
            string inputPath = Path.Combine(Application.streamingAssetsPath, "user_input.txt");

            Debug.Log("StreamingAssetsPath：" + Application.streamingAssetsPath);
            Debug.Log("準備寫入：" + inputPath);

            Directory.CreateDirectory(Application.streamingAssetsPath);

            File.WriteAllText(inputPath, command);

            Debug.Log("寫入後讀回：" + File.ReadAllText(inputPath));

            StartCoroutine(WaitAndExecute());
        }
        catch (System.Exception ex)
        {
            Debug.LogError("寫入 user_input.txt 失敗：" + ex);
        }
    }

    IEnumerator WaitAndExecute()
    {
        // Batch 架構下：csharp_server 先用一張 scene snapshot 排完所有步驟，
        // 再一次寫 current_step.json；JsonExecutor 收到後連續執行整批。
        // 這裡等 current_step.json 出現 / 更新，log 一下讓使用者知道 pipeline 通了。
        string stepPath = Path.Combine(SHARED_DIR, "current_step.json");
        var lastWrite = File.Exists(stepPath) ? File.GetLastWriteTime(stepPath) : System.DateTime.MinValue;

        float timeout = 600f;     // batch 會先完成所有 LLM motion plans 才開始執行
        float waited = 0f;
        float lastLogAt = 0f;

        while (waited < timeout)
        {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;

            if (File.Exists(stepPath) && File.GetLastWriteTime(stepPath) > lastWrite)
            {
                Debug.Log($"[UI] batch current_step.json 已更新（等了 {waited:F1} 秒），Executor 會自動連續執行");
                yield break;
            }

            if (waited - lastLogAt >= 15f)
            {
                Debug.Log($"[UI] 仍在等 csharp_server 排完整批步驟...（已等 {waited:F0} 秒 / 上限 {timeout:F0} 秒）");
                lastLogAt = waited;
            }
        }

        Debug.LogWarning($"[UI] 等待 batch current_step.json 逾時（{timeout} 秒）— 檢查 csharp_server / perception_server 是否在跑");
    }
}
