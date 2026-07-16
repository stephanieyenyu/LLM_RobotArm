using System.Collections.Generic;
using System.Text.Json.Serialization;

public class SceneObject
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }
}

public class PlacementStep
{
    // arrange_pattern 展開後每一步的來源（補貨區某顆積木）與目標（拼字擺放區某格）
    [JsonPropertyName("source_position")]
    public SceneObject? SourcePosition { get; set; }

    [JsonPropertyName("target_position")]
    public SceneObject? TargetPosition { get; set; }
}

public class RobotPlan
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("distance_cm")]
    public double? DistanceCm { get; set; }

    [JsonPropertyName("object_position")]
    public SceneObject? ObjectPosition { get; set; }

    [JsonPropertyName("target_position")]
    public SceneObject? TargetPosition { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    // arrange_pattern 專用
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("block_color")]
    public string? BlockColor { get; set; }

    [JsonPropertyName("placement_steps")]
    public List<PlacementStep>? PlacementSteps { get; set; }
}

public class LlmRobotPlanResult
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("reference_object")]
    public string? ReferenceObject { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("distance_cm")]
    public double? DistanceCm { get; set; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    // arrange_pattern 專用
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("block_color")]
    public string? BlockColor { get; set; }
}
