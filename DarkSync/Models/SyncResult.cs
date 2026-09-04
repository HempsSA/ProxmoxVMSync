namespace DarkSync.Models;

public class HealthInfo
{
    public string Status { get; set; } = "UNKNOWN";
    public string Message { get; set; } = "";
    public int SourceCount { get; set; }
    public int ExternalCount { get; set; }
    public DateTime? Newest { get; set; }
}

public class SyncResult
{
    public bool Dry { get; set; }
    public List<(string Action, int VmId, string Path)> Results { get; set; } = [];
    public Dictionary<int, HealthInfo> Health { get; set; } = [];
    public int SourceCount { get; set; }
    public int ExternalCount { get; set; }
    public List<string> Warnings { get; set; } = [];
}
