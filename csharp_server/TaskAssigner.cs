using System;
using System.Collections.Generic;
using System.Linq;

// -----------------------------------------------------------------
// Layer 3：Task Assigner
// 每一步呼叫一次：從剩下的 targets 中挑「下一個要放的」，配對到最合適的 supply。
//
// 「每步呼叫一次」而不是一次規劃全部，是因為場上狀況可能變化：
//   - 上一步失敗、supply 位置變了
//   - 使用者中途移走 supply
//   - 有其他物件擋住
// 每步都基於「剛掃到的 supply 清單」做決策，比 batch 規劃穩定得多。
//
// 目前用 greedy 演算法（不呼叫 LLM），未來可以擴充成 LLM 決策。
// -----------------------------------------------------------------

public static class TaskAssigner
{
    // supply 篩選：只認補貨區內、指定顏色 + 形狀對的
    private const double SUPPLY_ZONE_X_MAX = 0.30;

    /// <summary>
    /// 挑下一步。回傳 null 代表沒有可執行的（供應不足 / 無 target）。
    /// </summary>
    public static Assignment? Assign(
        List<TargetCell> remainingTargets,
        List<SceneObject> supplies,
        int nextStepId)
    {
        if (remainingTargets == null || remainingTargets.Count == 0)
            return null;

        // Step A：排序 targets — 遠端優先（Y 大、X 大），避免手臂後續跨過已放的
        var orderedTargets = remainingTargets
            .OrderByDescending(t => t.WorldY)
            .ThenByDescending(t => t.WorldX)
            .ToList();

        foreach (var target in orderedTargets)
        {
            string expectedName = $"{target.ExpectedColor}_{target.ExpectedShape}";

            // Step B：從 supply 池挑對應形狀 + 顏色的
            var candidates = supplies
                .Where(s => s != null
                            && string.Equals(s.Name, expectedName, StringComparison.Ordinal)
                            && s.X < SUPPLY_ZONE_X_MAX)
                .ToList();

            if (candidates.Count == 0)
                continue;   // 這個 target 的形狀沒 supply，看下一個

            // Step C：挑距離 target 最近的
            var chosen = candidates
                .OrderBy(s => Dist2DSquared(s, target))
                .First();

            // Target 的 Z 用 source 實測（同種積木高度一致）
            var targetWithMeasuredZ = new TargetCell
            {
                Row = target.Row,
                Col = target.Col,
                SecondRow = target.SecondRow,
                SecondCol = target.SecondCol,
                WorldX = target.WorldX,
                WorldY = target.WorldY,
                WorldZ = chosen.Z,
                ExpectedShape = target.ExpectedShape,
                ExpectedColor = target.ExpectedColor,
                ExpectedOrientation = target.ExpectedOrientation,
            };

            return new Assignment
            {
                StepId = nextStepId,
                Source = chosen,
                Target = targetWithMeasuredZ,
                Reasoning = $"far-first target r{target.Row}c{target.Col} → nearest {expectedName} at ({chosen.X:F3},{chosen.Y:F3})",
            };
        }

        // 全部 targets 都沒對應 supply
        return null;
    }

    private static double Dist2DSquared(SceneObject a, TargetCell b)
    {
        double dx = a.X - b.WorldX;
        double dy = a.Y - b.WorldY;
        return dx * dx + dy * dy;
    }
}
