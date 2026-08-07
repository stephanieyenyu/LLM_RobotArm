using System;
using System.Collections.Generic;
using System.Linq;

// -----------------------------------------------------------------
// Layer 5：Verifier
// 每步執行完後拿 perception snapshot 檢查是否放對；
// 也可以在整個任務完成後對 canonical pattern 做整體驗證。
//
// 這是分層架構最重要的一層——沒有它就永遠不知道到底成功沒。
// -----------------------------------------------------------------

public static class Verifier
{
    // 目標位置附近多遠算「有放對」
    private const double POSITION_TOLERANCE_M = 0.020;    // 2 cm

    // 源位置附近多遠算「還在原地」（沒被夾走）
    private const double SOURCE_MATCH_M = 0.030;

    /// <summary>
    /// 對剛執行完的一步做檢查。
    /// </summary>
    public static VerifyResult CheckStep(
        Assignment step,
        List<SceneObject> beforeSnapshot,
        List<SceneObject> afterSnapshot)
    {
        var result = new VerifyResult { StepId = step.StepId };

        if (step.Source == null || step.Target == null)
        {
            result.OverallStatus = "abort";
            result.Note = "Assignment 缺 source 或 target";
            return result;
        }

        // 1. Source 是否從補貨區消失？
        var sourceMatch = afterSnapshot.FirstOrDefault(o =>
            o.Name == step.Source.Name
            && Distance2D(o.X, o.Y, step.Source.X, step.Source.Y) < SOURCE_MATCH_M);
        result.SourceRemoved = (sourceMatch == null);

        // 2. Target 位置是否出現對應形狀 + 顏色的 piece？
        string expectedName = $"{step.Target.ExpectedColor}_{step.Target.ExpectedShape}";
        var atTarget = afterSnapshot
            .Where(o => Distance2D(o.X, o.Y, step.Target.WorldX, step.Target.WorldY) < POSITION_TOLERANCE_M)
            .OrderBy(o => Distance2D(o.X, o.Y, step.Target.WorldX, step.Target.WorldY))
            .FirstOrDefault();

        if (atTarget != null)
        {
            result.TargetOccupied = true;
            result.ShapeMatch = atTarget.Shape == step.Target.ExpectedShape;
            result.ColorMatch = atTarget.Name.Contains(step.Target.ExpectedColor);
            result.PositionErrorMm = Distance2D(atTarget.X, atTarget.Y,
                                                 step.Target.WorldX, step.Target.WorldY) * 1000.0;
            if (step.Target.ExpectedShape == "domino")
            {
                result.OrientationErrorDeg = Math.Abs(atTarget.SkewDeg);
            }
        }

        // 3. 綜合判定
        if (!result.SourceRemoved && !result.TargetOccupied)
        {
            result.OverallStatus = "retry";                          // 沒夾到、可再試一次
            result.Note = "沒夾到 cube，可能沒有真的抓到";
        }
        else if (result.SourceRemoved && !result.TargetOccupied)
        {
            result.OverallStatus = "retry";
            result.Note = "夾走了但目標位置沒東西——可能中途掉了";
        }
        else if (result.TargetOccupied && !result.ShapeMatch)
        {
            result.OverallStatus = "abort";
            result.Note = $"目標位置有東西但形狀不對（期望 {step.Target.ExpectedShape}、實際 {atTarget?.Shape})";
        }
        else if (result.TargetOccupied && !result.ColorMatch)
        {
            result.OverallStatus = "abort";
            result.Note = "目標位置有東西但顏色不對";
        }
        else if (result.TargetOccupied && result.PositionErrorMm > 15.0)
        {
            result.OverallStatus = "retry";
            result.Note = $"放到目標區但偏移 {result.PositionErrorMm:F1} mm，可重試微調";
        }
        else if (result.TargetOccupied)
        {
            result.OverallStatus = "ok";
            result.Note = $"位置準確（偏移 {result.PositionErrorMm:F1} mm）";
        }
        else
        {
            result.OverallStatus = "replan";
            result.Note = "狀況不明，重新規劃";
        }

        return result;
    }

    /// <summary>
    /// 對完整 pattern 做整體驗證：多少格對、多少格錯。
    /// </summary>
    public static List<(TargetCell target, bool matched)> CheckOverall(
        List<TargetCell> allTargets,
        List<SceneObject> snapshot)
    {
        var results = new List<(TargetCell, bool)>();
        foreach (var t in allTargets)
        {
            bool matched = snapshot.Any(o =>
                Distance2D(o.X, o.Y, t.WorldX, t.WorldY) < POSITION_TOLERANCE_M
                && o.Shape == t.ExpectedShape
                && o.Name.Contains(t.ExpectedColor));
            results.Add((t, matched));
        }
        return results;
    }

    private static double Distance2D(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
