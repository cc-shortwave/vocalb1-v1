using System.Text.Json.Serialization;

namespace VocalB1.Engine.Bridge;

/// <summary>A single line of JSON returned by the bridge process. Fields are filled on demand; unknown fields are ignored.</summary>
internal sealed class BridgeMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("value")] public double? Value { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("elapsedSec")] public double? ElapsedSec { get; set; }
    [JsonPropertyName("durationSec")] public double? DurationSec { get; set; }
    [JsonPropertyName("traceback")] public string? Traceback { get; set; }
    [JsonPropertyName("python")] public string? Python { get; set; }
    [JsonPropertyName("root")] public string? Root { get; set; }
    [JsonPropertyName("engine")] public string? Engine { get; set; }
    [JsonPropertyName("importError")] public string? ImportError { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }
}

internal static class BridgeJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Protocol requests must be single-line JSON (indentation disabled).</summary>
    public static readonly System.Text.Json.JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
