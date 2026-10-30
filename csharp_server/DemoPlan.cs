using System.Text.Json.Serialization;

// Demo wire format is intentionally independent of LLM motion planning.
public sealed class DemoBatch
{
    [JsonPropertyName("batch_id")] public int BatchId { get; set; }
    [JsonPropertyName("demo_fixed12")] public bool DemoFixed12 { get; set; } = true;
    [JsonPropertyName("bitmap")] public string[] Bitmap { get; set; } = { "100", "100", "111" };
    [JsonPropertyName("steps")] public List<DemoStep> Steps { get; set; } = new();
    [JsonPropertyName("comment")] public string Comment { get; set; } = "Single snapshot; fixed 12-command demo";
}

public sealed class DemoStep
{
    [JsonPropertyName("step_id")] public int StepId { get; set; }
    [JsonPropertyName("source_index")] public int SourceIndex { get; set; }
    [JsonPropertyName("source_position")] public SceneObject Source { get; set; } = new();
    [JsonPropertyName("target_position")] public SceneObject Target { get; set; } = new();
    [JsonPropertyName("commands")] public string[] Commands { get; set; } = (string[])DemoPlanner.Commands.Clone();
}

public static class DemoPlanner
{
    // Nominally 12 URScript programs per object. Contact correction is bounded
    // inside program 4, not an LLM-generated plan or a camera feedback loop.
    public static readonly string[] Commands = {
        "open", "source_travel", "source_hover", "pick_contact", "grasp", "source_lift",
        "target_travel", "target_hover", "target_descend", "release", "target_lift", "go_home"
    };

