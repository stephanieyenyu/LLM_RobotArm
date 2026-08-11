using System.Collections.Generic;
using System.Text.Json.Serialization;

// -----------------------------------------------------------------
// ?惜?嗆??梁鞈?憿
// 瘥?Layer 銋???I/O ?賜?ㄐ??record / class ?Ⅱ摰儔
// -----------------------------------------------------------------

/// <summary>
/// Layer 1 (PatternDesigner) ?撓?綽?canonical bitmap + 憿??
/// </summary>
public class CanonicalPattern
{
    public string PatternId { get; set; } = "";
    public int[,]? Bitmap { get; set; }
    public string BlockColor { get; set; } = "yellow";
}

/// <summary>
/// 撌乩??撖阡???嚗ayer 2 ?其???bitmap 摨扳?頧??漣璅?
/// </summary>
public class WorkspaceBounds
{
    // ?箸??椰銝?? QR frame ?漣璅?bitmap row=last, col=0 撠??ㄐ嚗?
    // ?曉??grid ?喃?閫?餈?QR2嚗?.622, 0嚗?
    //   grid X 蝭?嚗?.42 ??0.42 + 4?0.05 = 0.62嚗銝??末鞎?QR2嚗?
    //   grid Y 蝭?嚗?.03 ??0.03 + 4?0.05 = 0.23嚗 QR3 ?? 5 cm嚗?
    // ????箏漣嚗R frame 蝝?0.32, 0.40嚗? 皜?撌虫?閫??
    public double TargetOriginX { get; set; } = 0.42;
    public double TargetOriginY { get; set; } = 0.03;
    public double CellSize { get; set; } = 0.05;
    public double DefaultBlockZ { get; set; } = 0.025;
    public int MaxRows { get; set; } = 5;
    public int MaxCols { get; set; } = 5;
}

/// <summary>
/// Layer 2 (LayoutRealizer) ?撓?綽?瘥??澆?????漣璅?+ ????piece 撅祆扼?
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
    public string? ExpectedOrientation { get; set; }         // domino ??
    // 撠?domino嚗???蝚砌??潘??賊 (row, col+1) ??(row+1, col)嚗?
    public int? SecondRow { get; set; }
    public int? SecondCol { get; set; }
}

/// <summary>
/// Layer 3 (TaskAssigner) ?撓?綽??憿?supply ???芸?target??獢?
/// ?瘥?甇亦?甇?策 executor ??撠雿?
/// </summary>
public class Assignment
{
    public int StepId { get; set; }
    public SceneObject? Source { get; set; }                 // 敺?perception ???supply
    public TargetCell? Target { get; set; }
    public string Reasoning { get; set; } = "";              // debug log ??
}

/// <summary>
/// Layer 4 (Executor) ?瑁?摰?蝯?嚗nity 蝡臬神??step_done.json嚗?
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
/// Layer 5 (Verifier) 撠甇交??湧??炎?亦???
/// </summary>
public class VerifyResult
{
    public int StepId { get; set; }
    public bool SourceRemoved { get; set; }                  // 鋆疏???? supply 瘨仃鈭?
    public bool TargetOccupied { get; set; }                 // ?格?雿蔭?曉?镼踹?
    public bool ShapeMatch { get; set; }
    public bool ColorMatch { get; set; }
    public double PositionErrorMm { get; set; }
    public double OrientationErrorDeg { get; set; }
    public string OverallStatus { get; set; } = "ok";        // "ok" / "retry" / "replan" / "abort"
    public string Note { get; set; } = "";
}

/// <summary>
/// ?策 Unity ?瑁???per-step 瑼??澆?嚗神??current_step.json嚗?
/// </summary>
public class StepEnvelope
{
    [JsonPropertyName("step_id")]
    public int StepId { get; set; }
    [JsonPropertyName("done")]
    public bool Done { get; set; }                            // true = 隞餃?摰?嚗nity ??poll
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

