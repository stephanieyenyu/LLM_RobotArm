using OpenCvSharp;
using OpenCvSharp.Aruco;

public class QrCodeResult
{
    public string id { get; set; } = "";
    public double[] center_pixel { get; set; } = Array.Empty<double>();
    public double[][] corners { get; set; } = Array.Empty<double[]>();
}

public class QrCodeDetectorService
{
    private readonly Dictionary<int, string> arucoIdToQrName = new()
    {
        { 1, "QR1" },
        { 2, "QR2" },
        { 3, "QR3" },
        { 4, "QR4" }
    };

    public List<QrCodeResult> Detect(Mat image)
    {
        var results = new List<QrCodeResult>();

        if (image == null || image.Empty())
        {
            return results;
        }

        using Mat gray = new Mat();

        if (image.Channels() == 1)
        {
            image.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        }

        using Mat enhanced = new Mat();
        Cv2.EqualizeHist(gray, enhanced);

        var dictionary = CvAruco.GetPredefinedDictionary(
            PredefinedDictionaryType.Dict4X4_50
        );

        var detectorParameters = new DetectorParameters();
        var refineParameters = new RefineParameters(10f, 3f, true);
        using var arucoDetector = new ArucoDetector(dictionary, detectorParameters, refineParameters);

        arucoDetector.DetectMarkers(
            enhanced,
            out Point2f[][] markerCorners,
            out int[] markerIds,
            out _
        );

        if (markerIds == null || markerIds.Length == 0)
        {
            return results;
        }

        for (int i = 0; i < markerIds.Length; i++)
        {
            int arucoId = markerIds[i];

            if (!arucoIdToQrName.ContainsKey(arucoId))
            {
                continue;
            }

            string qrName = arucoIdToQrName[arucoId];

            Point2f[] pts = markerCorners[i];

            if (pts == null || pts.Length < 4)
            {
                continue;
            }

            // ArUco corners order:
            // top-left, top-right, bottom-right, bottom-left
            double tlX = Math.Round(pts[0].X, 2);
            double tlY = Math.Round(pts[0].Y, 2);

            double trX = Math.Round(pts[1].X, 2);
            double trY = Math.Round(pts[1].Y, 2);

            double brX = Math.Round(pts[2].X, 2);
            double brY = Math.Round(pts[2].Y, 2);

            double blX = Math.Round(pts[3].X, 2);
            double blY = Math.Round(pts[3].Y, 2);

            double[][] corners = new double[][]
            {
                new[] { tlX, tlY },
                new[] { trX, trY },
                new[] { brX, brY },
                new[] { blX, blY }
            };

            double cx = Math.Round((tlX + trX + brX + blX) / 4.0, 2);
            double cy = Math.Round((tlY + trY + brY + blY) / 4.0, 2);

            results.Add(new QrCodeResult
            {
                id = qrName,
                center_pixel = new[] { cx, cy },
                corners = corners
            });

            Console.WriteLine($"Detected ArUco ID {arucoId} as {qrName}, center=({cx}, {cy})");
        }

        return results;
    }
}