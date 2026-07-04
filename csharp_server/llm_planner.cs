using System.Text.Json;
using OpenAI.Chat;

public class LlmPlanner
{
    private readonly ChatClient _client;

    public LlmPlanner(string model = "gpt-5")
    {
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        }

        _client = new ChatClient(model, apiKey);
    }

    public async Task<RobotPlan> GeneratePlanAsync(
        string userCommand,
        List<SceneObject> sceneObjects
    )
    {
        if (string.IsNullOrWhiteSpace(userCommand))
            throw new ArgumentException("User command cannot be empty.", nameof(userCommand));

        if (sceneObjects == null || sceneObjects.Count == 0)
            throw new ArgumentException("Scene object list cannot be empty.", nameof(sceneObjects));

        List<string> objectNames = sceneObjects
            .Select(o => o.Name)
            .Distinct()
            .ToList();

        ChatCompletionOptions options = new()
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "robot_plan",
                jsonSchema: BinaryData.FromString(CreateJsonSchema(objectNames)),
                jsonSchemaIsStrict: true
            )
        };

        List<ChatMessage> messages = new()
        {
            new SystemChatMessage(
                """
                你是 UR3 機械手臂系統中的 LLM planner。
                你的任務是把使用者的自然語言指令解析成機械手臂任務計畫。
            
                支援三種 action：
                1. pick_and_place
                   - 表示把某個物件拿起來，放到另一個物件的位置上
                   - 需要輸出 object 和 target
                   - reference_object、direction、distance_cm 必須是 null
                2. move_relative
                   - 表示把某個物件往某個方向移動指定距離（以物件自己現在的位置為基準）
                   - 需要輸出 object、direction、distance_cm
                   - target、reference_object 必須是 null
                3. place_relative
                   - 表示把某個物件拿起來，放到「另一個參考物件」的某個方向、某個距離處
                   - 需要輸出 object、reference_object、direction、distance_cm
                   - target 必須是 null
                   - 例如「把手機放到杯子左邊 15 公分」→ object=cell phone, reference_object=cup, direction=left, distance_cm=15
                4. error
                   - 表示這個指令無法被安全地轉換成上述任何一種任務計畫
                   - 需要輸出 error_message，用一句簡短中文說明原因
                   - object、target、reference_object、direction、distance_cm 必須全部是 null
                   - 應該輸出 error 的情況（符合任一項即可）：
                     a. 使用者提到的物件名稱不在「可選擇的物件名稱」清單中，且找不到任何語意相近的清單物件可以合理對應
                     b. 使用者的指令看不出對應到 pick_and_place / move_relative / place_relative 三種之一的明確動作意圖
                     c. 使用者給的距離數字為負值、為 0，或明顯不合理（例如超過 200 公分）
                     d. 指令語意不完整，缺少必要資訊且無法用預設值合理補上（例如只說「移動」但完全沒有方向、也沒有目標物件）
                   - 遇到上述情況時，絕對不可以自己挑一個清單中存在的物件或方向來代替，必須輸出 error。

                規則：
                - action 只能是 "pick_and_place"、"move_relative"、"place_relative" 或 "error"。
                - object 必須從 Part B 提供的物件名稱清單中選擇。
                - pick_and_place 的 target 必須從物件清單中選擇。
                - place_relative 的 reference_object 必須從物件清單中選擇，且不可與 object 相同。
                - move_relative 與 place_relative 的 direction 只能是 left、right、forward、backward、up、down，依下列語意判斷，不限於固定詞組：
                  - 表示「左」方向的詞（例如：左移、向左、往左、左邊、左側、移到左邊、靠左、左挪…）→ direction=left
                  - 表示「右」方向的詞（例如：右移、向右、往右、右邊、右側、移到右邊、靠右、右挪…）→ direction=right
                  - 表示「前」方向的詞（例如：往前、向前、前面、前方、前移…）→ direction=forward
                  - 表示「後」方向的詞（例如：往後、向後、後面、後方、後退、退後…）→ direction=backward
                  - 表示「上」方向的詞（例如：往上、向上、上面、上方、抬高、舉高…）→ direction=up
                  - 表示「下」方向的詞（例如：往下、向下、下面、下方、放低、降低…）→ direction=down
                  - 上述僅為範例，請依語意理解使用者真實意圖判斷方向，不要求逐字匹配。
                - distance_cm 規則（適用 move_relative 與 place_relative）：
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
                - action 判斷的核心原則（依序判斷）：
                  1. 若指令同時出現「第二個物件名稱」+「方向詞」（含或不含距離）
                     → 一律是 place_relative（object=要搬動的物件，reference_object=參考物件）
                     → 例如「把杯子往左移到盤子旁邊」、「把手機放到書本前方 8 公分」
                  2. 若指令只有方向詞、沒有第二個物件
                     → 是 move_relative（例如「把杯子往左移動 10 公分」）
                  3. 若指令是「把 A 放到 B 旁邊/上面/裡面」這種兩個物件之間的擺放，且沒有方向詞
                     → 是 pick_and_place（例如「把杯子放到書本上面」）
                  4. 若以上三種都不符合，或符合前面列出的任一 error 情況
                     → 是 error
                - 最後只能輸出符合 JSON schema 的 JSON，不要加任何解釋文字。
                """
            ),
            new UserChatMessage(
                $"""
                使用者指令：
                {userCommand}

                Part B 根據 YOLO 與三個 QR code 計算出的場景物件資料：
                {JsonSerializer.Serialize(sceneObjects)}

                可選擇的物件名稱：
                {JsonSerializer.Serialize(objectNames)}
                """
            )
        };

        ChatCompletion completion = await _client.CompleteChatAsync(messages, options);

        string json = completion.Content[0].Text;

        LlmRobotPlanResult? llmResult =
            JsonSerializer.Deserialize<LlmRobotPlanResult>(json);

        if (llmResult == null)
            throw new InvalidOperationException("Failed to parse LLM response.");

        return BuildRobotPlan(llmResult, sceneObjects);
    }

    private static RobotPlan BuildRobotPlan(
        LlmRobotPlanResult llmResult,
        List<SceneObject> sceneObjects
    )
    {
        if (llmResult.Action == "error")
        {
            return new RobotPlan
            {
                Action = "error",
                Object = string.Empty,
                Target = null,
                Direction = null,
                DistanceCm = null,
                ObjectPosition = null,
                TargetPosition = null,
                ErrorMessage = string.IsNullOrWhiteSpace(llmResult.ErrorMessage)
                    ? "指令無法辨識或物件不存在。"
                    : llmResult.ErrorMessage
            };
        }

        SceneObject objectPosition = FindSceneObject(sceneObjects, llmResult.Object);

        if (llmResult.Action == "pick_and_place")
        {
            if (string.IsNullOrWhiteSpace(llmResult.Target))
                throw new InvalidOperationException("pick_and_place requires target.");

            if (!string.IsNullOrWhiteSpace(llmResult.ReferenceObject))
                throw new InvalidOperationException("pick_and_place must not have reference_object.");

            SceneObject targetPosition = FindSceneObject(sceneObjects, llmResult.Target);

            return new RobotPlan
            {
                Action = llmResult.Action,
                Object = llmResult.Object,
                Target = llmResult.Target,
                Direction = null,
                DistanceCm = null,
                ObjectPosition = objectPosition,
                TargetPosition = targetPosition
            };
        }

        if (llmResult.Action == "move_relative")
        {
            if (string.IsNullOrWhiteSpace(llmResult.Direction))
                throw new InvalidOperationException("move_relative requires direction.");

            if (llmResult.DistanceCm == null || llmResult.DistanceCm <= 0)
                throw new InvalidOperationException("move_relative requires positive distance_cm.");

            SceneObject targetPosition = CalculateRelativeTargetPosition(
                objectPosition,
                llmResult.Direction,
                llmResult.DistanceCm.Value
            );

            return new RobotPlan
            {
                Action = llmResult.Action,
                Object = llmResult.Object,
                Target = null,
                Direction = llmResult.Direction,
                DistanceCm = llmResult.DistanceCm,
                ObjectPosition = objectPosition,
                TargetPosition = targetPosition
            };
        }

        if (llmResult.Action == "place_relative")
        {
            if (string.IsNullOrWhiteSpace(llmResult.ReferenceObject))
                throw new InvalidOperationException("place_relative requires reference_object.");

            if (string.Equals(llmResult.ReferenceObject, llmResult.Object, StringComparison.Ordinal))
                throw new InvalidOperationException("place_relative reference_object cannot equal object.");

            if (string.IsNullOrWhiteSpace(llmResult.Direction))
                throw new InvalidOperationException("place_relative requires direction.");

            if (llmResult.DistanceCm == null || llmResult.DistanceCm <= 0)
                throw new InvalidOperationException("place_relative requires positive distance_cm.");

            SceneObject referencePosition = FindSceneObject(sceneObjects, llmResult.ReferenceObject);

            // 目標位置 = 參考物件位置 + direction * distance
            SceneObject targetPosition = CalculateRelativeTargetPosition(
                referencePosition,
                llmResult.Direction,
                llmResult.DistanceCm.Value
            );

            return new RobotPlan
            {
                Action = llmResult.Action,
                Object = llmResult.Object,
                Target = llmResult.ReferenceObject,   // 記錄參考物件名稱以利 debug
                Direction = llmResult.Direction,
                DistanceCm = llmResult.DistanceCm,
                ObjectPosition = objectPosition,
                TargetPosition = targetPosition
            };
        }

        throw new InvalidOperationException($"Unsupported action: {llmResult.Action}");
    }

    private static SceneObject CalculateRelativeTargetPosition(
        SceneObject original,
        string direction,
        double distanceCm
    )
    {
        double x = original.X;
        double y = original.Y;
        double z = original.Z;

        // Coordinate convention:
        // left/right  -> X axis
        // forward/backward -> Y axis
        // up/down -> Z axis
        // If Part B uses a different coordinate system, only modify this mapping.
        double distanceM = distanceCm / 100.0;

        switch (direction)
        {
            case "left":
                y -= distanceM;
                break;
            case "right":
                y += distanceM;
                break;
            case "forward":
                x -= distanceM;
                break;
            case "backward":
                x += distanceM;
                break;
            case "up":
                z += distanceM;
                break;
            case "down":
                z -= distanceM;
                break;
            default:
                throw new InvalidOperationException($"Unsupported direction: {direction}");
        }

        return new SceneObject
        {
            Name = original.Name + "_target",
            X = x,
            Y = y,
            Z = z
        };
    }

    private static SceneObject FindSceneObject(List<SceneObject> sceneObjects, string name)
    {
        SceneObject? result = sceneObjects.FirstOrDefault(o => o.Name == name);

        if (result == null)
            throw new InvalidOperationException($"Object '{name}' was not found in scene objects.");

        return result;
    }

    private static string CreateJsonSchema(List<string> objectNames)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["action"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { "pick_and_place", "move_relative", "place_relative", "error" }
                },
                ["object"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" },
                    ["enum"] = objectNames.Cast<object?>().Append(null).ToArray()
                },
                ["target"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" },
                    ["enum"] = objectNames.Cast<object?>().Append(null).ToArray()
                },
                ["reference_object"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" },
                    ["enum"] = objectNames.Cast<object?>().Append(null).ToArray()
                },
                ["direction"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" },
                    ["enum"] = new object?[]
                    {
                        "left",
                        "right",
                        "forward",
                        "backward",
                        "up",
                        "down",
                        null
                    }
                },
                ["distance_cm"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "number", "null" }
                },
                ["error_message"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" }
                }
            },
            ["required"] = new[]
            {
                "action",
                "object",
                "target",
                "reference_object",
                "direction",
                "distance_cm",
                "error_message"
            }
        };

        return JsonSerializer.Serialize(schema);
    }
}