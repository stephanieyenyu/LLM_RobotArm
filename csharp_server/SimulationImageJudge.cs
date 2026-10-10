using System.Text;
using System.Text.Json;
using OpenAI.Chat;

// -----------------------------------------------------------------
// 3D 模擬驗證的最後一關：Isaac Sim 幾何檢查通過後，讓 LLM 看 Isaac 的模擬畫面（相機位置跟實體相機相同）
// 判斷有沒有排出使用者要的立體圖形，第一行 PASS 才送實體手臂。2026-10-07 從 main 搬來：system prompt 是 main
// ExperimentLlm.Validate 的原文（獨立的結果驗證者），環境說明也是 main 3D 時給驗證者的那幾句。
// -----------------------------------------------------------------
public sealed class SimulationImageJudge
{
    const string SystemPrompt = "你是獨立的結果驗證者。只根據原始目標、初始場景及目前實際觀測判斷，不提供操作解法。檢查整體目標與局部幾何完整度（包含直線、連接與堆疊）。若目標要求平移、對齊、放置到座標或距離，實際結果與目標值在 0.02 m（2 公分）以內的量測誤差可接受，不得只因 2 公分內的座標偏差判定失敗；超過 2 公分或方向明顯錯誤才視為幾何未達標。沒有足夠觀測證據或目標含糊時不可通過。第一行僅寫 PASS 或 FAIL，後續自然語言描述觀測問題與不確定性。";
    const string AxesFacts = "QR 座標為公尺，X/Y 在桌面上，Z 向上；物件 Z 為頂面高度。";
    const string CoordinateDirections = "QR 工作座標方向：+X=左、-X=右、+Y=後、-Y=前、+Z=上、-Z=下；方向詞一律依 QR 工作座標解讀，不依相機畫面方向。";
    const string ObjectSizes = "cube 尺寸 0.025m，domino 為 0.05×0.025×0.025m。";
    const string FigureAcceptance = "目標要求排出圖形（字母、形狀等）時，驗收條件是圖形排在桌面上空著的地方：組成圖形的物件要跟沒有用到的物件明顯分開，不能夾雜在其他物件之間，圖形要能單獨被辨認出來。";
    const string DotMatrixReading = "用積木排的字母或圖形像點陣字：每一格放一塊積木，同一個方向用固定格距排，格子之間的空隙不算斷開。";
    const string UprightReading = "立起來的圖形（往上疊的）以正面判讀：從 -Y 那側（相機畫面下方）水平看過去，往右是 +X、往上是 +Z，不能左右或上下顛倒。";
    // 驗證看的是模擬畫面（2026-10-08 起相機只在收到指令時用一次，同 main）：2D 看 Unity 模擬結束時的主相機畫面，3D 看 Isaac Sim 的畫面
    public const string UnityImageNote = "附圖是 Unity 模擬（預覽）結束時主相機的畫面，不是相機照片；方塊位置是模擬算出的落點。";
    public const string IsaacImageNote = "附圖是 Isaac Sim 模擬（URSim 執行完這一批）結束時的畫面，模擬相機的位置與內參跟實體相機相同。";

    readonly ChatClient client;

    public SimulationImageJudge(string model = "gpt-5")
    {
        string? key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENAI_API_KEY is not set.");
        client = new ChatClient(model, key);
    }

    /// <summary>回傳 LLM 的判定全文（第一行 PASS 或 FAIL）。prompt 與回覆存在 dir。</summary>
    public async Task<string> JudgeAsync(string goal, List<SceneObject> initial, List<SceneObject> current, byte[]? image, string dir,
        string? imageNote = null, string name = "isaac_judge")
    {
        string user = $"原始目標：{goal}\n初始場景：{SceneText(initial)}\n目前場景：{SceneText(current)}\n" +
                      $"環境：{AxesFacts}{CoordinateDirections}{ObjectSizes}{FigureAcceptance}{DotMatrixReading}{UprightReading}\n" +
                      (image == null ? "影像不可取得，證據不足，不能通過。" : imageNote ?? IsaacImageNote);
        File.WriteAllText(Path.Combine(dir, name + ".system.txt"), SystemPrompt);
        File.WriteAllText(Path.Combine(dir, name + ".user.txt"), user);
        var parts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(user) };
        if (image != null) parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image), ImageMediaType(image)));
        ChatCompletion completion = await client.CompleteChatAsync(new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(parts),
        });
        string text = string.Concat(completion.Content.Select(p => p.Text));
        File.WriteAllText(Path.Combine(dir, name + ".txt"), text);
        return text;
    }

    /// <summary>
    /// 同一張畫面判定最多 votes 次，過半就停（通常 2 次）。LLM 對同一個圖形的判斷不一定一致（2026-10-10：同樣完整的 W，
    /// 一種字形判 PASS、另一種判 FAIL），用多數決減少隨機性；prompt 不變。每次的 prompt 與回覆存成 name_1、name_2…，
    /// name.txt 存合併結果：第一行是多數決的 PASS / FAIL，後面附票數與每一次的判定全文。
    /// </summary>
    public async Task<string> JudgeMajorityAsync(string goal, List<SceneObject> initial, List<SceneObject> current, byte[]? image,
        string dir, string? imageNote = null, string name = "isaac_judge", int votes = 3)
    {
        if (image == null) votes = 1;                 // 沒有影像一定判不通過，不用重複問
        int need = votes / 2 + 1, pass = 0, fail = 0;
        var verdicts = new List<string>();
        for (int i = 1; i <= votes && pass < need && fail < need; i++)
        {
            string text = await JudgeAsync(goal, initial, current, image, dir, imageNote, $"{name}_{i}");
            verdicts.Add(text);
            if (FirstLine(text) == "PASS") pass++; else fail++;
        }
        var combined = new StringBuilder();
        combined.AppendLine(pass >= need ? "PASS" : "FAIL");
        combined.AppendLine($"多數決：{pass} 次 PASS、{fail} 次 FAIL（最多判定 {votes} 次，過半就停）");
        for (int i = 0; i < verdicts.Count; i++)
            combined.AppendLine($"--- 第 {i + 1} 次：{verdicts[i].Trim()}");
        string result = combined.ToString().TrimEnd();
        File.WriteAllText(Path.Combine(dir, name + ".txt"), result);
        return result;
    }

    static string FirstLine(string text) => text.Split('\n')[0].Trim();

    // Unity 存的畫面是 PNG，相機與 Isaac 是 JPEG
    static string ImageMediaType(byte[] image) =>
        image.Length > 3 && image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47 ? "image/png" : "image/jpeg";

    static string SceneText(List<SceneObject> scene) =>
        JsonSerializer.Serialize(scene.Select((item, index) => new { index, item }));
}
