/// <summary>
/// Converts a routed single-object intent to one Assignment.
/// Direction convention preserves the legacy QR-frame behavior:
/// left=+X, right=-X, forward=-Y, backward=+Y.
/// </summary>
public static class SingleObjectTaskBuilder
{
    private const double SupplyZoneXMax = 0.30;
    // A supply object is expected to sit directly on the QR work plane, so its
    // perception Z (top surface height) is also its measured physical height.
    private const double MinMeasuredObjectHeightM = 0.005;
    private const double MaxMeasuredObjectHeightM = 0.100;
    // Do not command hard contact with the lower block. Release this far above
    // the ideal stacked pose so RealSense/TCP calibration error cannot drive the
    // gripper or held block into the support and trigger a protective stop.
    private const double StackReleaseClearanceM = 0.008;
    private const double MinX = 0.00;
    private const double MaxX = 0.65;
    private const double MinY = 0.00;
    private const double MaxY = 0.45;

    public static Assignment Build(RoutedCommand command, List<SceneObject> scene, int stepId)
    {
        if (string.IsNullOrWhiteSpace(command.ObjectName))
            throw new InvalidOperationException($"{command.Action} requires object_name.");

        SceneObject source = FindSource(scene, command.ObjectName);
        return command.Action switch
        {
            "move_relative" => BuildRelative(command, source, stepId),
            "stack" => BuildStack(command, scene, source, stepId),
            _ => throw new InvalidOperationException($"Unsupported single-object action: {command.Action}")
        };
    }

    /// <summary>
    /// Builds one layer of a multi-block tower. The tower location remains fixed,
    /// but its visible top and Z are reacquired from every fresh scene.
    /// </summary>
    public static Assignment BuildStackOntoLocation(
        string objectName,
        List<SceneObject> scene,
        double towerX,
        double towerY,
        double expectedTowerTopZ,
        int stepId,
        IReadOnlyList<SceneObject>? failedSources = null)
    {
        const double towerMatchRadiusM = 0.045;
        SceneObject reference = scene
            .Where(x => Distance2D(x.X, x.Y, towerX, towerY) < towerMatchRadiusM)
            .OrderByDescending(x => x.Z)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Tower top is not visible in the latest scene.");

        SceneObject source = scene
            .Where(x => x.Name == objectName)
            .Where(x => Distance2D(x.X, x.Y, towerX, towerY) >= towerMatchRadiusM)
            .Where(x => failedSources == null || !failedSources.Any(f =>
                f.Name == x.Name && Distance2D(f.X, f.Y, x.X, x.Y) < 0.035))
            .Where(x => x.Z >= MinMeasuredObjectHeightM && x.Z <= MaxMeasuredObjectHeightM)
            .OrderBy(x => x.Z)
            .ThenBy(x => Distance2D(x.X, x.Y, towerX, towerY))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No untried separate '{objectName}' remains for the next tower layer.");

        // Keep every layer on the original tower axis.  A higher layer's
        // camera contour shifts under perspective (and can even resemble a
        // domino), so its measured X/Y is not a stable stacking target.  The
        // fresh detection is used only for the current top-surface height.
        ValidateWorkspace(towerX, towerY);
        // The visible tower silhouette may be classified as a domino and its
        // depth can drift as layers occlude one another.  Use the accumulated
        // heights of successfully placed source blocks for commanded Z; the
        // fresh reference detection only proves that a tower is still present.
        double idealStackTopZ = expectedTowerTopZ + source.Z;
        double targetZ = idealStackTopZ + StackReleaseClearanceM;
        return new Assignment
        {
            StepId = stepId,
            Source = source,
            Target = MakeTarget(source, towerX, towerY, targetZ),
            Reasoning = $"multi-stack {source.Name} height {source.Z:F3} m on visible tower " +
                        $"observed top Z {reference.Z:F3} m; accumulated top Z " +
                        $"{expectedTowerTopZ:F3} m; ideal top Z {idealStackTopZ:F3} m; " +
                        $"fixed tower XY ({towerX:F3}, {towerY:F3}); " +
                        $"release clearance {StackReleaseClearanceM:F3} m; target Z {targetZ:F3} m"
        };
    }

