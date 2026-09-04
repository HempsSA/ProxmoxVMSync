using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DarkSync.Models;
using Renci.SshNet;

namespace DarkSync.Services;

public static partial class ScanService
{
    private static readonly Regex BackupRegex = BackupRegexGenerated();

    private const string PartialExts = ".tmp|.part|.partial|.incomplete";
    private static readonly HashSet<string> ExcludedSuffixes = new() { ".log", ".dat" };
    private static readonly HashSet<string> AllowedSuffixes = new() { ".zst" };

    public static string NormalizePath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (OperatingSystem.IsWindows() && path.StartsWith("//"))
            path = "\\\\" + path[2..].Replace('/', '\\');
        while (path.Length > 1 && (path.EndsWith('\\') || path.EndsWith('/')) && !path.EndsWith("\\\\"))
            path = path[..^1];
        return path;
    }

    public static Backup? ParseBackup(string name, long size, string path, string source, bool remote = false)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(PartialExts) || ExcludedSuffixes.Any(e => lower.EndsWith(e)))
            return null;
        if (!AllowedSuffixes.Any(e => lower.EndsWith(e)))
            return null;

        var m = BackupRegex.Match(name);
        if (!m.Success) return null;

        try
        {
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            var vmid = int.Parse(m.Groups["vmid"].Value);
            var dateStr = $"{m.Groups["date"].Value}-{m.Groups["time"].Value}";
            var when = DateTime.ParseExact(dateStr, "yyyy_MM_dd-HH_mm_ss", null);
            return new Backup(vmid, kind, when, path, size, source, remote);
        }
        catch
        {
            return null;
        }
    }

    public static (List<Backup> Backups, List<string> Errors) ScanLocal(string path, string source,
        CancellationToken cancel, IProgress<string>? progress = null)
    {
        var root = NormalizePath(path);
        var found = new List<Backup>();
        var errors = new List<string>();

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"{source}: cannot access {root}");

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(root, "*.*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            }))
            {
                if (cancel.IsCancellationRequested)
                    throw new OperationCanceledException();

                var dir = Path.GetDirectoryName(filePath);
                if (dir != null && Path.GetFileName(dir).StartsWith(".darksync_"))
                    continue;

                var name = Path.GetFileName(filePath);
                try
                {
                    var fi = new FileInfo(filePath);
                    var b = ParseBackup(name, fi.Length, filePath, source);
                    if (b != null) found.Add(b);
                }
                catch (Exception ex)
                {
                    errors.Add($"{filePath}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Add($"{root}: {ex.Message}");
        }

        return (found, errors);
    }

    public static (List<Backup> Backups, List<string> Errors) ScanSftp(Source src, string password,
        CancellationToken cancel, IProgress<string>? progress = null)
    {
        var (host, port, user, root) = SftpService.ParseUri(src.Path);
        using var client = SftpService.CreateClient(host, port, user, password, src.KeyFile);
        client.Connect();
        using var sftp = SftpService.CreateSftpClient(host, port, user, password, src.KeyFile);

        sftp.Connect();
        progress?.Report($"Connected to SFTP source {src.Name}");

        var found = new List<Backup>();
        var errors = new List<string>();
        var todo = new Stack<string>();
        todo.Push(root);
        var foldersScanned = 0;
        var entriesScanned = 0;

        try
        {
            while (todo.Count > 0)
            {
                if (cancel.IsCancellationRequested)
                    throw new OperationCanceledException();

                var folder = todo.Pop();
                foldersScanned++;
                progress?.Report($"SFTP scan {src.Name}: listing {folder}");

                foreach (var entry in sftp.ListDirectory(folder))
                {
                    if (cancel.IsCancellationRequested)
                        throw new OperationCanceledException();

                    if (entry.Name == "." || entry.Name == "..") continue;
                    entriesScanned++;
                    var fullPath = folder.TrimEnd('/') + "/" + entry.Name;

                    if (entry.IsDirectory)
                    {
                        if (!entry.Name.StartsWith(".darksync_"))
                            todo.Push(fullPath);
                    }
                    else if (entry.IsRegularFile)
                    {
                        var b = ParseBackup(entry.Name, entry.Length, fullPath, src.Name, true);
                        if (b != null) found.Add(b);
                    }
                }

                progress?.Report($"SFTP scan {src.Name}: {foldersScanned} folders, {entriesScanned} entries, {found.Count} backups");
            }
        }
        finally
        {
            sftp.Disconnect();
            client.Disconnect();
        }

        return (found, errors);
    }

    public static (List<Backup> Backups, List<string> Errors) ScanSource(Source src, string password,
        CancellationToken cancel, IProgress<string>? progress = null)
    {
        if (src.Path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
            return ScanSftp(src, password, cancel, progress);
        return ScanLocal(src.Path, src.Name, cancel, progress);
    }

    public static Dictionary<int, List<Backup>> Grouped(IEnumerable<Backup> items)
    {
        var d = new Dictionary<int, List<Backup>>();
        foreach (var b in items)
        {
            if (!d.TryGetValue(b.VmId, out var list))
            {
                list = new List<Backup>();
                d[b.VmId] = list;
            }
            list.Add(b);
        }
        foreach (var v in d.Values)
            v.Sort((a, b) => b.When.CompareTo(a.When));
        return d;
    }

    public static HealthInfo ComputeHealth(VmPolicy vm, List<Backup> local, List<Backup> external)
    {
        var sortedLocal = local.OrderByDescending(x => x.When).ToList();
        var sortedExternal = external.OrderByDescending(x => x.When).ToList();

        if (sortedLocal.Count == 0 && sortedExternal.Count == 0)
            return new HealthInfo { Status = "CRITICAL", Message = "No source or external backup found", SourceCount = 0, ExternalCount = 0 };
        if (sortedExternal.Count == 0)
            return new HealthInfo { Status = "MISSING", Message = $"0 of {vm.Copies} required copies", SourceCount = sortedLocal.Count, ExternalCount = 0 };
        if (sortedExternal.Count < vm.Copies)
            return new HealthInfo { Status = "UNDER-RETAINED", Message = $"{sortedExternal.Count} of {vm.Copies} required copies", SourceCount = sortedLocal.Count, ExternalCount = sortedExternal.Count };
        if (DateTime.Now - sortedExternal[0].When > TimeSpan.FromDays(vm.MaxAge))
            return new HealthInfo { Status = "STALE", Message = $"Newest copy is {(DateTime.Now - sortedExternal[0].When).Days} days old", SourceCount = sortedLocal.Count, ExternalCount = sortedExternal.Count, Newest = sortedExternal[0].When };
        return new HealthInfo { Status = "HEALTHY", Message = $"{sortedExternal.Count} external copies", SourceCount = sortedLocal.Count, ExternalCount = sortedExternal.Count, Newest = sortedExternal[0].When };
    }

    public static string SafeVmFolder(VmPolicy vm)
    {
        var note = (vm.Name ?? "").Trim();
        note = Regex.Replace(note, @"[<>:""/\\|?*\x00-\x1f]", "-");
        note = Regex.Replace(note, @"\s+", "-");
        note = Regex.Replace(note, @"-+", "-").Trim(' ', '.', '-');
        note = note.Length > 80 ? note[..80] : note;
        note = note.TrimEnd(' ', '.', '-');
        return string.IsNullOrEmpty(note) ? $"vm-{vm.VmId}" : $"vm-{vm.VmId}-{note}";
    }

    [GeneratedRegex(@"^vzdump-(?<kind>qemu|lxc|openvz)-(?<vmid>\d+)-(?<date>\d{4}_\d{2}_\d{2})-(?<time>\d{2}_\d{2}_\d{2})(?<ext>\..+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BackupRegexGenerated();
}
