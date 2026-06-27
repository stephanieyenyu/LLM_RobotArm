using OpenCvSharp;
using System;
using System.Text.Json;

public static class PartAExporter
{
    public static bool Run()
    {
        string imagePath = "images/test_scene.jpg";

        // useWebcam = true 會從攝影機拍；false 用現有圖片
        bool useWebcam = false;
        int cameraIndex = 0;

        Console.WriteLine("Current folder: " + Directory.GetCurrentDirectory());
        Console.WriteLine("Image path: " + Path.GetFullPath(imagePath));

        if (useWebcam)
        {
            var webcam = new WebcamCapture();
            bool captured = webcam.CaptureImage(
                outputPath: imagePath,
                cameraIndex: cameraIndex,
                width: 1280,
                height: 720
            );

            if (!captured)
                Console.WriteLine("Webcam capture failed. Falling back to existing images/test_scene.jpg.");
        }

        Console.WriteLine("Image exists: " + File.Exists(imagePath));

        Mat image = Cv2.ImRead(imagePath);

        if (image.Empty())
        {
            Console.WriteLine("Cannot read image. Put test_scene.jpg inside the images folder.");
            return false;
        }

        // --- QRCode 偵測 ---
        var qrDetector = new QrCodeDetectorService();
        var qrcodes = qrDetector.Detect(image);

        // --- 物件偵測：優先用 open-vocab，fallback 用 YOLO ---
        List<ObjectDetectionResult> objects;
        var openVocabDetector = new OpenVocabDetectorService();
        var openVocabObjects = openVocabDetector.Detect();

        if (openVocabObjects.Count > 0)
        {
            objects = openVocabObjects;
            Console.WriteLine("Using open-vocabulary detection results.");
        }
        else
        {
            var yoloDetector = new YoloDetectorService();
            objects = yoloDetector.Detect(image);
            Console.WriteLine("Using YOLO fallback detection results.");
        }

        // --- 座標對應（四點 homography，需要 QR1~QR4）---
        var coordinateMapper = new CoordinateMapper();
        var mappedObjects = coordinateMapper.MapObjectsToWorkspace(objects, qrcodes);

        Console.WriteLine($"QR codes detected: {qrcodes.Count}");
        Console.WriteLine($"Objects detected: {objects.Count}");

        if (!coordinateMapper.HasRequiredQrCodes(qrcodes))
            Console.WriteLine("Warning: QR1, QR2, QR3, QR4 are not all detected.");
        else
        {
            double qrArea = coordinateMapper.CalculateQrArea(qrcodes);
            Console.WriteLine($"QR workspace area in image pixels: {qrArea}");
            if (qrArea < 1000)
                Console.WriteLine("Warning: QR codes are too close together or nearly collinear.");
        }

        if (objects.Count == 0)
            Console.WriteLine("Warning: no objects detected.");

        // --- 畫視覺化圖 ---
        Mat visual = image.Clone();

        foreach (var qr in qrcodes)
        {
            if (qr.center_pixel.Length < 2) continue;

            int cx = (int)qr.center_pixel[0];
            int cy = (int)qr.center_pixel[1];

            Cv2.Circle(visual, new Point(cx, cy), 8, new Scalar(0, 0, 255), -1);
            Cv2.PutText(visual, qr.id, new Point(cx + 10, cy),
                HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 0, 255), 2);

            foreach (var corner in qr.corners)
            {
                if (corner.Length < 2) continue;
                Cv2.Circle(visual, new Point((int)corner[0], (int)corner[1]),
                    5, new Scalar(255, 0, 0), -1);
            }
        }

        foreach (var obj in mappedObjects)
        {
            if (obj.bbox.Length < 4) continue;

            int x1 = (int)obj.bbox[0], y1 = (int)obj.bbox[1];
            int x2 = (int)obj.bbox[2], y2 = (int)obj.bbox[3];

            Cv2.Rectangle(visual, new Point(x1, y1), new Point(x2, y2),
                new Scalar(0, 255, 0), 4);
            Cv2.PutText(visual, $"{obj.name} {obj.confidence}",
                new Point(x1, Math.Max(y1 - 10, 20)),
                HersheyFonts.HersheySimplex, 1.0, new Scalar(0, 255, 0), 3);
            Cv2.PutText(visual, $"x:{obj.world_position.x:F3} z:{obj.world_position.z:F3}",
                new Point(x1, Math.Min(y2 + 30, image.Height - 10)),
                HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 0), 2);
        }

        // --- 組 JSON 輸出 ---
        // 這份 JSON 是給 Part B 使用的，所以 objects 用原始 YOLO/open-vocab 偵測結果
        // 不使用 mappedObjects，因為 3D 座標要交給 coordinate_mapper_3d.py 計算
        var output = new
        {
            image_width = image.Width,
            image_height = image.Height,
            objects = objects,
            qrcodes = qrcodes
        };
        string json = JsonSerializer.Serialize(output,
            new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory("outputs");
        Directory.CreateDirectory("../sample_json");

        File.WriteAllText("outputs/detection_result.json", json);
        File.WriteAllText("../sample_json/detected_objects.json", json);

        Cv2.ImWrite("outputs/visual_result.jpg", visual);
        Cv2.ImWrite("../sample_json/visual_result.jpg", visual);


        Console.WriteLine(json);
        Console.WriteLine("Saved to outputs/detection_result.json");
        Console.WriteLine("Saved to ../sample_json/detected_objects.json");
        Console.WriteLine("Saved to outputs/visual_result.jpg");
        Console.WriteLine("Saved to ../sample_json/visual_result.jpg");
        image.Dispose();
        visual.Dispose();
        return true;
    }
}