    private static Assignment BuildRelative(RoutedCommand command, SceneObject source, int stepId)
    {
        if (command.Direction is not ("left" or "right" or "forward" or "backward"))
            throw new InvalidOperationException("move_relative requires a valid direction.");

        double distanceCm = command.DistanceCm ?? 5.0;
        if (distanceCm is < 1.0 or > 30.0)
            throw new InvalidOperationException("distance_cm must be between 1 and 30 cm.");

        double distanceM = distanceCm / 100.0;
        double targetX = source.X;
        double targetY = source.Y;
        switch (command.Direction)
        {
            case "left": targetX += distanceM; break;
            case "right": targetX -= distanceM; break;
            case "forward": targetY -= distanceM; break;
            case "backward": targetY += distanceM; break;
        }
        ValidateWorkspace(targetX, targetY);

        return new Assignment
        {
            StepId = stepId,
            Source = source,
            Target = MakeTarget(source, targetX, targetY, source.Z),
            Reasoning = $"move {source.Name} {command.Direction} {distanceCm:F1} cm"
        };
    }

    private static Assignment BuildStack(
        RoutedCommand command,
        List<SceneObject> scene,
        SceneObject source,
        int stepId)
    {
        if (string.IsNullOrWhiteSpace(command.ReferenceObjectName))
            throw new InvalidOperationException("stack requires reference_object_name.");

        SceneObject? reference = scene
            .Where(x => x.Name == command.ReferenceObjectName && !ReferenceEquals(x, source))
            .OrderByDescending(x => x.X >= SupplyZoneXMax)
            .ThenByDescending(x => DistanceSquared(x, source))
            .FirstOrDefault();
        if (reference == null)
            throw new InvalidOperationException($"Reference object '{command.ReferenceObjectName}' was not found.");

        ValidateWorkspace(reference.X, reference.Y);
        double measuredSourceHeight = source.Z;
        if (measuredSourceHeight < MinMeasuredObjectHeightM ||
            measuredSourceHeight > MaxMeasuredObjectHeightM)
        {
            throw new InvalidOperationException(
                $"Source height from perception is invalid: {measuredSourceHeight:F3} m. " +
                "Refresh the RealSense scene before stacking.");
        }

        if (reference.Z < MinMeasuredObjectHeightM)
        {
            throw new InvalidOperationException(
                $"Reference top height from perception is invalid: {reference.Z:F3} m.");
        }

        // Both values are measured in the QR work-plane frame:
        // reference.Z = top of lower object, source.Z = height of source on the table.
        double idealStackTopZ = reference.Z + measuredSourceHeight;
        double targetZ = idealStackTopZ + StackReleaseClearanceM;
        return new Assignment
        {
            StepId = stepId,
            Source = source,
            Target = MakeTarget(source, reference.X, reference.Y, targetZ),
            Reasoning = $"stack {source.Name} (measured height {measuredSourceHeight:F3} m) " +
                        $"on {reference.Name} top Z {reference.Z:F3} m; " +
                        $"ideal stack top Z {idealStackTopZ:F3} m; " +
                        $"release clearance {StackReleaseClearanceM:F3} m; " +
                        $"command target Z {targetZ:F3} m"
        };
    }

    private static SceneObject FindSource(List<SceneObject> scene, string name)
    {
        SceneObject? source = scene
            .Where(x => x.Name == name)
            .OrderByDescending(x => x.X < SupplyZoneXMax)
            .ThenBy(x => x.X)
            .FirstOrDefault();
        return source ?? throw new InvalidOperationException($"Object '{name}' was not found.");
    }

    private static TargetCell MakeTarget(SceneObject source, double x, double y, double z)
    {
        string color = source.Name.Contains('_') ? source.Name.Split('_')[0] : source.Name;
        return new TargetCell
        {
            Row = -1,
            Col = -1,
            WorldX = x,
            WorldY = y,
            WorldZ = z,
            ExpectedShape = source.Shape,
            ExpectedColor = color,
            ExpectedOrientation = source.Orientation
        };
    }

    private static void ValidateWorkspace(double x, double y)
    {
        if (x < MinX || x > MaxX || y < MinY || y > MaxY)
            throw new InvalidOperationException(
                $"Target ({x:F3},{y:F3}) is outside QR workspace " +
                $"X={MinX:F2}..{MaxX:F2}, Y={MinY:F2}..{MaxY:F2} m.");
    }

    private static double DistanceSquared(SceneObject a, SceneObject b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double Distance2D(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
