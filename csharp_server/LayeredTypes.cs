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
/// Self-supporting 3D voxel glyph represented as contiguous vertical columns.
/// A value N means N cubes from the table upward at that footprint cell.
/// </summary>
public class SpatialPattern
{
    public string PatternId { get; set; } = "";
    public int[,]? ColumnHeights { get; set; }
    public string BlockColor { get; set; } = "yellow";
}

/// <summary>
/// 工作區邊界設定，Layer 2 依此將 bitmap 映射為世界座標。
/// </summary>
public class WorkspaceBounds
{
    // Shared 2D/3D zoning in the QR frame. Objects are picked only from the
    // supply side; completed pattern blocks in the target side are never reused.
    public double SupplyZoneXMax { get; set; } = 0.35;
    public double TargetZoneXMin { get; set; } = 0.35;
    // 2D bitmap 的右下格中心固定在 QR frame；小圖形向左、向上展開。
    // 可用工作區約為 QR frame 內的 0.32 x 0.40，以下數值保留安全邊界。
    // LayoutRealizer 會拒絕超出目標擺放半徑 0.16..0.47 m 的目標。
    // 5.2 cm 格距：相鄰積木之間留 2.7 cm 給夾爪手指。
    public double TargetRightX { get; set; } = 0.708;
    public double TargetBottomY { get; set; } = 0.02;
    public double TargetOriginX { get; set; } = 0.49; // 3D placement uses its own origin.
    public double TargetOriginY { get; set; } = 0.04;
    public double CellSize { get; set; } = 0.052;
    public double SpatialCellSize { get; set; } = 0.052;
    public double DefaultBlockZ { get; set; } = 0.025;
    public int MaxRows { get; set; } = 5;
    public int MaxCols { get; set; } = 5;
    public int MaxLayers { get; set; } = 3;
    // 3D glyphs use their own fixed voxel volume.  Keep this separate from the
    // 5x5 planar bitmap so changing 3D does not alter existing 2D layouts.
    // Y/depth is fixed to one row: the upright glyph is built on a single
    // front-view plane. X remains 3 cells wide and Z remains 3 cubes high.
    public int SpatialRows { get; set; } = 1;
    public int SpatialCols { get; set; } = 3;
    public int SpatialLayers { get; set; } = 3;
    // Front row of the 3D volume. It was Y=0.110 m; Y=0.080 m moves
    // the 3D placement 3 cm toward the QR1-QR2 / camera side.
    public double SpatialTargetOriginY { get; set; } = 0.080;
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
    // 純模擬的 preview_only 批次：預覽結束時每塊放下的方塊（預覽前的位置與落點），用來更新模擬世界
    [JsonPropertyName("final_blocks")]
    public List<PreviewBlock>? FinalBlocks { get; set; }
}

/// <summary>Unity 預覽裡被放下的一塊方塊：from = 預覽前的中心（QR），x, y = 落點中心，z = 落點頂面。</summary>
public class PreviewBlock
{
    [JsonPropertyName("from_x")]
    public double FromX { get; set; }
    [JsonPropertyName("from_y")]
    public double FromY { get; set; }
    [JsonPropertyName("x")]
    public double X { get; set; }
    [JsonPropertyName("y")]
    public double Y { get; set; }
    [JsonPropertyName("z")]
    public double Z { get; set; }
    // domino 的 horizontal / vertical；cube 是空字串
    [JsonPropertyName("orientation")]
    public string? Orientation { get; set; }
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

