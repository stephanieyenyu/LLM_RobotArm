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

            if (result.ResultPoints == null || result.ResultPoints.Length == 0)
            {
                continue;
            }

            double cx = result.ResultPoints.Average(p => p.X);
            double cy = result.ResultPoints.Average(p => p.Y);

            double[][] corners = result.ResultPoints
                .Select(p => new[]
                {
                    Math.Round((double)p.X, 2),
                    Math.Round((double)p.Y, 2)
                })
                .ToArray();

            results.Add(new QrCodeResult
            {
                id = string.IsNullOrWhiteSpace(result.Text) ? $"QR{i + 1}" : result.Text,
                center_pixel = new[]
                {
                    Math.Round(cx, 2),
                    Math.Round(cy, 2)
                },
                corners = corners
            });
        }

        rgb.Dispose();
        return results;
    }
}