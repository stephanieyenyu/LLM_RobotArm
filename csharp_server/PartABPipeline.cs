using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

public static class PartABPipeline
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Running Part A + Part B Pipeline ===");

        Directory.CreateDirectory("images");
        Directory.CreateDirectory("outputs");
        Directory.CreateDirectory("../sample_json");

        string imagePath = "images/test_scene.jpg";

        // 1. Part A: 拍攝圖片
        WebcamCapture webcam = new WebcamCapture();
        bool captured = webcam.CaptureImage(imagePath);

        if (!captured)
        {
            Console.WriteLine("Webcam capture failed. Use existing images/test_scene.jpg if available.");
        }

        if (!File.Exists(imagePath))
        {
            Console.WriteLine($"Image not found: {imagePath}");
            return;
        }

        // 2. 讀取圖片
        using Mat image = Cv2.ImRead(imagePath);

        if (image.Empty())
        {
            Console.WriteLine("Failed to read image.");
            return;
        }

        Console.WriteLine($"Image loaded: {image.Width} x {image.Height}");

        // 3. Part A: 偵測 QRCode
        QrCodeDetectorService qrDetector = new QrCodeDetectorService();
        List<QrCodeResult> qrcodes = qrDetector.Detect(image);

        Console.WriteLine($"Detected QRCodes: {qrcodes.Count}");

        foreach (var qr in qrcodes)
        {
            Console.WriteLine($"{qr.id}: center=({qr.center_pixel[0]}, {qr.center_pixel[1]}), corners={qr.corners.Length}");
        }

        // 4. Part A: 偵測 YOLO 物件
        YoloDetectorService yoloDetector = new YoloDetectorService();
        List<ObjectDetectionResult> objects = yoloDetector.Detect(image);

        Console.WriteLine($"Detected objects: {objects.Count}");

        foreach (var obj in objects)
        {
            Console.WriteLine($"{obj.name}: confidence={obj.confidence}, center=({obj.center_pixel[0]}, {obj.center_pixel[1]})");
        }

        // 5. 組成 Part B 要讀的 JSON
        var detectionOutput = new
        {
            image_width = image.Width,
            image_height = image.Height,
            objects = objects,
            qrcodes = qrcodes
        };

        string json = JsonSerializer.Serialize(
            detectionOutput,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        );

        // 6. 輸出 Part A 結果
        File.WriteAllText("outputs/detection_result.json", json);
        File.WriteAllText("../sample_json/detected_objects.json", json);

        Cv2.ImWrite("outputs/visual_result.jpg", image);
        Cv2.ImWrite("../sample_json/visual_result.jpg", image);

        Console.WriteLine("Saved to outputs/detection_result.json");
        Console.WriteLine("Saved to ../sample_json/detected_objects.json");
        Console.WriteLine("Saved to outputs/visual_result.jpg");
        Console.WriteLine("Saved to ../sample_json/visual_result.jpg");

        // 7. Part B: 呼叫 Python 3D coordinate mapper
        Console.WriteLine("Running Part B Python coordinate mapper...");

        bool partBSuccess = await RunPythonPartBAsync();

        if (!partBSuccess)
        {
            Console.WriteLine("Part B failed. Please check the error message above.");
            return;
        }

        Console.WriteLine("=== Part A + Part B Pipeline finished ===");
    }

    private static async Task<bool> RunPythonPartBAsync()
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "coordinate_mapper_3d.py",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processInfo);

        if (process == null)
        {
            Console.WriteLine("Failed to start Part B Python process.");
            return false;
        }

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(output);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.WriteLine("Part B error:");
            Console.WriteLine(error);
        }

        Console.WriteLine($"Part B exited with code: {process.ExitCode}");

        return process.ExitCode == 0;
    }
}
