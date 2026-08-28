using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

/// <summary>
/// Designs a self-supporting upright voxel glyph. Feasibility is proposed by
/// the LLM and then enforced by deterministic size, support and inventory checks.
/// </summary>
public sealed class SpatialPatternDesigner
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(300);
    readonly ChatClient client;
    readonly int maxRows, maxCols, maxLayers;

    public SpatialPatternDesigner(int maxRows, int maxCols, int maxLayers, string model = "gpt-5")
    {
        string? key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        client = new ChatClient(model, key);
        this.maxRows = maxRows;
        this.maxCols = maxCols;
        this.maxLayers = maxLayers;
    }

    public async Task<SpatialPattern> DesignAsync(
        string command, string color, int cubeBudget)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "spatial_pattern",
                jsonSchema: BinaryData.FromString(BuildSchema()),
                jsonSchemaIsStrict: true),
        };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($$"""
                Design an upright, physically self-supporting 3D voxel rendering of the requested text or symbol.
                Represent it as column_heights: an exactly {{maxRows}} by {{maxCols}} integer matrix viewed from above.
                Each value N means a contiguous vertical column of N cubes starting on the table; gaps and overhangs are impossible.
                The available voxel volume is exactly {{maxRows}} x {{maxCols}} x {{maxLayers}}; every height must be 0..{{maxLayers}}, and the pattern may use at most {{cubeBudget}} cubes total.
                Decide feasibility first. A front-view pixel at height z can exist only when every voxel below it in the same column also exists. If that monotone, ground-supported silhouette cannot preserve the requested glyph, immediately return feasible=false; do not keep searching for a forced approximation.
                Prioritize human recognizability when viewed from the front. Do not use a supplied font, template, target-specific rule, or hardcoded glyph; none is provided.
                If the requested form cannot remain recognizable under continuous vertical support, size, height, and inventory constraints, return feasible=false rather than changing it into another form.
                feasible=false requires an empty column_heights array and a clear failure_reason.
                feasible=true requires an empty failure_reason.
                Return only schema-valid JSON.
                """),
            new UserChatMessage($"Original command: {command}\nBlock color: {color}"),
        };
        ChatCompletion completion;
        using (var timeout = new CancellationTokenSource(RequestTimeout))
        {
            try
            {
                var requestTask = client.CompleteChatAsync(
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
                        $"[3D Layer 1] LLM 仍在判斷，已等待 " +
                        $"{reportedSeconds}/{RequestTimeout.TotalSeconds:F0} 秒...");
                }

                completion = await requestTask;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"3D 可行性判斷逾時：LLM 在 {RequestTimeout.TotalSeconds:F0} 秒內沒有回應。");
            }
        }
        string responseText = completion.Content.Count > 0
            ? completion.Content[0].Text
            : "";
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException(
                $"LLM 未回傳 3D JSON（finish_reason={completion.FinishReason}）。" +
                "這是模型輸出或 token 額度問題，不代表字形不可行。");
        }

        var raw = JsonSerializer.Deserialize<SpatialResult>(responseText)
                  ?? throw new InvalidOperationException("3D pattern response parse failed.");
        if (!raw.Feasible)
            throw new SpatialPatternInfeasibleException(
                "此立體字在目前支撐與尺寸限制下不可行：" + raw.FailureReason);
        int[,] heights = ParseAndValidate(raw.ColumnHeights, cubeBudget);
        return new SpatialPattern
        {
            PatternId = string.IsNullOrWhiteSpace(raw.PatternId) ? command : raw.PatternId,
            ColumnHeights = heights,
            BlockColor = color,
        };
    }

    int[,] ParseAndValidate(List<List<int>> rows, int cubeBudget)
    {
        if (rows.Count == 0 || rows[0].Count == 0)
            throw new InvalidOperationException("3D pattern has no columns.");
        int cols = rows[0].Count;
        if (rows.Count != maxRows || cols != maxCols || rows.Any(r => r.Count != maxCols))
            throw new InvalidOperationException(
                $"3D pattern must be exactly {maxRows}x{maxCols}, with heights 0..{maxLayers}.");
        var result = new int[rows.Count, cols];
        int total = 0;
        for (int r = 0; r < rows.Count; r++)
        for (int c = 0; c < cols; c++)
        {
            int height = rows[r][c];
            if (height < 0 || height > maxLayers)
                throw new InvalidOperationException($"Column r{r}c{c} height {height} exceeds 0..{maxLayers}.");
            result[r, c] = height;
            total += height;
        }
        if (total == 0) throw new InvalidOperationException("3D pattern is empty.");
        if (total > cubeBudget)
            throw new InvalidOperationException($"3D pattern needs {total} cubes but only {cubeBudget} are available.");
        return result;
    }

    string BuildSchema() => JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["pattern_id"] = new { type = "string" },
            ["feasible"] = new { type = "boolean" },
            ["failure_reason"] = new { type = "string" },
            ["column_heights"] = new
            {
                // Empty is valid only for feasible=false. ParseAndValidate enforces
                // exactly maxRows x maxCols whenever feasible=true.
                type = "array", minItems = 0, maxItems = maxRows,
                items = new
                {
                    type = "array", minItems = maxCols, maxItems = maxCols,
                    items = new { type = "integer", minimum = 0, maximum = maxLayers },
                },
            },
        },
        required = new[] { "pattern_id", "feasible", "failure_reason", "column_heights" },
    });

    sealed class SpatialResult
    {
        [JsonPropertyName("pattern_id")] public string PatternId { get; set; } = "";
        [JsonPropertyName("feasible")] public bool Feasible { get; set; }
        [JsonPropertyName("failure_reason")] public string FailureReason { get; set; } = "";
        [JsonPropertyName("column_heights")] public List<List<int>> ColumnHeights { get; set; } = new();
    }
}

public sealed class SpatialPatternInfeasibleException : Exception
{
    public SpatialPatternInfeasibleException(string message) : base(message) { }
}
