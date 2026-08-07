using System.Collections.Generic;
using System.Text.Json.Serialization;

// -----------------------------------------------------------------
// 分層架構共用資料類別
// 每個 Layer 之間的 I/O 都用這裡的 record / class 明確定義
// -----------------------------------------------------------------

/// <summary>
/// Layer 1 (PatternDesigner) 的輸出：canonical bitmap + 顏色。
/// </summary>
public class CanonicalPattern
{
    public string PatternId { get; set; } = "";
    public int[,]? Bitmap { get; set; }
    public string BlockColor { get; set; } = "yellow";
}

/// <summary>
/// 工作區實體邊界，Layer 2 用來把 bitmap 座標轉世界座標。
/// </summary>
public class WorkspaceBounds
{
    // 擺放區「左下角」在 QR frame 的座標（bitmap row=last, col=0 對應這裡）。
    // 現在把 grid 右下角靠近 QR2（0.622, 0）：
    //   grid X 範圍：0.42 → 0.42 + 4×0.05 = 0.62（右下角剛好貼 QR2）
    //   grid Y 範圍：0.03 → 0.03 + 4×0.05 = 0.23（離 QR3 邊約 5 cm）
    // 遠離手臂基座（QR frame 約 0.32, 0.40）→ 減少左上角自撞。
    public double TargetOriginX { get; set; } = 0.42;
    public double TargetOriginY { get; set; } = 0.03;
    public double CellSize { get; set; } = 0.05;
    public double DefaultBlockZ { get; set; } = 0.025;
    public int MaxRows { get; set; } = 5;
    public int MaxCols { get; set; } = 5;
}

/// <summary>
/// Layer 2 (LayoutRealizer) 的輸出：每一格對應到的世界座標 + 期望的 piece 屬性。
/// </summary>
public class TargetCell
{
    public int Row { get; set; }
    public int Col { get; set; }
    public double WorldX { get; set; }
    public double WorldY { get; set; }
    public double WorldZ { get; set; }
    public string ExpectedShape { get; set; } = "cube";     // "cube" or "domino"
    public string ExpectedColor { get; set; } = "yellow";
    public string? ExpectedOrientation { get; set; }         // domino 才有
    // 對 domino：覆蓋的第二格（相鄰 (row, col+1) 或 (row+1, col)）
    public int? SecondRow { get; set; }
    public int? SecondCol { get; set; }
}

/// <summary>
/// Layer 3 (TaskAssigner) 的輸出：把「哪顆 supply → 哪個 target」定案。
/// 這是每一步真正送給 executor 的最小單位。
/// </summary>
public class Assignment
{
    public int StepId { get; set; }
    public SceneObject? Source { get; set; }                 // 從 perception 挑到的 supply
    public TargetCell? Target { get; set; }
    public string Reasoning { get; set; } = "";              // debug log 用
}

/// <summary>
/// Layer 4 (Executor) 執行完的結果（Unity 端寫入 step_done.json）。
/// </summary>
public class ExecutionResult
{
    [JsonPropertyName("step_id")]
    public int StepId { get; set; }
    [JsonPropertyName("completed")]
    public bool Completed { get; set; }
    [JsonPropertyName("error")]
    public string? Error { get; set; }
    [JsonPropertyName("duration_sec")]
    public double DurationSec { get; set; }
}

/// <summary>
/// Layer 5 (Verifier) 對單步或整體的檢查結果。
/// </summary>
public class VerifyResult
{
    public int StepId { get; set; }
    public bool SourceRemoved { get; set; }                  // 補貨區那顆 supply 消失了嗎
    public bool TargetOccupied { get; set; }                 // 目標位置現在有東西嗎
    public bool ShapeMatch { get; set; }
    public bool ColorMatch { get; set; }
    public double PositionErrorMm { get; set; }
    public double OrientationErrorDeg { get; set; }
    public string OverallStatus { get; set; } = "ok";        // "ok" / "retry" / "replan" / "abort"
    public string Note { get; set; } = "";
}

/// <summary>
/// 送給 Unity 執行的 per-step 檔案格式（寫入 current_step.json）。
/// </summary>
public class StepEnvelope
{
    [JsonPropertyName("step_id")]
    public int StepId { get; set; }
    [JsonPropertyName("done")]
    public bool Done { get; set; }                            // true = 任務完成，Unity 停 poll
    [JsonPropertyName("source_position")]
    public SceneObject? SourcePosition { get; set; }
    [JsonPropertyName("target_position")]
    public SceneObject? TargetPosition { get; set; }
    [JsonPropertyName("comment")]
    public string Comment { get; set; } = "";
}
