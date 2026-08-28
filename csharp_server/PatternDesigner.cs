using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

// OpenAI 與 Gemini 各自生成，再各自審查對方的候選圖。
public sealed class PatternDesigner
{
    const int MaxRounds = 2;
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
        string openFeedback = "", geminiFeedback = "";

        for (int round = 1; round <= MaxRounds; round++)
        {
            Console.WriteLine($"[Layer 1 dual] round {round}/{MaxRounds}: independent generation...");
            var openTask = GenerateOpenAi(command, color, cubes, dominoes, openFeedback);
            var gemTask = GenerateGemini(command, color, cubes, dominoes, geminiFeedback);
            await Task.WhenAll(openTask, gemTask);
            var open = await openTask;
            var gem = await gemTask;
            PrintCandidate("OpenAI", open);
            PrintCandidate("Gemini", gem);

            var openLocal = Validate(open, capacity);
            var gemLocal = Validate(gem, capacity);
            // Each model sees only the other model's candidate.
            var openReviewsGem = ReviewOpenAi(command, gem, gemLocal.Error);
            var gemReviewsOpen = ReviewGemini(command, open, openLocal.Error);
            await Task.WhenAll(openReviewsGem, gemReviewsOpen);
            var gemReview = await openReviewsGem;
            var openReview = await gemReviewsOpen;
            PrintReview("OpenAI reviews Gemini", gemReview);
            PrintReview("Gemini reviews OpenAI", openReview);

            var finalists = new List<Candidate>();
            if (openLocal.Valid && openReview.Accept && openReview.Recognizable)
                finalists.Add(open);
            if (gemLocal.Valid && gemReview.Accept && gemReview.Recognizable)
                finalists.Add(gem);

            AddRevisionIfValid(finalists, openReview, "Gemini revision by OpenAI", command, capacity);
            AddRevisionIfValid(finalists, gemReview, "OpenAI revision by Gemini", command, capacity);
            finalists = finalists
                .GroupBy(c => string.Join("/", c.Bitmap ?? new List<string>()))
                .Select(g => g.First())
                .ToList();

            Candidate? selected = null;
            if (finalists.Count == 1)
            {
                selected = finalists[0];
            }
            else if (finalists.Count > 1)
            {
                Console.WriteLine($"[Layer 1 vote] anonymously scoring {finalists.Count} finalists...");
                var openBallotTask = BallotOpenAi(command, finalists);
                var gemBallotTask = BallotGemini(command, finalists);
                await Task.WhenAll(openBallotTask, gemBallotTask);
                selected = SelectByAverageScore(finalists, await openBallotTask, await gemBallotTask);
            }
            if (selected != null)
            {
                Console.WriteLine($"[Layer 1 dual] selected={selected.Author} by anonymous dual-model vote");
                return new CanonicalPattern
                {
                    PatternId = string.IsNullOrWhiteSpace(selected.PatternId) ? command : selected.PatternId,
                    Bitmap = BitmapParser.Parse(selected.Bitmap!),
                    BlockColor = color,
                };
            }
            openFeedback = Feedback(openLocal.Error, openReview);
            geminiFeedback = Feedback(gemLocal.Error, gemReview);
        }
        throw new InvalidOperationException($"雙模型交叉評審在 {MaxRounds} 輪後仍未接受任何 bitmap。");
    }

    async Task<Candidate> GenerateOpenAi(string command, string color, int cubes, int dominoes, string feedback)
    {
        var options = new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
            jsonSchemaFormatName: "pattern_candidate",
            jsonSchema: BinaryData.FromString(CandidateSchema()),
            jsonSchemaIsStrict: true) };
        ChatCompletion completion = await openAi.CompleteChatAsync(new List<ChatMessage>
        {
            new SystemChatMessage(GenerationPrompt(cubes, dominoes)),
            new UserChatMessage(GenerationRequest(command, color, feedback)),
        }, options);
        return ParseCandidate(completion.Content[0].Text, "OpenAI");
    }

    async Task<Candidate> GenerateGemini(string command, string color, int cubes, int dominoes, string feedback)
        => ParseCandidate(await CallGemini(GenerationPrompt(cubes, dominoes), GenerationRequest(command, color, feedback), CandidateSchema()), "Gemini");

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

    string GenerationPrompt(int cubes, int dominoes) => $$"""
        Convert the requested text, symbol, or shape into a binary matrix.
        - Use at most {{maxRows}} rows and {{maxCols}} columns; every row has equal length.
        - Each character is only 0 (empty) or 1 (occupied).
        - Prioritize human visual recognizability over a standard font, fixed proportions, or conventional stroke placement.
        - You may adjust stroke position, thickness, turns, and local filling to improve recognizability.
        - Work independently. Do not assume or imitate another candidate.
        - No glyph template, target-specific stroke rule, font, example bitmap, or confusion list is supplied by this application.
        - Available pieces: {{cubes}} cubes and {{dominoes}} dominoes. A cube covers one cell and a domino covers two orthogonally adjacent cells.
        - If no sufficiently recognizable and realizable matrix fits, set feasible=false instead of forcing an answer.
        - With feasible=false bitmap is empty; with feasible=true failure_reason is empty.
        Return only JSON matching the schema.
        """;

    static string GenerationRequest(string command, string color, string feedback) => $"""
        Original user command: {command}
        Block color: {color}
        {(string.IsNullOrWhiteSpace(feedback) ? "" : "Previous cross-review feedback: " + feedback)}
        Generate a fresh candidate for the original command. Feedback is advisory and must not change the target.
        """;

    static string ReviewPrompt() => """
        Independently cross-review a binary bitmap made by another model.
        Judge whether the actual matrix clearly resembles the original request to a human and obeys the matrix constraints.
        Inspect the cells; do not trust candidate ID, author, or claimed label.
        Do not use any application-provided target template, target-specific stroke rule, font, example, or confusion list; none is provided.
        Reject uncertain, ambiguous, broken, or wrong-target candidates. Give concise structural problems and actionable corrections based only on this matrix.
        If you can materially improve the submitted matrix, set has_revision=true and return the complete revised bitmap in revised_bitmap.
        A revision must target the original request and must not merely describe edits in suggested_changes.
        If no revision is needed or possible, set has_revision=false and revised_bitmap=[];
        Return only JSON matching the schema.
        """;

    static string ReviewRequest(string command, Candidate c, string localError) => $"""
        Original user command: {command}
        Candidate bitmap:
        {string.Join(Environment.NewLine, c.Bitmap ?? new())}
        Deterministic format/inventory check: {(string.IsNullOrEmpty(localError) ? "valid" : localError)}
        Review this candidate without seeing the other candidate.
        """;

    static string BallotPrompt() => """
        Score anonymized binary matrices against the original request.
        Judge each actual matrix independently by human visual recognizability and fidelity to the requested target.
        Candidate order and identity carry no meaning. Do not infer authorship.
        Do not use application-provided templates, target-specific rules, fonts, examples, or confusion lists; none is supplied.
        Return exactly one score entry for every candidate index. Scores range from 0 to 1.
        Return only JSON matching the schema.
        """;

    static string BallotRequest(string command, List<Candidate> candidates)
    {
        var text = new StringBuilder($"Original user command: {command}\n");
        for (int i = 0; i < candidates.Count; i++)
        {
            text.AppendLine($"Candidate {i}:");
            foreach (string row in candidates[i].Bitmap ?? new()) text.AppendLine(row);
        }
        return text.ToString();
    }

    Validation Validate(Candidate c, int capacity)
    {
        if (!c.Feasible) return new(false, "generator reported infeasible: " + c.FailureReason);
        if (c.Bitmap == null || c.Bitmap.Count == 0) return new(false, "bitmap is empty");
        try
        {
            int[,] bitmap = BitmapParser.Parse(c.Bitmap);
            if (bitmap.GetLength(0) > maxRows || bitmap.GetLength(1) > maxCols) return new(false, $"bitmap exceeds {maxRows}x{maxCols}");
            int occupied = BitmapParser.CountOccupiedCells(bitmap);
            if (occupied == 0) return new(false, "bitmap has no occupied cell");
            if (occupied > capacity) return new(false, $"bitmap needs {occupied} cells but inventory covers {capacity}");
            return new(true, "");
        }
        catch (Exception ex) { return new(false, "invalid bitmap: " + ex.Message); }
    }

    void AddRevisionIfValid(
        List<Candidate> finalists, Review review, string author, string command, int capacity)
    {
        if (!review.HasRevision || review.RevisedBitmap.Count == 0) return;
        var revision = new Candidate
        {
            PatternId = command,
            Feasible = true,
            Bitmap = review.RevisedBitmap,
            Author = author,
        };
        Validation validation = Validate(revision, capacity);
        if (!validation.Valid)
        {
            Console.WriteLine($"[Layer 1 revision] rejected {author}: {validation.Error}");
            return;
        }
        finalists.Add(revision);
        PrintCandidate(author, revision);
    }

    Candidate SelectByAverageScore(List<Candidate> candidates, Ballot openBallot, Ballot gemBallot)
    {
        var openScores = NormalizeScores(openBallot, candidates.Count);
        var gemScores = NormalizeScores(gemBallot, candidates.Count);
        int bestIndex = 0;
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            double average = (openScores[i] + gemScores[i]) / 2.0;
            Console.WriteLine(
                $"[Layer 1 vote] candidate {i}: OpenAI={openScores[i]:F2}, " +
                $"Gemini={gemScores[i]:F2}, average={average:F2}");
            if (average > bestScore)
            {
                bestScore = average;
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

    Candidate ParseCandidate(string json, string author)
    {
        var c = JsonSerializer.Deserialize<Candidate>(json, jsonOptions) ?? throw new InvalidOperationException($"{author} candidate parse failed.");
        c.Author = author;
        return c;
    }
    Review ParseReview(string json) => JsonSerializer.Deserialize<Review>(json, jsonOptions) ?? throw new InvalidOperationException("Pattern review parse failed.");
    Ballot ParseBallot(string json) => JsonSerializer.Deserialize<Ballot>(json, jsonOptions) ?? throw new InvalidOperationException("Pattern ballot parse failed.");
    static string Feedback(string error, Review r) => string.Join("; ", new[] { error }.Concat(r.StructuralProblems).Concat(r.SuggestedChanges).Where(x => !string.IsNullOrWhiteSpace(x)));

    static void PrintCandidate(string name, Candidate c)
    {
        Console.WriteLine($"[Layer 1 dual] {name} candidate={c.PatternId}, feasible={c.Feasible}");
        foreach (var row in c.Bitmap ?? new()) Console.WriteLine("                  " + row.Replace('0', '□').Replace('1', '■'));
        if (!c.Feasible) Console.WriteLine("                  reason=" + c.FailureReason);
    }
    static void PrintReview(string name, Review r)
    {
        Console.WriteLine($"[Layer 1 cross] {name}: accept={r.Accept}, recognizable={r.Recognizable}, confidence={r.Confidence:F2}, observed={r.ObservedAs}");
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
        },
        required = new[] { "pattern_id", "feasible", "failure_reason", "suggested_rows", "suggested_cols", "suggested_occupied_cells", "bitmap" },
    });
    static string ReviewSchema() => JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["accept"] = new { type = "boolean" }, ["recognizable"] = new { type = "boolean" },
            ["confidence"] = new { type = "number", minimum = 0, maximum = 1 }, ["observed_as"] = new { type = "string" },
            ["structural_problems"] = new { type = "array", items = new { type = "string" } },
            ["suggested_changes"] = new { type = "array", items = new { type = "string" } },
            ["has_revision"] = new { type = "boolean" },
            ["revised_bitmap"] = new { type = "array", items = new { type = "string" } },
        },
        required = new[] { "accept", "recognizable", "confidence", "observed_as", "structural_problems", "suggested_changes", "has_revision", "revised_bitmap" },
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
        [JsonIgnore] public string Author { get; set; } = "";
    }
    sealed class Review
    {
        [JsonPropertyName("accept")] public bool Accept { get; set; }
        [JsonPropertyName("recognizable")] public bool Recognizable { get; set; }
        [JsonPropertyName("confidence")] public double Confidence { get; set; }
        [JsonPropertyName("observed_as")] public string ObservedAs { get; set; } = "";
        [JsonPropertyName("structural_problems")] public List<string> StructuralProblems { get; set; } = new();
        [JsonPropertyName("suggested_changes")] public List<string> SuggestedChanges { get; set; } = new();
        [JsonPropertyName("has_revision")] public bool HasRevision { get; set; }
        [JsonPropertyName("revised_bitmap")] public List<string> RevisedBitmap { get; set; } = new();
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
