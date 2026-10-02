using System.Text.Json.Serialization;
using OpenAI.Chat;

/// <summary>
/// 每個任務開始時的目標 bitmap 設計結果（release 260917 的 PatternDesigner），存成任務資料夾的 design.json，也寫進 result.json。
/// </summary>
public sealed class PatternDesign
{
    [JsonPropertyName("block_color")] public string BlockColor { get; set; } = "";
    // 目標 bitmap：0 = 空、1 = 放一塊；第一列 = 相機畫面最上方（+Y 那側）
    [JsonPropertyName("bitmap")] public List<string> Bitmap { get; set; } = new();
    // false = Unity 的 pattern審查關閉，只請 OpenAI 畫一次
    [JsonPropertyName("cross_reviewed")] public bool Reviewed { get; set; }
}

/// <summary>設計不出可用的 bitmap（任務記為失敗，不算基礎設施錯誤）。</summary>
public sealed class PatternDesignException : Exception
{
    public PatternDesignException(string message) : base(message) { }
}

/// <summary>缺少設計需要的設定（例如要交叉審查卻沒有 GEMINI_API_KEY），任務記為基礎設施錯誤。</summary>
public sealed class PatternDesignUnavailableException : Exception
{
    public PatternDesignUnavailableException(string message) : base(message) { }
}

/// <summary>設計階段每次呼叫模型的 prompt 與回覆，存在任務資料夾的 design/（依呼叫順序編號）。</summary>
public static class DesignLog
{
    static string? dir;
    static int count;

    public static void Start(string directory)
    {
        Directory.CreateDirectory(directory);
        dir = directory;
        count = 0;
    }

    public static void Write(string name, string system, string user, string response)
    {
        if (dir == null) return;
        string prefix = Path.Combine(dir, $"{Interlocked.Increment(ref count):00}_{name}");
        File.WriteAllText(prefix + ".system.txt", system);
        File.WriteAllText(prefix + ".user.txt", user);
        File.WriteAllText(prefix + ".txt", response);
    }

    public static void Write(string name, IEnumerable<ChatMessage> messages, string response)
    {
        string Text(ChatMessage m) => string.Concat(m.Content.Select(p => p.Text));
        var list = messages.ToList();
        Write(name, string.Join("\n", list.OfType<SystemChatMessage>().Select(Text)),
            string.Join("\n", list.OfType<UserChatMessage>().Select(Text)), response);
    }
}
