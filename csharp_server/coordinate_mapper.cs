using OpenCvSharp;

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
    private const double WorkspaceWidthMetres = 0.162;   // QR1 to QR2
    private const double WorkspaceDepthMetres = 0.37;   // QR1 to QR3
    private const double TableHeightMetres = 0.00;

    public List<ObjectWithWorldPosition> MapObjectsToWorkspace(
        List<ObjectDetectionResult> objects,
        List<QrCodeResult> qrcodes)
    {
        var qr1 = qrcodes.FirstOrDefault(q => q.id == "QR1");
        var qr2 = qrcodes.FirstOrDefault(q => q.id == "QR2");
        var qr3 = qrcodes.FirstOrDefault(q => q.id == "QR3");
        var qr4 = qrcodes.FirstOrDefault(q => q.id == "QR4");

        if (qr1 == null || qr2 == null || qr3 == null || qr4 == null)
        {
            Console.WriteLine("Cannot map coordinates: QR1, QR2, QR3, QR4 are not all detected.");
            return objects.Select(CopyWithoutWorldPosition).ToList();
        }

        Point2f[] imagePoints =
        {
            ToPoint2f(qr1.center_pixel), // bottom-left
            ToPoint2f(qr2.center_pixel), // bottom-right
            ToPoint2f(qr4.center_pixel), // top-right
            ToPoint2f(qr3.center_pixel)  // top-left
        };

        Point2f[] workspacePoints =
        {
            new Point2f(0.0f, 0.0f),
            new Point2f((float)WorkspaceWidthMetres, 0.0f),
            new Point2f((float)WorkspaceWidthMetres, (float)WorkspaceDepthMetres),
            new Point2f(0.0f, (float)WorkspaceDepthMetres)
        };

        Mat homography = Cv2.GetPerspectiveTransform(imagePoints, workspacePoints);

        var mappedObjects = new List<ObjectWithWorldPosition>();

        foreach (var obj in objects)
        {
            if (obj.center_pixel.Length < 2)
            {
                mappedObjects.Add(CopyWithoutWorldPosition(obj));
                continue;
            }

            double pixelX = obj.center_pixel[0];
            double pixelY = obj.center_pixel[1];

            Point2d worldPoint = ApplyHomography(homography, pixelX, pixelY);

            mappedObjects.Add(new ObjectWithWorldPosition
            {
                name = obj.name,
                confidence = obj.confidence,
                bbox = obj.bbox,
                center_pixel = obj.center_pixel,
                source = obj.source,
                world_position = new WorldPosition
                {
                    x = Math.Round(worldPoint.X, 3),
                    y = TableHeightMetres,
                    z = Math.Round(worldPoint.Y, 3)
                }
            });
        }

        homography.Dispose();

        return mappedObjects;
    }

    public bool HasRequiredQrCodes(List<QrCodeResult> qrcodes)
    {
        return qrcodes.Any(q => q.id == "QR1")
            && qrcodes.Any(q => q.id == "QR2")
            && qrcodes.Any(q => q.id == "QR3")
            && qrcodes.Any(q => q.id == "QR4");
    }

    public double CalculateQrArea(List<QrCodeResult> qrcodes)
    {
        var qr1 = qrcodes.FirstOrDefault(q => q.id == "QR1");
        var qr2 = qrcodes.FirstOrDefault(q => q.id == "QR2");
        var qr3 = qrcodes.FirstOrDefault(q => q.id == "QR3");
        var qr4 = qrcodes.FirstOrDefault(q => q.id == "QR4");

        if (qr1 == null || qr2 == null || qr3 == null || qr4 == null)
        {
            return 0;
        }

        var points = new List<double[]>
        {
            qr1.center_pixel,
            qr2.center_pixel,
            qr4.center_pixel,
            qr3.center_pixel
        };

        double area = 0;

        for (int i = 0; i < points.Count; i++)
        {
            int next = (i + 1) % points.Count;

            area += points[i][0] * points[next][1];
            area -= points[next][0] * points[i][1];
        }

        return Math.Abs(area) / 2.0;
    }

    private Point2f ToPoint2f(double[] pixel)
    {
        return new Point2f(
            (float)pixel[0],
            (float)pixel[1]
        );
    }

    private Point2d ApplyHomography(Mat homography, double x, double y)
    {
        double h00 = homography.At<double>(0, 0);
        double h01 = homography.At<double>(0, 1);
        double h02 = homography.At<double>(0, 2);

        double h10 = homography.At<double>(1, 0);
        double h11 = homography.At<double>(1, 1);
        double h12 = homography.At<double>(1, 2);

        double h20 = homography.At<double>(2, 0);
        double h21 = homography.At<double>(2, 1);
        double h22 = homography.At<double>(2, 2);

        double denominator = h20 * x + h21 * y + h22;

        if (Math.Abs(denominator) < 0.000001)
        {
            return new Point2d(0, 0);
        }

        double mappedX = (h00 * x + h01 * y + h02) / denominator;
        double mappedY = (h10 * x + h11 * y + h12) / denominator;

        return new Point2d(mappedX, mappedY);
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