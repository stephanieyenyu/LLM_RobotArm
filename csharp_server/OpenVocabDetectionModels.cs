public class OpenVocabOutput
{
    public int image_width { get; set; }
    public int image_height { get; set; }
    public List<ObjectDetectionResult> objects { get; set; } = new();
    public List<string> prompts { get; set; } = new();
}