    public static DemoBatch Build(RoutedCommand routed, string text, List<SceneObject> scene,
        WorkspaceBounds workspace, int batchId)
    {
        var batch = new DemoBatch { BatchId = batchId };
        // This short tabletop demonstration is not a tall-obstacle/3D planner.
        if (scene.Any(s => !double.IsFinite(s.Z) || s.Z > 0.07))
            throw new InvalidOperationException("Clear tall objects from the demo area (maximum observed top: 7 cm).");
        if (routed.Action == "arrange_pattern")
        {
            string color = text.Contains("黑") || text.Contains("black", StringComparison.OrdinalIgnoreCase)
                ? "black" : text.Contains("黃") || text.Contains("yellow", StringComparison.OrdinalIgnoreCase)
                ? "yellow" : scene.Where(IsPickable).Where(s => s.X < workspace.SupplyZoneXMax)
                    .Where(s => s.Name.EndsWith("_cube"))
                    .GroupBy(s => s.Name.Split('_')[0]).OrderByDescending(g => g.Count())
                    .Select(g => g.Key).FirstOrDefault() ?? "yellow";
            // 100 / 100 / 111: exactly five cubes, no domino packing.
            var available = scene.Select((s, index) => (s, index))
                .Where(p => p.s.Name == color + "_cube" && p.s.Shape == "cube")
                .Where(p => p.s.X < workspace.SupplyZoneXMax && IsPickable(p.s)).ToList();
            if (available.Count < 5)
                throw new InvalidOperationException($"Demo L needs 5 reachable {color} cubes; found {available.Count}. Pick radius: 0.20–0.47 m.");
            // Far row/column first; reserve a distinct source in the ONE snapshot.
            foreach (var cell in new[] { (r: 0, c: 0), (r: 1, c: 0), (r: 2, c: 2), (r: 2, c: 1), (r: 2, c: 0) })
            {
                if (available.Count == 0)
                    throw new InvalidOperationException("Duplicate/overlapping detections leave fewer than five distinct cubes.");
                double x = workspace.TargetOriginX + cell.c * workspace.CellSize;
                double y = workspace.TargetOriginY + (2 - cell.r) * workspace.CellSize;
                var chosen = available.OrderBy(p => Math.Pow(p.s.X - x, 2) + Math.Pow(p.s.Y - y, 2)).First();
                available.RemoveAll(p => Math.Sqrt(Math.Pow(p.s.X-chosen.s.X,2)+Math.Pow(p.s.Y-chosen.s.Y,2)) < 0.025);
                var source = Copy(chosen.s);
                source.Z = Math.Max(source.Z, workspace.DefaultBlockZ);
                batch.Steps.Add(new DemoStep {
                    StepId = batchId + batch.Steps.Count + 1, SourceIndex = chosen.index, Source = source,
                    Target = new SceneObject { Name = $"L_r{cell.r}c{cell.c}", X = x, Y = y,
                        Z = workspace.DefaultBlockZ, Shape = "cube" }
                });
            }
        }
        else if (routed.Action is "move_relative" or "stack")
        {
            // These task classes also use deterministic geometry and fixed motions.
            // Multi-layer stacks need re-observation and are outside this short demo.
            if (routed.Action == "stack" && ((routed.ObjectCount ?? 2) > 2 || routed.StackSequence.Count > 2))
                throw new InvalidOperationException("Demo supports a two-object stack only.");
            var a = SingleObjectTaskBuilder.Build(routed, scene, batchId + 1);
            var s = a.Source!;
            batch.Bitmap = Array.Empty<string>();
            batch.Steps.Add(new DemoStep { StepId = batchId + 1, SourceIndex = scene.IndexOf(s), Source = Copy(s),
                Target = new SceneObject { Name = "demo_target", X = a.Target!.WorldX, Y = a.Target.WorldY,
                    Z = a.Target.WorldZ, Shape = a.Target.ExpectedShape, Orientation = a.Target.ExpectedOrientation } });
        }
        else throw new InvalidOperationException($"Demo does not support {routed.Action}; request a flat L pattern, relative move or two-object stack.");

        foreach (var step in batch.Steps)
        {
            if (!IsPickable(step.Source)) throw new InvalidOperationException("Source outside pick radius or invalid height.");
            if (!InRadius(step.Target, 0.16)) throw new InvalidOperationException("Target outside 0.16–0.47 m placement radius.");
            if (step.SourceIndex < 0) throw new InvalidOperationException("Source index is missing.");
            foreach (var p in new[] { step.Source, step.Target })
                if (!double.IsFinite(p.Z) || p.X < -0.015 || p.X > 0.805 || p.Y < -0.015 || p.Y > 0.465 || p.Z < 0.005 || p.Z > 0.15)
                    throw new InvalidOperationException("Source/target outside demo workspace or height limits.");
            // Reject occupied tabletop destinations before sending ANY motion.
            foreach (var obstacle in scene.Where(o => !ReferenceEquals(o, scene[step.SourceIndex])))
                if (Math.Sqrt(Math.Pow(obstacle.X - step.Target.X, 2) + Math.Pow(obstacle.Y - step.Target.Y, 2)) < 0.03 &&
                    step.Target.Z + 0.012 < obstacle.Z + step.Source.Z)
                    throw new InvalidOperationException($"Target overlaps {obstacle.Name}; clear the demo target zone.");
        }
        return batch;
    }

    // Preserve this branch's calibrated QR1; radius values come from main 4b422a8.
    public static bool IsPickable(SceneObject s) => InRadius(s, 0.20) && double.IsFinite(s.Z) && s.Z >= 0.005 && s.Z <= 0.1;
    static bool InRadius(SceneObject s, double minimum)
    {
        double r = Math.Sqrt(Math.Pow(s.X - 0.38824, 2) + Math.Pow(s.Y - 0.35473, 2));
        return double.IsFinite(r) && r >= minimum && r <= 0.47;
    }
    static SceneObject Copy(SceneObject s) => new() { Name = s.Name, X = s.X, Y = s.Y, Z = s.Z,
        Shape = s.Shape, Orientation = s.Orientation, SkewDeg = s.SkewDeg };
}
