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
    /// Freezes all source/target pairings from one scene snapshot. A physical
    /// source is consumed at most once; the LLM may subsequently reorder these
    /// assignments but cannot invent coordinates or objects.
    /// </summary>
    public static List<Assignment> AssignBatch(
        IReadOnlyList<TargetCell> targets,
        IReadOnlyList<SceneObject> scene,
        ref int nextStepId)
    {
        var remainingTargets = targets.ToList();
        var remainingSupplies = scene
            .Where(s => s != null && s.X < SUPPLY_ZONE_X_MAX)
            .ToList();
        var assignments = new List<Assignment>();
        var protectedTargets = new List<TargetCell>();

        while (remainingTargets.Count > 0)
        {
            int id = checked(++nextStepId);
            var assignment = Assign(
                remainingTargets, remainingSupplies, id,
                recoveryMode: false, protectedTargets: protectedTargets);
            if (assignment == null) break;

            assignments.Add(assignment);
            protectedTargets.Add(assignment.Target!);
            remainingTargets.RemoveAll(t =>
                t.Row == assignment.Target!.Row && t.Col == assignment.Target.Col);

            // Remove the same detected piece using a tight coordinate match.
            remainingSupplies.RemoveAll(s =>
                s.Name == assignment.Source!.Name &&
                Math.Pow(s.X - assignment.Source.X, 2) +
                Math.Pow(s.Y - assignment.Source.Y, 2) < 1e-8);
        }

        if (remainingTargets.Count > 0)
            throw new InvalidOperationException(
                $"Cannot build complete batch: {remainingTargets.Count} target(s) have no unique supply.");
        return assignments;
    }

    /// <summary>
    /// 挑下一步。回傳 null 代表沒有可執行的（供應不足 / 無 target）。
    /// </summary>
    public static Assignment? Assign(
        List<TargetCell> remainingTargets,
        List<SceneObject> supplies,
        int nextStepId,
        bool recoveryMode = false,
        IReadOnlyList<TargetCell>? protectedTargets = null,
        IReadOnlyList<SceneObject>? failedSources = null)
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

            // Step B：一般執行只從補貨區取料。Recovery 時允許回收掉在
            // 工作區其他位置的同色同形積木，但不得移走已驗證成功的積木。
            var candidates = supplies
                .Where(s => s != null
                            && string.Equals(s.Name, expectedName, StringComparison.Ordinal)
                            && (recoveryMode || s.X < SUPPLY_ZONE_X_MAX)
                            && !OccupiesProtectedTarget(s, protectedTargets))
                .ToList();

            if (candidates.Count == 0)
                continue;   // 這個 target 的形狀沒 supply，看下一個

            // Only blacklist a source when there are more usable pieces of this
            // color/shape than the remaining targets require. This avoids getting
            // stuck on one bad piece without causing a false "out of supply" when
            // every available piece is genuinely needed.
            int requiredCount = remainingTargets.Count(t =>
                string.Equals($"{t.ExpectedColor}_{t.ExpectedShape}", expectedName,
                    StringComparison.Ordinal));
            if (candidates.Count > requiredCount && failedSources is { Count: > 0 })
            {
                var alternatives = candidates
                    .Where(c => !MatchesFailedSource(c, failedSources))
                    .ToList();
                if (alternatives.Count > 0)
                    candidates = alternatives;
            }

            // Step C：挑距離 target 最近的
            var chosen = candidates
                .OrderBy(s => Dist2DSquared(s, target))
                .First();

            // RealSense can underestimate the top surface by several millimetres.
            // Use at least the nominal target height, while preserving larger
            // measurements for genuinely taller objects.
            double safeBlockZ = Math.Max(chosen.Z, target.WorldZ);
            var sourceWithSafeZ = new SceneObject
            {
                Name = chosen.Name,
                X = chosen.X,
                Y = chosen.Y,
                Z = safeBlockZ,
                Shape = chosen.Shape,
                Orientation = chosen.Orientation,
                SkewDeg = chosen.SkewDeg,
            };

            // Use the same safe height for the source and target descents.
            var targetWithMeasuredZ = new TargetCell
            {
                Row = target.Row,
                Col = target.Col,
                SecondRow = target.SecondRow,
                SecondCol = target.SecondCol,
                WorldX = target.WorldX,
                WorldY = target.WorldY,
                WorldZ = safeBlockZ,
                ExpectedShape = target.ExpectedShape,
                ExpectedColor = target.ExpectedColor,
                ExpectedOrientation = target.ExpectedOrientation,
            };

            return new Assignment
            {
                StepId = nextStepId,
                Source = sourceWithSafeZ,
                Target = targetWithMeasuredZ,
                Reasoning = $"{(recoveryMode ? "recovery" : "far-first")} target " +
                            $"r{target.Row}c{target.Col} → nearest {expectedName} " +
                            $"at ({chosen.X:F3},{chosen.Y:F3}); measured Z={chosen.Z:F3}, " +
                            $"command Z={safeBlockZ:F3}",
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

    private static bool OccupiesProtectedTarget(
        SceneObject candidate,
        IReadOnlyList<TargetCell>? protectedTargets)
    {
        if (protectedTargets == null || protectedTargets.Count == 0)
            return false;

        const double protectedRadiusM = 0.025;
        double radiusSquared = protectedRadiusM * protectedRadiusM;
        return protectedTargets.Any(t =>
        {
            double dx = candidate.X - t.WorldX;
            double dy = candidate.Y - t.WorldY;
            return dx * dx + dy * dy < radiusSquared;
        });
    }

    private static bool MatchesFailedSource(
        SceneObject candidate,
        IReadOnlyList<SceneObject> failedSources)
    {
        const double samePieceRadiusM = 0.035;
        double radiusSquared = samePieceRadiusM * samePieceRadiusM;
        return failedSources.Any(f =>
        {
            if (!string.Equals(candidate.Name, f.Name, StringComparison.Ordinal)) return false;
            double dx = candidate.X - f.X;
            double dy = candidate.Y - f.Y;
            return dx * dx + dy * dy <= radiusSquared;
        });
    }
}
