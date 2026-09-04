using System.IO;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using DarkSync.Models;
using static DarkSync.Services.ScanService;
using static DarkSync.Services.SftpService;

namespace DarkSync.Services;

public static class SyncService
{
    private const long MinReserveBytes = 256L * 1024 * 1024;

    public static List<(string Action, int VmId, string Path)> MakePlan(Config config, List<Backup> source, List<Backup> external)
    {
        var sm = Grouped(source);
        var em = Grouped(external);
        var ops = new List<(string Action, int VmId, string Path)>();

        foreach (var vm in config.Vms)
        {
            if (!vm.Enabled) continue;
            sm.TryGetValue(vm.VmId, out var local);
            em.TryGetValue(vm.VmId, out var ext);
            local ??= new List<Backup>();
            ext ??= new List<Backup>();

            var existing = new HashSet<(string, long)>(ext.Select(x => (Path.GetFileName(x.Path), x.Size)));
            var candidates = local.Where(x => !existing.Contains((Path.GetFileName(x.Path), x.Size))).ToList();
            var need = Math.Max(0, vm.Copies - ext.Count);

            if (need == 0 && candidates.Count > 0 && (ext.Count == 0 || candidates[0].When > ext[0].When))
                need = 1;

            var additions = candidates.Take(need).ToList();
            ops.AddRange(additions.Select(b => ("copy", vm.VmId, b.Path)));

            var projected = ext.Concat(additions).OrderByDescending(x => x.When).ToList();
            if (config.Retention != "Keep all")
            {
                var destPath = NormalizePath(config.Destination);
                foreach (var b in projected.Skip(vm.Copies))
                {
                    if (!b.Remote && Path.GetDirectoryName(b.Path)?.StartsWith(destPath, StringComparison.OrdinalIgnoreCase) == true)
                        ops.Add(("remove", vm.VmId, b.Path));
                }
            }
        }

        return ops;
    }

