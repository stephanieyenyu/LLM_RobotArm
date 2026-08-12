using System.Collections.Generic;
using System.Text.Json.Serialization;

// -----------------------------------------------------------------
// 分層架構共用資料型別
// 每個 Layer 之間的 I/O 都使用下列 record / class 明確定義
// -----------------------------------------------------------------

/// <summary>
/// Layer 1 (PatternDesigner) 的輸出：canonical bitmap + 方塊顏色
/// </summary>
public class CanonicalPattern
{
    public string PatternId { get; set; } = "";
    public int[,]? Bitmap { get; set; }
    public string BlockColor { get; set; } = "yellow";
}

/// <summary>
/// 工作區邊界設定，Layer 2 依此將 bitmap 映射為世界座標。
/// </summary>
public class WorkspaceBounds
{
    // 目標區左下角在 QR frame 的座標（bitmap 最後一列、第 0 欄的中心）。
    // 現有 5x5 grid 必須避開 QR2 (0.622, 0)：
    //   grid X 範圍：0.42 ～ 0.42 + 4*0.05 = 0.62，最右欄接近但不超過 QR2。
    //   grid Y 範圍：0.03 ～ 0.03 + 4*0.05 = 0.23，距離 QR3 保留 5 cm。
    // 可用工作區約為 QR frame 內的 0.32 x 0.40，以下數值保留安全邊界。
    // Keep the complete 5x5 placement area away from the UR base while using
    // 5.5 cm center spacing (about 3 cm clear gap for a 2.5 cm cube).
    public double TargetOriginX { get; set; } = 0.48;
    public double TargetOriginY { get; set; } = 0.00;
    public double CellSize { get; set; } = 0.06;
    public double DefaultBlockZ { get; set; } = 0.025;
    public int MaxRows { get; set; } = 5;
    public int MaxCols { get; set; } = 5;
}

/// <summary>
/// Layer 2 (LayoutRealizer) 的輸出：每個要填滿的世界座標與預期方塊資訊。
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
    public string? ExpectedOrientation { get; set; }         // domino 方向
    // 若為 domino，第二個覆蓋格為 (row, col+1) 或 (row+1, col)。
    public int? SecondRow { get; set; }
    public int? SecondCol { get; set; }
}

/// <summary>
/// Layer 3 (TaskAssigner) 的輸出：將一個 supply 配對至一個 target 的任務。
/// 一次只產生一筆，方便 executor 執行與驗證。
/// </summary>
public class Assignment
{
    public int StepId { get; set; }
    public SceneObject? Source { get; set; }                 // perception 偵測到的 supply
    public TargetCell? Target { get; set; }
    public string Reasoning { get; set; } = "";              // debug log 用
}

/// <summary>
/// Layer 4 (Executor) 執行結果，由 Unity 寫入 step_done.json。
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
/// Layer 5 (Verifier) 對單步執行結果的驗證資訊。
/// </summary>
public class VerifyResult
{
    public int StepId { get; set; }
    public bool SourceRemoved { get; set; }                  // 原 supply 位置是否已清空
    public bool TargetOccupied { get; set; }                 // 目標位置是否已有物件
    public bool ShapeMatch { get; set; }
    public bool ColorMatch { get; set; }
    public double PositionErrorMm { get; set; }
    public double OrientationErrorDeg { get; set; }
    public string OverallStatus { get; set; } = "ok";        // "ok" / "retry" / "replan" / "abort"
    public string Note { get; set; } = "";
}

/// <summary>
/// 傳給 Unity 執行的單步資料，寫入 current_step.json。
/// </summary>
public class StepEnvelope
{
    [JsonPropertyName("step_id")]
    public int StepId { get; set; }
    [JsonPropertyName("done")]
    public bool Done { get; set; }                            // true = 全部完成，Unity 停止 polling
    [JsonPropertyName("source_position")]
    public SceneObject? SourcePosition { get; set; }
    [JsonPropertyName("target_position")]
    public SceneObject? TargetPosition { get; set; }
    [JsonPropertyName("comment")]
    public string Comment { get; set; } = "";

    [JsonPropertyName("action_sequence")]
    public List<RobotFunctionCall> ActionSequence { get; set; } = new();
}

/// <summary>
/// LLM Motion Planner may only compose these high-level robot functions.
/// Unity translates them to the existing, bounded URScript implementation.
/// </summary>
public class RobotFunctionCall
{
    [JsonPropertyName("function")]
    public string Function { get; set; } = "";

    // "source" or "target" for position-based functions.
    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("height_m")]
    public double? HeightM { get; set; }

    [JsonPropertyName("seconds")]
    public double? Seconds { get; set; }
}

public class MotionPlan
{
    [JsonPropertyName("action_sequence")]
    public List<RobotFunctionCall> ActionSequence { get; set; } = new();

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = "";
}

