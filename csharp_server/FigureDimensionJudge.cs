using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

/// <summary>
/// 設計目標圖形之前，請 LLM 判斷使用者指令要排的是平面（2d：PatternDesigner 畫 0/1 bitmap）還是立體
/// （3d：SpatialPatternDesigner 畫高度圖）。只問這一件事：不分類動作、不給支援的動作清單，程式也沒有關鍵字規則（2026-10-07）。
/// </summary>
public sealed class FigureDimensionJudge
{
    const string SystemPrompt =
        "判斷使用者指令要在桌面上排出的東西是平面的還是立體的。" +
        "2d：積木都直接放在桌面上、只有一層，從正上方看排出要求的樣子；3d：積木要往上疊、不只一層，排成立起來的結構。" +
        "只依指令本身的意思判斷。只回傳符合 schema 的 JSON。";
    readonly ChatClient client;

    public FigureDimensionJudge(string model)
    {
        string? key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        client = new ChatClient(model, key);
    }

    /// <summary>回傳 ("2d" 或 "3d", LLM 的理由)。呼叫或解析失敗時丟出例外，由呼叫端當基礎設施錯誤處理。</summary>
    public async Task<(string Dimension, string Reason)> JudgeAsync(string command)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "figure_dimension",
                jsonSchema: BinaryData.FromString(Schema),
                jsonSchemaIsStrict: true),
        };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage($"使用者指令：{command}"),
        };
        ChatCompletion completion = await client.CompleteChatAsync(messages, options);
        string text = completion.Content.Count > 0 ? completion.Content[0].Text : "";
        DesignLog.Write("dimension", messages, text);
        var result = JsonSerializer.Deserialize<Result>(text)
                     ?? throw new JsonException("2D/3D 判斷的回覆是空的。");
        if (result.Dimension is not ("2d" or "3d"))
            throw new JsonException($"2D/3D 判斷的回覆不是 2d 或 3d：{result.Dimension}");
        return (result.Dimension, result.Reason);
    }

    static readonly string Schema = JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["dimension"] = new { type = "string", @enum = new[] { "2d", "3d" } },
            ["reason"] = new { type = "string" },
        },
        required = new[] { "dimension", "reason" },
    });

    sealed class Result
    {
        [JsonPropertyName("dimension")] public string Dimension { get; set; } = "";
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }
}
