using System.Text.Json;
using OpenAI.Chat;

/// <summary>
/// Plans the order and safe high-level motions for every assignment in one LLM
/// request. Coordinates remain server-owned and never come from the model.
/// </summary>
public sealed class BatchMotionPlanner
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(240);
    private readonly ChatClient _client;

    public BatchMotionPlanner(string model = "gpt-5")
    {
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        _client = new ChatClient(model, apiKey);
    }

    public async Task<BatchMotionPlan> PlanAsync(
        IReadOnlyList<Assignment> assignments,
        IReadOnlyList<SceneObject> scene)
    {
        if (assignments.Count == 0)
            throw new ArgumentException("Batch must contain at least one assignment.");

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "complete_batch_plan",
                jsonSchema: BinaryData.FromString(BuildSchema()),
                jsonSchemaIsStrict: true)
        };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                """
                You plan one complete UR3e pick-and-place batch. Return every supplied
                step_id exactly once, in the safest execution order. Prefer far targets
                before near targets and bottom/supporting placements before upper ones.
                For each step compose only: move_above, descend, grasp, release, lift,
                wait, go_home. Every move_above, descend, and lift call must set location
                to source or target; location may be null only for grasp, release, wait,
                and go_home. Each pick must approach from
                above, descend, grasp, lift, travel while lifted, descend at target,
                release, lift, and finish at home. Clearance must be 0.08-0.15 m.
                Never output coordinates, URScript, joints, velocity, or extra steps.
                """),
            new UserChatMessage($$"""
                Candidate assignments (you may reorder, but not add/remove):
                {{JsonSerializer.Serialize(assignments)}}

                Single scene snapshot used for the entire batch:
                {{JsonSerializer.Serialize(scene)}}
                """)
        };

        using var timeout = new CancellationTokenSource(RequestTimeout);
        ChatCompletion completion;
        try
        {
            completion = await _client.CompleteChatAsync(messages, options, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Batch Planner timed out after 240 seconds.");
        }

        var plan = JsonSerializer.Deserialize<BatchMotionPlan>(completion.Content[0].Text)
            ?? throw new InvalidOperationException("Batch planner response parse failed.");
        NormalizeLocations(plan);
        return plan;
    }

    /// <summary>
    /// The shared function-call schema must allow location=null for grasp,
    /// release, wait, and go_home. Models occasionally copy that null onto a
    /// lift even though its phase makes the endpoint unambiguous. Fill only
    /// these mechanically certain omissions; the validator still rejects an
    /// unsafe order or any genuinely ambiguous call.
    /// </summary>
    private static void NormalizeLocations(BatchMotionPlan plan)
    {
        foreach (BatchMotionStep step in plan.Steps)
        {
            bool holding = false;
            bool released = false;
            foreach (RobotFunctionCall call in step.ActionSequence)
            {
                if (call.Function == "grasp")
                {
                    holding = true;
                    released = false;
                    continue;
                }
                if (call.Function == "release")
                {
                    holding = false;
                    released = true;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(call.Location)) continue;

                if (call.Function == "lift" && holding)
                    call.Location = "source";
                else if (call.Function == "lift" && released)
                    call.Location = "target";
                else if (call.Function is "move_above" or "descend")
                    call.Location = holding ? "target" : "source";
            }
        }
    }

    private static string BuildSchema()
    {
        var call = new Dictionary<string, object?>
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["function"] = new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { "move_above", "descend", "grasp", "release", "lift", "wait", "go_home" }
                },
                ["location"] = new Dictionary<string, object?>
                {
                    ["type"] = new[] { "string", "null" },
                    ["enum"] = new object?[] { "source", "target", null }
                },
                ["height_m"] = new Dictionary<string, object?> { ["type"] = new[] { "number", "null" } },
                ["seconds"] = new Dictionary<string, object?> { ["type"] = new[] { "number", "null" } },
            },
            ["required"] = new[] { "function", "location", "height_m", "seconds" }
        };
        var step = new Dictionary<string, object?>
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["step_id"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["action_sequence"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = call }
            },
            ["required"] = new[] { "step_id", "action_sequence" }
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["steps"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = step },
                ["reasoning"] = new Dictionary<string, object?> { ["type"] = "string" }
            },
            ["required"] = new[] { "steps", "reasoning" }
        });
    }
}
