using UnityEngine;
using UnityEngine.UIElements;

public class UIManager : MonoBehaviour
{
    public UIDocument uiDocument;
    public JsonExecutor executor;

    private TextField inputField;
    private Button sendButton;
    private Label statusLabel;

    void OnEnable()
    {
        var root = uiDocument.rootVisualElement;

        // 建立容器
        var container = new VisualElement();
        container.style.position = UnityEngine.UIElements.Position.Absolute;
        container.style.bottom = 20;
        container.style.left = 20;
        container.style.width = 400;
        container.style.backgroundColor = new Color(0, 0, 0, 0.7f);
        container.style.paddingTop = 10;
        container.style.paddingBottom = 10;
        container.style.paddingLeft = 10;
        container.style.paddingRight = 10;

        // 輸入框
        inputField = new TextField("指令：");
        inputField.style.marginBottom = 8;

        // 送出按鈕
        sendButton = new Button(() => OnSendCommand());
        sendButton.text = "執行";
        sendButton.style.marginBottom = 8;

        // 狀態顯示
        statusLabel = new Label("待機中...");
        statusLabel.style.color = Color.white;

        container.Add(inputField);
        container.Add(sendButton);
        container.Add(statusLabel);
        root.Add(container);
    }

    void OnSendCommand()
    {
        string command = inputField.value;
        if (string.IsNullOrEmpty(command)) return;

        statusLabel.text = $"執行中：{command}";
        executor.LoadAndExecute();
    }

    public void UpdateStatus(string status)
    {
        if (statusLabel != null)
            statusLabel.text = status;
    }
}