using OpenCvSharp;
using System.Text.Json;

string imagePath = "images/test_scene.jpg";

Console.WriteLine("Current folder: " + Directory.GetCurrentDirectory());
Console.WriteLine("Image path: " + Path.GetFullPath(imagePath));
Console.WriteLine("Image exists: " + File.Exists(imagePath));

Mat image = Cv2.ImRead(imagePath);

if (image.Empty())
{
    Console.WriteLine("Cannot read image. Put test_scene.jpg inside the images folder.");
    return;
}

var qrDetector = new QrCodeDetectorService();
var qrcodes = qrDetector.Detect(image);

var openVocabDetector = new OpenVocabDetectorService();
var openVocabObjects = openVocabDetector.Detect();

List<ObjectDetectionResult> objects;

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

Console.WriteLine($"Objects detected: {objects.Count}");

foreach (var obj in objects)
{
    Console.WriteLine($"{obj.name} {obj.confidence} [{string.Join(", ", obj.bbox)}]");
}

Mat visual = image.Clone();

foreach (var qr in qrcodes)
{
    int cx = (int)qr.center_pixel[0];
    int cy = (int)qr.center_pixel[1];

    Cv2.Circle(
        visual,
        new Point(cx, cy),
        8,
        new Scalar(0, 0, 255),
        -1
    );

    Cv2.PutText(
        visual,
        qr.id,
        new Point(cx + 10, cy),
        HersheyFonts.HersheySimplex,
        0.8,
        new Scalar(0, 0, 255),
        2
    );

    foreach (var corner in qr.corners)
    {
        int x = (int)corner[0];
        int y = (int)corner[1];

        Cv2.Circle(
            visual,
            new Point(x, y),
            5,
            new Scalar(255, 0, 0),
            -1
        );
    }
}

foreach (var obj in objects)
{
    if (obj.bbox.Length < 4)
    {
        continue;
    }

    int x1 = (int)obj.bbox[0];
    int y1 = (int)obj.bbox[1];
    int x2 = (int)obj.bbox[2];
    int y2 = (int)obj.bbox[3];

    Console.WriteLine($"Drawing box: {obj.name}, {x1}, {y1}, {x2}, {y2}");

    Cv2.Rectangle(
        visual,
        new Point(x1, y1),
        new Point(x2, y2),
        new Scalar(0, 255, 0),
        4
    );

    Cv2.PutText(
        visual,
        $"{obj.name} {obj.confidence}",
        new Point(x1, Math.Max(y1 - 10, 20)),
        HersheyFonts.HersheySimplex,
        1.0,
        new Scalar(0, 255, 0),
        3
    );
}

var coordinateMapper = new CoordinateMapper();
var mappedObjects = coordinateMapper.MapObjectsToWorkspace(objects, qrcodes);

var output = new
{
    image_width = image.Width,
    image_height = image.Height,
    objects = mappedObjects,
    qrcodes = qrcodes,
    workspace = new
    {
        origin = "QR1",
        x_axis = "QR1_to_QR2",
        z_axis = "QR1_to_QR3",
        unit = "metres"
    }
};

string json = JsonSerializer.Serialize(output, new JsonSerializerOptions
{
    WriteIndented = true
});

var qr1 = qrcodes.FirstOrDefault(q => q.id == "QR1");
var qr2 = qrcodes.FirstOrDefault(q => q.id == "QR2");
var qr3 = qrcodes.FirstOrDefault(q => q.id == "QR3");

if (qr1 == null || qr2 == null || qr3 == null)
{
    Console.WriteLine("Warning: QR1, QR2, QR3 are not all detected.");
}
else
{
    double area =
        Math.Abs(
            qr1.center_pixel[0] * (qr2.center_pixel[1] - qr3.center_pixel[1]) +
            qr2.center_pixel[0] * (qr3.center_pixel[1] - qr1.center_pixel[1]) +
            qr3.center_pixel[0] * (qr1.center_pixel[1] - qr2.center_pixel[1])
        ) / 2.0;

    Console.WriteLine($"QR triangle area: {area}");

    if (area < 1000)
    {
        Console.WriteLine("Warning: QR codes are too close to a straight line.");
    }
}

Directory.CreateDirectory("outputs");

File.WriteAllText("outputs/detection_result.json", json);
Cv2.ImWrite("outputs/visual_result.jpg", visual);

Console.WriteLine(json);
Console.WriteLine("Saved to outputs/detection_result.json");
Console.WriteLine("Saved to outputs/visual_result.jpg");