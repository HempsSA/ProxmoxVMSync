using System.Text.Json.Serialization;

namespace DarkSync.Models;

public class VmPolicy
{
    [JsonPropertyName("vmid")]
    public int VmId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("importance")]
    public int Importance { get; set; } = 1;

    [JsonPropertyName("copies")]
    public int Copies { get; set; } = 1;

    [JsonPropertyName("max_age")]
    public int MaxAge { get; set; } = 7;
}
