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

                支援五種 action：
                1. pick_and_place
                   - 表示把某個物件拿起來，放到另一個物件的位置上
                   - 需要輸出 object 和 target
                   - reference_object、direction、distance_cm、pattern、block_color、bitmap 必須是 null
                2. move_relative
                   - 表示把某個物件往某個方向移動指定距離（以物件自己現在的位置為基準）
                   - 需要輸出 object、direction、distance_cm
                   - target、reference_object、pattern、block_color、bitmap 必須是 null
                3. place_relative
                   - 表示把某個物件拿起來，放到「另一個參考物件」的某個方向、某個距離處
                   - 需要輸出 object、reference_object、direction、distance_cm
                   - target、pattern、block_color、bitmap 必須是 null
                   - 例如「把手機放到杯子左邊 15 公分」→ object=cell phone, reference_object=cup, direction=left, distance_cm=15
                4. arrange_pattern
                   - 表示使用多個積木排列出指定文字或圖案
                   - 需要輸出：
                       pattern（圖案描述文字，例如 "H"、"Heart"、"Arrow"、"OpenAI"）
                       block_color（"yellow" 或 "black"）
                       bitmap（二維圖案，字串陣列）
                   - bitmap 規則：
                     - 是字串陣列
                     - 每一列只能包含 0 或 1（1 = 放積木、0 = 留白）
                     - 每一列長度必須完全一致
                     - bitmap 最大不可超過 5 × 5（超過會被 PlacementPlanner 拒絕）
                     - 請直接生成 bitmap，不可以假設 C# 已經知道 H 或愛心長什麼樣
                   - block_color 只能是 "yellow" 或 "black"；未指定時預設 "yellow"
                   - object、target、reference_object、direction、distance_cm 全部必須是 null

                   例如「用黑色積木排 H」→
                   {
                     "action": "arrange_pattern",
                     "pattern": "H",
                     "block_color": "black",
                     "bitmap": [
                       "10001",
                       "10001",
                       "11111",
                       "10001",
                       "10001"
                     ]
                   }
                5. error
                   - 表示指令無法執行（見下方「何時回傳 error」）
                   - object、target、reference_object、direction、distance_cm、pattern、block_color、bitmap 全部為 null
                   - error_message 必須填一句**中文**說明為什麼不能執行、簡短明確、能讓使用者理解

                何時回傳 error（優先於前四種）：
                - 使用者指令中的物件**不在物件清單裡**（例如清單只有 cup、cell phone，使用者說「把飛機推倒」）
                  → error_message 例：「場景中沒有偵測到『飛機』這個物件，目前可操作的物件是 cup、cell phone。」
                - 移動距離**過大或不合理**（例如「往下移 1 公尺」會撞穿桌面、「往上 3 公尺」超出手臂可達範圍）
                  → error_message 例：「往下移動 100 公分會撞穿桌面，機械手臂無法執行。」
                - 使用者要求把物件放到**桌面以下、桌面外、或機械手臂不可達的位置**
                  → error_message 例：「無法把物件放到桌子下面，這超出機械手臂的工作平面。」
                - 使用者要求**負距離**、**同一個物件疊到自己身上**、**兩個 place_relative 物件相同**等邏輯錯誤
                  → error_message 例：「不能把 A 放到 A 自己旁邊。」
                - 指令**語意完全無法解析**成任何一種 action（例如「幫我唱歌」、「你叫什麼名字」）
                  → error_message 例：「這不是機械手臂可以執行的動作指令。」
                - 使用者提到**危險或損害性的操作**（打人、砸東西、撞壞）
                  → error_message 例：「基於安全考量無法執行此動作。」

                規則：
                - action 只能是 "pick_and_place"、"move_relative"、"place_relative"、"arrange_pattern" 或 "error"。
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
                  1. 若指令是「拚出 / 排出 / 擺出 XXX（字母、形狀、直線）」這類「用多顆積木組成圖形」的意圖
                     → 是 arrange_pattern（例如「拚出 H」、「排一條 5 顆的線」、「拚一個方框」）
                  2. 若指令同時出現「第二個物件名稱」+「方向詞」（含或不含距離）
                     → 一律是 place_relative（object=要搬動的物件，reference_object=參考物件）
                     → 例如「把杯子往左移到盤子旁邊」、「把手機放到書本前方 8 公分」
                  3. 若指令只有方向詞、沒有第二個物件
                     → 是 move_relative（例如「把杯子往左移動 10 公分」）
                  4. 若指令是「把 A 放到 B 旁邊/上面/裡面」這種兩個物件之間的擺放，且沒有方向詞
                     → 是 pick_and_place（例如「把杯子放到書本上面」）
                  5. 若上述任一條件的物件不存在、動作無法執行、或指令不合理 → 回傳 error
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
        // error action：不執行動作，只回傳說明給 console + Unity
        if (llmResult.Action == "error")
        {
            return new RobotPlan
            {
                Action = "error",
                Object = "",
                Target = null,
                Direction = null,
                DistanceCm = null,
                ObjectPosition = null,
                TargetPosition = null,
                ErrorMessage = string.IsNullOrWhiteSpace(llmResult.ErrorMessage)
                    ? "指令無法執行（未提供原因）"
                    : llmResult.ErrorMessage,
            };
        }

        // arrange_pattern action：用多顆積木拚出 pattern
        // LLM 直接產生 bitmap → BitmapParser 轉 int[,] → PlacementPlanner 排步驟
        if (llmResult.Action == "arrange_pattern")
        {
            if (string.IsNullOrWhiteSpace(llmResult.Pattern))
                throw new InvalidOperationException("arrange_pattern requires pattern.");

            string blockColor = string.IsNullOrWhiteSpace(llmResult.BlockColor)
                ? "yellow"
                : llmResult.BlockColor;

            if (llmResult.Bitmap == null || llmResult.Bitmap.Count == 0)
            {
                return new RobotPlan
                {
                    Action = "error",
                    Object = "",
                    Pattern = llmResult.Pattern,
                    Bitmap = llmResult.Bitmap,
                    BlockColor = blockColor,
                    ErrorMessage = "LLM 沒有產生 bitmap。",
                };
            }

            int[,] bitmap;
            try
            {
                bitmap = BitmapParser.Parse(llmResult.Bitmap);
            }
            catch (Exception ex)
            {
                return new RobotPlan
                {
                    Action = "error",
                    Object = "",
                    Pattern = llmResult.Pattern,
                    Bitmap = llmResult.Bitmap,
                    BlockColor = blockColor,
                    ErrorMessage = $"Bitmap 格式錯誤：{ex.Message}",
                };
            }

            PlacementPlanner.PlanResult planResult =
                PlacementPlanner.Plan(bitmap, sceneObjects, blockColor);

            if (planResult.Error != null)
            {
                return new RobotPlan
                {
                    Action = "error",
                    Object = "",
                    Pattern = llmResult.Pattern,
                    Bitmap = llmResult.Bitmap,
                    BlockColor = blockColor,
                    ErrorMessage = planResult.Error,
                };
            }

            return new RobotPlan
            {
                Action = "arrange_pattern",
                Object = "",
                Pattern = llmResult.Pattern,
                Bitmap = llmResult.Bitmap,
                BlockColor = blockColor,
                PlacementSteps = planResult.Steps,
            };
        }

        if (string.IsNullOrWhiteSpace(llmResult.Object))
            throw new InvalidOperationException($"{llmResult.Action} requires a valid object.");

        SceneObject objectPosition = FindSceneObject(sceneObjects, llmResult.Object);

        if (llmResult.Action == "pick_and_place")
        {
            if (string.IsNullOrWhiteSpace(llmResult.Target))
                throw new InvalidOperationException("pick_and_place requires target.");

            if (!string.IsNullOrWhiteSpace(llmResult.ReferenceObject))
                throw new InvalidOperationException("pick_and_place must not have reference_object.");

            SceneObject targetObj = FindSceneObject(sceneObjects, llmResult.Target);

            // 疊放：目標 z = 目標物件頂面 z + 被搬物件自己的高度
            //（objectPosition.Z 是「被搬物件頂面」，也就是它自己的總高度）
            SceneObject targetPosition = new SceneObject
            {
                Name = targetObj.Name + "_stack",
                X = targetObj.X,
                Y = targetObj.Y,
                Z = targetObj.Z + objectPosition.Z,
            };

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

            // 水平方向（left/right/forward/backward）→ 保留被搬物件自己的高度
            // 避免用參考物件 B 的高度導致把 A 放太高或太低
            if (llmResult.Direction != "up" && llmResult.Direction != "down")
            {
                targetPosition.Z = objectPosition.Z;
            }

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
                    ["enum"] = new[] { "pick_and_place", "move_relative", "place_relative", "arrange_pattern", "error" }
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
                },
                ["pattern"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" }
                },
                ["block_color"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" },
                    ["enum"] = new object?[] { "yellow", "black", null }
                },
                ["bitmap"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "array", "null" },
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string"
                    }
                },
            },
            ["required"] = new[]
            {
                "action",
                "object",
                "target",
                "reference_object",
                "direction",
                "distance_cm",
                "error_message",
                "pattern",
                "block_color",
                "bitmap"
            }
        };

        return JsonSerializer.Serialize(schema);
    }
}
