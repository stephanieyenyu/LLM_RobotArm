using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

/// <summary>
/// Designs a self-supporting upright voxel glyph. Feasibility is proposed by
/// the LLM and then enforced by deterministic size, support and inventory checks.
/// 2026-10-07：pattern審查開著時跟 PatternDesigner 走同一套雙模型流程：OpenAI 與 Gemini 用同一個生成 prompt 各畫一張，
/// 各自用 orientation review prompt 審對方的正面圖，兩張都通過時用 PatternDesigner 的投票 prompt 對正面圖做 80/20 加權投票，
/// 最多 2 輪（prompt 都是 release 260917 的原文）。pattern審查關閉時照 260917：OpenAI 畫一張、再由 OpenAI 檢查正面方向。
/// </summary>
public sealed class SpatialPatternDesigner
{
    const int MaxRounds = 2;
    const double OpenAiVoteWeight = 0.80;
    const double GeminiVoteWeight = 0.20;
    // 只剩一張候選時，兩個模型打分（0～1）80/20 加權後要達到這個分數才採用：分數範圍的中點（2026-10-07）
    const double MinSingleCandidateScore = 0.50;
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(300);
    readonly ChatClient client;
    // Gemini 的連線（含 GEMINI_API_KEY）與投票 prompt 跟 2D 的 PatternDesigner 共用
    readonly PatternDesigner partner;
    readonly int maxRows, maxCols, maxLayers;

    public SpatialPatternDesigner(int maxRows, int maxCols, int maxLayers, PatternDesigner partner, string model = "gpt-5")
    {
        string? key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        client = new ChatClient(model, key);
        this.partner = partner;
        this.maxRows = maxRows;
        this.maxCols = maxCols;
        this.maxLayers = maxLayers;
    }

    public async Task<SpatialPattern> DesignAsync(
        string command, string color, int cubeBudget)
    {
        if (PatternDesigner.SkipReview)
        {
            Console.WriteLine("[3D Layer 1] pattern審查關閉：照 260917 只請 OpenAI 畫一次，再由 OpenAI 檢查正面方向。");
            var single = Check("OpenAI", await GenerateOpenAi(command, color, cubeBudget, ""), cubeBudget);
            if (single.FrontView == null)
                throw new SpatialPatternInfeasibleException(
                    "此立體字在目前支撐與尺寸限制下不可行：" + single.Error);
            var review = await ReviewOpenAi(command, single.FrontView);
            PrintReview("[3D orientation]", review);
            if (!Accepted(review))
            {
                string reason = string.IsNullOrWhiteSpace(review.FailureReason)
                    ? "固定正面方向下無法清楚辨識為指定字形。"
                    : review.FailureReason;
                throw new SpatialPatternInfeasibleException(
                    "方向與字形檢查未通過：" + reason);
            }
            return Pattern(command, single.Raw, single.Heights!, color);
        }

        if (partner.GeminiKeyMissing)
            throw new PatternDesignUnavailableException("沒有設定 GEMINI_API_KEY，不能做雙模型交叉審查：設定後重開 csharp_server，" +
                                                        "或在 Unity 把「pattern審查」關掉（只用 OpenAI 畫一次）。");
        string openFeedback = "", geminiFeedback = "";
        for (int round = 1; round <= MaxRounds; round++)
        {
            Console.WriteLine($"[3D Layer 1 dual] round {round}/{MaxRounds}: independent generation...");
            var openTask = GenerateOpenAi(command, color, cubeBudget, openFeedback);
            var gemTask = GenerateGemini(command, color, cubeBudget, geminiFeedback);
            await Task.WhenAll(openTask, gemTask);
            var open = Check("OpenAI", await openTask, cubeBudget);
            var gem = Check("Gemini", await gemTask, cubeBudget);

            // 各自只審對方的候選；本地檢查（可行、方向宣告、尺寸、層數、cube 數）沒過的候選不送審
            var gemReviewTask = ReviewCandidate(command, gem, openAiReviews: true);
            var openReviewTask = ReviewCandidate(command, open, openAiReviews: false);
            await Task.WhenAll(gemReviewTask, openReviewTask);
            var gemReview = await gemReviewTask;
            var openReview = await openReviewTask;
            if (gemReview != null) PrintReview("[3D Layer 1 cross] OpenAI reviews Gemini:", gemReview);
            if (openReview != null) PrintReview("[3D Layer 1 cross] Gemini reviews OpenAI:", openReview);

            var finalists = new List<Candidate>();
            if (openReview != null && Accepted(openReview)) finalists.Add(open);
            if (gemReview != null && Accepted(gemReview)) finalists.Add(gem);
            finalists = finalists.GroupBy(c => c.FrontView).Select(g => g.First()).ToList();

            Candidate? selected = null;
            double singleScore = 0;
            if (finalists.Count > 0)
            {
                // 2026-10-07：只剩一張（另一張沒過互審，或兩張一樣被合併）也要兩個模型打分，加權達 MinSingleCandidateScore 才採用
                Console.WriteLine(finalists.Count > 1
                    ? $"[3D Layer 1 vote] anonymously scoring {finalists.Count} finalists..."
                    : $"[3D Layer 1 vote] 只有一張候選，兩個模型仍要打分（加權達 {MinSingleCandidateScore:F2} 才採用）...");
                var openBallotTask = BallotOpenAi(command, finalists);
                var gemBallotTask = BallotGemini(command, finalists);
                await Task.WhenAll(openBallotTask, gemBallotTask);
                var openBallot = await openBallotTask;
                var gemBallot = await gemBallotTask;
                selected = SelectByWeightedScore(finalists, openBallot, gemBallot);
                if (finalists.Count == 1)
                {
                    singleScore = NormalizeScores(openBallot, 1)[0] * OpenAiVoteWeight + NormalizeScores(gemBallot, 1)[0] * GeminiVoteWeight;
                    if (singleScore < MinSingleCandidateScore)
                    {
                        Console.WriteLine($"[3D Layer 1 vote] 唯一的候選加權分數 {singleScore:F2}，未達 {MinSingleCandidateScore:F2}，不採用，下一輪重畫");
                        selected = null;
                    }
                }
            }
            if (selected != null)
            {
                Console.WriteLine(finalists.Count > 1
                    ? $"[3D Layer 1 dual] selected={selected.Author} by anonymous dual-model vote"
                    : $"[3D Layer 1 dual] selected={selected.Author}（唯一的候選，雙模型加權分數 {singleScore:F2}）");
                return Pattern(command, selected.Raw, selected.Heights!, color);
            }
            openFeedback = Feedback(open.Error, openReview);
            geminiFeedback = Feedback(gem.Error, gemReview);
        }
        throw new InvalidOperationException($"雙模型交叉評審在 {MaxRounds} 輪後仍未接受任何 3D 高度圖。");
    }

