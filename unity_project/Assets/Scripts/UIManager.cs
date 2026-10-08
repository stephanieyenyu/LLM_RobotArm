using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
using System.IO;

[DefaultExecutionOrder(100)]
public class UIManager : MonoBehaviour
{
    public UIDocument uiDocument;
    public JsonExecutor executor;

    // 兩邊共用的資料夾（Unity / csharp_server 都指到這裡）
    // 如果之後換電腦或換路徑，只要改這一行
    private string SHARED_DIR => Application.streamingAssetsPath;

    private TextField inputField;
    private Button sendButton;
    // pattern審查開關的目前狀態（從 skip_pattern_review.txt 讀回來；OnGUI 每幀用，不每幀讀檔）
    private bool patternReviewEnabled = true;
    private Label statusLabel;
    private Coroutine uiMonitor;
    private string fallbackCommand = "";

    void OnEnable()
    {
        // 下方備用指令列（OnGUI）也顯示 pattern審查狀態，不能等 UI Toolkit 面板建好才讀
        patternReviewEnabled = ReadPatternReviewEnabled();
        // UIDocument may rebuild its root after OnEnable (including domain
        // reload in Play Mode). Build only after its panel has been attached.
        uiMonitor = StartCoroutine(EnsureUiReady());
    }

    IEnumerator EnsureUiReady()
    {
        yield return null;
        if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
        if (uiDocument == null)
        {
            Debug.LogError("[UI] UIManager 缺少 UIDocument，無法建立輸入框。", this);
            yield break;
        }
        while (isActiveAndEnabled)
        {
            var root = uiDocument.rootVisualElement;
            if (root != null && root.panel != null && root.Q<VisualElement>("robot-manual-controls") == null)
                BuildUi(root);
            yield return new WaitForSecondsRealtime(0.25f);
        }
    }

    void OnDisable()
    {
        if (uiMonitor != null) StopCoroutine(uiMonitor);
        StopAllCoroutines();
        uiMonitor = null;
        var root = uiDocument != null ? uiDocument.rootVisualElement : null;
        root?.Q<VisualElement>("robot-command-bar")?.RemoveFromHierarchy();
        root?.Q<Label>("robot-status")?.RemoveFromHierarchy();
        root?.Q<VisualElement>("robot-manual-controls")?.RemoveFromHierarchy();
    }

    void BuildUi(VisualElement root)
    {
        root.Q<Label>("robot-status")?.RemoveFromHierarchy();
        root.Q<VisualElement>("robot-manual-controls")?.RemoveFromHierarchy();
        root.style.width = Length.Percent(100);
        root.style.height = Length.Percent(100);

        var container = new VisualElement();
        container.name = "robot-command-bar";
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

        // 自由規劃實驗的驗證固定開啟。
        inputField = new TextField("");
        inputField.name = "robot-command-input";
        inputField.style.flexGrow = 1;
        inputField.style.marginRight = 5;
        inputField.style.height = 40;
        inputField.focusable = true;

        sendButton = new Button(() => OnSendCommand());
        sendButton.name = "robot-command-send";
        sendButton.text = "執行";
        sendButton.style.height = 40;
        sendButton.style.width = 80;
        // The command bar is rendered by OnGUI below. Keep these controls as
        // callback objects only; do not depend on UIDocument for command input.

        statusLabel = new Label("");
        statusLabel.name = "robot-status";
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
        // 右上角只放三個手動控制按鈕：鬆開 / 夾緊 / 回 Home。
        // 模式、場景與其他開關都在下方指令列（OnGUI），三個資料夾（main、zero-constraint、rulebased）同一套介面
        // ---------------------------------------------------------
        var controlPanel = new VisualElement();
        controlPanel.name = "robot-manual-controls";
        controlPanel.style.position = UnityEngine.UIElements.Position.Absolute;
        controlPanel.style.top = 10;
        controlPanel.style.right = 10;
        controlPanel.style.flexDirection = FlexDirection.Column;
        controlPanel.style.backgroundColor = new Color(0, 0, 0, 0.7f);
        controlPanel.style.paddingTop = 6;
        controlPanel.style.paddingBottom = 6;
        controlPanel.style.paddingLeft = 6;
        controlPanel.style.paddingRight = 6;

        var openBtn = new Button(() => {
            ResolveExecutor();
            if (executor != null) executor.ReleaseGripper();
            else ShowMessage("找不到 JsonExecutor，無法控制夾爪。");
        });
        openBtn.text = "鬆開夾爪";
        openBtn.style.height = 36;
        openBtn.style.width = 120;
        openBtn.style.marginBottom = 4;

        var gripBtn = new Button(() => {
            ResolveExecutor();
            if (executor != null) executor.GripGripper();
            else ShowMessage("找不到 JsonExecutor，無法控制夾爪。");
        });
        gripBtn.text = "夾緊夾爪";
        gripBtn.style.height = 36;
        gripBtn.style.width = 120;
        gripBtn.style.marginBottom = 4;

        var homeBtn = new Button(OnHomeRequested);
        homeBtn.text = "回 Home";
        homeBtn.style.height = 36;
        homeBtn.style.width = 120;

        controlPanel.Add(openBtn);
        controlPanel.Add(gripBtn);
        controlPanel.Add(homeBtn);
        root.Add(controlPanel);
        Debug.Log("[UI] 輸入框、執行按鈕與手動控制已建立。", this);
    }

