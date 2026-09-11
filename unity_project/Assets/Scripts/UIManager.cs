using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class UIManager : MonoBehaviour
{
    public UIDocument uiDocument;
    public JsonExecutor executor;
    public SceneSyncer sceneSyncer;   // 拖 PerceptionSync 進來（新增方塊用）

    // 兩邊共用的資料夾（Unity / csharp_server 都指到這裡）
    // 如果之後換電腦或換路徑，只要改這一行
    private string SHARED_DIR => Application.streamingAssetsPath;

    private TextField inputField;
    private Button sendButton;
    private Label statusLabel;

    // 手動生方塊的計數（決定下一顆放哪）
    private int manualSpawnCount = 0;

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

        // ---------------------------------------------------------
        // 左上角模擬工具：手動新增黃色方塊 / 清空
        // 讓沒接相機的情況下也能測試 pick-and-place
        // ---------------------------------------------------------
        var simPanel = new VisualElement();
        simPanel.style.position = UnityEngine.UIElements.Position.Absolute;
        simPanel.style.top = 10;
        simPanel.style.left = 10;
        simPanel.style.flexDirection = FlexDirection.Column;
        simPanel.style.backgroundColor = new Color(0, 0, 0, 0.7f);
        simPanel.style.paddingTop = 6;
        simPanel.style.paddingBottom = 6;
        simPanel.style.paddingLeft = 6;
        simPanel.style.paddingRight = 6;

        var simLabel = new Label("模擬工具");
        simLabel.style.color = Color.white;
        simLabel.style.marginBottom = 4;
        simPanel.Add(simLabel);

        var addYellowBtn = new Button(() => OnAddManualCube("yellow"));
        addYellowBtn.text = "＋ 黃色方塊";
        addYellowBtn.style.height = 36;
        addYellowBtn.style.width = 130;
        addYellowBtn.style.marginBottom = 4;
        simPanel.Add(addYellowBtn);

        var addBlackBtn = new Button(() => OnAddManualCube("black"));
        addBlackBtn.text = "＋ 黑色方塊";
        addBlackBtn.style.height = 36;
        addBlackBtn.style.width = 130;
        addBlackBtn.style.marginBottom = 4;
        simPanel.Add(addBlackBtn);

        var clearBtn = new Button(() => OnClearManualCubes());
        clearBtn.text = "清空所有方塊";
        clearBtn.style.height = 36;
        clearBtn.style.width = 130;
        simPanel.Add(clearBtn);

        // 跳過 pattern 審查：寫進跟 csharp_server 共用的檔案，C# server 每次
        // 排 pattern 前都會重讀這個檔案，所以這裡勾選/取消隨時生效，不用重開
        // Unity 或 csharp_server。
        var skipReviewToggle = new Toggle("跳過 pattern 審查");
        skipReviewToggle.style.marginTop = 4;
        skipReviewToggle.style.color = Color.white;
        skipReviewToggle.value = ReadSkipPatternReviewFlag();
        skipReviewToggle.RegisterValueChangedCallback(evt => WriteSkipPatternReviewFlag(evt.newValue));
        simPanel.Add(skipReviewToggle);

        root.Add(simPanel);
    }

    // ---------------------------------------------------------
    // 跳過 pattern 審查開關：跟 csharp_server/PatternDesigner.cs 共用同一個
    // StreamingAssets 底下的旗標檔，寫 "1"/"0"。
    // ---------------------------------------------------------
    string SkipPatternReviewFlagPath => Path.Combine(SHARED_DIR, "skip_pattern_review.txt");

    bool ReadSkipPatternReviewFlag()
    {
        try
        {
            return File.Exists(SkipPatternReviewFlagPath) &&
                   File.ReadAllText(SkipPatternReviewFlagPath).Trim() == "1";
        }
        catch (IOException)
        {
            return false;
        }
    }

    void WriteSkipPatternReviewFlag(bool value)
    {
        try
        {
            File.WriteAllText(SkipPatternReviewFlagPath, value ? "1" : "0");
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[UI] 寫入 skip_pattern_review.txt 失敗：{e.Message}");
        }
    }

    // ---------------------------------------------------------
    // 模擬工具實作
    // ---------------------------------------------------------
    SceneSyncer ResolveSceneSyncer()
    {
        if (sceneSyncer != null) return sceneSyncer;
        if (executor != null && executor.sceneSyncer != null)
        {
            sceneSyncer = executor.sceneSyncer;
            return sceneSyncer;
        }
        sceneSyncer = FindObjectOfType<SceneSyncer>();
        return sceneSyncer;
    }

    void OnAddManualCube(string color)
    {
        var syncer = ResolveSceneSyncer();
        if (syncer == null)
        {
            Debug.LogWarning("[UI] 找不到 SceneSyncer，無法生方塊");
            return;
        }

        // 在補貨區內按 grid 依序排：從左下角開始，每列 6 顆
        const float xMargin = 0.03f;
        const float yMargin = 0.03f;
        const float spacing = 0.04f;   // cube 2.5cm + 1.5cm gap
        const int colsPerRow = 6;

        int col = manualSpawnCount % colsPerRow;
        int row = manualSpawnCount / colsPerRow;
        float qrX = xMargin + col * spacing;
        float qrY = yMargin + row * spacing;
        float qrZ = syncer.cubeSizeM;  // Z 是 cube 頂面高度

        Color c = color == "black"
            ? new Color(0.1f, 0.1f, 0.1f)
            : new Color(1f, 0.85f, 0.1f);

        string name = color + "_manual_" + manualSpawnCount;
        var go = syncer.SpawnCube(name, qrX, qrY, qrZ, c);
        manualSpawnCount++;

        Debug.Log($"[UI] 已新增 {color} 方塊 #{manualSpawnCount} @ QR({qrX:F3}, {qrY:F3})");
        SaveManualSceneToFile();
    }

    void OnClearManualCubes()
    {
        var syncer = ResolveSceneSyncer();
        if (syncer == null) return;
        var cubes = syncer.GetCurrentCubes();
        int n = cubes.Count;
        for (int i = cubes.Count - 1; i >= 0; i--)
        {
            if (cubes[i] != null) Destroy(cubes[i]);
        }
        cubes.Clear();
        manualSpawnCount = 0;
        Debug.Log($"[UI] 已清空 {n} 個方塊");
        SaveManualSceneToFile();
    }

    // 把目前所有 cube 位置寫成 manual_scene.json，供 fake_perception.py 讀取
    void SaveManualSceneToFile()
    {
        var syncer = ResolveSceneSyncer();
        if (syncer == null) return;

        var cubes = syncer.GetCurrentCubes();
        float halfHeight = syncer.cubeSizeM / 2f;
        var sb = new StringBuilder();
        sb.Append("{\"image_width\":1280,\"image_height\":720,\"qrcodes\":[],\"objects\":[");

        bool first = true;
        foreach (var cube in cubes)
        {
            if (cube == null) continue;
            Vector3 p = cube.transform.localPosition;
            // Unity → QR frame（跟 SceneSyncer.QRToUnity 對稱）
            //   Unity  Z →  QR X
            //   Unity -X →  QR Y
            //   Unity  Y →  QR Z
            float qrX = p.z;
            float qrY = -p.x;
            float qrZ = p.y + halfHeight;       // 頂面高度

            string colorName = cube.name.Contains("black") ? "black" : "yellow";
            if (!first) sb.Append(",");
            first = false;
            string sx = qrX.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            string sy = qrY.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            string sz = qrZ.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            sb.Append("{");
            sb.Append("\"name\":\"").Append(colorName).Append("_cube\",");
            sb.Append("\"confidence\":1.0,");
            sb.Append("\"shape\":\"cube\",");
            sb.Append("\"orientation\":null,");    // 跟實機一致：cube 無方向用 null
            sb.Append("\"skew_deg\":0.0,");         // domino 才會有非 0 值
            sb.Append("\"source\":\"manual\",");
            sb.Append("\"position\":{\"x\":").Append(sx)
              .Append(",\"y\":").Append(sy)
              .Append(",\"z\":").Append(sz)
              .Append(",\"source\":\"manual\"}");
            sb.Append("}");
        }
        long ts = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        sb.Append("],\"timestamp\":").Append(ts).Append("}");

        try
        {
            string path = Path.Combine(SHARED_DIR, "manual_scene.json");
            File.WriteAllText(path, sb.ToString());
            Debug.Log($"[UI] 已寫入 manual_scene.json ({cubes.Count} 顆方塊)");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[UI] 寫 manual_scene.json 失敗: {ex.Message}");
        }
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
