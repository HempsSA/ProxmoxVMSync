namespace DarkSync.Models;

public record Backup(
    int VmId,
    string Kind,
    DateTime When,
    string Path,
    long Size,
    string Source,
    bool Remote = false
);
