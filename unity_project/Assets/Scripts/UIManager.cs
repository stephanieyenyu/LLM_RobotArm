using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
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

        inputField = new TextField("");
        inputField.style.flexGrow = 1;
        inputField.style.marginRight = 5;
        inputField.style.height = 40;
        inputField.focusable = true;

        sendButton = new Button(() => OnSendCommand());
        sendButton.text = "執行";
        sendButton.style.height = 40;
        sendButton.style.width = 80;

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
        // Server 會一次產生完整 batch_plan.json；Executor 收到後自行逐步執行。
        string batchPath = Path.Combine(SHARED_DIR, "batch_plan.json");
        var lastWrite = File.Exists(batchPath) ? File.GetLastWriteTime(batchPath) : System.DateTime.MinValue;

        float timeout = 120f;     // gpt-5 設計 pattern 有時要一分鐘以上
        float waited = 0f;
        float lastLogAt = 0f;

        while (waited < timeout)
        {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;

            if (File.Exists(batchPath) && File.GetLastWriteTime(batchPath) > lastWrite)
            {
                Debug.Log($"[UI] 完整 batch_plan.json 已產生（等了 {waited:F1} 秒），Executor 開始依序執行");
                yield break;
            }

            if (waited - lastLogAt >= 15f)
            {
                Debug.Log($"[UI] 仍在等 csharp_server 產生完整 Batch Plan...（已等 {waited:F0} 秒 / 上限 {timeout:F0} 秒）");
                lastLogAt = waited;
            }
        }

        Debug.LogWarning($"[UI] 等待 batch_plan.json 逾時（{timeout} 秒）— 檢查 csharp_server / perception_server 是否在跑");
    }
}
