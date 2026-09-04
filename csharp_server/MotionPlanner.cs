using System.Text.Json;
using OpenAI.Chat;

/// <summary>
/// Layer 4A: asks the LLM to compose a motion from a small robot-function API.
/// It never accepts raw URScript, joint angles, speeds, or arbitrary coordinates.
/// </summary>
public sealed class MotionPlanner
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(180);
    private readonly ChatClient _client;

    public MotionPlanner(string model = "gpt-5")
    {
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        _client = new ChatClient(model, apiKey);
    }

    public async Task<MotionPlan> PlanAsync(
        Assignment assignment,
        IReadOnlyList<SceneObject> scene,
        string? previousFeedback = null)
    {
        if (assignment.Source == null || assignment.Target == null)
            throw new ArgumentException("Assignment must contain source and target.");

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "robot_function_plan",
                jsonSchema: BinaryData.FromString(BuildSchema()),
                jsonSchemaIsStrict: true)
        };

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                """
                You are the motion planner for a UR3e pick-and-place system.
                Compose a safe plan using only the supplied high-level robot functions.
                Never output URScript, joint angles, speeds, or coordinates.

                Allowed functions:
                - move_above(location, height_m): move at safe height above source or target
                - descend(location): descend vertically to source or target
                - grasp(): close gripper; only at source after descend(source)
                - release(): open gripper; only at target after descend(target)
                - lift(location, height_m): lift vertically above source or target
                - wait(seconds): wait for settling, 0.1 to 3.0 seconds
                - go_home(): return to the configured home pose; use only when explicitly requested

                Safety rules:
                1. Approach source from above, descend, grasp, then lift before traveling.
                2. Travel to target only while lifted/safe.
                3. Approach target from above, descend, release, then lift.
                4. Do not finish each step with go_home. End after lifting safely above the target
                   so a batch can continue directly into the next step.
                5. height_m must be between 0.05 and 0.15.
                6. Use no more than 20 function calls.
                7. Use at least 0.08 m clearance above both endpoints. Increase it when
                   the perceived scene contains taller blocks, stacks, or obstacles.
                8. Never plan a lateral move while the tool is descended. Never place
                   through an occupied target; an occupied target is legal only for a
                   stack whose target Z is above the perceived lower object.
                Return only schema-valid JSON.
                """),
            new UserChatMessage($$"""
                Assignment:
                {{JsonSerializer.Serialize(assignment)}}

                Latest perceived scene:
                {{JsonSerializer.Serialize(scene)}}

                Previous verification/execution feedback:
                {{previousFeedback ?? "none"}}
                """)
        };

        ChatCompletion completion;
        using (var timeout = new CancellationTokenSource(RequestTimeout))
        {
            try
            {
                var requestTask = _client.CompleteChatAsync(
                    messages, options, timeout.Token);
                int reportedSeconds = 0;
                while (!requestTask.IsCompleted && !timeout.IsCancellationRequested)
                {
                    Task finished = await Task.WhenAny(
                        requestTask,
                        Task.Delay(TimeSpan.FromSeconds(10), timeout.Token));
                    if (finished == requestTask || timeout.IsCancellationRequested)
                        break;

                    reportedSeconds += 10;
                    Console.WriteLine(
                        $"[MotionPlanner] LLM 仍在規劃，已等待 " +
                        $"{reportedSeconds}/{RequestTimeout.TotalSeconds:F0} 秒...");
                }

                completion = await requestTask;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Motion Planner 逾時：LLM 在 " +
                    $"{RequestTimeout.TotalSeconds:F0} 秒內沒有回應。");
            }
        }
        return JsonSerializer.Deserialize<MotionPlan>(completion.Content[0].Text)
               ?? throw new InvalidOperationException("Motion planner response parse failed.");
    }

    private static string BuildSchema()
    {
        var call = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
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

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["action_sequence"] = new Dictionary<string, object?>
                {
                    ["type"] = "array", ["items"] = call
                },
                ["reasoning"] = new Dictionary<string, object?> { ["type"] = "string" }
            },
            ["required"] = new[] { "action_sequence", "reasoning" }
        });
    }
}

