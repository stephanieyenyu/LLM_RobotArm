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
                Use a fixed, canonical viewing convention: the requested glyph is read from the front,
                +Z is visually upward, the table is the bottom support edge, +X runs left-to-right,
                and depth/Y runs front-to-back. Never rotate, mirror, turn sideways, or invert the
                requested glyph merely to make it physically feasible.
                Represent it as column_heights: an exactly {{maxRows}} by {{maxCols}} integer matrix.
                Each value N means a contiguous vertical column of N cubes starting on the table; gaps and overhangs are impossible.
                The available voxel volume is exactly {{maxRows}} x {{maxCols}} x {{maxLayers}}; every height must be 0..{{maxLayers}}, and the pattern may use at most {{cubeBudget}} cubes total.
                Decide feasibility first. A front-view pixel at height z can exist only when every voxel below it in the same column also exists. If that monotone, ground-supported silhouette cannot preserve the requested glyph, immediately return feasible=false; do not keep searching for a forced approximation.
                Prioritize human recognizability when viewed from the front. Do not use a supplied font, template, target-specific rule, or hardcoded glyph; none is provided.
                If the requested form cannot remain recognizable under continuous vertical support, size, height, and inventory constraints, return feasible=false rather than changing it into another form.
                For feasible=true, view_direction must be front, up_axis must be +z,
                support_edge must be bottom, rotation_deg must be 0, and mirrored must be false.
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
        ValidateDeclaredOrientation(raw);
        int[,] heights = ParseAndValidate(raw.ColumnHeights, cubeBudget);
        await ValidateFrontViewAsync(command, heights);
        return new SpatialPattern
        {
            PatternId = string.IsNullOrWhiteSpace(raw.PatternId) ? command : raw.PatternId,
            ColumnHeights = heights,
            BlockColor = color,
        };
    }

    static void ValidateDeclaredOrientation(SpatialResult raw)
    {
        if (!string.Equals(raw.ViewDirection, "front", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(raw.UpAxis, "+z", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(raw.SupportEdge, "bottom", StringComparison.OrdinalIgnoreCase) ||
            raw.RotationDeg != 0 || raw.Mirrored)
        {
            throw new SpatialPatternInfeasibleException(
                "方向檢查失敗：圖形必須以正面閱讀、+Z 朝上、底邊支撐，且不得旋轉或鏡射。");
        }
    }

    async Task ValidateFrontViewAsync(string command, int[,] heights)
    {
        string frontBitmap = BuildFrontBitmap(heights);
        Console.WriteLine("[3D orientation] fixed front view (+Z up, table at bottom):");
        foreach (string row in frontBitmap.Split('\n'))
            Console.WriteLine("                 " + row);

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "spatial_orientation_review",
                jsonSchema: BinaryData.FromString(BuildOrientationReviewSchema()),
                jsonSchemaIsStrict: true),
        };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                """
                Independently review a voxel glyph in its fixed physical orientation.
                Read the bitmap exactly as displayed: top row is highest +Z, bottom row
                touches the table, columns run left-to-right, and the viewer looks from
                the front. Do not rotate, mirror, transpose, invert, or reinterpret the
                bitmap. Decide whether it clearly represents the exact text or symbol
                requested by the user in that orientation. If it resembles the requested
                glyph only after any rotation or mirroring, set accept=false and identify
                that transformation. Do not substitute a different glyph merely because
                it is physically supportable. Return only schema-valid JSON.
                """),
            new UserChatMessage($$"""
                Original request: {{command}}

                Fixed front-view bitmap (1=voxel, 0=empty):
                {{frontBitmap}}
                """),
        };

        ChatCompletion completion = await CompleteWithProgressAsync(
            messages, options, "[3D orientation] reviewer");
        string responseText = completion.Content.Count > 0
            ? completion.Content[0].Text
            : "";
        var review = JsonSerializer.Deserialize<OrientationReview>(responseText)
                     ?? throw new InvalidOperationException(
                         "3D orientation review response parse failed.");
        Console.WriteLine(
            $"[3D orientation] accept={review.Accept}, recognizable={review.Recognizable}, " +
            $"rotation={review.RequiresRotation}, mirrored={review.RequiresMirroring}, " +
            $"observed={review.ObservedAs}");
        if (!review.Accept || !review.Recognizable ||
            review.RequiresRotation || review.RequiresMirroring)
        {
            string reason = string.IsNullOrWhiteSpace(review.FailureReason)
                ? "固定正面方向下無法清楚辨識為指定字形。"
                : review.FailureReason;
            throw new SpatialPatternInfeasibleException(
                "方向與字形檢查未通過：" + reason);
        }
    }

    string BuildFrontBitmap(int[,] heights)
    {
        var lines = new List<string>();
        // With one Y/depth row, each X column height directly defines the fixed
        // front silhouette. Keep this projection deterministic and unrotated.
        for (int z = maxLayers; z >= 1; z--)
        {
            var line = new char[heights.GetLength(1)];
            for (int x = 0; x < heights.GetLength(1); x++)
            {
                bool occupied = false;
                for (int y = 0; y < heights.GetLength(0); y++)
                    occupied |= heights[y, x] >= z;
                line[x] = occupied ? '1' : '0';
            }
            lines.Add(new string(line));
        }
        return string.Join("\n", lines);
    }

    async Task<ChatCompletion> CompleteWithProgressAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatCompletionOptions options,
        string label)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            var requestTask = client.CompleteChatAsync(messages, options, timeout.Token);
            int reportedSeconds = 0;
            while (!requestTask.IsCompleted && !timeout.IsCancellationRequested)
            {
                Task finished = await Task.WhenAny(
                    requestTask, Task.Delay(TimeSpan.FromSeconds(10), timeout.Token));
                if (finished == requestTask || timeout.IsCancellationRequested) break;
                reportedSeconds += 10;
                Console.WriteLine(
                    $"{label} 仍在判斷，已等待 " +
                    $"{reportedSeconds}/{RequestTimeout.TotalSeconds:F0} 秒...");
            }
            return await requestTask;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{label} 逾時：LLM 在 {RequestTimeout.TotalSeconds:F0} 秒內沒有回應。");
        }
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
            ["view_direction"] = new { type = "string", @enum = new[] { "front" } },
            ["up_axis"] = new { type = "string", @enum = new[] { "+z" } },
            ["support_edge"] = new { type = "string", @enum = new[] { "bottom" } },
            ["rotation_deg"] = new { type = "integer", @enum = new[] { 0 } },
            ["mirrored"] = new { type = "boolean" },
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
        required = new[]
        {
            "pattern_id", "feasible", "failure_reason", "view_direction",
            "up_axis", "support_edge", "rotation_deg", "mirrored", "column_heights"
        },
    });

    static string BuildOrientationReviewSchema() => JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["accept"] = new { type = "boolean" },
            ["recognizable"] = new { type = "boolean" },
            ["observed_as"] = new { type = "string" },
            ["requires_rotation"] = new { type = "boolean" },
            ["requires_mirroring"] = new { type = "boolean" },
            ["failure_reason"] = new { type = "string" },
        },
        required = new[]
        {
            "accept", "recognizable", "observed_as", "requires_rotation",
            "requires_mirroring", "failure_reason"
        },
    });

    sealed class SpatialResult
    {
        [JsonPropertyName("pattern_id")] public string PatternId { get; set; } = "";
        [JsonPropertyName("feasible")] public bool Feasible { get; set; }
        [JsonPropertyName("failure_reason")] public string FailureReason { get; set; } = "";
        [JsonPropertyName("view_direction")] public string ViewDirection { get; set; } = "";
        [JsonPropertyName("up_axis")] public string UpAxis { get; set; } = "";
        [JsonPropertyName("support_edge")] public string SupportEdge { get; set; } = "";
        [JsonPropertyName("rotation_deg")] public int RotationDeg { get; set; }
        [JsonPropertyName("mirrored")] public bool Mirrored { get; set; }
        [JsonPropertyName("column_heights")] public List<List<int>> ColumnHeights { get; set; } = new();
    }

    sealed class OrientationReview
    {
        [JsonPropertyName("accept")] public bool Accept { get; set; }
        [JsonPropertyName("recognizable")] public bool Recognizable { get; set; }
        [JsonPropertyName("observed_as")] public string ObservedAs { get; set; } = "";
        [JsonPropertyName("requires_rotation")] public bool RequiresRotation { get; set; }
        [JsonPropertyName("requires_mirroring")] public bool RequiresMirroring { get; set; }
        [JsonPropertyName("failure_reason")] public string FailureReason { get; set; } = "";
    }
}

public sealed class SpatialPatternInfeasibleException : Exception
{
    public SpatialPatternInfeasibleException(string message) : base(message) { }
}
