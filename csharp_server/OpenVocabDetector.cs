using System.Diagnostics;
using System.Text.Json;

public class OpenVocabDetectorService
{
    private readonly string pythonExe = @"open_vocab_env\Scripts\python.exe";
    private readonly string scriptPath = @"open_vocab\detect_open_vocab.py";
    private readonly string outputPath = @"open_vocab\outputs\objects_open.json";

    public List<ObjectDetectionResult> Detect()
    {
        if (!File.Exists(pythonExe))
        {
            Console.WriteLine("Open-vocabulary Python environment not found.");
            return new List<ObjectDetectionResult>();
        }

        if (!File.Exists(scriptPath))
        {
            Console.WriteLine("Open-vocabulary detector script not found.");
            return new List<ObjectDetectionResult>();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = scriptPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);

        if (process == null)
        {
            Console.WriteLine("Could not start open-vocabulary detector.");
            return new List<ObjectDetectionResult>();
        }

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Console.WriteLine("Open-vocabulary detector failed.");
            Console.WriteLine(stderr);
            return new List<ObjectDetectionResult>();
        }

        if (!File.Exists(outputPath))
        {
            Console.WriteLine("Open-vocabulary output file not found.");
            return new List<ObjectDetectionResult>();
        }

        string json = File.ReadAllText(outputPath);

        var result = JsonSerializer.Deserialize<OpenVocabOutput>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }
        );

        return result?.objects ?? new List<ObjectDetectionResult>();
    }
}