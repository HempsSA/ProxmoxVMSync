using System.Text.Json.Serialization;

namespace DarkSync.Models;

public class Source
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "PVE";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("key_file")]
    public string KeyFile { get; set; } = "";
}