    static SpatialPattern Pattern(string command, SpatialResult raw, int[,] heights, string color) => new()
    {
        PatternId = string.IsNullOrWhiteSpace(raw.PatternId) ? command : raw.PatternId,
        ColumnHeights = heights,
        BlockColor = color,
    };

    // release 260917 的生成 prompt：前 16 行是原文；最後一行（2026-10-07 加）說明 column_heights 的列、欄各對應哪個方向。
    // 原文沒把矩陣的列、欄跟 X/Y 對起來，Gemini 把 1×3 的 3 個數字當成前後方向，認為正面只有一欄、判定 L 不可行
    string GenerationPrompt(int cubeBudget) => $$"""
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
        column_heights has exactly {{maxRows}} row(s) along depth/Y; within each row, the {{maxCols}} numbers are the column heights from left to right along +X.
        """;

    // 260917 的使用者訊息；第二輪起加上 PatternDesigner 的交叉審查回饋句（同 2D 流程）
    static string GenerationRequest(string command, string color, string feedback) =>
        $"Original command: {command}\nBlock color: {color}" + (string.IsNullOrWhiteSpace(feedback) ? "" :
            $"\nPrevious cross-review feedback: {feedback}\nGenerate a fresh candidate for the original command. Feedback is advisory and must not change the target.");

    // release 260917 的正面方向審查 prompt（原文）
    const string ReviewPrompt = """
        Independently review a voxel glyph in its fixed physical orientation.
        Read the bitmap exactly as displayed: top row is highest +Z, bottom row
        touches the table, columns run left-to-right, and the viewer looks from
        the front. Do not rotate, mirror, transpose, invert, or reinterpret the
        bitmap. Decide whether it clearly represents the exact text or symbol
        requested by the user in that orientation. If it resembles the requested
        glyph only after any rotation or mirroring, set accept=false and identify
        that transformation. Do not substitute a different glyph merely because
        it is physically supportable. Return only schema-valid JSON.
        """;

