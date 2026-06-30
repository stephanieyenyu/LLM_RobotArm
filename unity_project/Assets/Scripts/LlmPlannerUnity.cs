using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[System.Serializable]
public class SceneObjectData
{
    public string name;
    public float x, y, z;
}

public class LlmPlannerUnity : MonoBehaviour
{
    [Header("OpenAI 設定")]
    public string model = "gpt-4o-mini";

    [Header("場景物件（測試用）")]
    public List<SceneObjectData> sceneObjects = new()
{
    new SceneObjectData { name = "bottle", x = 0, y = 30, z = 10 },
    new SceneObjectData { name = "cup", x = 20, y = 10, z = 10 },
    new SceneObjectData { name = "scissors", x = -30, y = 0, z = 10 },
    new SceneObjectData { name = "box", x = -20, y = -10, z = 10 }
};

    private string apiKey;

    void Start()
    {
        apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("找不到 OPENAI_API_KEY 環境變數");
        }
    }

    public IEnumerator GeneratePlan(string userCommand, Action<string> onComplete)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("API Key 未設定");
            yield break;
        }

        var objectNames = new List<string>();
        foreach (var obj in sceneObjects) objectNames.Add(obj.name);

        string systemPrompt = $@"你是 UR3 機械手臂系統中的 LLM planner。
    你的任務是把使用者的自然語言指令解析成機械手臂任務計畫。
            
                支援兩種 action：
                1. pick_and_place
                   - 表示把某個物件拿起來，放到另一個物件或目標位置
                   - 需要輸出 object 和 target
                   - direction 必須是 null
                   - distance_cm 必須是 null
                2. move_relative
                   - 表示把某個物件往某個方向移動指定距離
                   - 需要輸出 object、direction、distance_cm
                   - target 必須是 null
            
                規則：
                - action 只能是 "pick_and_place" 或 "move_relative"。
                - object 必須從 Part B 提供的物件名稱清單中選擇。
                - pick_and_place 的 target 也必須從 Part B 提供的物件名稱清單中選擇。
                - move_relative 的 direction 只能是 left、right、forward、backward、up、down，依下列語意判斷，不限於固定詞組：
                  - 表示「左」方向的詞（例如：左移、向左、往左、左邊、左側、移到左邊、靠左、左挪…）→ direction=left
                  - 表示「右」方向的詞（例如：右移、向右、往右、右邊、右側、移到右邊、靠右、右挪…）→ direction=right
                  - 表示「前」方向的詞（例如：往前、向前、前面、前方、前移…）→ direction=forward
                  - 表示「後」方向的詞（例如：往後、向後、後面、後方、後退、退後…）→ direction=backward
                  - 表示「上」方向的詞（例如：往上、向上、上面、上方、抬高、舉高…）→ direction=up
                  - 表示「下」方向的詞（例如：往下、向下、下面、下方、放低、降低…）→ direction=down
                  - 上述僅為範例，請依語意理解使用者真實意圖判斷方向，不要求逐字匹配。
                - distance_cm 規則：
                  - 使用公分為單位，輸出純數字（可為小數），不要加單位文字。
                  - 使用者輸入的距離可能以多種形式出現，皆須正確轉換為數字：
                    - 阿拉伯數字：例如 5、10、3.5
                    - 中文數字：例如 五、十、三點五、兩
                    - 中文數字+量詞混合：例如 五公分、十公分、三公分半（=3.5）、半公分（=0.5）
                    - 公分以外的單位需自動換算成公分：例如 1 公尺 = 100、10 毫米 = 1
                  - 若指令中完全沒有提及距離數字，但動作明確是方向性移動，distance_cm 可填入合理預設值 5。
                - 不可以自己創造不存在的物件名稱。
                - 不可以輸出或編造座標。
                - 物件原始位置與新位置會由 C# 程式根據 Part B 座標計算。
                - 如果中文名稱和英文物件名稱語意相近，請選擇最符合的英文物件名稱。
                - action 判斷的核心原則：
                  - 只要指令中包含方向詞（左/右/前/後/上/下，或其同義表達）→ 一律是 move_relative，target 必須為 null。
                  - 只有在指令明確是「把 A 放到 B 旁邊/上面/裡面」這種兩個不同物件之間的擺放關係，且沒有方向詞時 → 才是 pick_and_place。
                  - 若同時出現方向詞與另一個物件名稱（例如「把杯子往左移到盤子旁邊」），仍優先視為 move_relative，並以方向詞與距離為主；若無法判斷距離，套用預設值 5。
                - 最後只能輸出符合 JSON schema 的 JSON，不要加任何解釋文字。;

        string userPrompt = $@"使用者指令：{userCommand}
場景物件資料：{JsonConvert.SerializeObject(sceneObjects)}";

        var requestBody = new
        {
            model = model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            response_format = new { type = "json_object" }
        };

        string json = JsonConvert.SerializeObject(requestBody);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

        var request = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyBytes);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", "Bearer " + apiKey);

        Debug.Log("呼叫 OpenAI...");
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("API 錯誤：" + request.error);
            Debug.LogError(request.downloadHandler.text);
            yield break;
        }

        var response = JObject.Parse(request.downloadHandler.text);
        string llmContent = response["choices"][0]["message"]["content"].ToString();
        Debug.Log("LLM 回應：" + llmContent);

        // 補上座標資訊
        var llmResult = JObject.Parse(llmContent);
        string action = llmResult["action"]?.ToString();
        string objName = llmResult["object"]?.ToString();
        string target = llmResult["target"]?.ToString();
        string direction = llmResult["direction"]?.ToString();
        float? distance = llmResult["distance_cm"]?.ToObject<float?>();

        var objPos = sceneObjects.Find(o => o.name == objName);
        SceneObjectData targetPos = null;

        if (action == "pick_and_place" && !string.IsNullOrEmpty(target))
        {
            targetPos = sceneObjects.Find(o => o.name == target);
        }
        else if (action == "move_relative" && objPos != null && distance.HasValue)
        {
            targetPos = new SceneObjectData
            {
                name = objName + "_target",
                x = objPos.x,
                y = objPos.y,
                z = objPos.z
            };
            switch (direction)
            {
                case "left": targetPos.x -= distance.Value; break;
                case "right": targetPos.x += distance.Value; break;
                case "forward": targetPos.y += distance.Value; break;
                case "backward": targetPos.y -= distance.Value; break;
                case "up": targetPos.z += distance.Value; break;
                case "down": targetPos.z -= distance.Value; break;
            }
        }

        var fullPlan = new
        {
            action = action,
            @object = objName,
            target = target,
            direction = direction,
            distance_cm = distance,
            object_position = objPos,
            target_position = targetPos
        };

        string finalJson = JsonConvert.SerializeObject(fullPlan, Formatting.Indented);
        onComplete?.Invoke(finalJson);
    }
}
