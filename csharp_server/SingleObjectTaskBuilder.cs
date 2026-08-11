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
        double targetZ = reference.Z + measuredSourceHeight;
        return new Assignment
        {
            StepId = stepId,
            Source = source,
            Target = MakeTarget(source, reference.X, reference.Y, targetZ),
            Reasoning = $"stack {source.Name} (measured height {measuredSourceHeight:F3} m) " +
                        $"on {reference.Name} top Z {reference.Z:F3} m; target Z {targetZ:F3} m"
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
}
