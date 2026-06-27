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
        new SceneObjectData { name = "bottle", x = 30, y = 20, z = 15 },
        new SceneObjectData { name = "cup", x = 25, y = 10, z = 15 },
        new SceneObjectData { name = "scissors", x = 35, y = 25, z = 12 },
        new SceneObjectData { name = "box", x = 40, y = -20, z = 10 }
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

        string systemPrompt = $@"你是 UR3 機械手臂的 LLM planner。
根據使用者的自然語言指令，產生對應的動作 JSON。

支援的 action：
1. pick_and_place - 拿起物件放到目標位置
   - 需要 object 和 target
2. move_relative - 移動物件相對距離
   - 需要 object、direction、distance_cm

規則：
- action 必須是 ""pick_and_place"" 或 ""move_relative""
- object 必須從場景物件名稱中選一個
- direction 必須是 left/right/forward/backward/up/down 之一
- 只輸出 JSON，不要其他文字

場景物件：{JsonConvert.SerializeObject(objectNames)}";

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