    // 只有 3D 疊放批次（BatchEnvelope.LayeredGrasp）才填：來源積木頂面、放好後頂面的真實高度（公尺，
    // LayeredHeights 依場景結構算出）。0 = 沒填、不寫出欄位，2D 批次的 JSON 不變。
    [JsonPropertyName("source_top_m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double SourceTopM { get; set; }

    [JsonPropertyName("target_top_m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double TargetTopM { get; set; }
}

/// <summary>
/// 傳給 Unity 的整批任務。C# 先用同一份 scene snapshot 排完所有步驟，
/// Unity 收到後連續執行，不再等 C# 每步重新掃描與派工。
/// </summary>
public class BatchEnvelope
{
    [JsonPropertyName("batch_id")]
    public int BatchId { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<StepEnvelope> Steps { get; set; } = new();

    // 這一批送到哪台手臂："ursim" = 只在 URSim 執行（3D 疊放的 Isaac Sim 驗證用）；
    // 空字串 / 沒有這個欄位 = 真實手臂（舊版 Unity 讀不到也是實機，行為不變）。
    [JsonPropertyName("robot_target")]
    public string RobotTarget { get; set; } = "";

    // 3D 疊放的批次（URSim 驗證與通過後的實機執行）才設 true：Unity 的 descend 改成積木頂面對齊
    // 2.5 cm 層高、指尖停在頂面下 12.5 mm（積木高度正中間）。false 時不寫出這個欄位，2D 批次送給 Unity 的 JSON
    // 跟加欄位前逐字相同；舊版 Unity 讀不到時也是 false，行為不變。
    [JsonPropertyName("layered_grasp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LayeredGrasp { get; set; }

    // 這一批的軌跡在前一批已經在 Unity 預覽過（3D 疊放的正式執行，跟 URSim 驗證那批同一條軌跡）：
    // Unity 不再播預覽，直接執行，模擬只動一次。false 時不寫出這個欄位，2D 批次的 JSON 不變。
    [JsonPropertyName("skip_preview")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SkipPreview { get; set; }

    // 純模擬的 2D 批次：Unity 預覽與 bitmap 比對通過就是執行完成，不連 URSim、也不連實體手臂，
    // 回報預覽結束時的方塊落點（ExecutionResult.FinalBlocks）。false 時不寫出這個欄位，實機批次的 JSON 不變。
    [JsonPropertyName("preview_only")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreviewOnly { get; set; }

    // 這一批是否關閉「模擬結束比對 bitmap」（對照組）。欄位名刻意用 disabled：Unity 讀不到
    // 這個欄位時預設 false，等於驗證開啟，版本不同步時不會默默變成對照組。
    [JsonPropertyName("verification_disabled")]
    public bool VerificationDisabled { get; set; }

    // 模擬結束比對 bitmap 用。只有 2D 整批才有（FigureBitmap 依計畫放下的物件畫出），其他批次為 null
    // （Unity 就不比對）。bitmap：■□ 字串；expected_cells：每個物件應該落在哪一格與 QR 座標。
    // 這份清單跟 steps 分開產生，才抓得到漏放或多放。
    [JsonPropertyName("bitmap")]
    public List<string>? Bitmap { get; set; }

    [JsonPropertyName("expected_cells")]
    public List<ExpectedCell>? ExpectedCells { get; set; }

    [JsonPropertyName("cell_size_m")]
    public double CellSizeM { get; set; }

    // X、Y 格距分開（手指沿 X 開合，X 方向通常排得比 Y 疏）；舊版 Unity 讀不到時用 cell_size_m
    [JsonPropertyName("cell_size_x_m")]
    public double CellSizeXM { get; set; }

    [JsonPropertyName("cell_size_y_m")]
    public double CellSizeYM { get; set; }
}

/// <summary>
/// Unity 模擬結束比對 bitmap 的結果（StreamingAssets/sim_check.json），由 server 印在 terminal。
/// </summary>
public class SimulationCheckReport
{
    [JsonPropertyName("batch_id")]
    public int BatchId { get; set; }
    [JsonPropertyName("performed")]
    public bool Performed { get; set; }
    [JsonPropertyName("skipped_reason")]
    public string? SkippedReason { get; set; }
    [JsonPropertyName("passed")]
    public bool Passed { get; set; }
    [JsonPropertyName("verification_enabled")]
    public bool VerificationEnabled { get; set; }
    [JsonPropertyName("expected_count")]
    public int ExpectedCount { get; set; }
    [JsonPropertyName("correct_count")]
    public int CorrectCount { get; set; }
    // 畫面重疊率 = 預覽結束時 Unity 俯視畫面的方塊像素跟預期佔地的交集 ÷ 聯集；大於門檻才算吻合（2026-10-01 起，
    // 之前是下面的格子重疊率）
    [JsonPropertyName("overlap_ratio")]
    public double OverlapRatio { get; set; }
    [JsonPropertyName("overlap_threshold")]
    public double OverlapThreshold { get; set; }
    // 座標比對的格子重疊率 = 放對的格數 ÷（預期格數 + 圖案範圍內多出來的格數），只當說明
    [JsonPropertyName("cell_overlap_ratio")]
    public double CellOverlapRatio { get; set; }
    // 比對圖（Unity StreamingAssets 底下的檔名，左：Unity 俯視畫面，右：綠 = 重疊、紅 = 該有沒有、藍 = 多出來）
    [JsonPropertyName("image_file")]
    public string? ImageFile { get; set; }
    // 比對圖的版面：三格（Unity 俯視畫面 / 預期 / 疊合）每格寬、格與格的間隔（像素），
    // 以及圖上左上角對應的 QR 座標（x0, y1，公尺）與每公尺幾像素；server 用來加註文字與 3D 的層數
    [JsonPropertyName("image_panel_width")]
    public int ImagePanelWidth { get; set; }
    [JsonPropertyName("image_gap")]
    public int ImageGap { get; set; }
    [JsonPropertyName("image_x0_m")]
    public double ImageX0M { get; set; }
    [JsonPropertyName("image_y1_m")]
    public double ImageY1M { get; set; }
    [JsonPropertyName("image_px_per_m")]
    public double ImagePxPerM { get; set; }
    // 預覽排完、場景還原前拍的 Unity 照片：正上方的完整畫面（範圍跟比對相同）與主相機（Game 視窗）的畫面
    [JsonPropertyName("photo_file")]
    public string? PhotoFile { get; set; }
    [JsonPropertyName("view_file")]
    public string? ViewFile { get; set; }
    [JsonPropertyName("expected_rows")]
    public List<string> ExpectedRows { get; set; } = new();
    [JsonPropertyName("result_rows")]
    public List<string> ResultRows { get; set; } = new();
    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();
    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// bitmap 裡一個應該放積木的物件（cube 佔一格，domino 佔兩格），QR frame 座標。
/// </summary>
public class ExpectedCell
{
    [JsonPropertyName("row")]
    public int Row { get; set; }
    [JsonPropertyName("col")]
    public int Col { get; set; }
    // domino 的第二格；cube 為 -1（Unity JsonUtility 不支援 nullable）
    [JsonPropertyName("second_row")]
    public int SecondRow { get; set; } = -1;
    [JsonPropertyName("second_col")]
    public int SecondCol { get; set; } = -1;
    [JsonPropertyName("x")]
    public double X { get; set; }
    [JsonPropertyName("y")]
    public double Y { get; set; }
    // 方塊頂面高度
    [JsonPropertyName("z")]
    public double Z { get; set; }
    [JsonPropertyName("shape")]
    public string Shape { get; set; } = "cube";
    [JsonPropertyName("orientation")]
    public string? Orientation { get; set; }
}

/// <summary>
/// LLM Motion Planner may only compose these high-level robot functions.
/// Unity translates them to the existing, bounded URScript implementation.
/// </summary>
public class RobotFunctionCall
{
    [JsonPropertyName("tcp_pose")]
    public TcpPose? TcpPose { get; set; }
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

public class TcpPose
{
    [JsonRequired, JsonPropertyName("x")] public double X { get; set; }
    [JsonRequired, JsonPropertyName("y")] public double Y { get; set; }
    [JsonRequired, JsonPropertyName("z")] public double Z { get; set; }
    [JsonRequired, JsonPropertyName("rx")] public double Rx { get; set; }
    [JsonRequired, JsonPropertyName("ry")] public double Ry { get; set; }
    [JsonRequired, JsonPropertyName("rz")] public double Rz { get; set; }
}

public class MotionPlan
{
    [JsonPropertyName("action_sequence")]
    public List<RobotFunctionCall> ActionSequence { get; set; } = new();

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = "";
}

