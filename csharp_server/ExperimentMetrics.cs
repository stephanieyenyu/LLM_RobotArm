using System.Text.Json;
public static class ExperimentMetrics
{
    public static void Write(string root)
    {
        var outcomes = new List<(bool success, string status, int attempts)>();
        foreach (var directory in Directory.GetDirectories(root))
        {
            var path = Path.Combine(directory, "result.json");
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            outcomes.Add((r.GetProperty("success").GetBoolean(), r.GetProperty("status").GetString() ?? "", r.GetProperty("attempts").GetInt32()));
        }
        var valid = outcomes.Where(r => r.status is "success" or "failed").ToList();
        var successes = valid.Where(r => r.success).ToList();
        double? Rate(int attempt) => valid.Count == 0 ? null : (double)successes.Count(r => r.attempts <= attempt) / valid.Count;
        var summary = new { total_trials = outcomes.Count, valid_trials = valid.Count,
            infrastructure_errors = outcomes.Count(r => r.status == "infrastructure_error"),
            adapter_errors = outcomes.Count(r => r.status == "adapter_error"),
            execution_unknown = outcomes.Count(r => r.status == "execution_unknown"),
            success_rate_within_10 = Rate(10), first_attempt_success_rate = Rate(1),
            mean_attempts_among_successes = successes.Count == 0 ? (double?)null : successes.Average(r => r.attempts),
            cumulative_success_rates = Enumerable.Range(1, 10).Select(i => new { attempt = i, success_rate = Rate(i) }),
            evaluation = "independent_visual_model; requires human calibration" };
        File.WriteAllText(Path.Combine(root, "metrics.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }
}
