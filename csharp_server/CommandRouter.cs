using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

/// <summary>
/// Routes a natural-language command before the task-specific planners run.
/// The LLM identifies intent and entities only; C# computes every coordinate.
/// </summary>
public sealed class CommandRouter
{
    private readonly ChatClient _client;

    public CommandRouter(string model = "gpt-5")
    {
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        _client = new ChatClient(model, apiKey);
    }

    public async Task<RoutedCommand> RouteAsync(string userCommand, IReadOnlyList<SceneObject> scene)
    {
        if (string.IsNullOrWhiteSpace(userCommand))
            throw new ArgumentException("Command is empty.", nameof(userCommand));

        var objectNames = scene.Select(x => x.Name).Distinct().OrderBy(x => x).ToArray();
        if (objectNames.Length == 0)
            throw new InvalidOperationException("Scene contains no selectable objects.");

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "routed_robot_command",
                jsonSchema: BinaryData.FromString(BuildSchema(objectNames)),
                jsonSchemaIsStrict: true)
        };

        var indexedScene = scene.Select((item, index) => new { index, item }).ToList();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                """
                You route Chinese natural-language commands for a UR3e block system.

                Supported actions:
                - arrange_pattern: arrange blocks into a bitmap pattern, such as 排 H or 排一個十字。
                - move_relative: move one existing block left/right/forward/backward by a distance.
                - stack: put one existing block on top of another existing block.

                Rules:
                - Select object_name and reference_object_name only from the supplied scene names.
                - For arrange_pattern, all object/direction/distance fields must be null.
                - For move_relative, object_name, direction and distance_cm are required; reference is null.
                - If move distance is omitted, use 5 cm.
                - For stack, object_name is the block being moved and reference_object_name is the lower block.
                - For stack, direction and distance are null.
                - Never output coordinates or robot motions.
                Return only schema-valid JSON.
                """),
            new UserChatMessage($$"""
                User command: {{userCommand}}

                Indexed scene objects:
                {{JsonSerializer.Serialize(indexedScene)}}
                """)
        };

        ChatCompletion completion = await _client.CompleteChatAsync(messages, options);
        return JsonSerializer.Deserialize<RoutedCommand>(completion.Content[0].Text)
               ?? throw new InvalidOperationException("Command router response parse failed.");
    }

    private static string BuildSchema(string[] objectNames)
    {
        object?[] nullableNames = objectNames.Cast<object?>().Append(null).ToArray();
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["action"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { "arrange_pattern", "move_relative", "stack" }
                },
                ["object_name"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" }, ["enum"] = nullableNames
                },
                ["reference_object_name"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" }, ["enum"] = nullableNames
                },
                ["direction"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" },
                    ["enum"] = new object?[] { "left", "right", "forward", "backward", null }
                },
                ["distance_cm"] = new Dictionary<string, object?> { ["type"] = new[] { "number", "null" } },
                ["reasoning"] = new Dictionary<string, object?> { ["type"] = "string" }
            },
            ["required"] = new[]
            {
                "action", "object_name", "reference_object_name", "direction", "distance_cm", "reasoning"
            }
        };
        return JsonSerializer.Serialize(schema);
    }
}

public sealed class RoutedCommand
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("object_name")]
    public string? ObjectName { get; set; }

    [JsonPropertyName("reference_object_name")]
    public string? ReferenceObjectName { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("distance_cm")]
    public double? DistanceCm { get; set; }

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = "";
}
