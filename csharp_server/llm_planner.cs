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
                - move_relative 的 direction 只能是 left、right、forward、backward、up、down。
                - distance_cm 使用公分為單位，只輸出數字。
                - 不可以自己創造不存在的物件名稱。
                - 不可以輸出或編造座標。
                - 物件原始位置與新位置會由 C# 程式根據 Part B 座標計算。
                - 如果中文名稱和英文物件名稱語意相近，請選擇最符合的英文物件名稱。
                - 判斷 action 的強制規則：
                - 「往右移動」、「右移」、「移到右邊」→ action=move_relative, direction=right
                - 「往左移動」、「左移」、「移到左邊」→ action=move_relative, direction=left
                - 「往前」→ action=move_relative, direction=forward
                - 「往後」→ action=move_relative, direction=backward
                - 「往上」→ action=move_relative, direction=up
                - 「往下」→ action=move_relative, direction=down
                - 有公分/cm/距離數字 → 一定是 move_relative，distance_cm 填數字，target 填 null
                - 只有在把物件放到另一個不同物件旁邊時才用 pick_and_place
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
                x -= distanceM;
                break;
            case "right":
                x += distanceM;
                break;
            case "forward":
                y += distanceM;
                break;
            case "backward":
                y -= distanceM;
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