    void ResolveExecutor()
    {
        if (executor == null) executor = FindObjectOfType<JsonExecutor>();
    }

    void OnHomeRequested()
    {
        Debug.Log("[UI] 已按下回 Home。", this);
        ResolveExecutor();
        if (executor == null)
        {
            ShowMessage("找不到 JsonExecutor，無法回 Home。");
            return;
        }
        executor.TryGoHome(out string message);
        ShowMessage(message);
    }

    string ModeButtonText() => RunMode.IsSim ? "模式：純模擬" : "模式：實機";
    string SceneButtonText() => "場景：" + Path.GetFileNameWithoutExtension(RunMode.Scene);

    void OnToggleSimMode()
    {
        ResolveExecutor();
        if (executor != null && executor.IsBusy)
        {
            ShowMessage("手臂正在執行，執行完再切換模式。");
            return;
        }
        try
        {
            RunMode.SetSim(!RunMode.IsSim);
        }
        catch (System.Exception ex)
        {
            ShowMessage("切換模式失敗（寫不了 run_mode.json）：" + ex.Message);
            return;
        }
        ShowMessage(RunMode.IsSim
            ? $"已切成純模擬：正在把 {RunMode.Scene} 載入 Isaac Sim，畫面會顯示模擬的積木，不需要 perception_server。" +
              "2D 只用 Unity 模擬驗證（畫面重疊率＋LLM 看 Unity 畫面），不用開 URSim，沒開 Isaac 時用 csharp_server 內建的虛擬世界；" +
              "3D 由 Isaac Sim 模擬驗證，需要 URSim 與 isaac_sim_server（--ursim_ip）。"
            : "已切成實機：畫面改顯示相機看到的積木，下一個指令起動作送實體手臂。要先開 perception_server。");
    }

    // 輪流切換 repo 根目錄 sim_scenes/ 裡的虛擬場景檔（SceneSyncer 會馬上重建 Isaac 世界，執行中不能換）
    void OnCycleScene()
    {
        ResolveExecutor();
        if (executor != null && executor.IsBusy)
        {
            ShowMessage("手臂正在執行，執行完再換場景。");
            return;
        }
        var scenes = RunMode.AvailableScenes();
        if (scenes.Length == 0)
        {
            ShowMessage("找不到虛擬場景檔（repo 根目錄 sim_scenes/*.json）。");
            return;
        }
        string next = scenes[(System.Array.IndexOf(scenes, RunMode.Scene) + 1) % scenes.Length];
        try
        {
            RunMode.SetScene(next);
        }
        catch (System.Exception ex)
        {
            ShowMessage("換場景失敗（寫不了 run_mode.json）：" + ex.Message);
            return;
        }
        ShowMessage($"虛擬場景：{next}，正在載入 Isaac Sim…");
    }

    // ---------------------------------------------------------
    // pattern審查開關：跟 csharp_server/PatternDesigner 共用 StreamingAssets/skip_pattern_review.txt，
    // 檔案記的是「跳過」："1" = 跳過交叉審查（只請 OpenAI 畫一次就採用），其他或檔案不存在 = OpenAI 與 Gemini
    // 各畫一張、互相審查、投票。server 每個任務開始時重讀，下一個指令生效。
    // ---------------------------------------------------------
    string SkipPatternReviewFlagPath => Path.Combine(SHARED_DIR, "skip_pattern_review.txt");
    string PatternReviewButtonText() => patternReviewEnabled ? "pattern審查：開" : "pattern審查：關";

