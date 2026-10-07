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
    // 座標慣例與物件尺寸是設備事實，拆解、規劃、反思看到同一份（2026-10-07 之前只有規劃有：拆解把桌上方塊的 Z 0.025 當成中心，
    // 推成邊長 5 cm，疊高時每層多算一倍，反思也沒有尺寸可以對照）
    const string AxesFacts = "QR 座標為公尺，X/Y 在桌面上，Z 向上；物件 Z 為頂面高度。";
    const string ObjectSizes = "cube 尺寸 0.025m，domino 為 0.05×0.025×0.025m。";
    const string CoordinateDirections = "QR 工作座標方向：+X=左、-X=右、+Y=後、-Y=前、+Z=上、-Z=下；方向詞一律依 QR 工作座標解讀，不依相機畫面方向。";
    // 夾爪幾何是設備事實，不是解法（2026-09-30 實測：張開時內側約 3.5 cm）
    const string GripperFacts = "夾爪為平行雙指，每根手指厚約 0.015 m、寬約 0.015 m；張開時兩指內側間距只有約 0.035 m：只跨得住物件 0.025 m 寬的邊，跨不住 domino 的 0.05 m 長邊。夾 cube 時兩指落在 cube 的 ±X 兩側；夾 domino 時系統會轉動夾爪讓兩指跨住短邊、落在長邊兩側（horizontal domino 在 ±Y 兩側，vertical domino 在 ±X 兩側）。descend 夾取或放置時，兩指會下降到物件在閉合方向上的兩側：每根手指內側離物件表面只有約 0.005 m、外側離物件中心約 0.0325 m，垂直閉合方向只佔物件中心線兩側各約 0.0075 m；旁邊物件的佔地碰到這兩塊手指範圍就會被撞到，例如閉合方向上相鄰的兩個 cube，中心要相距約 0.045 m 以上。只能夾取上方沒有其他物件的物件。";
    // 桌面範圍與手臂基座位置是設備事實。手臂可達範圍不給（2026-10-02 起）：到不到得了由執行前檢查與 Unity 判斷，
    // LLM 只從失敗的回饋得知
    const string WorkspaceFacts = "桌面工作區是 QR1～QR4 標記圍成的矩形 X=0.000..0.805 m、Y=0.000..0.371 m，相機在這個範圍內量得到物件位置。手臂基座在 QR (0.393, 0.357)。";
    // 排圖形的驗收條件（不劃擺放區，但圖形要排在空的地方）；規劃、反思與整體驗證看到的是同一句
    // 整體驗證怎麼看用積木排的字母（2026-10-07）：手指夾在 cube 的 ±X 兩側，相鄰的塊一定隔著空隙，不能要求邊貼邊；
    // 之前驗證者把 system prompt 的「連接」當成要接觸，排好的立體 L（3 1 1，格距 4 cm）被判斷開。規劃的 LetterDesign 是同樣的規範
    const string DotMatrixReading = "用積木排的字母或圖形像點陣字：每一格放一塊積木，同一個方向用固定格距排，格子之間的空隙不算斷開。";
    const string FigureAcceptance = "目標要求排出圖形（字母、形狀等）時，驗收條件是圖形排在桌面上空著的地方：組成圖形的物件要跟沒有用到的物件明顯分開，不能夾雜在其他物件之間，圖形要能單獨被辨認出來。";
    // 純模擬沒開 Isaac Sim 時，附圖是 TopViewRenderer 依座標畫的示意圖（Program 每個任務開始時設定）
    public bool SchematicImage { get; set; }
    const string SchematicImageNote = "附圖是程式依目前場景座標畫的俯視示意圖（純模擬沒開 Isaac Sim），不是相機照片：上方 = +Y、右方 = +X，方塊上的數字是場景 index。";
    string ImageNote => SchematicImage ? SchematicImageNote : "";
    // 設計階段選出的目標 bitmap（release 260917 的 PatternDesigner / SpatialPatternDesigner，每個任務開始時設定；
    // null = 指令不是排圖形，照常自由規劃）
    public List<string>? TargetBitmap { get; set; }
    string TargetNote => TargetBitmap == null ? "" :
        "\n目標圖形：這個任務的目標圖形已經在設計階段選出，用下面的 bitmap 表示" +
        "（第一列是相機畫面最上方、+Y 那側；往右是 +X，也就是方向詞的「左」）：\n" + string.Join("\n", TargetBitmap) +
        "\n0 = 空；1 = 放一塊；2 以上 = 那格要疊幾層。請把積木排成這個 bitmap，不必自己重新設計圖形：每一格對應桌面上一個位置，" +
        "同一個方向用固定格距（相鄰格的中心距，要讓手指放得下），圖形排在桌面上空著的地方。" +
        "執行前會檢查計畫排出的圖形跟這個 bitmap 完全相同，不同就不執行。";
    // 排字母的規範：每個字母最多 5×5 格、要能辨識；方向以相機畫面為準（跟 terminal 印的 bitmap、整體驗證看的畫面相同）
    const string LetterDesign = "在桌面上排字母時，每個字母設計在最多 5×5 的方格上（高最多 5 格、寬最多 5 格）：每個方向用固定的格距（相鄰格的中心距，格距要讓手指放得下，見夾爪事實），每一格放一個 cube，domino 佔同一排相鄰的兩格；筆畫要像 5×5 點陣字一樣足以辨識是哪個字母。字母形狀以相機畫面判讀：畫面上方是 +Y、畫面右方是 +X（也就是方向詞的「左」），字母在這個方向下要是正的，不能上下或左右顛倒。";
    // 目標是立體的（設計階段 LLM 判斷為 3d）時，上面平面字母的規範不適用，改給立起來的圖形怎麼看（同 260917 SpatialPatternDesigner：
    // 從正面讀、+Z 朝上、+X 由左到右、Y 由前往後）。平面任務的 prompt 不變
    public bool TargetIs3D { get; set; }
    const string UprightReading = "立起來的圖形（往上疊的）以正面判讀：從 -Y 那側（相機畫面下方）水平看過去，往右是 +X、往上是 +Z，不能左右或上下顛倒。";
    string LetterNote => TargetIs3D ? UprightReading : LetterDesign;
    static string Prompt(List<string> rules) => Role + (rules.Count == 0 ? "" :
        "\n本任務依失敗經驗累積的暫定規則如下，按時間由舊到新排列。不衝突的舊規則持續有效；若新規則根據較新證據明確修正或取代舊規則，以新規則優先：\n" + RuleText(rules));
    static string SceneText(List<SceneObject> scene) => JsonSerializer.Serialize(scene.Select((item, index) => new { index, item }));
    public Task<string> Decompose(string goal, List<SceneObject> scene, List<string> rules, string feedback, byte[]? image, string dir) => Call(Prompt(rules),
        $"目標：{goal}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n環境：{AxesFacts}{CoordinateDirections}{ObjectSizes}{WorkspaceFacts}{FigureAcceptance}{LetterNote}{ImageNote}{TargetNote}\n注意：index 只代表這一張目前場景清單的位置，每次重新觀測都可能重排，不能當作跨輪次的物件身分。請自行拆解本輪的子任務，以自然語言描述。", image, dir, "decomposition");
    public Task<string> Plan(string goal, List<SceneObject> scene, string hierarchy, List<string> rules, string feedback, byte[]? image, string dir) => Call(Prompt(rules),
        $"目標：{goal}\n子任務：{hierarchy}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n" +
        $"環境：{AxesFacts}{CoordinateDirections}{ObjectSizes}{WorkspaceFacts}{FigureAcceptance}{LetterNote}{ImageNote}場景 index 只適用本輪目前清單，重新觀測後可能重排。{TargetNote}\n" +
        "每一輪 operation plan 都必須在文字中完整列出每個 target 的實際 (x,y,z) 數值；不得只寫「沿用 P0~P4」、「維持既定目標點」或其他需要查舊輪上下文的代號。\n" +
        "執行介面提供 move_above(location,height_m)、descend(location)、grasp()、release()、lift(location,height_m)、wait(seconds)。location 可為 source 或 target；source 是本次操作所選的來源物件，target 是把來源物件放到的位置，target 的 Z 指來源物件放好後的頂面高度（與場景物件 Z 同一慣例）；height_m 是端點上方的距離。" +
        "參數範圍：height_m 0.05～0.15 m，seconds 0.1～3。同一個操作內的函式依序執行，中途不會重新感知；每個操作開始前系統會重新觀測來源物件位置。" +
        GripperFacts +
        "介面不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條確定且可執行的動作路徑，執行失敗後由下一輪根據新觀測重規劃。整批動作完成後，執行器會在收尾路徑安全時回到 Ready/Home；若收尾路徑不安全，會留在最後的安全抬升位置，不會因此否定已完成的任務動作。規劃內不得自行加入 go_home。這只是設備能力說明，不規定任務拆解、動作順序或完成方式。\n" +
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
        "請根據實際目標與目前觀測，自行規劃操作。迭代執行期間無法向使用者追問或等待補充資料；本輪計畫只能使用提示中已有的目標、場景、結果與暫定規則。請以自然語言自行決定本輪操作與參數。為讓本地執行介面忠實辨識你的決定，每組抓放都必須在同一行寫出「source index N → target (x, y, z)」，並在「執行路徑開始」與「執行路徑結束」之間依相同順序逐行列出要執行的函式呼叫。", image, dir, "operation_plan");
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
        "你是獨立的結果驗證者。只根據原始目標、初始場景及目前實際觀測判斷，不提供操作解法。檢查整體目標與局部幾何完整度（包含直線、連接與堆疊）。若目標要求平移、對齊、放置到座標或距離，實際結果與目標值在 0.02 m（2 公分）以內的量測誤差可接受，不得只因 2 公分內的座標偏差判定失敗；超過 2 公分或方向明顯錯誤才視為幾何未達標。沒有足夠觀測證據或目標含糊時不可通過。第一行僅寫 PASS 或 FAIL，後續自然語言描述觀測問題與不確定性。",
        $"原始目標：{goal}\n初始場景：{SceneText(initial)}\n目前場景：{SceneText(current)}\n環境：{AxesFacts}{CoordinateDirections}{ObjectSizes}{FigureAcceptance}{DotMatrixReading}{(TargetIs3D ? UprightReading : "")}\n" + (image == null ? "影像不可取得，證據不足，不能通過。" : SchematicImage ? SchematicImageNote : "附圖是目前實際相機畫面。"), image, dir, "global_validation");
    // 反思只規定輸出格式，內容寫什麼完全由 LLM 自己判斷（2026-10-02 起）；設備能力與夾爪是事實，放在環境事實裡
    // 每輪操作數、每個操作函式數的上限不給（2026-10-07 起，規劃也一樣），超過時由轉譯失敗得知、訊息也不寫數字
    const string InterfaceFacts = "執行介面：設備只提供 source/target 與 move_above、descend、grasp、release、lift、wait（height_m 0.05～0.15 m、seconds 0.1～3；同一個操作內依序執行，中途不會重新感知），不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條動作路徑。執行器會在收尾路徑安全時自動回 Ready/Home，否則留在最後的安全抬升位置。";
    public Task<string> Reflect(string goal, string plan, string translation, string feedback, List<string> rules, List<SceneObject> scene, string dir) => Call(
        "你是 UR3 機械手臂的任務控制者，負責分析這一輪的結果。輸出格式：第一行只寫 GIVE_UP 或 CONTINUE（GIVE_UP = 放棄這個任務，CONTINUE = 繼續下一輪）；" +
        "第二行起寫什麼由你決定，這些內容會累積起來，交給之後每一輪的規劃與反思參考。",
        $"目標：{goal}\n本輪操作：{plan}\n轉譯結果（內部執行資料）：{translation}\n本輪結果：{feedback}\n本輪最新場景：{SceneText(scene)}\n" +
        $"環境事實：QR 座標為公尺，X/Y 在桌面上，Z 向上；場景物件的 Z 是物件頂面高度。{CoordinateDirections}{ObjectSizes}{WorkspaceFacts}{InterfaceFacts}{GripperFacts}{FigureAcceptance}{LetterNote}{TargetNote}\n" +
        $"既有暫定規則（由舊到新）：{RuleText(rules)}", null, dir, "reflection");
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
