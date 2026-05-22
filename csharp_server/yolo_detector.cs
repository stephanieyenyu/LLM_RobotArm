using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

public class YoloDetectorService
{
    private readonly InferenceSession session;
    private readonly string inputName;

    private const int InputWidth = 640;
    private const int InputHeight = 640;
    private const float ConfidenceThreshold = 0.35f;
    private const float NmsThreshold = 0.45f;

    private readonly string[] classNames =
    {
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
        "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
        "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
        "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
        "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake",
        "chair", "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop",
        "mouse", "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
        "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier",
        "toothbrush"
    };

    private readonly HashSet<string> allowedObjects = new HashSet<string>
{
    "cup",
    "bottle",
    "book",
    "cell phone",
    "mouse",
    "keyboard",
    "laptop"
};

    public YoloDetectorService()
    {
        string modelPath = "models/yolo11n.onnx";

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"YOLO model not found: {modelPath}");
        }

        session = new InferenceSession(modelPath);
        inputName = session.InputMetadata.Keys.First();
    }

    public List<ObjectDetectionResult> Detect(Mat image)
    {
        int originalWidth = image.Width;
        int originalHeight = image.Height;

        using Mat resized = new Mat();
        Cv2.Resize(image, resized, new Size(InputWidth, InputHeight));

        using Mat rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        var inputTensor = new DenseTensor<float>(new[] { 1, 3, InputHeight, InputWidth });

        for (int y = 0; y < InputHeight; y++)
        {
            for (int x = 0; x < InputWidth; x++)
            {
                Vec3b pixel = rgb.At<Vec3b>(y, x);

                inputTensor[0, 0, y, x] = pixel.Item0 / 255.0f;
                inputTensor[0, 1, y, x] = pixel.Item1 / 255.0f;
                inputTensor[0, 2, y, x] = pixel.Item2 / 255.0f;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);

        var output = results.First().AsTensor<float>();

        Console.WriteLine("YOLO output shape: " + string.Join(" x ", output.Dimensions.ToArray()));

        int dim1 = output.Dimensions[1];
        int dim2 = output.Dimensions[2];

        var detections = new List<RawDetection>();

        if (dim1 < dim2)
        {
            ParseOutputShapeOne(output, detections, originalWidth, originalHeight);
        }
        else
        {
            ParseOutputShapeTwo(output, detections, originalWidth, originalHeight);
        }

        Console.WriteLine($"Raw detections above threshold: {detections.Count}");

        var finalDetections = ApplyNms(detections);

        Console.WriteLine($"Final detections after NMS: {finalDetections.Count}");

        return finalDetections.Select(d => new ObjectDetectionResult
        {
            name = d.Name,
            confidence = Math.Round(d.Confidence, 3),
            bbox = new double[]
            {
                Math.Round(d.X1, 2),
                Math.Round(d.Y1, 2),
                Math.Round(d.X2, 2),
                Math.Round(d.Y2, 2)
            },
            center_pixel = new double[]
            {
                Math.Round((d.X1 + d.X2) / 2.0, 2),
                Math.Round((d.Y1 + d.Y2) / 2.0, 2)
            }
        }).ToList();
    }

    private void ParseOutputShapeOne(
        Tensor<float> output,
        List<RawDetection> detections,
        int originalWidth,
        int originalHeight)
    {
        int attributes = output.Dimensions[1];
        int boxes = output.Dimensions[2];

        for (int i = 0; i < boxes; i++)
        {
            float cx = output[0, 0, i];
            float cy = output[0, 1, i];
            float w = output[0, 2, i];
            float h = output[0, 3, i];

            int bestClassId = -1;
            float bestScore = 0;

            for (int c = 4; c < attributes; c++)
            {
                float score = output[0, c, i];

                if (score > bestScore)
                {
                    bestScore = score;
                    bestClassId = c - 4;
                }
            }

            AddDetection(
                detections,
                cx,
                cy,
                w,
                h,
                bestClassId,
                bestScore,
                originalWidth,
                originalHeight
            );
        }
    }

    private void ParseOutputShapeTwo(
        Tensor<float> output,
        List<RawDetection> detections,
        int originalWidth,
        int originalHeight)
    {
        int boxes = output.Dimensions[1];
        int attributes = output.Dimensions[2];

        for (int i = 0; i < boxes; i++)
        {
            float cx = output[0, i, 0];
            float cy = output[0, i, 1];
            float w = output[0, i, 2];
            float h = output[0, i, 3];

            int bestClassId = -1;
            float bestScore = 0;

            for (int c = 4; c < attributes; c++)
            {
                float score = output[0, i, c];

                if (score > bestScore)
                {
                    bestScore = score;
                    bestClassId = c - 4;
                }
            }

            AddDetection(
                detections,
                cx,
                cy,
                w,
                h,
                bestClassId,
                bestScore,
                originalWidth,
                originalHeight
            );
        }
    }

    private void AddDetection(
        List<RawDetection> detections,
        float cx,
        float cy,
        float w,
        float h,
        int classId,
        float confidence,
        int originalWidth,
        int originalHeight)
    {
        if (confidence < ConfidenceThreshold)
        {
            return;
        }

        if (classId < 0 || classId >= classNames.Length)
        {
            return;
        }

        float scaleX = originalWidth / (float)InputWidth;
        float scaleY = originalHeight / (float)InputHeight;

        float x1 = (cx - w / 2) * scaleX;
        float y1 = (cy - h / 2) * scaleY;
        float x2 = (cx + w / 2) * scaleX;
        float y2 = (cy + h / 2) * scaleY;

        x1 = Math.Clamp(x1, 0, originalWidth - 1);
        y1 = Math.Clamp(y1, 0, originalHeight - 1);
        x2 = Math.Clamp(x2, 0, originalWidth - 1);
        y2 = Math.Clamp(y2, 0, originalHeight - 1);

        string objectName = classNames[classId];

        if (!allowedObjects.Contains(objectName))
        {
            return;
        }

        detections.Add(new RawDetection
        {
            Name = objectName,
            Confidence = confidence,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2
        });
    }

    private List<RawDetection> ApplyNms(List<RawDetection> detections)
    {
        var finalDetections = new List<RawDetection>();

        var sorted = detections
            .OrderByDescending(d => d.Confidence)
            .ToList();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            finalDetections.Add(best);
            sorted.RemoveAt(0);

            sorted = sorted
                .Where(d => CalculateIoU(best, d) < NmsThreshold)
                .ToList();
        }

        return finalDetections;
    }

    private float CalculateIoU(RawDetection a, RawDetection b)
    {
        float x1 = Math.Max(a.X1, b.X1);
        float y1 = Math.Max(a.Y1, b.Y1);
        float x2 = Math.Min(a.X2, b.X2);
        float y2 = Math.Min(a.Y2, b.Y2);

        float intersectionWidth = Math.Max(0, x2 - x1);
        float intersectionHeight = Math.Max(0, y2 - y1);
        float intersectionArea = intersectionWidth * intersectionHeight;

        float areaA = Math.Max(0, a.X2 - a.X1) * Math.Max(0, a.Y2 - a.Y1);
        float areaB = Math.Max(0, b.X2 - b.X1) * Math.Max(0, b.Y2 - b.Y1);

        float unionArea = areaA + areaB - intersectionArea;

        if (unionArea <= 0)
        {
            return 0;
        }

        return intersectionArea / unionArea;
    }

    private class RawDetection
    {
        public string Name { get; set; } = "";
        public float Confidence { get; set; }
        public float X1 { get; set; }
        public float Y1 { get; set; }
        public float X2 { get; set; }
        public float Y2 { get; set; }
    }
}