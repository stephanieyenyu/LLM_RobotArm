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

        Mat rgb = new Mat();
        Cv2.CvtColor(image, rgb, ColorConversionCodes.BGR2RGB);

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
            //   [0] 左下 finder pattern 中心 (bottom-left)
            //   [1] 左上 finder pattern 中心 (top-left)
            //   [2] 右上 finder pattern 中心 (top-right)
            // 右下角沒有 finder pattern，用向量補出來：
            //   右下 = 左下 + 右上 - 左上
            var p0 = result.ResultPoints[0]; // bottom-left
            var p1 = result.ResultPoints[1]; // top-left
            var p2 = result.ResultPoints[2]; // top-right

            double brX = Math.Round(p0.X + p2.X - p1.X, 2); // bottom-right X
            double brY = Math.Round(p0.Y + p2.Y - p1.Y, 2); // bottom-right Y

            // 四個角點，順序對應 coordinate_mapper_3d.py 的 solvePnP object_points：
            // top-left, top-right, bottom-right, bottom-left
            double[][] corners = new double[][]
            {
                new[] { Math.Round((double)p1.X, 2), Math.Round((double)p1.Y, 2) }, // top-left
                new[] { Math.Round((double)p2.X, 2), Math.Round((double)p2.Y, 2) }, // top-right
                new[] { brX, brY },                                                   // bottom-right (補算)
                new[] { Math.Round((double)p0.X, 2), Math.Round((double)p0.Y, 2) }  // bottom-left
            };

            double cx = Math.Round((p0.X + p1.X + p2.X + brX) / 4.0, 2);
            double cy = Math.Round((p0.Y + p1.Y + p2.Y + brY) / 4.0, 2);

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