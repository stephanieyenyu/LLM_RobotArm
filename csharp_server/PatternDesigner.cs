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

    public async Task<CanonicalPattern> DesignAsync(
        string userCommand,
        string blockColor = "yellow",
        int cubeBudget = 0,
        int dominoBudget = 0)
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
                 - 積木不足時不要硬生成簡化版，應該回報無法完成
                 - 請根據可用積木數量自行決定 bitmap 的 rows 和 columns
                 - 不一定要用滿最大尺寸；積木較少時，請生成較小但仍可辨識的圖案
                 - 對稱字母（H、O、X、I、A、T、U、V、W、M、Y）必須完全對稱
                 - 筆劃寬度統一為 1 格
                 - 複雜中文字、人物、動物或圖示若在 {{_maxRows}}×{{_maxCols}}
                   與最多 {{maxCoveredCells}} 格內無法保留主要辨識特徵，必須拒絕
                 - feasible=false 時：bitmap 必須是空陣列，failure_reason 說明原因，
                   並提供建議的最小 rows、columns 與 occupied cells
                 - feasible=true 時：failure_reason 必須是空字串，bitmap 才能包含圖案
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

        if (!raw.Feasible)
        {
            throw new InvalidOperationException(
                $"目前限制下無法清楚排出此圖案：{raw.FailureReason} " +
                $"建議至少 {raw.SuggestedRows}×{raw.SuggestedCols}、" +
                $"可覆蓋 {raw.SuggestedOccupiedCells} 格。");
        }

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
                ["feasible"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                ["failure_reason"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["suggested_rows"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["suggested_cols"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["suggested_occupied_cells"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["bitmap"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["type"] = "string" }
                }
            },
            ["required"] = new[]
            {
                "pattern_id", "feasible", "failure_reason", "suggested_rows",
                "suggested_cols", "suggested_occupied_cells", "bitmap"
            }
        };
        return JsonSerializer.Serialize(schema);
    }

    private class LlmPatternResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("pattern_id")]
        public string? PatternId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("feasible")]
        public bool Feasible { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("failure_reason")]
        public string FailureReason { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("suggested_rows")]
        public int SuggestedRows { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("suggested_cols")]
        public int SuggestedCols { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("suggested_occupied_cells")]
        public int SuggestedOccupiedCells { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bitmap")]
        public List<string>? Bitmap { get; set; }

    }
}
