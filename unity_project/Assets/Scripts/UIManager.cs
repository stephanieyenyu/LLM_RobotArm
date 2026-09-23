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
    private Label statusLabel;
    private Coroutine uiMonitor;
    private string fallbackCommand = "";

    void OnEnable()
    {
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
        // 右上角三個手動控制按鈕：鬆開 / 夾緊 / 回 Home
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
        Debug.Log("[UI] 輸入框、執行按鈕與手動控制已建立。", this);
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
        const float height = 38f;
        float y = Mathf.Max(margin, Screen.height - height - margin);
        GUI.Box(new Rect(0, y - 6f, Screen.width, height + 12f), GUIContent.none);
        GUI.SetNextControlName("RobotCommandFallback");
        fallbackCommand = GUI.TextField(
            new Rect(margin, y, Mathf.Max(100f, Screen.width - buttonWidth - margin * 3f), height),
            fallbackCommand ?? "");
        if (GUI.Button(new Rect(Screen.width - buttonWidth - margin, y, buttonWidth, height), "執行"))
            SubmitCommand(fallbackCommand);
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
