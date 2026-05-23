public class WorldPosition
{
    public double x { get; set; }
    public double y { get; set; }
    public double z { get; set; }
}

public class ObjectWithWorldPosition : ObjectDetectionResult
{
    public WorldPosition world_position { get; set; } = new();
}

public class CoordinateMapper
{
    private const double WorkspaceWidthMetres = 0.60;   // QR1 to QR2
    private const double WorkspaceDepthMetres = 0.40;   // QR1 to QR3
    private const double TableHeightMetres = 0.00;

    public List<ObjectWithWorldPosition> MapObjectsToWorkspace(
        List<ObjectDetectionResult> objects,
        List<QrCodeResult> qrcodes)
    {
        var qr1 = qrcodes.FirstOrDefault(q => q.id == "QR1");
        var qr2 = qrcodes.FirstOrDefault(q => q.id == "QR2");
        var qr3 = qrcodes.FirstOrDefault(q => q.id == "QR3");

        if (qr1 == null || qr2 == null || qr3 == null)
        {
            Console.WriteLine("Cannot map coordinates: QR1, QR2, QR3 are not all detected.");
            return objects.Select(o => CopyWithoutWorldPosition(o)).ToList();
        }

        double[] p1 = qr1.center_pixel;
        double[] p2 = qr2.center_pixel;
        double[] p3 = qr3.center_pixel;

        double vxX = p2[0] - p1[0];
        double vxY = p2[1] - p1[1];

        double vzX = p3[0] - p1[0];
        double vzY = p3[1] - p1[1];

        double determinant = vxX * vzY - vxY * vzX;

        if (Math.Abs(determinant) < 0.0001)
        {
            Console.WriteLine("Cannot map coordinates: QR points are almost collinear.");
            return objects.Select(o => CopyWithoutWorldPosition(o)).ToList();
        }

        var mappedObjects = new List<ObjectWithWorldPosition>();

        foreach (var obj in objects)
        {
            double px = obj.center_pixel[0] - p1[0];
            double py = obj.center_pixel[1] - p1[1];

            double alpha = (px * vzY - py * vzX) / determinant;
            double beta = (vxX * py - vxY * px) / determinant;

            double worldX = alpha * WorkspaceWidthMetres;
            double worldZ = beta * WorkspaceDepthMetres;

            mappedObjects.Add(new ObjectWithWorldPosition
            {
                name = obj.name,
                confidence = obj.confidence,
                bbox = obj.bbox,
                center_pixel = obj.center_pixel,
                source = obj.source,
                world_position = new WorldPosition
                {
                    x = Math.Round(worldX, 3),
                    y = TableHeightMetres,
                    z = Math.Round(worldZ, 3)
                }
            });
        }

        return mappedObjects;
    }

    private ObjectWithWorldPosition CopyWithoutWorldPosition(ObjectDetectionResult obj)
    {
        return new ObjectWithWorldPosition
        {
            name = obj.name,
            confidence = obj.confidence,
            bbox = obj.bbox,
            center_pixel = obj.center_pixel,
            source = obj.source,
            world_position = new WorldPosition
            {
                x = 0,
                y = 0,
                z = 0
            }
        };
    }
}
