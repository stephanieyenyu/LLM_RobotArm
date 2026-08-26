using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

// -----------------------------------------------------------------
// Pattern Verifier
// 獨立檢查 PatternDesigner 產生的 bitmap 是否符合使用者原始意圖。
// -----------------------------------------------------------------

public class PatternVerifier
{
    private readonly ChatClient _client;

    public PatternVerifier(string model = "gpt-5")
    {
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");

        _client = new ChatClient(model, apiKey);
    }

    public async Task<PatternVerificationResult> VerifyAsync(string userCommand, CanonicalPattern pattern)
    {
        if (string.IsNullOrWhiteSpace(userCommand))
            throw new ArgumentException("User command is empty.", nameof(userCommand));

        if (pattern.Bitmap == null)
            throw new ArgumentException("Pattern bitmap is missing.", nameof(pattern));

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "pattern_verification",
                jsonSchema: BinaryData.FromString(BuildSchema()),
                jsonSchemaIsStrict: true
            )
        };

        string bitmapText = RenderBitmap(pattern.Bitmap, one: "1", zero: "0");
        string asciiText = RenderBitmap(pattern.Bitmap, one: "■", zero: "□");

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                """
                你是 PatternVerifier，只負責檢查 bitmap 是否符合使用者原始指令。

                檢查規則：
                - bitmap 是低解析度積木圖案，不要求藝術性，但必須讓一般人可辨識主要特徵。
                - 不要因為 pattern_id 或使用者指令寫了某個字，就直接判定正確；只能根據 bitmap 本身判斷。
                - 如果使用者要求中文字，需檢查主要筆畫比例與結構。例如「土」應該上橫較短、下橫較長，中央有直線；若上下橫都很長，較像「工」。
                - 如果圖案更像其他字、數字或形狀，is_correct 必須是 false，recognized_as 填入最像的內容。
                - 如果目前尺寸或積木數量限制下難以清楚表達，is_correct 必須是 false，feedback 說明應拒絕或需要更大 bitmap。
                - feedback 要能直接拿去要求 PatternDesigner 修正。
                只輸出符合 JSON schema 的 JSON，不加解釋文字。
                """
            ),
            new UserChatMessage(
                $"""
                使用者原始指令：
                {userCommand}

                Pattern ID：
                {pattern.PatternId}

                Bitmap：
                {bitmapText}

                ASCII render：
                {asciiText}

                請判斷這個 bitmap 是否符合使用者原始指令。
                """
            )
        };

        ChatCompletion completion = await _client.CompleteChatAsync(messages, options);
        string json = completion.Content[0].Text;

        return JsonSerializer.Deserialize<PatternVerificationResult>(json)
               ?? throw new InvalidOperationException("Pattern verifier response parse failed.");
    }

    private static string RenderBitmap(int[,] bitmap, string one, string zero)
    {
        var rows = new List<string>();
        for (int r = 0; r < bitmap.GetLength(0); r++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < bitmap.GetLength(1); c++)
                sb.Append(bitmap[r, c] == 1 ? one : zero);
            rows.Add(sb.ToString());
        }
        return string.Join(Environment.NewLine, rows);
    }

    private static string BuildSchema()
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["is_correct"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                ["confidence"] = new Dictionary<string, object?>
                {
                    ["type"] = "number",
                    ["minimum"] = 0,
                    ["maximum"] = 1
                },
                ["recognized_as"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["reason"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["feedback"] = new Dictionary<string, object?> { ["type"] = "string" }
            },
            ["required"] = new[]
            {
                "is_correct", "confidence", "recognized_as", "reason", "feedback"
            }
        };
        return JsonSerializer.Serialize(schema);
    }
}

public class PatternVerificationResult
{
    [JsonPropertyName("is_correct")]
    public bool IsCorrect { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("recognized_as")]
    public string RecognizedAs { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("feedback")]
    public string Feedback { get; set; } = "";
}
