using OpenCvSharp;

public class WebcamCapture
{
    public bool CaptureImage(
        string outputPath,
        int cameraIndex = 0,
        int width = 1280,
        int height = 720
    )
    {
        using var capture = new VideoCapture(cameraIndex,VideoCaptureAPIs.DSHOW);

        if (!capture.IsOpened())
        {
            Console.WriteLine($"Cannot open webcam at index {cameraIndex}.");
            return false;
        }

        capture.Set(VideoCaptureProperties.FrameWidth, width);
        capture.Set(VideoCaptureProperties.FrameHeight, height);

        using var frame = new Mat();

        // Camera warm-up: 讀幾張 + 讓 exposure/AWB 有時間穩定
        for (int i = 0; i < 15; i++)
        {
            capture.Read(frame);
            System.Threading.Thread.Sleep(30);   // 給相機 ~450ms 暖機
        }

        // 亮度檢查 + 重試：若整張太暗（相機還沒穩），最多再拍 10 張
        const double MinMeanBrightness = 15.0;   // 0-255，全黑 = 0
        const int MaxRetries = 10;

        double meanBrightness = 0;
        int retry = 0;

        while (retry <= MaxRetries)
        {
            capture.Read(frame);

            if (frame.Empty())
            {
                Console.WriteLine("Cannot read frame from webcam.");
                return false;
            }

            using (Mat gray = new Mat())
            {
                if (frame.Channels() == 1)
                    frame.CopyTo(gray);
                else
                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

                meanBrightness = Cv2.Mean(gray).Val0;
            }

            if (meanBrightness >= MinMeanBrightness)
                break;

            Console.WriteLine($"Frame too dark (mean brightness {meanBrightness:F1}), retry {retry + 1}/{MaxRetries}...");
            System.Threading.Thread.Sleep(100);
            retry++;
        }

        if (meanBrightness < MinMeanBrightness)
        {
            Console.WriteLine($"Webcam still dark after {MaxRetries} retries (mean brightness {meanBrightness:F1}). Aborting.");
            return false;
        }

        Console.WriteLine($"Frame accepted (mean brightness {meanBrightness:F1})");

        string? directory = Path.GetDirectoryName(outputPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bool saved = Cv2.ImWrite(outputPath, frame);

        if (!saved)
        {
            Console.WriteLine($"Failed to save webcam image to: {outputPath}");
            return false;
        }

        Console.WriteLine($"Webcam image saved to: {outputPath}");
        Console.WriteLine($"Captured frame size: {frame.Width} x {frame.Height}");

        return true;
    }
}
