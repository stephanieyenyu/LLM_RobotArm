using OpenAI.Chat;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

public sealed class ExperimentLlm
{
    readonly ChatClient client;
    static readonly HttpClient ModelHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    const string Role = "你是 UR3 機械手臂的任務控制者。根據使用者目標與目前觀測，自行規劃並完成任務。";
    public ExperimentLlm(string model)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        client = new ChatClient(model, new ApiKeyCredential(key), new OpenAIClientOptions {
            NetworkTimeout = Timeout.InfiniteTimeSpan,
            Transport = new HttpClientPipelineTransport(ModelHttpClient)
        });
    }
    static string Prompt(List<string> rules) => Role + (rules.Count == 0 ? "" : "\n本任務自行產生的暫定規則與失敗摘要：\n" + string.Join("\n", rules));
    static string SceneText(List<SceneObject> scene) => JsonSerializer.Serialize(scene.Select((item, index) => new { index, item }));
    public Task<string> Decompose(string goal, List<SceneObject> scene, List<string> rules, string feedback, byte[]? image, string dir) => Call(Prompt(rules),
        $"目標：{goal}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n請自行拆解本輪的子任務，以自然語言描述。", image, dir, "decomposition");
    public Task<string> Plan(string goal, List<SceneObject> scene, string hierarchy, List<string> rules, string feedback, byte[]? image, string dir) => Call(Prompt(rules),
        $"目標：{goal}\n子任務：{hierarchy}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n" +
        "環境：QR 座標為公尺，X/Y 在桌面上，Z 向上；物件 Z 為頂面高度。cube 尺寸 0.025m，domino 為 0.05×0.025×0.025m。\n" +
        "執行介面提供 move_above(location,height_m)、descend(location)、grasp()、release()、lift(location,height_m)、wait(seconds)、go_home()。location 可為 source 或 target；source/target 是本次操作所選物件與位置的代稱；height_m 是端點上方的距離。這只是設備能力說明，不規定任務拆解、動作順序或完成方式。單次最多 50 個操作，每個操作最多 20 個函式。請以自然語言自行決定本輪操作與參數。", image, dir, "operation_plan");
    public async Task<TranslatedPlan> Translate(string plan, List<SceneObject> scene, string dir)
    {
        var text = await Call("你是忠實的執行介面轉譯器。只能轉譯明確寫出的操作；不得補上抓放順序、參數、物件選擇、布局、完成狀態或修正。location 只能是 source、target 或不適用時的 null。缺失或歧義時回報 error，steps 為空。",
            $"場景：{SceneText(scene)}\n原始操作描述：{plan}\n" +
            "輸出內部 JSON：{\"error\":\"\",\"steps\":[{\"source_index\":0,\"target\":{\"name\":\"來源的原始name\",\"x\":0.0,\"y\":0.0,\"z\":0.0,\"shape\":\"cube\",\"orientation\":null},\"actions\":[{\"function\":\"函式名\",\"location\":null,\"height_m\":null,\"seconds\":null}]}]}。target 是原始自然語言計畫明確指定的操作位置；若計畫沒有另一位置，複製 source 位置供內部通訊。數字只是格式示意，不是預設值。", null, dir, "translation");
        try
        {
        using var document = JsonDocument.Parse(text.Trim());
        if (!document.RootElement.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("轉譯器輸出缺少 steps 陣列。");
        int stepIndex = -1;
        foreach (var step in steps.EnumerateArray())
        {
            stepIndex++;
            if (!step.TryGetProperty("source_index", out var sourceIndex) || !sourceIndex.TryGetInt32(out _))
                throw new InvalidOperationException($"轉譯器 steps[{stepIndex}] 缺少整數 source_index。");
            if (!step.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"轉譯器 steps[{stepIndex}] 缺少 target 物件。");
            foreach (var field in new[] { "name", "shape", "x", "y", "z" })
                if (!target.TryGetProperty(field, out _)) throw new InvalidOperationException($"轉譯器 steps[{stepIndex}].target 缺少欄位：{field}。");
            if (!step.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"轉譯器 steps[{stepIndex}] 缺少 actions 陣列。");
        }
        return JsonSerializer.Deserialize<TranslatedPlan>(text.Trim(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("轉譯器没有產生可讀資料。");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new TranslationContractException("內部轉譯輸出不符合執行介面：" + ex.Message, ex);
        }
    }
    public Task<string> Validate(string goal, List<SceneObject> initial, List<SceneObject> current, byte[]? image, string dir) => Call(
        "你是獨立的結果驗證者。只根據原始目標、初始場景及目前實際觀測判斷，不提供操作解法。檢查整體目標與局部幾何完整度（包含直線、連接與堆疊）。沒有足夠觀測證據或目標含糊時不可通過。第一行僅寫 PASS 或 FAIL，後續自然語言描述觀測問題與不確定性。",
        $"原始目標：{goal}\n初始場景：{SceneText(initial)}\n目前場景：{SceneText(current)}\n" + (image == null ? "影像不可取得，證據不足，不能通過。" : "附圖是目前實際相機畫面。"), image, dir, "global_validation");
    public Task<string> Reflect(string goal, string plan, string feedback, List<string> rules, string dir) => Call(
        "你是 UR3 任務控制者。分析未達標結果，區分觀測事實與原因假設。先判斷本輪失敗是否代表目標本質上不可能達成（例如這組結構在物理上不可能疊放穩定），第一行只寫 GIVE_UP 或 CONTINUE；只有清楚的物理不可能證據才能寫 GIVE_UP，只是這次嘗試方法不對或資訊不足時寫 CONTINUE。第二行起，CONTINUE 時以自然語言產生下一輪暫定規則，GIVE_UP 時說明判斷依據。規則必須來自本輪失敗證據；資訊不足時寫明未知，不能把假設當事實，不能改變原始目標。設備實際只提供 source/target 與 move_above、descend、grasp、release、lift、wait、go_home；這是能力邊界，不是預先指定的解題順序。",
        $"目標：{goal}\n本輪操作：{plan}\n結果：{feedback}\n舊規則：{string.Join("\n", rules)}", null, dir, "reflection");
    async Task<string> Call(string system, string user, byte[]? image, string dir, string name)
    {
        File.WriteAllText(Path.Combine(dir, name + ".system.txt"), system);
        File.WriteAllText(Path.Combine(dir, name + ".user.txt"), user);
        var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(user) };
        if (image != null) parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image), "image/jpeg"));
        Console.WriteLine($"[LLM] {name}…（API 無逾時限制，等待回覆）");
        using var progress = new CancellationTokenSource();
        Task reporter = ReportProgress(name, progress.Token);
        ChatCompletion completion;
        try
        {
            completion = await client.CompleteChatAsync(new List<ChatMessage> { new SystemChatMessage(system), new UserChatMessage(parts) });
        }
        finally
        {
            progress.Cancel();
            try { await reporter; } catch (OperationCanceledException) { }
        }
        Console.WriteLine($"[LLM] {name} 已收到回覆。");
        var text = string.Concat(completion.Content.Select(p => p.Text));
        File.WriteAllText(Path.Combine(dir, name + ".txt"), text);
        File.WriteAllText(Path.Combine(dir, name + ".usage.json"), JsonSerializer.Serialize(completion.Usage));
        return text;
    }

    static async Task ReportProgress(string name, CancellationToken cancellationToken)
    {
        int elapsed = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            elapsed += 15;
            Console.WriteLine($"[LLM] {name} 仍在執行，已等待 {elapsed} 秒（無逾時限制）...");
        }
    }
}
public sealed class TranslatedPlan
{
    public string Error { get; set; } = "";
    public List<TranslatedStep> Steps { get; set; } = new();
}
public sealed class TranslatedStep
{
    [System.Text.Json.Serialization.JsonPropertyName("source_index")]
    public int SourceIndex { get; set; } = -1;
    public SceneObject? Target { get; set; }
    public List<RobotFunctionCall> Actions { get; set; } = new();
}

public sealed class TranslationContractException : Exception
{
    public TranslationContractException(string message, Exception inner) : base(message, inner) { }
}
