/// <summary>
/// Deterministic safety gate between the LLM and Unity.
/// Validation failure causes replanning; unsafe plans are never executed.
/// </summary>
public static class MotionPlanValidator
{
    private static readonly HashSet<string> Allowed = new()
    {
        "move_above", "descend", "grasp", "release", "lift", "wait", "go_home"
    };

    public static bool TryValidate(MotionPlan? plan, out string error)
    {
        error = "";
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

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

