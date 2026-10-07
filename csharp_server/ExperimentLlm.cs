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
    public Task<string> Plan(string goal, List<SceneObject> scene, string hierarchy, string feedback, byte[]? image, string dir) => Call(Role,
        $"目標：{goal}\n子任務：{hierarchy}\n目前場景：{SceneText(scene)}\n上次結果：{feedback}\n" +
        $"環境：QR 座標為公尺，X/Y 在桌面上，Z 向上；物件 Z 為頂面高度。{CoordinateDirections}cube 尺寸 0.025m，domino 為 0.05×0.025×0.025m。{WorkspaceFacts}{FigureAcceptance}{(CanonicalBitmap ? "" : LetterDesign)}{ImageNote}場景 index 只適用本輪目前清單，重新觀測後可能重排。\n" +
        "每一輪 operation plan 都必須在文字中完整列出每個 target 的實際 (x,y,z) 數值；不得只寫「沿用 P0~P4」、「維持既定目標點」或其他需要查舊輪上下文的代號。\n" +
        "執行介面提供 move_above(location,height_m)、descend(location)、grasp()、release()、lift(location,height_m)、wait(seconds)。location 可為 source 或 target；source 是本次操作所選的來源物件，target 是把來源物件放到的位置，target 的 Z 指來源物件放好後的頂面高度（與場景物件 Z 同一慣例）；height_m 是端點上方的距離。" +
        "每個移動動作的 TCP 位置與方向都由你決定：列出 tcp_pose(x,y,z,rx,ry,rz)。x/y/z 為 QR 工作座標的實際 TCP 座標（公尺），不是積木頂面；rx/ry/rz 為 UR 基座座標的旋轉向量（弧度），不是 Euler 角。URSim 負責 IK，不要提供關節角度。請自行決定所有需要的中間 TCP 點，執行器不另補避障路徑。\n" +
        "參數範圍：height_m 0.05～0.15 m，seconds 0.1～3；單次最多 50 個操作，每個操作最多 20 個函式。同一個操作內的函式依序執行，中途不會重新感知；每個操作開始前系統會重新觀測來源物件位置。" +
        GripperFacts +
        "介面不提供條件分支或同輪失敗後續跑；每輪只能提交一條確定且可執行的動作路徑，執行失敗後由下一輪根據新觀測重規劃。整批最後的 TCP 位置與方向仍由你決定，執行器不附加 Ready/Home 收尾。規劃內不得自行加入 go_home。這只是設備能力說明，不規定任務拆解、動作順序或完成方式。\n" +
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
        "請根據實際目標與目前觀測，自行規劃操作。迭代執行期間無法向使用者追問或等待補充資料；本輪計畫只能使用提示中已有的目標、場景與結果，每一輪都是獨立的新嘗試，不會沿用先前輪次的任何判斷。請以自然語言完整描述你選的來源 index、目標座標，以及各操作依序執行的函式與參數。動作順序與高度都由你自行決定。", image, dir, "operation_plan");
    public async Task<TranslatedPlan> Translate(string plan, List<SceneObject> scene, string dir,
        string goal, string hierarchy)
    {
        const string system = """
            你是 UR3 機械手臂的執行資料規劃者。根據原始目標、子任務、目前場景與操作計畫，自行決定並輸出完整執行資料。
            來源積木、目標座標、動作順序和每個參數都由你決定；沿用操作計畫已有的明確決定，若缺少必要決定，依提供的目標與觀測補全。不要依固定抓放模板補動作。
            若子任務提供選定的 bitmap，沿用該 bitmap；操作計畫的 bitmap_grid 是這張圖的擺放座標，不得另改圖形或擺放座標。
            只輸出 JSON 物件，根欄位為 Error（字串）及 Steps（陣列）。可執行時 Error 為空；不可執行或資訊不足時 Error 說明原因且 Steps 為空。
            每筆 Steps 包含 source_index（目前場景的整數 index）、Target（物件）及 Actions（陣列）。
            Target 包含 x、y、z（公尺；z 為放好後頂面高度）、name、shape、orientation、skew_deg。身分依你選的來源積木，位置與方向由你決定。
            每筆 Actions 包含 function、location、height_m、seconds；不用的參數填 null。
            每個 move_above、descend、lift 動作必須另有 tcp_pose 物件，包含你決定的完整 x,y,z,rx,ry,rz。非移動動作 tcp_pose=null。
            tcp_pose 的 x/y/z 是 QR 工作座標的實際 TCP 位置（公尺），z 是 TCP 本身高度，不是物件頂面。rx/ry/rz 是 UR 基座座標的旋轉向量（弧度），不是 Euler 角。
            URSim 依此姿態自動計算關節 IK。每個 TCP 點都是你的決定，程式不改方向、不補抬升或中間避障點。若需要中間點，請自行列出額外移動動作。
            可用 function：move_above、descend、grasp、release、lift、wait。
            move_above/lift 使用 location=source或target 及 height_m；descend 使用 location；grasp/release 無參數；wait 使用 seconds。
            height_m 是端點上方的距離，範圍 0.05～0.15 公尺；seconds 範圍 0.1～3。單批最多 50 個操作，每個操作最多 20 個函式。
            不提供關節角度，不加入介面未提供的動作。不要讀取或假設前一輪計畫。
            """;
        var response = await Call(system,
            $"原始目標：{goal}\n子任務：{hierarchy}\n目前場景：{SceneText(scene)}\n操作計畫：{plan}\n{CoordinateDirections}{WorkspaceFacts}{GripperFacts}",
            null, dir, "translation", jsonResponse: true);
        try
        {
            var result = JsonSerializer.Deserialize<TranslatedPlan>(response,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("LLM 執行資料為 null。");
            if (result.Steps == null || result.Steps.Any(step => step == null || step.Target == null ||
                step.Actions == null || step.Actions.Any(action => action == null)))
                throw new JsonException("LLM 執行資料的 Steps、Target 或 Actions 有缺漏或 null。");
            return result;
        }
        catch (JsonException ex)
        {
            throw new TranslationContractException("LLM 執行資料不是有效 JSON：" + ex.Message, ex);
        }
    }
    public Task<string> Validate(string goal, List<SceneObject> initial, List<SceneObject> current, byte[]? image, string dir) => Call(
        "你是獨立的結果驗證者。只根據原始目標、初始場景及目前實際觀測判斷，不提供操作解法。檢查整體目標與局部幾何完整度（包含直線、連接與堆疊）。若目標要求平移、對齊、放置到座標或距離，實際結果與目標值在 0.02 m（2 公分）以內的量測誤差可接受，不得只因 2 公分內的座標偏差判定失敗；超過 2 公分或方向明顯錯誤才視為幾何未達標。沒有足夠觀測證據或目標含糊時不可通過。第一行僅寫 PASS 或 FAIL，後續自然語言描述觀測問題與不確定性。",
        $"原始目標：{goal}\n初始場景：{SceneText(initial)}\n目前場景：{SceneText(current)}\n環境：{CoordinateDirections}{FigureAcceptance}\n" + (image == null ? "影像不可取得，證據不足，不能通過。" : SchematicImage ? SchematicImageNote : "附圖是目前實際相機畫面。"), image, dir, "global_validation");
    async Task<string> Call(string system, string user, byte[]? image, string dir, string name,
        bool jsonResponse = false)
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
            var options = new ChatCompletionOptions();
            if (jsonResponse) options.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
            completion = await client.CompleteChatAsync(new List<ChatMessage> { new SystemChatMessage(system), new UserChatMessage(parts) }, options);
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