    public static async Task<SyncResult> ExecuteAsync(
        Config config, string password, bool dry,
        CancellationToken cancel,
        IProgress<(int Current, int Total, string Message)>? progress = null)
    {
        var sourceDefs = config.Sources.ToDictionary(s => s.Name);
        var allSourceBackups = new List<Backup>();
        var warnings = new List<string>();

        progress?.Report((0, 0, "Scanning sources..."));

        foreach (var src in config.Sources.Where(s => s.Enabled))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var (items, errs) = ScanSource(src, password, cancel, new Progress<string>(m => progress?.Report((0, 0, m))));
                allSourceBackups.AddRange(items);
                warnings.AddRange(errs);
                progress?.Report((0, 0, $"Source {src.Name}: {items.Count} backups found"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                warnings.Add($"{src.Name}: {ex.Message}");
            }
        }

        progress?.Report((0, 0, "Scanning destination..."));
        var (externalBackups, destErrs) = ScanLocal(config.Destination, "External", cancel);
        warnings.AddRange(destErrs);

        progress?.Report((0, 0, "Planning sync..."));
        var ops = MakePlan(config, allSourceBackups, externalBackups);
        var results = new List<(string, int, string)>();

        var destBackupsForRemoval = ops.Any(a => a.Action == "remove")
            ? ScanLocal(config.Destination, "External", cancel).Backups
            : new List<Backup>();

        for (int i = 0; i < ops.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (action, vmid, opPath) = ops[i];

            if (action == "copy")
            {
                var vm = config.Vms.First(v => v.VmId == vmid);
                var backup = allSourceBackups.First(b => b.Path == opPath);
                var targetDir = Path.Combine(config.Destination, SafeVmFolder(vm));
                var target = Path.Combine(targetDir, Path.GetFileName(backup.Path));

                if (dry)
                {
                    results.Add(("Would copy", vmid, target));
                }
                else
                {
                    var root = Path.GetPathRoot(config.Destination);
                    if (!string.IsNullOrEmpty(root))
                    {
                        var di = new DriveInfo(root);
                        if (di.AvailableFreeSpace < Math.Max(config.MinFreeGb * 1024L * 1024 * 1024, backup.Size + MinReserveBytes))
                            throw new InvalidOperationException($"Insufficient free space for VM {vmid}");
                    }

                    Directory.CreateDirectory(targetDir);
                    var tmp = target + $".darksync_tmp_{Guid.NewGuid():N}";

                    Stream? inp = null;
                    SshClient? client = null;
                    SftpClient? sftp = null;

                    try
                    {
                        var transport = backup.Remote ? "SFTP" : "local/SMB";
                        progress?.Report((i, ops.Count, $"[{i + 1}/{ops.Count}] Copying VM {vmid} via {transport}: {Path.GetFileName(backup.Path)} ({HumanBytes(backup.Size)})"));

                        if (backup.Remote && sourceDefs.TryGetValue(backup.Source, out var srcDef))
                        {
                            var (host, port, user, _) = ParseUri(srcDef.Path);
                            client = CreateClient(host, port, user, password, srcDef.KeyFile);
                            client.Connect();
                            sftp = CreateSftpClient(host, port, user, password, srcDef.KeyFile);
                            sftp.Connect();
                            inp = sftp.Open(backup.Path, FileMode.Open, FileAccess.Read);
                        }
                        else
                        {
                            inp = new FileStream(backup.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                        }

                        await CopyStreamAsync(inp, tmp, cancel, backup.Size,
                            new Progress<(long Done, long Total, double Rate, double? Eta)>(p =>
                            {
                                var pct = p.Total > 0 ? (double)p.Done / p.Total * 100 : 0;
                                var eta = p.Eta.HasValue ? $" | ETA {p.Eta.Value / 60:m\\m\\ s\\s}" : "";
                                progress?.Report((i, ops.Count, $"[{i + 1}/{ops.Count}] Copying VM {vmid}: {HumanBytes(p.Done)} / {HumanBytes(p.Total)} ({pct:F1}%) | {HumanBytes((long)p.Rate)}/s{eta}"));
                            }));

                        var tmpInfo = new FileInfo(tmp);
                        if (tmpInfo.Length != backup.Size)
                            throw new IOException($"Size verification failed for VM {vmid}");

                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                File.Move(tmp, target, overwrite: true);
                                break;
                            }
                            catch (IOException) when (attempt < 2)
                            {
                                await Task.Delay(1000, cancel);
                            }
                        }

                        results.Add(("Copied", vmid, target));
                    }
                    finally
                    {
                        try { File.Delete(tmp); } catch { }
                        inp?.Dispose();
                        sftp?.Dispose();
                        client?.Dispose();
                    }
                }
            }
            else
            {
                var current = destBackupsForRemoval.Where(b => b.VmId == vmid).ToList();
                var vmPolicy = config.Vms.First(v => v.VmId == vmid);
                if (current.Count <= vmPolicy.Copies) continue;

                if (dry)
                {
                    var label = config.Retention == "Delete permanently" ? "Would delete" : "Would recycle";
                    results.Add((label, vmid, opPath));
                }
                else if (config.Retention == "Move to recycle folder")
                {
                    try
                    {
                        var recycleDir = Path.Combine(config.Destination, ".darksync_recycle",
                            DateTime.Now.ToString("yyyyMMdd_HHmmss"), SafeVmFolder(vmPolicy));
                        Directory.CreateDirectory(recycleDir);
                        File.Move(opPath, Path.Combine(recycleDir, Path.GetFileName(opPath)));
                        results.Add(("Recycled", vmid, opPath));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Recycle failed VM {vmid}: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            try { File.Delete(opPath); break; }
                            catch (IOException) when (attempt < 2) { await Task.Delay(1000, cancel); }
                        }
                        results.Add(("Deleted", vmid, opPath));
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Delete failed VM {vmid}: {ex.Message}");
                    }
                }
            }

