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
        using var capture = new VideoCapture(cameraIndex);

        if (!capture.IsOpened())
        {
            Console.WriteLine($"Cannot open webcam at index {cameraIndex}.");
            return false;
        }

        capture.Set(VideoCaptureProperties.FrameWidth, width);
        capture.Set(VideoCaptureProperties.FrameHeight, height);

        using var frame = new Mat();

        // Camera warm-up. Some webcams return dark or stale frames at first.
        for (int i = 0; i < 15; i++)
        {
            capture.Read(frame);
        }

        if (frame.Empty())
        {
            Console.WriteLine("Cannot read frame from webcam.");
            return false;
        }

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