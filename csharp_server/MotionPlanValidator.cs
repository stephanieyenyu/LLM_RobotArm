/// <summary>
/// Deterministic safety gate between the LLM and Unity.
/// Validation failure causes replanning; unsafe plans are never executed.
/// </summary>
public static class MotionPlanValidator
{
    private const double MinX = 0.00;
    private const double MaxX = 0.72;
    private const double MinY = 0.00;
    private const double MaxY = 0.45;
    private const double MinTravelClearanceM = 0.08;
    private const double ObstacleClearanceM = 0.03;
    private const double MaxTransferDistanceM = 0.70;
    private const double OccupancyRadiusM = 0.020;
    private static readonly HashSet<string> Allowed = new()
    {
        "move_above", "descend", "grasp", "release", "lift", "wait", "go_home"
    };

    public static bool TryValidate(
        MotionPlan? plan,
        Assignment assignment,
        IReadOnlyList<SceneObject> scene,
        out string error)
    {
        error = "";
        if (assignment.Source == null || assignment.Target == null)
            return Fail("assignment is missing source or target", out error);

        var source = assignment.Source;
        var target = assignment.Target;
        if (!InsideWorkspace(source.X, source.Y) || !InsideWorkspace(target.WorldX, target.WorldY))
            return Fail("source or target is outside the validated QR workspace", out error);

        double transferDistance = Distance2D(source.X, source.Y, target.WorldX, target.WorldY);
        if (transferDistance > MaxTransferDistanceM)
            return Fail($"transfer distance {transferDistance:F3} m exceeds {MaxTransferDistanceM:F2} m", out error);

        // An occupied target is only legal when the requested target Z is above the
        // perceived obstacle top (stacking). This prevents placing through another block.
        foreach (var obstacle in scene.Where(o => !ReferenceEquals(o, source)))
        {
            if (Distance2D(obstacle.X, obstacle.Y, target.WorldX, target.WorldY) > OccupancyRadiusM)
                continue;
            if (target.WorldZ + 0.003 < obstacle.Z + Math.Max(source.Z, 0.005))
                return Fail($"target overlaps {obstacle.Name} without a safe stacking height", out error);
        }

        double highestObstacle = scene.Count == 0 ? 0.0 : scene.Max(o => Math.Max(0.0, o.Z));
        double requiredSourceLift = Math.Clamp(
            highestObstacle + ObstacleClearanceM - source.Z,
            MinTravelClearanceM,
            0.15);
        double requiredTargetLift = Math.Clamp(
            highestObstacle + ObstacleClearanceM - target.WorldZ,
            MinTravelClearanceM,
            0.15);

        if (plan == null || plan.ActionSequence.Count == 0)
            return Fail("empty action_sequence", out error);
        if (plan.ActionSequence.Count > 20)
            return Fail("action_sequence exceeds 20 calls", out error);

        string phase = "start";
        bool holding = false;
        bool released = false;

        for (int i = 0; i < plan.ActionSequence.Count; i++)
        {
            var a = plan.ActionSequence[i];
            if (!Allowed.Contains(a.Function))
                return Fail($"call {i}: unsupported function '{a.Function}'", out error);

            if (a.Function is "move_above" or "lift")
            {
                if (a.Location is not ("source" or "target"))
                    return Fail($"call {i}: {a.Function} requires source/target", out error);
                if (a.HeightM is < 0.05 or > 0.15 || a.HeightM == null)
                    return Fail($"call {i}: height_m must be 0.05..0.15", out error);

                double required = a.Location == "source" ? requiredSourceLift : requiredTargetLift;
                if (a.HeightM.Value + 1e-6 < required)
                    return Fail(
                        $"call {i}: height_m {a.HeightM.Value:F3} is below collision clearance {required:F3}",
                        out error);
            }
            if (a.Function == "descend" && a.Location is not ("source" or "target"))
                return Fail($"call {i}: descend requires source/target", out error);
            if (a.Function == "wait" && (a.Seconds is < 0.1 or > 3.0 || a.Seconds == null))
                return Fail($"call {i}: seconds must be 0.1..3.0", out error);

            switch (a.Function)
            {
                case "move_above" when a.Location == "source" && !holding:
                    phase = "above_source";
                    break;
                case "descend" when a.Location == "source" && phase == "above_source" && !holding:
                    phase = "at_source";
                    break;
                case "grasp" when phase == "at_source" && !holding:
                    holding = true;
                    phase = "grasped";
                    break;
                case "lift" when a.Location == "source" && phase == "grasped" && holding:
                    phase = "carrying_safe";
                    break;
                case "move_above" when a.Location == "target" && phase == "carrying_safe" && holding:
                    phase = "above_target";
                    break;
                case "descend" when a.Location == "target" && phase == "above_target" && holding:
                    phase = "at_target";
                    break;
                case "release" when phase == "at_target" && holding:
                    holding = false;
                    released = true;
                    phase = "released";
                    break;
                case "lift" when a.Location == "target" && phase == "released" && !holding:
                    phase = "retreated";
                    break;
                case "go_home" when phase == "retreated" && !holding:
                    phase = "home";
                    break;
                case "wait":
                    break;
                default:
                    return Fail($"call {i}: unsafe order for {a.Function}", out error);
            }
        }

        if (!released || holding || phase != "home")
            return Fail("plan must release, retreat, and finish at home", out error);
        return true;
    }

    private static bool InsideWorkspace(double x, double y) =>
        x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    private static double Distance2D(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

