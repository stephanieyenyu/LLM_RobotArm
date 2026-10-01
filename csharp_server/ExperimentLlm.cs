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
    static string RuleText(List<string> rules) => string.Join("\n\n",
        rules.Select((rule, index) => $"--- 暫定規則版本 {index + 1} ---\n{rule}"));
    const string CoordinateDirections = "QR 工作座標方向：+X=左、-X=右、+Y=後、-Y=前、+Z=上、-Z=下；方向詞一律依 QR 工作座標解讀，不依相機畫面方向。";
    const string TargetZoneDefinition = "黃色擺放區 TargetZone 是桌面上標記的唯一合法擺放區，範圍 X=0.500..0.708 m、Y=0.020..0.228 m。若目標提到「擺放區」、「黃色區域」或未指定其他放置位置，所有最終 target 中心都必須落在這個範圍內；格局、間距與排列方式由你自行依任務需求決定，不得用全場 min/max、左右方向推論或舊輪 P0/P1 代號取代 TargetZone 定義。";
    static string Prompt(List<string> rules) => Role + (rules.Count == 0 ? "" :
        "\n本任務依失敗經驗累積的暫定規則如下，按時間由舊到新排列。不衝突的舊規則持續有效；若新規則根據較新證據明確修正或取代舊規則，以新規則優先：\n" + RuleText(rules));
    static string SceneText(List<SceneObject> scene) => JsonSerializer.Serialize(scene.Select((item, index) => new { index, item }));
    public Task<string> Decompose(string goal, List<SceneObject> scene, List<string> rules, string feedback, byte[]? image, string dir) => Call(Prompt(rules),
        $"目標：{goal}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n環境：{CoordinateDirections}{TargetZoneDefinition}\n注意：index 只代表這一張目前場景清單的位置，每次重新觀測都可能重排，不能當作跨輪次的物件身分。請自行拆解本輪的子任務，以自然語言描述。", image, dir, "decomposition");
    public Task<string> Plan(string goal, List<SceneObject> scene, string hierarchy, List<string> rules, string feedback, byte[]? image, string dir) => Call(Prompt(rules),
        $"目標：{goal}\n子任務：{hierarchy}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n" +
        $"環境：QR 座標為公尺，X/Y 在桌面上，Z 向上；物件 Z 為頂面高度。{CoordinateDirections}cube 尺寸 0.025m，domino 為 0.05×0.025×0.025m。{TargetZoneDefinition}場景 index 只適用本輪目前清單，重新觀測後可能重排。\n" +
        "擺放區任務的硬性規則：若原始目標要求在擺放區排圖形，所有本輪 target 中心都必須落在 TargetZone 範圍內，間距與排列方式由你依圖形需求自行決定；若因物件佔用需避讓，也只能在 TargetZone 範圍內另選安全位置，不得把圖形移到 TargetZone 外。每一輪 operation plan 都必須在文字中完整列出每個 target 的實際 (x,y,z) 數值；不得只寫「沿用 P0~P4」、「維持既定目標點」或其他需要查舊輪上下文的代號。暫定規則若與 TargetZone 定義衝突，以本段 TargetZone 定義優先。\n" +
        "執行介面提供 move_above(location,height_m)、descend(location)、grasp()、release()、lift(location,height_m)、wait(seconds)。location 可為 source 或 target；source/target 是本次操作所選物件與位置的代稱；height_m 是端點上方的距離。介面不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條確定且可執行的動作路徑，執行失敗後由下一輪根據新觀測重規劃。整批動作完成後，執行器會在收尾路徑安全時回到 Ready/Home；若收尾路徑不安全，會留在最後的安全抬升位置，不會因此否定已完成的任務動作。規劃內不得自行加入 go_home。這只是設備能力說明，不規定任務拆解、動作順序或完成方式。\n" +
        "低階操作範例（僅示範介面語意，不是固定解法）：\n" +
        "若要將目前場景 index 0 的物件移到桌面位置 x=0.10、y=0.15、z=0.02：\n" +
        "move_above(source, 0.12)\n" +
        "descend(source)\n" +
        "grasp()\n" +
        "lift(source, 0.12)\n" +
        "move_above(target, 0.12)\n" +
        "descend(target)\n" +
        "release()\n" +
        "lift(target, 0.12)\n" +
        "請根據實際目標與目前觀測，自行規劃操作。迭代執行期間無法向使用者追問或等待補充資料；本輪計畫只能使用提示中已有的目標、場景、結果與暫定規則。單次最多 50 個操作，每個操作最多 20 個函式。請以自然語言自行決定本輪操作與參數。為讓本地執行介面忠實辨識你的決定，每組抓放都必須在同一行寫出「source index N → target (x, y, z)」，並在「執行路徑開始」與「執行路徑結束」之間依相同順序逐行列出要執行的函式呼叫。", image, dir, "operation_plan");
    public Task<TranslatedPlan> Translate(string plan, List<SceneObject> scene, string dir)
    {
        File.WriteAllText(Path.Combine(dir, "translation.system.txt"),
            "本地確定性自然語言轉譯器；沒有呼叫 LLM，也沒有要求模型填寫 JSON Schema。");
        File.WriteAllText(Path.Combine(dir, "translation.user.txt"), plan);
        var translated = NaturalLanguagePlanAdapter.Translate(plan, scene);
        File.WriteAllText(Path.Combine(dir, "translation.txt"),
            JsonSerializer.Serialize(translated, new JsonSerializerOptions { WriteIndented = true }));
        return Task.FromResult(translated);
    }
    public Task<string> Validate(string goal, List<SceneObject> initial, List<SceneObject> current, byte[]? image, string dir) => Call(
        "你是獨立的結果驗證者。只根據原始目標、初始場景及目前實際觀測判斷，不提供操作解法。檢查整體目標與局部幾何完整度（包含直線、連接與堆疊）。若目標要求平移、對齊、放置到座標或距離，實際結果與目標值在 0.02 m（2 公分）以內的量測誤差可接受，不得只因 2 公分內的座標偏差判定失敗；超過 2 公分或方向明顯錯誤才視為幾何未達標。對 TargetZone 邊界也必須套用相同容差：物件中心與 TargetZone 邊界的差距在 0.02 m 以內時，視為在擺放區內；不得只因 x 略小於 0.500 或其他邊界數值而判定失敗。沒有足夠觀測證據或目標含糊時不可通過。第一行僅寫 PASS 或 FAIL，後續自然語言描述觀測問題與不確定性。",
        $"原始目標：{goal}\n初始場景：{SceneText(initial)}\n目前場景：{SceneText(current)}\n環境：{CoordinateDirections}{TargetZoneDefinition}\n" + (image == null ? "影像不可取得，證據不足，不能通過。" : "附圖是目前實際相機畫面。"), image, dir, "global_validation");
    public Task<string> Reflect(string goal, string plan, string feedback, List<string> rules, List<SceneObject> scene, string dir) => Call(
        "你是 UR3 任務控制者。分析未達標結果，區分觀測事實與原因假設，以自然語言產生本輪失敗摘要以及要新增或修正的暫定規則。規則必須來自本輪失敗證據；資訊不足時寫明未知，不能把假設當事實，不能改變原始目標。本輪結果是最新證據，不得把舊輪錯誤誤報為本輪事實。若目標要求平移、對齊、放置到座標或距離，2 公分以內的量測誤差屬可接受範圍；不要把 2 公分內的座標偏差寫成失敗原因或新增修正規則，除非另有明確幾何問題。物件中心與本輪計畫指定的 target 座標相差 0.02 m 以內時，必須視為已達成，不得產生再次對中、重複抓放或只因些微座標偏差而移動該物件的規則。不衝突的舊規則會繼續使用，不必全部重寫；若本輪新證據推翻舊規則，必須明確指出取代哪一條及原因，較新的修正優先。暫定規則不得改寫 TargetZone 擺放區定義，且不得要求下一輪用未展開的 P0/P1/P2 等代號或「沿用既定目標點」；下一輪規劃必須能直接列出完整 target 座標。迭代執行期間無法向使用者追問或等待補充資料，下一輪規則必須能利用提示中已有的目標、場景與結果直接規劃。場景 index 每次觀測可能重排，不是跨輪次物件身分。設備實際只提供 source/target 與 move_above、descend、grasp、release、lift、wait，不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條確定的動作路徑。執行器會在收尾路徑安全時自動回 Ready/Home，否則留在最後的安全抬升位置；暫定規則不得要求規劃器加入 go_home。這是能力邊界，不是預先指定的解題順序。",
        $"目標：{goal}\n本輪操作：{plan}\n本輪結果：{feedback}\n本輪最新場景：{SceneText(scene)}\n環境事實：QR 座標為公尺，X/Y 在桌面上，Z 向上；場景物件的 Z 是物件頂面高度。{CoordinateDirections}{TargetZoneDefinition}\n既有暫定規則（由舊到新）：{RuleText(rules)}", null, dir, "reflection");
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