    bool ReadPatternReviewEnabled()
    {
        try
        {
            return !(File.Exists(SkipPatternReviewFlagPath) && File.ReadAllText(SkipPatternReviewFlagPath).Trim() == "1");
        }
        catch (IOException)
        {
            return true;
        }
    }

    void TogglePatternReview()
    {
        bool enable = !ReadPatternReviewEnabled();
        try
        {
            File.WriteAllText(SkipPatternReviewFlagPath, enable ? "0" : "1");
        }
        catch (IOException e)
        {
            ShowMessage("切換 pattern審查失敗（寫不了 skip_pattern_review.txt）：" + e.Message);
            return;
        }
        // 狀態一律從檔案讀回來，寫入失敗時畫面不會顯示成已切換
        patternReviewEnabled = ReadPatternReviewEnabled();
        ShowMessage(patternReviewEnabled
            ? "pattern審查開啟：下一個指令起，OpenAI 與 Gemini 各畫一張目標 bitmap（立體的是高度圖）、互相審查、投票（需要 GEMINI_API_KEY）。"
            : "pattern審查關閉：下一個指令起，只請 OpenAI 畫一次目標 bitmap 就採用（立體的照 260917 再由 OpenAI 檢查正面方向）。");
    }

    public void ShowMessage(string message)
    {
        if (statusLabel == null) return;

        statusLabel.text = message;
        statusLabel.style.display = DisplayStyle.Flex;
    }

    void OnGUI()
    {
        // UI Toolkit can temporarily lose its runtime panel after a domain
        // reload. Keep a separate immediate-mode command bar available so the
        // experiment can still be started without editing the scene.
        const float margin = 10f;
        const float buttonWidth = 90f;
        const float modeWidth = 120f;
        const float sceneWidth = 150f;
        const float reviewWidth = 130f;
        const float height = 38f;
        float y = Mathf.Max(margin, Screen.height - height - margin);
        GUI.Box(new Rect(0, y - 6f, Screen.width, height + 12f), GUIContent.none);
        // 由右往左：執行、pattern審查、模式切換、（純模擬時）換場景，剩下的寬度給輸入框
        float x = Screen.width - margin - buttonWidth;
        if (GUI.Button(new Rect(x, y, buttonWidth, height), "執行"))
            SubmitCommand(fallbackCommand);
        x -= margin + reviewWidth;
        if (GUI.Button(new Rect(x, y, reviewWidth, height), PatternReviewButtonText()))
            TogglePatternReview();
        x -= margin + modeWidth;
        if (GUI.Button(new Rect(x, y, modeWidth, height), ModeButtonText()))
            OnToggleSimMode();
        if (RunMode.IsSim)
        {
            x -= margin + sceneWidth;
            if (GUI.Button(new Rect(x, y, sceneWidth, height), SceneButtonText()))
                OnCycleScene();
        }
        GUI.SetNextControlName("RobotCommandFallback");
        fallbackCommand = GUI.TextField(
            new Rect(margin, y, Mathf.Max(100f, x - margin * 2f), height),
            fallbackCommand ?? "");
    }

    void OnSendCommand()
    {
        SubmitCommand(inputField != null ? inputField.value : fallbackCommand);
    }

    void SubmitCommand(string command)
    {
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
            fallbackCommand = "";
            if (inputField != null) inputField.value = "";

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
        // 實驗流程先確認任務初始桌面，再由 LLM 自由規劃。
        // 每個操作經最新場景與安全檢查後寫入 current_step.json。
        // 這裡等 current_step.json 出現 / 更新，log 一下讓使用者知道 pipeline 通了。
        string stepPath = Path.Combine(SHARED_DIR, "current_step.json");
        var lastWrite = File.Exists(stepPath) ? File.GetLastWriteTime(stepPath) : System.DateTime.MinValue;

        float waited = 0f;
        float lastLogAt = 0f;

        // Keep waiting until the server publishes an operation. Slow model
        // calls and table-reset waits do not expire at the UI layer.
        while (true)
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
                Debug.Log($"[UI] 等待桌面恢復初始配置或 LLM 規劃...（已等 {waited:F0} 秒，無逾時限制）");
                lastLogAt = waited;
            }
        }

    }
}
