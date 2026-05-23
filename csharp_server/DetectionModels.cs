public class ObjectDetectionResult
{
    public string name { get; set; } = "";
    public double confidence { get; set; }
    public double[] bbox { get; set; } = Array.Empty<double>();
    public double[] center_pixel { get; set; } = Array.Empty<double>();
    public string source { get; set; } = "";

}