    static string ReviewRequest(string command, string frontBitmap) => $$"""
        Original request: {{command}}

        Fixed front-view bitmap (1=voxel, 0=empty):
        {{frontBitmap}}
        """;

    async Task<SpatialResult> GenerateOpenAi(string command, string color, int cubeBudget, string feedback)
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
            new SystemChatMessage(GenerationPrompt(cubeBudget)),
            new UserChatMessage(GenerationRequest(command, color, feedback)),
        };
        ChatCompletion completion = await CompleteWithProgressAsync(messages, options, "[3D Layer 1] LLM");
        string responseText = completion.Content.Count > 0
            ? completion.Content[0].Text
            : "";
        DesignLog.Write("spatial_design", messages, responseText);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException(
                $"LLM 未回傳 3D JSON（finish_reason={completion.FinishReason}）。" +
                "這是模型輸出或 token 額度問題，不代表字形不可行。");
        }
        return JsonSerializer.Deserialize<SpatialResult>(responseText)
               ?? throw new InvalidOperationException("3D pattern response parse failed.");
    }

    async Task<SpatialResult> GenerateGemini(string command, string color, int cubeBudget, string feedback)
        => JsonSerializer.Deserialize<SpatialResult>(await partner.CallGemini(
               GenerationPrompt(cubeBudget), GenerationRequest(command, color, feedback), BuildSchema()))
           ?? throw new InvalidOperationException("Gemini 3D pattern response parse failed.");

    async Task<OrientationReview> ReviewOpenAi(string command, string frontBitmap)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "spatial_orientation_review",
                jsonSchema: BinaryData.FromString(BuildOrientationReviewSchema()),
                jsonSchemaIsStrict: true),
        };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(ReviewPrompt),
            new UserChatMessage(ReviewRequest(command, frontBitmap)),
        };
        ChatCompletion completion = await CompleteWithProgressAsync(
            messages, options, "[3D orientation] reviewer");
        string responseText = completion.Content.Count > 0
            ? completion.Content[0].Text
            : "";
        DesignLog.Write("spatial_orientation_review", messages, responseText);
        return JsonSerializer.Deserialize<OrientationReview>(responseText)
               ?? throw new InvalidOperationException(
                   "3D orientation review response parse failed.");
    }

    async Task<OrientationReview?> ReviewCandidate(string command, Candidate candidate, bool openAiReviews)
    {
        if (candidate.FrontView == null) return null;
        return openAiReviews ? await ReviewOpenAi(command, candidate.FrontView) : await ReviewGemini(command, candidate.FrontView);
    }

    async Task<OrientationReview> ReviewGemini(string command, string frontBitmap)
        => JsonSerializer.Deserialize<OrientationReview>(await partner.CallGemini(
               ReviewPrompt, ReviewRequest(command, frontBitmap), BuildOrientationReviewSchema()))
           ?? throw new InvalidOperationException("Gemini 3D orientation review response parse failed.");

    // 投票：PatternDesigner 的投票 prompt 與 schema（260917 原文），候選圖是各自的正面圖
    async Task<Ballot> BallotOpenAi(string command, List<Candidate> candidates)
    {
        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "pattern_ballot",
                jsonSchema: BinaryData.FromString(PatternDesigner.BallotSchema()),
                jsonSchemaIsStrict: true),
        };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(PatternDesigner.BallotPrompt()),
            new UserChatMessage(BallotRequest(command, candidates)),
        };
        ChatCompletion completion = await CompleteWithProgressAsync(messages, options, "[3D Layer 1 vote] OpenAI");
        string responseText = completion.Content.Count > 0 ? completion.Content[0].Text : "";
        DesignLog.Write("openai_ballot", messages, responseText);
        return ParseBallot(responseText);
    }

    async Task<Ballot> BallotGemini(string command, List<Candidate> candidates)
        => ParseBallot(await partner.CallGemini(
            PatternDesigner.BallotPrompt(), BallotRequest(command, candidates), PatternDesigner.BallotSchema()));

    // 格式同 PatternDesigner.BallotRequest
    static string BallotRequest(string command, List<Candidate> candidates)
    {
        var text = new StringBuilder($"Original user command: {command}\n");
        for (int i = 0; i < candidates.Count; i++)
        {
            text.AppendLine($"Candidate {i}:");
            foreach (string row in candidates[i].FrontView!.Split('\n')) text.AppendLine(row);
        }
        return text.ToString();
    }

    static Ballot ParseBallot(string json) =>
        JsonSerializer.Deserialize<Ballot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("3D pattern ballot parse failed.");

    static Candidate SelectByWeightedScore(List<Candidate> candidates, Ballot openBallot, Ballot gemBallot)
    {
        var openScores = NormalizeScores(openBallot, candidates.Count);
        var gemScores = NormalizeScores(gemBallot, candidates.Count);
        int bestIndex = 0;
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            double weightedScore = openScores[i] * OpenAiVoteWeight + gemScores[i] * GeminiVoteWeight;
            Console.WriteLine(
                $"[3D Layer 1 vote] candidate {i}: OpenAI={openScores[i]:F2}, " +
                $"Gemini={gemScores[i]:F2}, weighted={weightedScore:F2} " +
                $"(OpenAI {OpenAiVoteWeight:P0} / Gemini {GeminiVoteWeight:P0})");
            if (weightedScore > bestScore)
            {
                bestScore = weightedScore;
                bestIndex = i;
            }
        }
        return candidates[bestIndex];
    }

    static double[] NormalizeScores(Ballot ballot, int count)
    {
        var scores = Enumerable.Repeat(0.0, count).ToArray();
        foreach (var entry in ballot.Scores)
            if (entry.CandidateIndex >= 0 && entry.CandidateIndex < count)
                scores[entry.CandidateIndex] = Math.Clamp(entry.Score, 0.0, 1.0);
        return scores;
    }

    // 本地檢查一張候選：生成端宣告可行、方向宣告正確、尺寸／層數／cube 數在限制內才有正面圖可以送審
    Candidate Check(string author, SpatialResult raw, int cubeBudget)
    {
        var candidate = new Candidate { Author = author, Raw = raw };
        Console.WriteLine($"[3D Layer 1] {author} candidate={raw.PatternId}, feasible={raw.Feasible}");
        try
        {
            if (!raw.Feasible)
                throw new SpatialPatternInfeasibleException("generator reported infeasible: " + raw.FailureReason);
            ValidateDeclaredOrientation(raw);
            candidate.Heights = ParseAndValidate(raw.ColumnHeights ??= new(), cubeBudget);
            candidate.FrontView = BuildFrontBitmap(candidate.Heights);
            Console.WriteLine("                  column_heights: " + string.Join(" / ", raw.ColumnHeights.Select(r => string.Join(" ", r))));
            Console.WriteLine("                  fixed front view (+Z up, table at bottom):");
            foreach (string row in candidate.FrontView.Split('\n'))
                Console.WriteLine("                  " + row.Replace('0', '□').Replace('1', '■'));
        }
        catch (Exception ex) when (ex is SpatialPatternInfeasibleException or InvalidOperationException)
        {
            candidate.Error = ex.Message;
            Console.WriteLine("                  rejected: " + ex.Message);
        }
        return candidate;
    }

    static bool Accepted(OrientationReview review) =>
        review.Accept && review.Recognizable && !review.RequiresRotation && !review.RequiresMirroring;

    static string Feedback(string error, OrientationReview? review) => string.Join("; ", new[]
    {
        error,
        review == null || Accepted(review) ? "" : $"reviewer observed it as: {review.ObservedAs}",
        review == null || Accepted(review) ? "" : review.FailureReason,
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    static void PrintReview(string label, OrientationReview review)
    {
        Console.WriteLine(
            $"{label} accept={review.Accept}, recognizable={review.Recognizable}, " +
            $"rotation={review.RequiresRotation}, mirrored={review.RequiresMirroring}, " +
            $"observed={review.ObservedAs}");
        if (!string.IsNullOrWhiteSpace(review.FailureReason))
            Console.WriteLine("                  reason: " + review.FailureReason);
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

    sealed class Candidate
    {
        public string Author { get; set; } = "";
        public SpatialResult Raw { get; set; } = new();
        public int[,]? Heights { get; set; }
        // 正面圖（一列一行、最上面是最高層）；本地檢查沒過時是 null
        public string? FrontView { get; set; }
        public string Error { get; set; } = "";
    }

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

    sealed class Ballot
    {
        [JsonPropertyName("scores")] public List<BallotScore> Scores { get; set; } = new();
    }

    sealed class BallotScore
    {
        [JsonPropertyName("candidate_index")] public int CandidateIndex { get; set; }
        [JsonPropertyName("score")] public double Score { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }
}

public sealed class SpatialPatternInfeasibleException : Exception
{
    public SpatialPatternInfeasibleException(string message) : base(message) { }
}
