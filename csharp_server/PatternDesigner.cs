using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

// OpenAI 與 Gemini 各自生成，再各自審查對方的候選圖。
public sealed class PatternDesigner
{
    const int MaxRounds = 2;
    const double MinimumWinningScore = 0.75;
    const double OpenAiVoteWeight = 0.50;
    const double GeminiVoteWeight = 0.50;
    readonly ChatClient openAi;
    readonly HttpClient gemini;
    readonly string geminiModel;
    readonly int maxRows, maxCols;
    readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public PatternDesigner(int maxRows, int maxCols, string openAiModel = "gpt-5", string? geminiModel = null)
    {
        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(openAiKey)) throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        if (string.IsNullOrWhiteSpace(geminiKey)) throw new InvalidOperationException("GEMINI_API_KEY is not set.");
        openAi = new ChatClient(openAiModel, openAiKey);
        this.geminiModel = geminiModel ?? Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.1-flash-lite";
        gemini = new HttpClient { Timeout = TimeSpan.FromSeconds(150) };
        gemini.DefaultRequestHeaders.Add("x-goog-api-key", geminiKey);
        gemini.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        this.maxRows = maxRows;
        this.maxCols = maxCols;
    }

    public async Task<CanonicalPattern> DesignAsync(string command, string color = "yellow", int cubes = 0, int dominoes = 0)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("User command is empty.");
        int capacity = cubes + dominoes * 2;
        foreach (var canvas in CanvasSizes())
        {
          string openFeedback = "", geminiFeedback = "";
          for (int round = 1; round <= MaxRounds; round++)
          {
            Console.WriteLine($"[Layer 1 adaptive] canvas {canvas.Rows}x{canvas.Cols}, round {round}/{MaxRounds}...");
            var openTask = GenerateOpenAi(command, color, cubes, dominoes, openFeedback, canvas.Rows, canvas.Cols);
            var gemTask = GenerateGemini(command, color, cubes, dominoes, geminiFeedback, canvas.Rows, canvas.Cols);
            await Task.WhenAll(openTask, gemTask);
            var open = await openTask;
            var gem = await gemTask;
            PrintCandidate("OpenAI", open);
            PrintCandidate("Gemini", gem);

            var openLocal = Validate(open, capacity, canvas.Rows, canvas.Cols);
            var gemLocal = Validate(gem, capacity, canvas.Rows, canvas.Cols);
            // Each model sees only the other model's candidate.
            var openReviewsGem = ReviewOpenAi(command, gem, gemLocal.Error);
            var gemReviewsOpen = ReviewGemini(command, open, openLocal.Error);
            await Task.WhenAll(openReviewsGem, gemReviewsOpen);
            var gemReview = await openReviewsGem;
            var openReview = await gemReviewsOpen;
            PrintReview("OpenAI reviews Gemini", gemReview);
            PrintReview("Gemini reviews OpenAI", openReview);

            var finalists = new List<Candidate>();
            open.Author = "Candidate 1: OpenAI original";
            gem.Author = "Candidate 2: Gemini original";
            // 1/2: both independently generated originals enter the anonymous
            // ballot as long as deterministic format/inventory checks pass.
            if (openLocal.Valid)
                finalists.Add(open);
            else
                Console.WriteLine($"[Layer 1 candidate 1] OpenAI original rejected: {openLocal.Error}");
            if (gemLocal.Valid)
                finalists.Add(gem);
            else
                Console.WriteLine($"[Layer 1 candidate 2] Gemini original rejected: {gemLocal.Error}");

            // 3: Gemini's revision of OpenAI. 4: OpenAI's revision of Gemini.
            AddRevisionIfValid(finalists, openReview,
                "Candidate 3: OpenAI revised by Gemini", command, capacity,
                canvas.Rows, canvas.Cols);
            AddRevisionIfValid(finalists, gemReview,
                "Candidate 4: Gemini revised by OpenAI", command, capacity,
                canvas.Rows, canvas.Cols);
            Candidate? selected = null;
            if (finalists.Count > 0)
            {
                Console.WriteLine($"[Layer 1 vote] anonymously scoring {finalists.Count} finalists...");
                var openBallotTask = BallotOpenAi(command, finalists);
                var gemBallotTask = BallotGemini(command, finalists);
                await Task.WhenAll(openBallotTask, gemBallotTask);
                selected = SelectByWeightedScore(
                    finalists, await openBallotTask, await gemBallotTask,
                    out double winningScore);
                if (winningScore < MinimumWinningScore)
                {
                    Console.WriteLine($"[Layer 1 vote] best weighted score {winningScore:F2} " +
                                      $"is below {MinimumWinningScore:F2}; redraw/escalate canvas");
                    selected = null;
                }
            }
            if (selected != null)
            {
                Console.WriteLine($"[Layer 1 dual] selected={selected.Author} by anonymous 50/50 dual-model vote");
                Console.WriteLine("[Layer 1 FINAL] 最終採用 Bitmap：");
                foreach (string row in selected.Bitmap!)
                    Console.WriteLine("                  " + row.Replace('0', '□').Replace('1', '■'));
                return new CanonicalPattern
                {
                    PatternId = string.IsNullOrWhiteSpace(selected.PatternId) ? command : selected.PatternId,
                    Bitmap = BitmapParser.Parse(selected.Bitmap!),
                    BlockColor = color,
                };
            }
            openFeedback = Feedback(openLocal.Error, openReview, open);
            geminiFeedback = Feedback(gemLocal.Error, gemReview, gem);
          }
          if (canvas.Rows < maxRows || canvas.Cols < maxCols)
              Console.WriteLine($"[Layer 1 adaptive] {canvas.Rows}x{canvas.Cols} was not accepted; escalating canvas.");
        }
        throw new InvalidOperationException($"雙模型在 5x5 到 {maxRows}x{maxCols} 的自評與交叉評審後仍無可接受 bitmap。");
    }

    IEnumerable<(int Rows, int Cols)> CanvasSizes()
    {
        yield return (Math.Min(5, maxRows), Math.Min(5, maxCols));
        if (maxRows > 5 || maxCols > 5) yield return (maxRows, maxCols);
    }

    async Task<Candidate> GenerateOpenAi(string command, string color, int cubes, int dominoes, string feedback, int rows, int cols)
    {
        var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
            jsonSchemaFormatName: "pattern_candidate",
            jsonSchema: BinaryData.FromString(CandidateSchema()),
            jsonSchemaIsStrict: true) };
        ChatCompletion completion = await openAi.CompleteChatAsync(new List<ChatMessage>
        {
            new SystemChatMessage(GenerationPrompt(cubes, dominoes, rows, cols)),
            new UserChatMessage(GenerationRequest(command, color, feedback)),
        }, options);
        return ParseCandidate(completion.Content[0].Text, "OpenAI");
    }

    async Task<Candidate> GenerateGemini(string command, string color, int cubes, int dominoes, string feedback, int rows, int cols)
        => ParseCandidate(await CallGemini(GenerationPrompt(cubes, dominoes, rows, cols), GenerationRequest(command, color, feedback), CandidateSchema()), "Gemini");

    async Task<Review> ReviewOpenAi(string command, Candidate candidate, string localError)
    {
        var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
            jsonSchemaFormatName: "pattern_review",
            jsonSchema: BinaryData.FromString(ReviewSchema()),
            jsonSchemaIsStrict: true) };
        ChatCompletion completion = await openAi.CompleteChatAsync(new List<ChatMessage>
        {
            new SystemChatMessage(ReviewPrompt()),
            new UserChatMessage(ReviewRequest(command, candidate, localError)),
        }, options);
        return ParseReview(completion.Content[0].Text);
    }

    async Task<Review> ReviewGemini(string command, Candidate candidate, string localError)
        => ParseReview(await CallGemini(ReviewPrompt(), ReviewRequest(command, candidate, localError), ReviewSchema()));

    async Task<Ballot> BallotOpenAi(string command, List<Candidate> candidates)
    {
        var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
            jsonSchemaFormatName: "pattern_ballot",
            jsonSchema: BinaryData.FromString(BallotSchema()),
            jsonSchemaIsStrict: true) };
        ChatCompletion completion = await openAi.CompleteChatAsync(new List<ChatMessage>
        {
            new SystemChatMessage(BallotPrompt()),
            new UserChatMessage(BallotRequest(command, candidates)),
        }, options);
        return ParseBallot(completion.Content[0].Text);
    }

    async Task<Ballot> BallotGemini(string command, List<Candidate> candidates)
        => ParseBallot(await CallGemini(
            BallotPrompt(), BallotRequest(command, candidates), BallotSchema()));

    async Task<string> CallGemini(string system, string user, string schemaJson)
    {
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseJsonSchema = JsonDocument.Parse(schemaJson).RootElement.Clone(),
            },
        };
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(geminiModel)}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var response = await gemini.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Gemini API {(int)response.StatusCode}: {responseBody}");
        using var doc = JsonDocument.Parse(responseBody);
        var candidates = doc.RootElement.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0) throw new InvalidOperationException("Gemini response contains no candidate.");
        return candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Gemini response contains no text.");
    }

    string GenerationPrompt(int cubes, int dominoes, int rows, int cols) => $$"""
        將使用者要求的文字、符號或形狀轉換成二進位矩陣。
        - 本次畫布固定為 {{rows}} 列、{{cols}} 欄；每一列都必須剛好包含 {{cols}} 個格子。
        - 每個字元只能是 0（空白）或 1（佔用）。
        - 人類能否清楚辨識比標準字型、固定比例或傳統筆畫位置更重要。
        - 你可以調整筆畫位置、粗細、轉折與局部填充，以提高可辨識度。
        - 請依照人類視覺判斷進行檢查：圖案身分必須正確、方向直立且不可鏡像；
          預期筆畫必須連續，不可有意外斷點或孤立雜點；代表性端點、轉折、交叉、
          對稱或非對稱特徵以及負空間必須正確；比例需平衡，且不可存在更合理的其他解讀。
        - 請獨立完成，不可假設或模仿另一個候選圖案。
        - 本應用程式不提供字形模板、特定目標的筆畫規則、字型、Bitmap 範例或易混淆清單。
        - 可用積木為 {{cubes}} 顆立方體與 {{dominoes}} 顆骨牌。立方體佔一格，骨牌佔兩個正交相鄰格。
        - 若此畫布無法容納足夠清楚且可實際排列的矩陣，請設定 feasible=false，不要勉強輸出錯誤圖案。
        - 完成繪製後，忽略 pattern_id，只觀察實際格子進行盲目自我審查。
          回報這些格子實際看起來像什麼。只有當判讀結果毫無歧義地符合原始要求，
          且信心分數大於或等於 0.75 時，才能設定 self_exact_target_match=true 與 self_accept=true。
          如果 self_observed_as 不是原始要求的精確目標，self_exact_target_match 與 self_accept 都必須為 false。
        - 如果需要更高解析度才能呈現此圖案的身分特徵，請設定
          self_needs_larger_canvas=true、self_accept=false，並說明目前無法容納哪個關鍵特徵。
          不可只因為 {{rows}}x{{cols}} 是目前的限制，就接受品質不佳的近似圖案。
        - feasible=false 時 bitmap 必須是空陣列；feasible=true 時 failure_reason 必須是空字串。
        僅回傳符合指定 Schema 的 JSON。
        """;

    static string GenerationRequest(string command, string color, string feedback) => $"""
        原始使用者指令：{command}
        積木顏色：{color}
        {(string.IsNullOrWhiteSpace(feedback) ? "" : "上一輪交叉評審回饋：" + feedback)}
        請針對原始指令產生全新的候選圖案。回饋僅供修正參考，不得改變原始目標。
        """;

    static string ReviewPrompt() => """
        請獨立交叉審查另一個模型產生的二進位 Bitmap。
        判斷實際矩陣對人類而言是否清楚符合原始要求，並遵守矩陣限制。
        請直接檢查格子，不要相信候選編號、作者或它宣稱的標籤。
        請運用人類視覺邏輯：確認圖案身分正確、方向直立且沒有鏡像；檢查筆畫連續性、
        代表性端點、轉折、交叉、對稱性與負空間；確認比例平衡，並評估是否容易被解讀成其他圖案。
        如果目前解析度無法清楚呈現使用者要求的圖案，請設定 needs_larger_canvas=true，
        不要接受可能誤導的近似結果。
        exact_target_match 表示實際看到的圖案是否就是原始要求，而不只是「可以辨識出某個圖案」。
        如果 observed_as 與原始要求不同，exact_target_match 與 accept 必須為 false。
        如果 structural_problems 包含任何會改變圖案身分的問題，accept 必須為 false。
        不需要符合特定字型或完全一致的模板。
        請拒絕不確定、有歧義、結構破損或目標錯誤的候選圖案。
        僅根據此矩陣，提供精簡的結構問題與可執行的修正方式。
        如果你能明顯改善送審矩陣，請設定 has_revision=true，並在 revised_bitmap 回傳完整修正版 Bitmap。
        修正版必須仍以原始要求為目標，不可只在 suggested_changes 中描述修改方式。
        如果不需要修改或無法修改，請設定 has_revision=false 且 revised_bitmap=[]。
        僅回傳符合指定 Schema 的 JSON。
        """;

    static string ReviewRequest(string command, Candidate c, string localError) => $"""
        原始使用者指令：{command}
        候選 Bitmap：
        {string.Join(Environment.NewLine, c.Bitmap ?? new())}
        確定性格式與庫存檢查：{(string.IsNullOrEmpty(localError) ? "有效" : localError)}
        請在看不到另一個候選圖案的情況下審查此候選圖案。
        """;

    static string BallotPrompt() => """
        請根據原始要求，為匿名的二進位矩陣評分。
        依照人類視覺可辨識度及對指定目標的忠實程度，獨立判斷每一個實際矩陣。
        候選順序與身分沒有任何意義，不可推測作者。
        不可使用應用程式提供的模板、特定目標規則、字型、範例或易混淆清單；本程式沒有提供這些資料。
        如果矩陣不是原始要求的精確目標、存在身分歧義、方向錯誤、關鍵結構問題，
        或需要更大畫布才能清楚表達，分數必須低於 0.75。
        每個候選索引必須剛好回傳一筆評分，分數範圍為 0 到 1。
        僅回傳符合指定 Schema 的 JSON。
        """;

    static string BallotRequest(string command, List<Candidate> candidates)
    {
        var text = new StringBuilder($"原始使用者指令：{command}\n");
        for (int i = 0; i < candidates.Count; i++)
        {
            text.AppendLine($"候選圖案 {i}：");
            foreach (string row in candidates[i].Bitmap ?? new()) text.AppendLine(row);
        }
        return text.ToString();
    }

    Validation Validate(Candidate c, int capacity, int canvasRows, int canvasCols)
    {
        if (!c.Feasible) return new(false, "generator reported infeasible: " + c.FailureReason);
        if (c.Bitmap == null || c.Bitmap.Count == 0) return new(false, "bitmap is empty");
        try
        {
            int[,] bitmap = BitmapParser.Parse(c.Bitmap);
            if (bitmap.GetLength(0) != canvasRows || bitmap.GetLength(1) != canvasCols)
                return new(false, $"bitmap must be exactly {canvasRows}x{canvasCols}");
            int occupied = BitmapParser.CountOccupiedCells(bitmap);
            if (occupied == 0) return new(false, "bitmap has no occupied cell");
            if (occupied > capacity) return new(false, $"bitmap needs {occupied} cells but inventory covers {capacity}");
            return new(true, "");
        }
        catch (Exception ex) { return new(false, "invalid bitmap: " + ex.Message); }
    }

    void AddRevisionIfValid(
        List<Candidate> finalists,
        Review review,
        string author,
        string command,
        int capacity,
        int canvasRows,
        int canvasCols)
    {
        if (!review.HasRevision || review.RevisedBitmap.Count == 0)
        {
            Console.WriteLine($"[Layer 1 revision] {author}: reviewer did not provide a revision");
            return;
        }

        var revision = new Candidate
        {
            PatternId = command,
            Feasible = true,
            Bitmap = review.RevisedBitmap,
            Author = author,
        };
        Validation validation = Validate(revision, capacity, canvasRows, canvasCols);
        if (!validation.Valid)
        {
            Console.WriteLine($"[Layer 1 revision] {author} rejected: {validation.Error}");
            return;
        }
        finalists.Add(revision);
        Console.WriteLine($"[Layer 1 revision] {author} entered ballot:");
        foreach (string row in revision.Bitmap)
            Console.WriteLine("                  " + row.Replace('0', '□').Replace('1', '■'));
    }

    Candidate SelectByWeightedScore(
        List<Candidate> candidates,
        Ballot openBallot,
        Ballot gemBallot,
        out double winningScore)
    {
        var openScores = NormalizeScores(openBallot, candidates.Count);
        var gemScores = NormalizeScores(gemBallot, candidates.Count);
        int bestIndex = 0;
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            double weightedScore = openScores[i] * OpenAiVoteWeight +
                                   gemScores[i] * GeminiVoteWeight;
            Console.WriteLine(
                $"[Layer 1 vote] candidate {i}: OpenAI={openScores[i]:F2}, " +
                $"Gemini={gemScores[i]:F2}, weighted={weightedScore:F2} " +
                $"(OpenAI {OpenAiVoteWeight:P0} / Gemini {GeminiVoteWeight:P0})");
            if (weightedScore > bestScore)
            {
                bestScore = weightedScore;
                bestIndex = i;
            }
        }
        winningScore = bestScore;
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

    Candidate ParseCandidate(string json, string author)
    {
        var c = JsonSerializer.Deserialize<Candidate>(json, jsonOptions) ?? throw new InvalidOperationException($"{author} candidate parse failed.");
        c.Author = author;
        return c;
    }
    Review ParseReview(string json) => JsonSerializer.Deserialize<Review>(json, jsonOptions) ?? throw new InvalidOperationException("Pattern review parse failed.");
    Ballot ParseBallot(string json) => JsonSerializer.Deserialize<Ballot>(json, jsonOptions) ?? throw new InvalidOperationException("Pattern ballot parse failed.");
    static string Feedback(string error, Review r, Candidate self) => string.Join("; ",
        new[] { error, self.SelfCheckReason }
            .Concat(r.StructuralProblems).Concat(r.SuggestedChanges)
            .Where(x => !string.IsNullOrWhiteSpace(x)));

    static void PrintCandidate(string name, Candidate c)
    {
        Console.WriteLine($"[Layer 1 dual] {name} candidate={c.PatternId}, feasible={c.Feasible}");
        foreach (var row in c.Bitmap ?? new()) Console.WriteLine("                  " + row.Replace('0', '□').Replace('1', '■'));
        if (!c.Feasible) Console.WriteLine("                  reason=" + c.FailureReason);
        Console.WriteLine($"                  self: accept={c.SelfAccept}, recognizable={c.SelfRecognizable}, " +
                          $"exact_match={c.SelfExactTargetMatch}, " +
                          $"needs_larger={c.SelfNeedsLargerCanvas}, confidence={c.SelfConfidence:F2}, " +
                          $"observed={c.SelfObservedAs}, reason={c.SelfCheckReason}");
    }
    static void PrintReview(string name, Review r)
    {
        Console.WriteLine($"[Layer 1 cross] {name}: accept={r.Accept}, recognizable={r.Recognizable}, " +
                          $"exact_match={r.ExactTargetMatch}, " +
                          $"needs_larger={r.NeedsLargerCanvas}, confidence={r.Confidence:F2}, observed={r.ObservedAs}");
        foreach (var x in r.StructuralProblems) Console.WriteLine("                  problem: " + x);
        foreach (var x in r.SuggestedChanges) Console.WriteLine("                  suggestion: " + x);
        if (r.HasRevision)
        {
            Console.WriteLine("                  structured revision:");
            foreach (var row in r.RevisedBitmap)
                Console.WriteLine("                  " + row.Replace('0', '□').Replace('1', '■'));
        }
    }

    static string CandidateSchema() => JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["pattern_id"] = new { type = "string" }, ["feasible"] = new { type = "boolean" },
            ["failure_reason"] = new { type = "string" }, ["suggested_rows"] = new { type = "integer" },
            ["suggested_cols"] = new { type = "integer" }, ["suggested_occupied_cells"] = new { type = "integer" },
            ["bitmap"] = new { type = "array", items = new { type = "string" } },
            ["self_accept"] = new { type = "boolean" },
            ["self_recognizable"] = new { type = "boolean" },
            ["self_exact_target_match"] = new { type = "boolean" },
            ["self_needs_larger_canvas"] = new { type = "boolean" },
            ["self_confidence"] = new { type = "number", minimum = 0, maximum = 1 },
            ["self_observed_as"] = new { type = "string" },
            ["self_check_reason"] = new { type = "string" },
        },
        required = new[] { "pattern_id", "feasible", "failure_reason", "suggested_rows", "suggested_cols", "suggested_occupied_cells", "bitmap",
            "self_accept", "self_recognizable", "self_exact_target_match", "self_needs_larger_canvas", "self_confidence", "self_observed_as", "self_check_reason" },
    });
    static string ReviewSchema() => JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["accept"] = new { type = "boolean" }, ["recognizable"] = new { type = "boolean" },
            ["exact_target_match"] = new { type = "boolean" },
            ["confidence"] = new { type = "number", minimum = 0, maximum = 1 }, ["observed_as"] = new { type = "string" },
            ["structural_problems"] = new { type = "array", items = new { type = "string" } },
            ["suggested_changes"] = new { type = "array", items = new { type = "string" } },
            ["has_revision"] = new { type = "boolean" },
            ["revised_bitmap"] = new { type = "array", items = new { type = "string" } },
            ["needs_larger_canvas"] = new { type = "boolean" },
            ["recommended_canvas_size"] = new { type = "integer", minimum = 5, maximum = 7 },
        },
        required = new[] { "accept", "recognizable", "exact_target_match", "confidence", "observed_as", "structural_problems", "suggested_changes", "has_revision", "revised_bitmap",
            "needs_larger_canvas", "recommended_canvas_size" },
    });
    static string BallotSchema() => JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["scores"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        ["candidate_index"] = new { type = "integer" },
                        ["score"] = new { type = "number", minimum = 0, maximum = 1 },
                        ["reason"] = new { type = "string" },
                    },
                    required = new[] { "candidate_index", "score", "reason" },
                },
            },
        },
        required = new[] { "scores" },
    });

    sealed class Candidate
    {
        [JsonPropertyName("pattern_id")] public string PatternId { get; set; } = "";
        [JsonPropertyName("feasible")] public bool Feasible { get; set; }
        [JsonPropertyName("failure_reason")] public string FailureReason { get; set; } = "";
        [JsonPropertyName("suggested_rows")] public int SuggestedRows { get; set; }
        [JsonPropertyName("suggested_cols")] public int SuggestedCols { get; set; }
        [JsonPropertyName("suggested_occupied_cells")] public int SuggestedOccupiedCells { get; set; }
        [JsonPropertyName("bitmap")] public List<string>? Bitmap { get; set; }
        [JsonPropertyName("self_accept")] public bool SelfAccept { get; set; }
        [JsonPropertyName("self_recognizable")] public bool SelfRecognizable { get; set; }
        [JsonPropertyName("self_exact_target_match")] public bool SelfExactTargetMatch { get; set; }
        [JsonPropertyName("self_needs_larger_canvas")] public bool SelfNeedsLargerCanvas { get; set; }
        [JsonPropertyName("self_confidence")] public double SelfConfidence { get; set; }
        [JsonPropertyName("self_observed_as")] public string SelfObservedAs { get; set; } = "";
        [JsonPropertyName("self_check_reason")] public string SelfCheckReason { get; set; } = "";
        [JsonIgnore] public string Author { get; set; } = "";
    }
    sealed class Review
    {
        [JsonPropertyName("accept")] public bool Accept { get; set; }
        [JsonPropertyName("recognizable")] public bool Recognizable { get; set; }
        [JsonPropertyName("exact_target_match")] public bool ExactTargetMatch { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("observed_as")] public string ObservedAs { get; set; } = "";
        [JsonPropertyName("structural_problems")] public List<string> StructuralProblems { get; set; } = new();
        [JsonPropertyName("suggested_changes")] public List<string> SuggestedChanges { get; set; } = new();
        [JsonPropertyName("has_revision")] public bool HasRevision { get; set; }
        [JsonPropertyName("revised_bitmap")] public List<string> RevisedBitmap { get; set; } = new();
        [JsonPropertyName("needs_larger_canvas")] public bool NeedsLargerCanvas { get; set; }
        [JsonPropertyName("recommended_canvas_size")] public int RecommendedCanvasSize { get; set; }
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
    readonly record struct Validation(bool Valid, string Error);
}
