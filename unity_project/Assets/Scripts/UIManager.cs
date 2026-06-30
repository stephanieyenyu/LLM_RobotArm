using UnityEngine;
using UnityEngine.UIElements;
using System.Collections;
using System.IO;

public class UIManager : MonoBehaviour
{
    public UIDocument uiDocument;
    public JsonExecutor executor;

    private TextField inputField;
    private Button sendButton;

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
    }

    void OnSendCommand()
    {
        string command = inputField.value;
        if (string.IsNullOrEmpty(command)) return;

        Debug.Log("使用者輸入：" + command);

        string inputPath = Path.Combine(Application.streamingAssetsPath, "user_input.txt");
        File.WriteAllText(inputPath, command);

        StartCoroutine(WaitAndExecute());
    }

    IEnumerator WaitAndExecute()
    {
        string planPath = Path.Combine(Application.streamingAssetsPath, "robot_plan.json");
        var lastWrite = File.Exists(planPath) ? File.GetLastWriteTime(planPath) : System.DateTime.MinValue;

        // 等最多 30 秒讓 terminal LLM 產生新的 robot_plan.json
        float timeout = 30f;
        float waited = 0f;

        while (waited < timeout)
        {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;

            if (File.Exists(planPath) && File.GetLastWriteTime(planPath) > lastWrite)
            {
                Debug.Log("robot_plan.json 已更新，開始執行");
                executor.LoadAndExecute();
                yield break;
            }
        }

        Debug.LogWarning("等待 robot_plan.json 更新逾時");
    }
}