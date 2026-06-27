using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public static class PartAExporter
{
    public static bool Run()
    {
        Console.WriteLine("=== Running Part A Detection ===");

        Directory.CreateDirectory("images");
        Directory.CreateDirectory("outputs");
        Directory.CreateDirectory("../sample_json");

        string imagePath = "images/test_scene.jpg";

        // 1. 先嘗試用 webcam 拍照
        WebcamCapture webcam = new WebcamCapture();
        bool captured = webcam.CaptureImage(imagePath);

        if (!captured)
        {
            Console.WriteLine("Webcam capture failed. Use existing images/test_scene.jpg if available.");
        }

        // 2. 如果 webcam 失敗，就用原本的 test_scene.jpg
        if (!File.Exists(imagePath))
        {
            Console.WriteLine($"Image not found: {imagePath}");
            return false;
        }

        using Mat image = Cv2.ImRead(imagePath);

        if (image.Empty())
        {
            Console.WriteLine("Failed to read image.");
            return false;
        }

        Console.WriteLine($"Image loaded: {image.Width} x {image.Height}");

        // 3. 偵測 QRCode
        QrCodeDetectorService qrDetector = new QrCodeDetectorService();
        List<QrCodeResult> qrcodes = qrDetector.Detect(image);

        Console.WriteLine($"Detected QRCodes: {qrcodes.Count}");

        foreach (var qr in qrcodes)
        {
            Console.WriteLine($"{qr.id}: center=({qr.center_pixel[0]}, {qr.center_pixel[1]}), corners={qr.corners.Length}");
        }

        // 4. 偵測 YOLO 物件
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

        // 6. 輸出 JSON
        File.WriteAllText("outputs/detection_result.json", json);
        File.WriteAllText("../sample_json/detected_objects.json", json);

        // 7. 輸出圖片備份
        Cv2.ImWrite("outputs/visual_result.jpg", image);
        Cv2.ImWrite("../sample_json/visual_result.jpg", image);

        Console.WriteLine("Saved to outputs/detection_result.json");
        Console.WriteLine("Saved to ../sample_json/detected_objects.json");
        Console.WriteLine("Saved to outputs/visual_result.jpg");
        Console.WriteLine("Saved to ../sample_json/visual_result.jpg");

        Console.WriteLine("=== Part A Detection finished ===");

        return true;
    }
}