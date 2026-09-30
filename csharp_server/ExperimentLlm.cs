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
    const string TargetZoneDefinition = "黃色擺放區 TargetZone 是桌面上標記的唯一合法擺放區：5×5 格點，格距 0.052 m，格中心 X=0.500,0.552,0.604,0.656,0.708 m，Y=0.020,0.072,0.124,0.176,0.228 m。若目標提到「擺放區」、「黃色區域」或未指定其他放置位置，所有最終 target 中心必須選自這 25 個格點或明確落在 X=0.500..0.708、Y=0.020..0.228 範圍內；不得用全場 min/max、左右方向推論或舊輪 P0/P1 代號取代 TargetZone 定義。";
    static string Prompt(List<string> rules) => Role + (rules.Count == 0 ? "" :
        "\n本任務依失敗經驗累積的暫定規則如下，按時間由舊到新排列。不衝突的舊規則持續有效；若新規則根據較新證據明確修正或取代舊規則，以新規則優先：\n" + RuleText(rules));
    static string SceneText(List<SceneObject> scene) => JsonSerializer.Serialize(scene.Select((item, index) => new { index, item }));
    public Task<string> Decompose(string goal, List<SceneObject> scene, List<string> rules, string feedback, byte[]? image, string dir) => Call(Prompt(rules),
        $"目標：{goal}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n環境：{CoordinateDirections}{TargetZoneDefinition}\n注意：index 只代表這一張目前場景清單的位置，每次重新觀測都可能重排，不能當作跨輪次的物件身分。請自行拆解本輪的子任務，以自然語言描述。", image, dir, "decomposition");
    public Task<string> Plan(string goal, List<SceneObject> scene, string hierarchy, List<string> rules, string feedback, byte[]? image, string dir) => Call(Prompt(rules),
        $"目標：{goal}\n子任務：{hierarchy}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n" +
        $"環境：QR 座標為公尺，X/Y 在桌面上，Z 向上；物件 Z 為頂面高度。{CoordinateDirections}cube 尺寸 0.025m，domino 為 0.05×0.025×0.025m。{TargetZoneDefinition}場景 index 只適用本輪目前清單，重新觀測後可能重排。\n" +
        "擺放區任務的硬性規則：若原始目標要求在擺放區排圖形，請先從 TargetZone 25 個格點中選出本輪所有 target；若因物件佔用需避讓，也只能在 TargetZone 範圍內另選安全格點，不得把圖形移到 TargetZone 外。每一輪 operation plan 都必須在文字中完整列出每個 target 的實際 (x,y,z) 數值；不得只寫「沿用 P0~P4」、「維持既定目標點」或其他需要查舊輪上下文的代號。暫定規則若與 TargetZone 定義衝突，以本段 TargetZone 定義優先。\n" +
        "執行介面提供 move_above(location,height_m)、descend(location)、grasp()、release()、lift(location,height_m)、wait(seconds)。location 可為 source 或 target；source 是本次操作所選的來源物件，target 是把來源物件放到的位置，target 的 Z 指來源物件放好後的頂面高度（與場景物件 Z 同一慣例）；height_m 是端點上方的距離。" +
        "參數範圍：height_m 0.05～0.15 m，seconds 0.1～3；單次最多 50 個操作，每個操作最多 20 個函式。同一個操作內的函式依序執行，中途不會重新感知；每個操作開始前系統會重新觀測來源物件位置。" +
        "介面不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條確定且可執行的動作路徑，執行失敗後由下一輪根據新觀測重規劃。整批任務動作成功完成後，執行器會統一回到 Home；規劃內不得自行加入 go_home。這只是設備能力說明，不規定任務拆解、動作順序或完成方式。\n" +
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
        "請根據實際目標與目前觀測，自行規劃操作。迭代執行期間無法向使用者追問或等待補充資料；本輪計畫只能使用提示中已有的目標、場景、結果與暫定規則。", image, dir, "operation_plan");
    public async Task<TranslatedPlan> Translate(string plan, List<SceneObject> scene, string dir)
    {
        var text = await Call("你是忠實的執行介面轉譯器。只能轉譯明確寫出的操作；不得補上抓放順序、參數、物件選擇、布局、完成狀態或修正。" +
            "function 只能是 move_above、descend、grasp、release、lift、wait；location 只能是 source、target 或不適用時的 null。整批任務成功後由執行器統一回 Home，不轉譯 go_home。" +
            "介面不支援任意 XY 偏移、條件分支或自訂函式。缺失、歧義或無法忠實轉譯時回報 error，steps 為空。",
            $"場景：{SceneText(scene)}\n原始操作描述：{plan}\n" +
            "輸出內部 JSON：{\"error\":\"\",\"steps\":[{\"source_index\":0,\"target\":{\"name\":\"來源的原始name\",\"x\":0.0,\"y\":0.0,\"z\":0.0,\"shape\":\"cube\",\"orientation\":null},\"actions\":[{\"function\":\"函式名\",\"location\":null,\"height_m\":null,\"seconds\":null}]}]}。target 是原始自然語言計畫明確指定的操作位置；若計畫沒有另一位置，複製 source 位置供內部通訊。數字只是格式示意，不是預設值。" +
            "target 的 x、y 為放置位置，z 為來源物件放好後的頂面高度（與場景物件 z 同一慣例）；orientation 只有 domino 需要（horizontal 或 vertical），其他為 null。\n" +
            "Pick and Place 轉譯範例：若場景 index 0 是 {name:black_cube,x:0.20,y:0.10,z:0.02,shape:cube,orientation:null}，原始操作明確要求把它放到 x=0.10,y=0.15,z=0.02，並依序 move_above(source,0.12)、descend(source)、grasp()、lift(source,0.12)、move_above(target,0.12)、descend(target)、release()、lift(target,0.12)，則輸出：\n" +
            "{\"error\":\"\",\"steps\":[{\"source_index\":0,\"target\":{\"name\":\"black_cube\",\"x\":0.10,\"y\":0.15,\"z\":0.02,\"shape\":\"cube\",\"orientation\":null},\"actions\":[{\"function\":\"move_above\",\"location\":\"source\",\"height_m\":0.12,\"seconds\":null},{\"function\":\"descend\",\"location\":\"source\",\"height_m\":null,\"seconds\":null},{\"function\":\"grasp\",\"location\":null,\"height_m\":null,\"seconds\":null},{\"function\":\"lift\",\"location\":\"source\",\"height_m\":0.12,\"seconds\":null},{\"function\":\"move_above\",\"location\":\"target\",\"height_m\":0.12,\"seconds\":null},{\"function\":\"descend\",\"location\":\"target\",\"height_m\":null,\"seconds\":null},{\"function\":\"release\",\"location\":null,\"height_m\":null,\"seconds\":null},{\"function\":\"lift\",\"location\":\"target\",\"height_m\":0.12,\"seconds\":null}]}]}\n" +
            "範例只示範忠實轉譯與欄位對應，不是固定動作計畫。target 的 name、shape、orientation 描述被搬運的來源物件放置後的狀態，不是名為 target 或 tabletop 的新物件。只輸出 JSON。", null, dir, "translation");
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
            foreach (var field in new[] { "x", "y", "z" })
                if (!target.TryGetProperty(field, out _)) throw new InvalidOperationException($"轉譯器 steps[{stepIndex}].target 缺少欄位：{field}。");
            if (!step.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"轉譯器 steps[{stepIndex}] 缺少 actions 陣列。");
            int actionIndex = -1;
            foreach (var action in actions.EnumerateArray())
            {
                actionIndex++;
                if (!action.TryGetProperty("function", out var function) ||
                    function.ValueKind != JsonValueKind.String ||
                    !AllowedFunctions.Contains(function.GetString() ?? ""))
                    throw new InvalidOperationException(
                        $"轉譯器 steps[{stepIndex}].actions[{actionIndex}] 使用不支援的 function。");
                if (action.TryGetProperty("location", out var location) &&
                    location.ValueKind != JsonValueKind.Null &&
                    (location.ValueKind != JsonValueKind.String ||
                     (location.GetString() != "source" && location.GetString() != "target")))
                    throw new InvalidOperationException(
                        $"轉譯器 steps[{stepIndex}].actions[{actionIndex}] 使用不支援的 location。");
            }
        }
        var translated = JsonSerializer.Deserialize<TranslatedPlan>(text.Trim(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("轉譯器没有產生可讀資料。");
        ExperimentChecks.FillTargetIdentity(translated.Steps, scene);
        return translated;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new TranslationContractException("內部轉譯輸出不符合執行介面：" + ex.Message, ex);
        }
    }
    public Task<string> Validate(string goal, List<SceneObject> initial, List<SceneObject> current, byte[]? image, string dir) => Call(
        "你是獨立的結果驗證者。只根據原始目標、初始場景及目前實際觀測判斷，不提供操作解法。檢查整體目標與局部幾何完整度（包含直線、連接與堆疊）。若目標要求平移、對齊、放置到座標或距離，實際結果與目標值在 0.02 m（2 公分）以內的量測誤差可接受，不得只因 2 公分內的座標偏差判定失敗；超過 2 公分或方向明顯錯誤才視為幾何未達標。沒有足夠觀測證據或目標含糊時不可通過。第一行僅寫 PASS 或 FAIL，後續自然語言描述觀測問題與不確定性。",
        $"原始目標：{goal}\n初始場景：{SceneText(initial)}\n目前場景：{SceneText(current)}\n環境：{CoordinateDirections}{TargetZoneDefinition}\n" + (image == null ? "影像不可取得，證據不足，不能通過。" : "附圖是目前實際相機畫面。"), image, dir, "global_validation");
    public Task<string> Reflect(string goal, string plan, string translation, string feedback, List<string> rules, List<SceneObject> scene, string dir) => Call(
        "你是 UR3 任務控制者。分析未達標結果，區分觀測事實與原因假設。「實際流程（系統紀錄）」與「轉譯結果」是系統記錄的觀測事實；沒有出現在紀錄裡的步驟、檢查或量測都沒有發生，不能寫成觀測事實。" +
        "先判斷本輪失敗是否代表目標本質上不可能達成（例如這組結構在物理上不可能疊放穩定），第一行只寫 GIVE_UP 或 CONTINUE；只有清楚的物理不可能證據才能寫 GIVE_UP，只是這次嘗試方法不對或資訊不足時寫 CONTINUE。" +
        "第二行起，CONTINUE 時以自然語言產生本輪失敗摘要以及要新增或修正的暫定規則，GIVE_UP 時說明判斷依據。規則必須來自本輪失敗證據；資訊不足時寫明未知，不能把假設當事實，不能改變原始目標。本輪結果是最新證據，不得把舊輪錯誤誤報為本輪事實。若目標要求平移、對齊、放置到座標或距離，2 公分以內的量測誤差屬可接受範圍；不要把 2 公分內的座標偏差寫成失敗原因或新增修正規則，除非另有明確幾何問題。不衝突的舊規則會繼續使用，不必全部重寫；若本輪新證據推翻舊規則，必須明確指出取代哪一條及原因，較新的修正優先。暫定規則不得改寫 TargetZone 擺放區定義，且不得要求下一輪用未展開的 P0/P1/P2 等代號或「沿用既定目標點」；下一輪規劃必須能直接列出完整 target 座標。迭代執行期間無法向使用者追問或等待補充資料，下一輪規則必須能利用提示中已有的目標、場景與結果直接規劃。場景 index 每次觀測可能重排，不是跨輪次物件身分。設備實際只提供 source/target 與 move_above、descend、grasp、release、lift、wait（height_m 0.05～0.15 m、seconds 0.1～3、每個操作最多 20 個函式；同一個操作內依序執行，中途不會重新感知），不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條確定的動作路徑。整批任務成功後由執行器統一回 Home，暫定規則不得要求規劃器加入 go_home。這是能力邊界，不是預先指定的解題順序。",
        $"目標：{goal}\n本輪操作：{plan}\n轉譯結果（內部執行資料）：{translation}\n本輪結果：{feedback}\n本輪最新場景：{SceneText(scene)}\n環境事實：QR 座標為公尺，X/Y 在桌面上，Z 向上；場景物件的 Z 是物件頂面高度。{CoordinateDirections}{TargetZoneDefinition}\n既有暫定規則（由舊到新）：{RuleText(rules)}", null, dir, "reflection");
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

    static readonly HashSet<string> AllowedFunctions = new(StringComparer.Ordinal)
    {
        "move_above", "descend", "grasp", "release", "lift", "wait"
    };
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
