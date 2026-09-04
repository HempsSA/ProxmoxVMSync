namespace DarkSync.Models;

public class HistoryEntry
{
    public long Id { get; set; }
    public string Timestamp { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Result { get; set; } = "";
    public string Details { get; set; } = "";
    public int SourceCount { get; set; }
    public int ExternalCount { get; set; }
    public int Actions { get; set; }
    public int Warnings { get; set; }
    public string VmSnapshotJson { get; set; } = "[]";
}
