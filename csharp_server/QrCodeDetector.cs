using OpenCvSharp;
using ZXing;
using ZXing.Common;
using System.Runtime.InteropServices;

public class QrCodeResult
{
    public string id { get; set; } = "";
    public double[] center_pixel { get; set; } = Array.Empty<double>();
    public double[][] corners { get; set; } = Array.Empty<double[]>();
}

public class QrCodeDetectorService
{
    public List<QrCodeResult> Detect(Mat image)
    {
        var results = new List<QrCodeResult>();
        Mat gray = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);

        Mat enhanced = new Mat();
        Cv2.EqualizeHist(gray, enhanced);

        Mat processed = new Mat();
        Cv2.CvtColor(enhanced, processed, ColorConversionCodes.GRAY2BGR);

        Mat rgb = new Mat();
        Cv2.CvtColor(processed, rgb, ColorConversionCodes.BGR2RGB);

        gray.Dispose();
        enhanced.Dispose();
        processed.Dispose();

        if (!rgb.IsContinuous())
        {
            rgb = rgb.Clone();
        }

        int width = rgb.Width;
        int height = rgb.Height;
        int channels = rgb.Channels();

        if (channels != 3)
        {
            Console.WriteLine($"Unexpected channel count: {channels}");
            return results;
        }

        byte[] pixels = new byte[width * height * channels];
        Marshal.Copy(rgb.Data, pixels, 0, pixels.Length);

        var source = new RGBLuminanceSource(
            pixels,
            width,
            height,
            RGBLuminanceSource.BitmapFormat.RGB24
        );

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat>
                {
                    BarcodeFormat.QR_CODE
                }
            }
        };

        var decodedResults = reader.DecodeMultiple(source);

        if (decodedResults == null)
        {
            rgb.Dispose();
            return results;
        }

        for (int i = 0; i < decodedResults.Length; i++)
        {
            var result = decodedResults[i];

            if (result.ResultPoints == null || result.ResultPoints.Length < 3)
            {
                continue;
            }

            // ZXing QR ResultPoints 固定順序：
            //   [0] bottom-left finder pattern
            //   [1] top-left finder pattern
            //   [2] top-right finder pattern
            // 右下角沒有 finder pattern，用平行四邊形法則補出來：
            //   bottom-right = bottom-left + top-right - top-left
            var bl = result.ResultPoints[0]; // bottom-left
            var tl = result.ResultPoints[1]; // top-left
            var tr = result.ResultPoints[2]; // top-right

            double brX = Math.Round(bl.X + tr.X - tl.X, 2);
            double brY = Math.Round(bl.Y + tr.Y - tl.Y, 2);

            // 角點順序對應 coordinate_mapper_3d.py 的 get_qr_object_points()：
            // top-left, top-right, bottom-right, bottom-left
            double[][] corners = new double[][]
            {
                new[] { Math.Round((double)tl.X, 2), Math.Round((double)tl.Y, 2) }, // top-left
                new[] { Math.Round((double)tr.X, 2), Math.Round((double)tr.Y, 2) }, // top-right
                new[] { brX, brY },                                                   // bottom-right（補算）
                new[] { Math.Round((double)bl.X, 2), Math.Round((double)bl.Y, 2) }  // bottom-left
            };

            // center = 四角點平均
            double cx = Math.Round((tl.X + tr.X + brX + bl.X) / 4.0, 2);
            double cy = Math.Round((tl.Y + tr.Y + brY + bl.Y) / 4.0, 2);

            results.Add(new QrCodeResult
            {
                id = string.IsNullOrWhiteSpace(result.Text) ? $"QR{i + 1}" : result.Text,
                center_pixel = new[] { cx, cy },
                corners = corners
            });
        }

        rgb.Dispose();
        return results;
    }
}
