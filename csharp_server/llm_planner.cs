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
        SceneObject objectPosition = FindSceneObject(sceneObjects, llmResult.Object);

        if (llmResult.Action == "pick_and_place")
        {
            if (string.IsNullOrWhiteSpace(llmResult.Target))
                throw new InvalidOperationException("pick_and_place requires target.");

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
                x += distanceM;
                break;
            case "right":
                x -= distanceM;
                break;
            case "forward":
                y -= distanceM;
                break;
            case "backward":
                y += distanceM;
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
                    ["enum"] = new[] { "pick_and_place", "move_relative" }
                },
                ["object"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = objectNames
                },
                ["target"] = new Dictionary<string, object?>
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
                }
            },
            ["required"] = new[]
            {
                "action",
                "object",
                "target",
                "direction",
                "distance_cm"
            }
        };

        return JsonSerializer.Serialize(schema);
    }
}
