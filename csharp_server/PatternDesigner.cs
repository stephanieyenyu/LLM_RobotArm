using System.Text.Json;
using OpenAI.Chat;

// -----------------------------------------------------------------
// Layer 1：Pattern Designer
// 只負責一件事：把使用者的自然語言意圖 → canonical bitmap（純圖案）
// 不管 supply 有什麼、不管 target 座標、不管執行順序。
// -----------------------------------------------------------------

public class PatternDesigner
{
    private readonly ChatClient _client;
    private readonly int _maxRows;
    private readonly int _maxCols;

    public PatternDesigner(int maxRows, int maxCols, string model = "gpt-5")
    {
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");

        _client = new ChatClient(model, apiKey);
        _maxRows = maxRows;
        _maxCols = maxCols;
    }

public async Task<CanonicalPattern> DesignAsync(string userCommand, string blockColor = "yellow"int cubeBudget = 0, int dominoBudget = 0)
    {
        if (string.IsNullOrWhiteSpace(userCommand))
            throw new ArgumentException("User command is empty.", nameof(userCommand));

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "pattern_plan",
                jsonSchema: BinaryData.FromString(BuildSchema()),
                jsonSchemaIsStrict: true
            )
        };

int maxCoveredCells = cubeBudget + dominoBudget * 2;
var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                $$"""
                規則：
                 - bitmap 是二維字串陣列，每一列同長度
                 - 每格只能是 "0"（空）或 "1"（有積木）
                 - 大小限制：最多 {{_maxRows}} × {{_maxCols}}
                 - 目前可用 cube 數量：{{cubeBudget}}
                 - 目前可用 domino 數量：{{dominoBudget}}
                 - 一顆 cube 可覆蓋 bitmap 中 1 格
                 - 一顆 domino 可覆蓋 bitmap 中相鄰的 2 格
                 - 請根據可用積木數量自行決定 bitmap 的 rows 和 columns
                 - 不一定要用滿最大尺寸；積木較少時，請生成較小但仍可辨識的圖案
                 - 對稱字母（H、O、X、I、A、T、U、V、W、M、Y）必須完全對稱
                 - 筆劃寬度統一為 1 格
                 - 生成後你必須在 self_verification 欄位裡：
                     a) 檢查對稱性（水平 / 垂直是否對稱）
                     b) render ASCII 圖（■ 代表 1、□ 代表 0）
                     c) 有問題就重畫再輸出
                 只輸出符合 JSON schema 的 JSON，不加解釋文字。
                """
            ),
            new UserChatMessage(
                $"""
                使用者指令：{userCommand}

                目前積木顏色是「{blockColor}」。請根據指令生成 bitmap。
                """
            )
        };

        ChatCompletion completion = await _client.CompleteChatAsync(messages, options);
        string json = completion.Content[0].Text;

        var raw = JsonSerializer.Deserialize<LlmPatternResult>(json)
                  ?? throw new InvalidOperationException("Pattern LLM response parse failed.");

        if (raw.Bitmap == null || raw.Bitmap.Count == 0)
            throw new InvalidOperationException("Pattern LLM returned empty bitmap.");

     int[,] bitmap = BitmapParser.Parse(raw.Bitmap);

     int occupiedCells = BitmapParser.CountOccupiedCells(bitmap);
     if (occupiedCells > maxCoveredCells)
     {
     throw new InvalidOperationException(
         $"LLM 產生的 bitmap 需要 {occupiedCells} 格，但目前可用積木最多只能覆蓋 {maxCoveredCells} 格。"
     );
     }

     return new CanonicalPattern
        {
            PatternId = raw.PatternId ?? userCommand,
            Bitmap = bitmap,
            BlockColor = blockColor,
        };
    }

    private string BuildSchema()
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["pattern_id"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["bitmap"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["type"] = "string" }
                },
                ["self_verification"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["vertical_symmetric"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                        ["horizontal_symmetric"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                        ["ascii_render"] = new Dictionary<string, object?> { ["type"] = "string" },
                    },
                    ["required"] = new[] { "vertical_symmetric", "horizontal_symmetric", "ascii_render" }
                }
            },
            ["required"] = new[] { "pattern_id", "bitmap", "self_verification" }
        };
        return JsonSerializer.Serialize(schema);
    }

    private class LlmPatternResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("pattern_id")]
        public string? PatternId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bitmap")]
        public List<string>? Bitmap { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("self_verification")]
        public JsonElement? SelfVerification { get; set; }
    }
}
