using System.Text.Json.Serialization;

namespace DarkSync.Models;

public class Config
{
    [JsonPropertyName("sources")]
    public List<Source> Sources { get; set; } = [];

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    [JsonPropertyName("archive_id")]
    public string ArchiveId { get; set; } = "";

    [JsonPropertyName("vms")]
    public List<VmPolicy> Vms { get; set; } = [];

    [JsonPropertyName("min_free_gb")]
    public int MinFreeGb { get; set; } = 5;

    [JsonPropertyName("retention")]
    public string Retention { get; set; } = "Keep all";

    [JsonPropertyName("recycle_days")]
    public int RecycleDays { get; set; } = 7;

    [JsonPropertyName("ntfy_enabled")]
    public bool NtfyEnabled { get; set; }

    [JsonPropertyName("ntfy_server")]
    public string NtfyServer { get; set; } = "https://ntfy.sh";

    [JsonPropertyName("ntfy_topic")]
    public string NtfyTopic { get; set; } = "";

    [JsonPropertyName("ntfy_token")]
    public string NtfyToken { get; set; } = "";

    [JsonPropertyName("ntfy_priority")]
    public string NtfyPriority { get; set; } = "high";

    [JsonPropertyName("ntfy_on_success")]
    public bool NtfyOnSuccess { get; set; } = true;

    [JsonPropertyName("ntfy_on_failure")]
    public bool NtfyOnFailure { get; set; } = true;

    [JsonPropertyName("schedule_enabled")]
    public bool ScheduleEnabled { get; set; }

    [JsonPropertyName("schedule_time")]
    public string ScheduleTime { get; set; } = "02:00";

    [JsonPropertyName("schedule_dry_run")]
    public bool ScheduleDryRun { get; set; }

    [JsonPropertyName("schedule_last_run_date")]
    public string ScheduleLastRunDate { get; set; } = "";
}
