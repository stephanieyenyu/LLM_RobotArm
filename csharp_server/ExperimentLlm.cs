using OpenAI.Chat;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    const string CoordinateDirections = "QR 工作座標方向：+X=左、-X=右、+Y=後、-Y=前、+Z=上、-Z=下；方向詞一律依 QR 工作座標解讀，不依相機畫面方向。";
    // 夾爪幾何是設備事實，不是解法（2026-09-30 實測：張開時內側約 3.5 cm）
    const string GripperFacts = "夾爪為平行雙指，每根手指厚約 0.015 m、寬約 0.015 m；張開時兩指內側間距只有約 0.035 m：只跨得住物件 0.025 m 寬的邊，跨不住 domino 的 0.05 m 長邊。夾 cube 時兩指落在 cube 的 ±X 兩側；夾 domino 時系統會轉動夾爪讓兩指跨住短邊、落在長邊兩側（horizontal domino 在 ±Y 兩側，vertical domino 在 ±X 兩側）。descend 夾取或放置時，兩指會下降到物件在閉合方向上的兩側：每根手指內側離物件表面只有約 0.005 m、外側離物件中心約 0.0325 m，垂直閉合方向只佔物件中心線兩側各約 0.0075 m；旁邊物件的佔地碰到這兩塊手指範圍就會被撞到，例如閉合方向上相鄰的兩個 cube，中心要相距約 0.045 m 以上。只能夾取上方沒有其他物件的物件。";
    // 桌面座標資訊
    const string WorkspaceFacts = "桌面工作區是 QR1～QR4 標記圍成的矩形 X=0.000..0.805 m、Y=0.000..0.371 m，相機在這個範圍內量得到物件位置。手臂基座在 QR 工作座標 (X=0.393 m, Y=0.357 m)。";
    // 排圖形的驗收條件（不劃擺放區，但圖形要排在空的地方）；規劃、反思與整體驗證看到的是同一句
    const string FigureAcceptance = "目標要求排出圖形（字母、形狀等）時，驗收條件是圖形排在桌面上空著的地方：組成圖形的物件要跟沒有用到的物件明顯分開，不能夾雜在其他物件之間，圖形要能單獨被辨認出來。本架構沒有跨輪次的記憶，每一輪都是從頭開始的獨立新嘗試，不會接續上一輪已經排好的部分；因此這個圖形需要的所有積木都必須在同一輪 operation plan 裡一次全部規劃、一次全部執行完，不能只規劃其中一部分、留到下一輪才排剩下的，否則永遠不會有一輪是完整的圖形。";
    // 純模擬沒開 Isaac Sim 時，附圖是 TopViewRenderer 依座標畫的示意圖（Program 每個任務開始時設定）
    public bool SchematicImage { get; set; }
    public bool CanonicalBitmap { get; set; }
    const string SchematicImageNote = "附圖是程式依目前場景座標畫的俯視示意圖（純模擬沒開 Isaac Sim），不是相機照片：上方 = +Y、右方 = +X，方塊上的數字是場景 index。";
    string ImageNote => SchematicImage ? SchematicImageNote : "";
    // 排字母的規範：每個字母最多 5×5 格、要能辨識；方向以相機畫面為準（跟 terminal 印的 bitmap、整體驗證看的畫面相同）
    const string LetterDesign = "在桌面上排字母時，每個字母設計在最多 5×5 的方格上（高最多 5 格、寬最多 5 格）：每個方向用固定的格距（相鄰格的中心距，格距要讓手指放得下，見夾爪事實），每一格放一個 cube，domino 佔同一排相鄰的兩格；筆畫要像 5×5 點陣字一樣足以辨識是哪個字母。字母是否正立直接用 X/Y 座標數值判斷，這跟前面方向詞（左右前後）的定義是分開的兩件事，不要混用：相機畫面上方對應 Y 值較大、下方對應 Y 值較小；畫面右方對應 X 值較大、左方對應 X 值較小。字母要在這個座標對應下正立，不能上下或左右顛倒——例如正立的大寫 L：直筆畫沿 Y 方向排列，橫筆畫接在直筆畫 Y 值最小（畫面最下方）的那一端，往 X 值變大（畫面右方）的方向延伸；橫筆畫不能接在 Y 值最大（畫面最上方）的那一端，否則會變成上下顛倒的字母。";
    static string SceneText(List<SceneObject> scene) => JsonSerializer.Serialize(scene.Select((item, index) => new { index, item }));
    public Task<string> Decompose(string goal, List<SceneObject> scene, string feedback, byte[]? image, string dir) => Call(Role,
        $"目標：{goal}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n環境：{CoordinateDirections}{WorkspaceFacts}{FigureAcceptance}{(CanonicalBitmap ? "" : LetterDesign)}{ImageNote}\n注意：index 只代表這一張目前場景清單的位置，每次重新觀測都可能重排，不能當作跨輪次的物件身分。請自行拆解本輪的子任務，以自然語言描述。若本輪子任務需要在桌面排出平面字母、符號或圖形，另以獨立一行寫「需要平面 bitmap」，圖形會由雙 LLM 生成；其他任務不寫此行。", image, dir, "decomposition");
    // 0% 架構：規劃不再寫自然語言給本地規則解析，LLM 直接輸出符合 schema 的結構化資料——
    // 動作順序、高度、target 的 x/y/z/orientation 都是 LLM 自己的輸出內容，沒有中間的人寫規則在猜測或補值。
    public Task<string> Plan(string goal, List<SceneObject> scene, string hierarchy, string feedback, byte[]? image, string dir) => Call(Role,
        $"目標：{goal}\n子任務：{hierarchy}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n" +
        $"環境：QR 座標為公尺，X/Y 在桌面上，Z 向上；物件 Z 為頂面高度。{CoordinateDirections}cube 尺寸 0.025m，domino 為 0.05×0.025×0.025m。{WorkspaceFacts}{FigureAcceptance}{(CanonicalBitmap ? "" : LetterDesign)}{ImageNote}場景 index 只適用本輪目前清單，重新觀測後可能重排。\n" +
        "執行介面提供 move_above(location,height_m)、descend(location)、grasp()、release()、lift(location,height_m)、wait(seconds)。location 可為 source 或 target；source 是本次操作所選的來源物件，target 是把來源物件放到的位置，target 的 z 指來源物件放好後的頂面高度（與場景物件 z 同一慣例）；height_m 是端點上方的距離。" +
        "參數範圍：height_m 0.05～0.15 m，seconds 0.1～3；單次最多 50 個操作，每個操作最多 20 個函式。同一個操作內的函式依序執行，中途不會重新感知；每個操作開始前系統會重新觀測來源物件位置。" +
        GripperFacts +
        "介面不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條確定且可執行的動作路徑，執行失敗後由下一輪根據新觀測重規劃。整批動作完成後，執行器會在收尾路徑安全時回到 Ready/Home；若收尾路徑不安全，會留在最後的安全抬升位置，不會因此否定已完成的任務動作。規劃內不得自行加入 go_home。這只是設備能力說明，不規定任務拆解、動作順序或完成方式；抬升、移動、下降、抓取／放開要怎麼排序、高度怎麼選，由你自己決定。\n" +
        "請根據實際目標與目前觀測，自行規劃操作，直接輸出符合給定 JSON schema 的結構化資料，不要輸出 schema 以外的文字。迭代執行期間無法向使用者追問或等待補充資料；本輪計畫只能使用提示中已有的目標、場景與結果，每一輪都是獨立的新嘗試，不會沿用先前輪次的任何判斷。steps 依你決定的順序列出每一次抓放：source_index 是本次操作所選來源在目前場景清單的位置；target 的 x/y/z 是你決定的放置座標，orientation 只在來源是 domino 時才有意義（vertical 或 horizontal，決定抓取時要不要轉動夾爪；來源是 cube 時填 null）；actions 是你決定的完整函式呼叫順序。" +
        (CanonicalBitmap
            ? "bitmap_grid 填入你為上面雙 LLM 選出的目標 bitmap 決定的實際擺放座標系：left_x/top_y 是 bitmap 第 0 列第 0 欄的格中心（即使該格是空白）、cell_x/cell_y 是你選的格距、top_z 是放置後頂面高度，都填實際公尺數值；這個欄位只描述座標系，不是手臂動作，steps 仍要照上面的規則逐一列出。"
            : "這一輪不需要排圖形，bitmap_grid 填 null。"),
        image, dir, "operation_plan", PlanResponseFormat);
    public Task<TranslatedPlan> Translate(string plan, List<SceneObject> scene, string dir)
    {
        File.WriteAllText(Path.Combine(dir, "translation.system.txt"),
            "LLM 直接輸出結構化 JSON（OpenAI structured output，schema 見 operation_plan 呼叫）；這裡只檢查欄位型別與範圍，不猜測、不補值。");
        File.WriteAllText(Path.Combine(dir, "translation.user.txt"), plan);
        var translated = ParsePlannedOperations(plan, scene);
        File.WriteAllText(Path.Combine(dir, "translation.txt"),
            JsonSerializer.Serialize(translated, new JsonSerializerOptions { WriteIndented = true }));
        return Task.FromResult(translated);
    }
    static TranslatedPlan ParsePlannedOperations(string json, IReadOnlyList<SceneObject> scene)
    {
        PlannedOperations? parsed;
        try { parsed = JsonSerializer.Deserialize<PlannedOperations>(json); }
        catch (JsonException ex) { return new TranslatedPlan { Error = "LLM 輸出的 JSON 無法解析：" + ex.Message }; }
        if (parsed == null || parsed.Steps.Count == 0)
            return new TranslatedPlan { Error = "LLM 沒有輸出任何操作步驟。" };
        if (parsed.Steps.Count > 50)
            return new TranslatedPlan { Error = $"操作步驟有 {parsed.Steps.Count} 個，超過單次 50 步上限。" };
        var translated = new TranslatedPlan();
        for (int i = 0; i < parsed.Steps.Count; i++)
        {
            var step = parsed.Steps[i];
            if (step.SourceIndex < 0 || step.SourceIndex >= scene.Count)
                return new TranslatedPlan { Error = $"第 {i + 1} 個操作：來源 index {step.SourceIndex} 超出目前場景範圍（0～{scene.Count - 1}）。" };
            if (step.Target == null)
                return new TranslatedPlan { Error = $"第 {i + 1} 個操作：沒有提供 target。" };
            if (step.Actions.Count == 0 || step.Actions.Count > 20)
                return new TranslatedPlan { Error = $"第 {i + 1} 個操作：有 {step.Actions.Count} 個函式呼叫，必須是 1～20 個。" };
            var source = scene[step.SourceIndex];
            translated.Steps.Add(new TranslatedStep
            {
                SourceIndex = step.SourceIndex,
                Target = new SceneObject
                {
                    Name = source.Name, X = step.Target.X, Y = step.Target.Y, Z = step.Target.Z,
                    // shape 是來源物件本身的物理屬性，搬動不會改變，不交給 LLM 決定；
                    // orientation（domino 要不要轉向）才是放置時真正要決定的事，交給 LLM。
                    Shape = source.Shape, Orientation = step.Target.Orientation ?? source.Orientation, SkewDeg = source.SkewDeg,
                },
                Actions = step.Actions,
            });
        }
        if (parsed.BitmapGrid != null)
        {
            var g = parsed.BitmapGrid;
            if (!double.IsFinite(g.LeftX) || !double.IsFinite(g.TopY) || !double.IsFinite(g.CellX) ||
                !double.IsFinite(g.CellY) || !double.IsFinite(g.TopZ) || g.CellX <= 0 || g.CellY <= 0)
                return new TranslatedPlan { Error = "bitmap_grid 格距須為正數且參數須為有限數值。" };
            translated.BitmapGrid = new BitmapGridChoice
                { LeftX = g.LeftX, TopY = g.TopY, CellX = g.CellX, CellY = g.CellY, TopZ = g.TopZ };
        }
        return translated;
    }
    static ChatCompletionOptions PlanResponseFormat => new() { ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
        jsonSchemaFormatName: "operation_plan", jsonSchema: BinaryData.FromString(PlanSchema()), jsonSchemaIsStrict: true) };
    static string PlanSchema() => JsonSerializer.Serialize(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["bitmap_grid"] = new
            {
                type = new[] { "object", "null" }, additionalProperties = false,
                properties = new Dictionary<string, object>
                {
                    ["left_x"] = new { type = "number" }, ["top_y"] = new { type = "number" },
                    ["cell_x"] = new { type = "number" }, ["cell_y"] = new { type = "number" },
                    ["top_z"] = new { type = "number" },
                },
                required = new[] { "left_x", "top_y", "cell_x", "cell_y", "top_z" },
            },
            ["steps"] = new
            {
                type = "array",
                items = new
                {
                    type = "object", additionalProperties = false,
                    properties = new Dictionary<string, object>
                    {
                        ["source_index"] = new { type = "integer" },
                        ["target"] = new
                        {
                            type = "object", additionalProperties = false,
                            properties = new Dictionary<string, object>
                            {
                                ["x"] = new { type = "number" }, ["y"] = new { type = "number" }, ["z"] = new { type = "number" },
                                ["orientation"] = new { type = new[] { "string", "null" }, @enum = new[] { "vertical", "horizontal", null } },
                            },
                            required = new[] { "x", "y", "z", "orientation" },
                        },
                        ["actions"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object", additionalProperties = false,
                                properties = new Dictionary<string, object>
                                {
                                    ["function"] = new { type = "string", @enum = new[] { "move_above", "descend", "grasp", "release", "lift", "wait" } },
                                    ["location"] = new { type = new[] { "string", "null" }, @enum = new[] { "source", "target", null } },
                                    ["height_m"] = new { type = new[] { "number", "null" } },
                                    ["seconds"] = new { type = new[] { "number", "null" } },
                                },
                                required = new[] { "function", "location", "height_m", "seconds" },
                            },
                        },
                    },
                    required = new[] { "source_index", "target", "actions" },
                },
            },
        },
        required = new[] { "bitmap_grid", "steps" },
    });
    public Task<string> Validate(string goal, List<SceneObject> initial, List<SceneObject> current, byte[]? image, string dir) => Call(
        "你是獨立的結果驗證者。只根據原始目標、初始場景及目前實際觀測判斷，不提供操作解法。檢查整體目標與局部幾何完整度（包含直線、連接與堆疊）。若目標要求平移、對齊、放置到座標或距離，實際結果與目標值在 0.02 m（2 公分）以內的量測誤差可接受，不得只因 2 公分內的座標偏差判定失敗；超過 2 公分或方向明顯錯誤才視為幾何未達標。沒有足夠觀測證據或目標含糊時不可通過。第一行僅寫 PASS 或 FAIL，後續自然語言描述觀測問題與不確定性。",
        $"原始目標：{goal}\n初始場景：{SceneText(initial)}\n目前場景：{SceneText(current)}\n環境：{CoordinateDirections}{FigureAcceptance}\n" + (image == null ? "影像不可取得，證據不足，不能通過。" : SchematicImage ? SchematicImageNote : "附圖是目前實際相機畫面。"), image, dir, "global_validation");
    async Task<string> Call(string system, string user, byte[]? image, string dir, string name, ChatCompletionOptions? options = null)
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
            var messages = new List<ChatMessage> { new SystemChatMessage(system), new UserChatMessage(parts) };
            completion = options != null
                ? await client.CompleteChatAsync(messages, options)
                : await client.CompleteChatAsync(messages);
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
    // LLM 自己選的 bitmap 擺放座標系（排圖形任務才有）；取代舊版用 regex 從文字裡找 bitmap_grid(...) 的做法。
    public BitmapGridChoice? BitmapGrid { get; set; }
}
public sealed class BitmapGridChoice
{
    public double LeftX { get; set; }
    public double TopY { get; set; }
    public double CellX { get; set; }
    public double CellY { get; set; }
    public double TopZ { get; set; }
}
public sealed class TranslatedStep
{
    [JsonPropertyName("source_index")]
    public int SourceIndex { get; set; } = -1;
    public SceneObject? Target { get; set; }
    public List<RobotFunctionCall> Actions { get; set; } = new();
}

// LLM 直接輸出的結構化計畫（operation_plan 的 JSON schema），轉成 TranslatedPlan 前的原始形狀。
sealed class PlannedOperations
{
    [JsonPropertyName("bitmap_grid")] public PlannedBitmapGrid? BitmapGrid { get; set; }
    [JsonPropertyName("steps")] public List<PlannedStep> Steps { get; set; } = new();
}
sealed class PlannedBitmapGrid
{
    [JsonPropertyName("left_x")] public double LeftX { get; set; }
    [JsonPropertyName("top_y")] public double TopY { get; set; }
    [JsonPropertyName("cell_x")] public double CellX { get; set; }
    [JsonPropertyName("cell_y")] public double CellY { get; set; }
    [JsonPropertyName("top_z")] public double TopZ { get; set; }
}
sealed class PlannedStep
{
    [JsonPropertyName("source_index")] public int SourceIndex { get; set; } = -1;
    [JsonPropertyName("target")] public PlannedTarget? Target { get; set; }
    [JsonPropertyName("actions")] public List<RobotFunctionCall> Actions { get; set; } = new();
}
sealed class PlannedTarget
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }
}

public sealed class TranslationContractException : Exception
{
    public TranslationContractException(string message, Exception inner) : base(message, inner) { }
}
