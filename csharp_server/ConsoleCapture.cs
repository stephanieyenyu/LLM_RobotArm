using System.Text;

/// <summary>
/// 一個任務期間 terminal 印出的內容，同時另存一份到 run 資料夾（console.txt）；Dispose 時換回原本的 Console.Out。
/// tools/letter_batch.py 依字母匯出 terminal 紀錄時讀這個檔。顏色（Console.ForegroundColor）只影響畫面，檔案是純文字。
/// </summary>
public sealed class ConsoleCapture : IDisposable
{
    readonly TextWriter original;
    readonly Tee tee;

    ConsoleCapture(string path, string header)
    {
        original = Console.Out;
        var file = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true };
        file.WriteLine(header);
        tee = new Tee(original, file);
        Console.SetOut(tee);
    }

    public static ConsoleCapture Start(string path, string header) => new(path, header);

    public void Dispose()
    {
        Console.SetOut(original);
        tee.Detach();
    }

    // 別的執行緒可能在換回之前就拿到這個 writer：檔案關掉後只寫畫面，不丟例外
    sealed class Tee : TextWriter
    {
        readonly TextWriter screen;
        StreamWriter? file;
        readonly object gate = new();

        public Tee(TextWriter screen, StreamWriter file) { this.screen = screen; this.file = file; }

        public override Encoding Encoding => screen.Encoding;
        public override void Write(char value) { screen.Write(value); lock (gate) file?.Write(value); }
        public override void Write(string? value) { screen.Write(value); lock (gate) file?.Write(value); }
        public override void WriteLine(string? value) { screen.WriteLine(value); lock (gate) file?.WriteLine(value); }
        public override void Flush() { screen.Flush(); lock (gate) file?.Flush(); }

        public void Detach()
        {
            lock (gate) { file?.Dispose(); file = null; }
        }
    }
}