            progress?.Report((i + 1, ops.Count, $"[{i + 1}/{ops.Count}] Completed {action} VM {vmid}"));
        }

        var finalExternal = dry ? externalBackups : ScanLocal(config.Destination, "External", cancel).Backups;
        var sm = Grouped(allSourceBackups);
        var fm = Grouped(finalExternal);
        var health = new Dictionary<int, HealthInfo>();
        foreach (var vm in config.Vms.Where(v => v.Enabled))
        {
            health[vm.VmId] = ComputeHealth(vm, sm.GetValueOrDefault(vm.VmId) ?? new List<Backup>(), fm.GetValueOrDefault(vm.VmId) ?? new List<Backup>());
        }

        return new SyncResult
        {
            Dry = dry,
            Results = results,
            Health = health,
            SourceCount = allSourceBackups.Count,
            ExternalCount = finalExternal.Count,
            Warnings = warnings
        };
    }

    public static async Task CopyStreamAsync(Stream input, string tmpPath, CancellationToken cancel,
        long totalSize, IProgress<(long Done, long Total, double Rate, double? Eta)>? progress = null)
    {
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        var started = DateTime.UtcNow;
        DateTime lastUpdate = DateTime.MinValue;

        await using var output = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);

        progress?.Report((0, totalSize, 0, null));

        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancel);
            if (read == 0) break;

            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            copied += read;

            var now = DateTime.UtcNow;
            if (now - lastUpdate >= TimeSpan.FromMilliseconds(500) || (totalSize > 0 && copied >= totalSize))
            {
                var elapsed = (now - started).TotalSeconds;
                if (elapsed < 0.001) elapsed = 0.001;
                var rate = copied / elapsed;
                var remaining = totalSize > 0 ? Math.Max(0, totalSize - copied) : 0;
                var eta = rate > 0 && totalSize > 0 ? remaining / rate : (double?)null;
                progress?.Report((copied, totalSize, rate, eta));
                lastUpdate = now;
            }
        }

        await output.FlushAsync(cancel);
    }

    public static string HumanBytes(double value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        foreach (var unit in units)
        {
            if (value < 1024 || unit == "TB")
                return $"{value:F1} {unit}";
            value /= 1024;
        }
        return $"{value:F1} PB";
    }

    public static string ValidateArchive(Config config, bool requireWrite = false)
    {
        var raw = NormalizePath(config.Destination);
        if (string.IsNullOrEmpty(raw))
            throw new DestinationUnavailableException("No external archive destination is configured.");

        var dir = new DirectoryInfo(raw);
        if (!dir.Exists)
            throw new DestinationUnavailableException($"External archive is not reachable: {dir}");

        var markerFile = Path.Combine(dir.FullName, ".darksync_archive_id");
        if (!File.Exists(markerFile))
            throw new DestinationUnavailableException($"Archive marker is unavailable: {markerFile}");

        var markerJson = File.ReadAllText(markerFile);
        using var doc = JsonDocument.Parse(markerJson);
        var actualId = doc.RootElement.TryGetProperty("archive_id", out var aid) ? aid.GetString() ?? "" : "";

        if (string.IsNullOrEmpty(config.ArchiveId) || actualId != config.ArchiveId)
            throw new DestinationUnavailableException($"Wrong external archive. Expected '{config.ArchiveId}', found '{actualId}'.");

        dir.EnumerateFiles().FirstOrDefault();

        if (requireWrite)
        {
            var probe = Path.Combine(dir.FullName, $".darksync_probe_{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(probe, Encoding.UTF8.GetBytes("DarkSync destination test"));
            }
            finally
            {
                try { File.Delete(probe); } catch { }
            }
        }

        return dir.FullName;
    }
}

public class DestinationUnavailableException(string message) : Exception(message);
