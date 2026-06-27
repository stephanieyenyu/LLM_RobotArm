using UnityEngine;
using UnityEngine.UIElements;

public class UIManager : MonoBehaviour
{
    public UIDocument uiDocument;
    public JsonExecutor executor;

    private TextField inputField;
    private Button sendButton;

    void OnEnable()
    {
        var root = uiDocument.rootVisualElement;

        // 底部橫向容器
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

        // 輸入框
        inputField = new TextField("");
        inputField.style.flexGrow = 1;
        inputField.style.marginRight = 5;
        inputField.style.height = 40;
        inputField.focusable = true;

        // 送出按鈕
        sendButton = new Button(() => OnSendCommand());
        sendButton.text = "執行";
        sendButton.style.height = 40;
        sendButton.style.width = 80;

        container.Add(inputField);
        container.Add(sendButton);
        root.Add(container);
    }

    void OnSendCommand()
    {
        executor.LoadAndExecute();
    }

    public void UpdateStatus(string status)
    {
        Debug.Log("狀態：" + status);
    }
}