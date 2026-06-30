using OpenCvSharp;
using OpenCvSharp.Aruco;
using System.Collections.Generic;

public class QrCodeResult
{
    public string id { get; set; } = "";
    public double[] center_pixel { get; set; } = System.Array.Empty<double>();
    public double[][] corners { get; set; } = System.Array.Empty<double[]>();
}

public class QrCodeDetectorService
{
    // ArUco ID → QR 名稱對應
    private readonly Dictionary<int, string> idToName = new()
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
            return results;

        // 轉灰階
        using Mat gray = new Mat();
        if (image.Channels() == 1)
            image.CopyTo(gray);
        else
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

        // ArUco 偵測
        var dictionary = CvAruco.GetPredefinedDictionary(ArucoDictionaryName.Dict4X4_50);
        var detectorParameters = DetectorParameters.Create();

        Point2f[][] corners;
        int[] ids;
        Point2f[][] rejected;

        CvAruco.DetectMarkers(gray, dictionary, out corners, out ids, detectorParameters, out rejected);

        if (ids == null || ids.Length == 0)
            return results;

        for (int i = 0; i < ids.Length; i++)
        {
            int arucoId = ids[i];

            if (!idToName.TryGetValue(arucoId, out string? name))
                continue;

            var c = corners[i]; // 4 個角點，順序：top-left, top-right, bottom-right, bottom-left

            double cx = (c[0].X + c[1].X + c[2].X + c[3].X) / 4.0;
            double cy = (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4.0;

            results.Add(new QrCodeResult
            {
                id = name,
                center_pixel = new[] { System.Math.Round(cx, 2), System.Math.Round(cy, 2) },
                corners = new double[][]
                {
                    new[] { System.Math.Round((double)c[0].X, 2), System.Math.Round((double)c[0].Y, 2) }, // top-left
                    new[] { System.Math.Round((double)c[1].X, 2), System.Math.Round((double)c[1].Y, 2) }, // top-right
                    new[] { System.Math.Round((double)c[2].X, 2), System.Math.Round((double)c[2].Y, 2) }, // bottom-right
                    new[] { System.Math.Round((double)c[3].X, 2), System.Math.Round((double)c[3].Y, 2) }  // bottom-left
                }
            });
        }

        return results;
    }
}