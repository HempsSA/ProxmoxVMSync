using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DarkSync.Models;
using DarkSync.Services;

namespace DarkSync.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public const string AppName = "DarkSync Proxmox Archive";
    public const string Version = "2.0.0";

    private Config _config = new();
    private CancellationTokenSource? _syncCts;
    private CancellationTokenSource? _sftpTestCts;
    private DispatcherTimer? _schedulerTimer;
    private bool _scheduleRunning;

    // ── Sources ──────────────────────────────────────────────────
    [ObservableProperty] private string _sourcesText = "";
    [ObservableProperty] private string _sftpPassword = "";
    [ObservableProperty] private bool _rememberSftp;
    [ObservableProperty] private string _statusText = "Ready";

    // ── Destination ──────────────────────────────────────────────
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private string _archiveId = "";

    // ── Retention ────────────────────────────────────────────────
    [ObservableProperty] private string _retention = "Keep all";
    public string[] RetentionOptions { get; } = ["Keep all", "Move to recycle folder", "Delete permanently"];

    [ObservableProperty] private int _minFreeGb = 5;

    // ── VM Table ─────────────────────────────────────────────────
    public ObservableCollection<VmRow> VmRows { get; } = [];

    // ── Scheduler ────────────────────────────────────────────────
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private string _scheduleTime = "02:00";
    [ObservableProperty] private bool _scheduleDryRun;
    [ObservableProperty] private string _scheduleStatusText = "Scheduler not configured";
    public string[] ScheduleModes { get; } = ["Sync / Copy", "Dry Run"];

    // ── Notifications ────────────────────────────────────────────
    [ObservableProperty] private bool _ntfyEnabled;
    [ObservableProperty] private string _ntfyServer = "https://ntfy.sh";
    [ObservableProperty] private string _ntfyTopic = "";
    [ObservableProperty] private string _ntfyToken = "";
    [ObservableProperty] private string _ntfyPriority = "high";
    public string[] NtfyPriorities { get; } = ["min", "low", "default", "high", "urgent"];
    [ObservableProperty] private bool _ntfyOnSuccess = true;
    [ObservableProperty] private bool _ntfyOnFailure = true;

    // ── History ──────────────────────────────────────────────────
    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];
    [ObservableProperty] private string _historySummary = "No history loaded";
    public ObservableCollection<VmSnapshotRow> HistoryVmRows { get; } = [];

    private List<HistoryRow> _allHistoryRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistorySummary))]
    private string _historyFilter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeToggleLabel))]
    private bool _isDarkTheme = ThemeService.Current == ThemeService.Dark;

    public string ThemeToggleLabel => IsDarkTheme ? "☀ Light" : "🌙 Dark";

    // ── Is Running ───────────────────────────────────────────────
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isSftpTestRunning;

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        var theme = IsDarkTheme ? ThemeService.Dark : ThemeService.Light;
        ThemeService.Apply(System.Windows.Application.Current, theme);
        ThemeService.Save(theme);
    }

    public MainViewModel()
    {
        HistoryService.Initialize();
        _config = ConfigService.Load();
        LoadFromConfig();
        LoadSavedPassword();
        StartScheduler();
        RefreshHistory();
        _ = CheckScheduleAsync();
    }

    private void LoadFromConfig()
    {
        SourcesText = string.Join("\n", _config.Sources.Select(s =>
            s.Name + "|" + s.Path + (string.IsNullOrEmpty(s.KeyFile) ? "" : "|" + s.KeyFile)));
        Destination = _config.Destination;
        ArchiveId = _config.ArchiveId;
        Retention = _config.Retention;
        MinFreeGb = _config.MinFreeGb;
        NtfyEnabled = _config.NtfyEnabled;
        NtfyServer = _config.NtfyServer;
        NtfyTopic = _config.NtfyTopic;
        NtfyToken = _config.NtfyToken;
        NtfyPriority = _config.NtfyPriority;
        NtfyOnSuccess = _config.NtfyOnSuccess;
        NtfyOnFailure = _config.NtfyOnFailure;
        ScheduleEnabled = _config.ScheduleEnabled;
        ScheduleTime = _config.ScheduleTime;
        ScheduleDryRun = _config.ScheduleDryRun;

        VmRows.Clear();
        foreach (var vm in _config.Vms)
            VmRows.Add(new VmRow(vm));
    }

    private Config CollectConfig()
    {
        var sources = new List<Source>();
        foreach (var line in SourcesText.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('|', 3);
            var name = parts.Length > 1 ? parts[0].Trim() : $"PVE{sources.Count + 1}";
            var path = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
            var keyFile = parts.Length > 2 ? parts[2].Trim() : "";
            sources.Add(new Source { Name = name, Path = path, Enabled = true, KeyFile = keyFile });
        }

        var vms = VmRows.Select(r => new VmPolicy
        {
            VmId = r.VmId,
            Name = r.Name,
            Enabled = r.Enabled,
            Importance = r.Importance,
            Copies = r.Copies,
            MaxAge = r.MaxAge
        }).ToList();

        return new Config
        {
            Sources = sources,
            Destination = Destination.Trim(),
            ArchiveId = ArchiveId.Trim(),
            Vms = vms,
            MinFreeGb = MinFreeGb,
            Retention = Retention,
            RecycleDays = _config.RecycleDays,
            NtfyEnabled = NtfyEnabled,
            NtfyServer = NtfyServer,
            NtfyTopic = NtfyTopic,
            NtfyToken = NtfyToken,
            NtfyPriority = NtfyPriority,
            NtfyOnSuccess = NtfyOnSuccess,
            NtfyOnFailure = NtfyOnFailure,
            ScheduleEnabled = ScheduleEnabled,
            ScheduleTime = ScheduleTime,
            ScheduleDryRun = ScheduleDryRun,
            ScheduleLastRunDate = _config.ScheduleLastRunDate
        };
    }

    // ── Commands ─────────────────────────────────────────────────

    [RelayCommand]
    private void Save()
    {
        try
        {
            _config = CollectConfig();
            ConfigService.Save(_config);
            SavePassword();
            StatusText = "Configuration saved";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ExportSettings()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export DarkSync Settings",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"darksync_settings_{DateTime.Now:yyyyMMdd}.json"
            };
            if (dlg.ShowDialog() != true) return;

            _config = CollectConfig();
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            StatusText = $"Settings exported to {Path.GetFileName(dlg.FileName)}";
            MessageBox.Show($"Settings exported successfully.\n\n{dlg.FileName}", "Export complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ImportSettings()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import DarkSync Settings",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            var json = File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<Config>(json);
            if (imported == null)
            {
                MessageBox.Show("The file does not contain valid DarkSync settings.", "Import failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show(
                $"Import settings from {Path.GetFileName(dlg.FileName)}?\n\nThis will replace all current settings.",
                "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _config = imported;
            LoadFromConfig();
            SavePassword();
            StatusText = $"Settings imported from {Path.GetFileName(dlg.FileName)}";
            MessageBox.Show("Settings imported successfully.", "Import complete",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "External archive",
            InitialDirectory = Destination
        };
        if (dlg.ShowDialog() == true)
            Destination = dlg.FolderName;
    }

    [RelayCommand]
    private void InitializeArchive()
    {
        try
        {
            var dir = ScanService.NormalizePath(Destination);
            Directory.CreateDirectory(dir);
            var aid = string.IsNullOrWhiteSpace(ArchiveId)
                ? $"PROXMOX-{Guid.NewGuid().ToString("N")[..8].ToUpper()}"
                : ArchiveId;
            var markerPath = Path.Combine(dir, ".darksync_archive_id");
            var marker = new { archive_id = aid, created = DateTime.Now.ToString("o") };
            File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
            ArchiveId = aid;
            Save();
            MessageBox.Show($"Archive initialized.\nArchive ID: {aid}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Initialization failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void AddVms()
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            "Comma or space separated VM IDs:", "Add VM IDs", "");
        if (string.IsNullOrWhiteSpace(input)) return;

        var existing = new HashSet<int>(VmRows.Select(r => r.VmId));
        var ids = System.Text.RegularExpressions.Regex.Matches(input, @"\d+")
            .Select(m => int.Parse(m.Value))
            .Where(id => !existing.Contains(id))
            .OrderBy(id => id);

        foreach (var id in ids)
            VmRows.Add(new VmRow(new VmPolicy { VmId = id }));
    }

    [RelayCommand]
    private void RemoveSelectedVms(System.Collections.IList selected)
    {
        if (selected == null) return;
        var toRemove = selected.Cast<object>().OfType<VmRow>().ToList();
        foreach (var row in toRemove)
            VmRows.Remove(row);
    }

    [RelayCommand]
    private async Task TestSftpAsync()
    {
        if (IsRunning || IsSftpTestRunning) return;
        Save();

        var sftpSources = _config.Sources
            .Where(s => s.Enabled && s.Path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sftpSources.Count == 0)
        {
            MessageBox.Show("No enabled SFTP sources found.", "Test SFTP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsSftpTestRunning = true;
        _sftpTestCts = new CancellationTokenSource();
        StatusText = "Testing SFTP connections...";

        try
        {
            var results = new List<(string Name, string Path, bool Ok, string Message)>();
            foreach (var src in sftpSources)
            {
                if (_sftpTestCts.Token.IsCancellationRequested) break;
                try
                {
                    var (host, port, user, root) = SftpService.ParseUri(src.Path);
                    using var client = SftpService.CreateClient(host, port, user, SftpPassword, src.KeyFile);
                    client.Connect();
                    using var sftp = SftpService.CreateSftpClient(host, port, user, SftpPassword, src.KeyFile);
                    sftp.Connect();
                    var entries = sftp.ListDirectory(root).ToList();
                    var files = entries.Count(e => e.IsRegularFile);
                    var folders = entries.Count(e => e.IsDirectory && e.Name != "." && e.Name != "..");
                    var zst = entries.Count(e => e.IsRegularFile && e.Name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase));
                    results.Add((src.Name, src.Path, true, $"Connected; {files} files, {folders} folders, {zst} .zst files"));
                    sftp.Disconnect();
                    client.Disconnect();
                }
                catch (Exception ex)
                {
                    results.Add((src.Name, src.Path, false, ex.Message));
                }
            }

            var passed = results.Count(r => r.Ok);
            var failed = results.Count - passed;
            StatusText = $"SFTP test complete: {passed} passed, {failed} failed";
            var msg = string.Join("\n\n", results.Select(r => $"{(r.Ok ? "PASS" : "FAIL")} - {r.Name}\n{r.Path}\n{r.Message}"));
            MessageBox.Show(msg, failed == 0 ? "SFTP test passed" : "SFTP test results",
                MessageBoxButton.OK, failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            HistoryService.Add("SFTP Test", failed == 0 ? "Success" : "Completed with warnings",
                string.Join("; ", results.Select(r => $"{r.Name}: {r.Message}")));
            RefreshHistory();
        }
        catch (Exception ex)
        {
            StatusText = "SFTP test failed";
            HistoryService.Add("SFTP Test", "Failed", ex.Message);
            RefreshHistory();
            MessageBox.Show(ex.Message, "SFTP test failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSftpTestRunning = false;
            _sftpTestCts?.Dispose();
            _sftpTestCts = null;
        }
    }

    [RelayCommand]
    private async Task RunSyncAsync(bool dry = false)
    {
        if (IsRunning) return;

        try
        {
            _config = CollectConfig();
            ConfigService.Save(_config);
            SyncService.ValidateArchive(_config, requireWrite: !dry);
        }
        catch (DestinationUnavailableException ex)
        {
            StatusText = "Sync aborted: " + ex.Message;
            HistoryService.Add(dry ? "Dry Run" : "Sync / Copy", "Aborted - destination unavailable", ex.Message);
            RefreshHistory();
            try { await NtfyService.SendAsync(_config, "DarkSync Sync Aborted", ex.Message, false); } catch { }
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Invalid configuration", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!dry && MessageBox.Show($"Copy required backups now?\n\nOld-copy handling: {Retention}",
            "Run sync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsRunning = true;
        _syncCts = new CancellationTokenSource();
        StatusText = "Scanning and planning...";

        try
        {
            var result = await System.Threading.Tasks.Task.Run(() =>
                SyncService.ExecuteAsync(_config, SftpPassword, dry, _syncCts.Token,
                    new Progress<(int, int, string)>(p =>
                    {
                        Application.Current.Dispatcher.Invoke(() => StatusText = p.Item3);
                    })));

            // Update VM table health
            foreach (var (vmid, h) in result.Health)
            {
                var row = VmRows.FirstOrDefault(r => r.VmId == vmid);
                if (row != null)
                {
                    row.SourceCount = h.SourceCount;
                    row.ExternalCount = h.ExternalCount;
                    row.NewestExternal = h.Newest?.ToString("yyyy-MM-dd HH:mm") ?? "None";
                    row.HealthStatus = h.Status;
                    row.HealthDetails = h.Message;
                }
            }

            StatusText = $"{(dry ? "Dry run" : "Sync")} complete: {result.Results.Count} operation(s); warnings: {result.Warnings.Count}";
            var vmSnapshot = JsonSerializer.Serialize(result.Health.ToDictionary(
                h => h.Key.ToString(),
                h => new
                {
                    vmid = h.Key,
                    name = _config.Vms.FirstOrDefault(v => v.VmId == h.Key)?.Name ?? "",
                    importance = _config.Vms.FirstOrDefault(v => v.VmId == h.Key)?.Importance ?? 1,
                    required_copies = _config.Vms.FirstOrDefault(v => v.VmId == h.Key)?.Copies ?? 1,
                    source_copies = h.Value.SourceCount,
                    external_copies = h.Value.ExternalCount,
                    newest_external = h.Value.Newest?.ToString("o") ?? "",
                    health = h.Value.Status,
                    details = h.Value.Message
                }));

            HistoryService.Add(
                dry ? "Dry Run" : "Sync / Copy",
                dry ? "Dry run completed" : "Success",
                StatusText,
                result.SourceCount, result.ExternalCount, result.Results.Count, result.Warnings.Count,
                vmSnapshot);

            RefreshHistory();

            if (!dry)
            {
                try
                {
                    await NtfyService.SendAsync(_config, "DarkSync Sync Complete",
                        $"{StatusText}\nSource backups: {result.SourceCount}\nExternal backups: {result.ExternalCount}\nWarnings: {result.Warnings.Count}", true);
                }
                catch { }
            }

            if (result.Results.Count > 0)
            {
                var lines = result.Results.Take(30).Select(r => $"{r.Item1} VM {r.Item2}: {r.Item3}");
                MessageBox.Show(StatusText + "\n\n" + string.Join("\n", lines), "Results", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = "Failed; see error log";
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "darksync_proxmox_error.log"), ex.ToString()); } catch { }
            HistoryService.Add("Operation", "Failed", ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message);
            RefreshHistory();
            try { await NtfyService.SendAsync(_config, "DarkSync Backup Failed", ex.Message.Length > 3000 ? ex.Message[..3000] : ex.Message, false); } catch { }
            MessageBox.Show(ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message, "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRunning = false;
            _syncCts?.Dispose();
            _syncCts = null;
        }
    }

    [RelayCommand]
    private Task DryRunAsync() => RunSyncAsync(true);

    [RelayCommand]
    private Task SyncAsync() => RunSyncAsync(false);

    [RelayCommand]
    private void Cancel()
    {
        _syncCts?.Cancel();
        _sftpTestCts?.Cancel();
    }

    [RelayCommand]
    private async Task TestNtfyAsync()
    {
        try
        {
            var cfg = CollectConfig();
            cfg.NtfyEnabled = true;
            await NtfyService.SendAsync(cfg, "DarkSync Test",
                $"DarkSync Proxmox Archive ntfy test succeeded.\nComputer: {Environment.MachineName}\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", true);
            MessageBox.Show("Test notification sent successfully.", "ntfy test", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ntfy test failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── History ──────────────────────────────────────────────────

    [RelayCommand]
    private void RefreshHistory()
    {
        var entries = HistoryService.LoadAll();
        _allHistoryRows.Clear();
        foreach (var e in entries)
            _allHistoryRows.Add(new HistoryRow(e));

        ApplyHistoryFilter();
    }

    partial void OnHistoryFilterChanged(string value)
        => ApplyHistoryFilter();

    private void ApplyHistoryFilter()
    {
        HistoryRows.Clear();
        var all = _allHistoryRows;
        var filter = HistoryFilter?.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            foreach (var r in all)
                HistoryRows.Add(r);
        }
        else
        {
            foreach (var r in all)
            {
                if (r.Operation?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
                    r.Result?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
                    r.Timestamp?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
                    r.Details?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)
                    HistoryRows.Add(r);
            }
        }

        HistorySummary = $"{HistoryRows.Count} of {all.Count} record(s); newest first";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (MessageBox.Show("Delete all saved run-history records?", "Clear history",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            HistoryService.Clear();
            RefreshHistory();
            HistoryVmRows.Clear();
        }
    }

    public void ShowHistoryVms(HistoryRow? selected)
    {
        HistoryVmRows.Clear();
        if (selected == null) return;

        try
        {
            var vms = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(selected.VmSnapshotJson);
            if (vms == null) return;
            foreach (var vm in vms)
            {
                HistoryVmRows.Add(new VmSnapshotRow
                {
                    VmId = vm.TryGetValue("vmid", out var vid) ? vid.GetInt32() : 0,
                    Name = vm.TryGetValue("name", out var vn) ? vn.GetString() ?? "" : "",
                    Importance = vm.TryGetValue("importance", out var vi) ? vi.GetInt32() : 1,
                    RequiredCopies = vm.TryGetValue("required_copies", out var vc) ? vc.GetInt32() : 1,
                    SourceCopies = vm.TryGetValue("source_copies", out var vs) ? vs.GetInt32() : 0,
                    ExternalCopies = vm.TryGetValue("external_copies", out var ve) ? ve.GetInt32() : 0,
                    NewestExternal = vm.TryGetValue("newest_external", out var vn2)
                        ? (vn2.GetString()?.Replace('T', ' ') ?? "") : "",
                    Health = vm.TryGetValue("health", out var vh) ? vh.GetString() ?? "UNKNOWN" : "UNKNOWN",
                    Details = vm.TryGetValue("details", out var vd) ? vd.GetString() ?? "" : ""
                });
            }
        }
        catch { }
    }

    // ── Scheduler ────────────────────────────────────────────────

    private void StartScheduler()
    {
        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _schedulerTimer.Tick += async (_, _) => await CheckScheduleAsync();
        _schedulerTimer.Start();
    }

    private async Task CheckScheduleAsync()
    {
        if (_scheduleRunning || !ScheduleEnabled) return;

        if (!TimeOnly.TryParse(ScheduleTime, out var scheduled)) return;

        var now = DateTime.Now;
        var today = now.Date.ToString("yyyy-MM-dd");
        var nowTime = new TimeOnly(now.Hour, now.Minute);
        var due = nowTime >= scheduled;

        if (_config.ScheduleLastRunDate == today) return;
        if (!due) return;

        _scheduleRunning = true;
        try
        {
            StatusText = $"Starting scheduled {(ScheduleDryRun ? "Dry Run" : "Sync")}...";
            var wasRunning = IsRunning;
            await RunSyncAsync(ScheduleDryRun);

            // Only mark as run if the operation actually executed
            // (not if the user declined the confirmation dialog)
            if (!wasRunning && !IsRunning)
            {
                _config.ScheduleLastRunDate = today;
                ConfigService.Save(_config);
            }
            UpdateScheduleStatus();
        }
        catch (Exception ex)
        {
            StatusText = $"Scheduled run failed: {ex.Message}";
        }
        finally
        {
            _scheduleRunning = false;
        }
    }

    partial void OnScheduleEnabledChanged(bool value) => UpdateScheduleStatus();
    partial void OnScheduleTimeChanged(string value) => UpdateScheduleStatus();
    partial void OnScheduleDryRunChanged(bool value) => UpdateScheduleStatus();

    private void UpdateScheduleStatus()
    {
        if (!ScheduleEnabled)
        {
            ScheduleStatusText = "Disabled";
            return;
        }
        var mode = ScheduleDryRun ? "Dry Run" : "Sync / Copy";
        var last = string.IsNullOrEmpty(_config.ScheduleLastRunDate) ? "never" : _config.ScheduleLastRunDate;
        ScheduleStatusText = $"Enabled: {mode} daily at {ScheduleTime}. Last run: {last}.";
    }

    // ── Credentials ──────────────────────────────────────────────

    private void LoadSavedPassword()
    {
        try
        {
            var saved = CredentialService.LoadPassword();
            if (saved != null)
            {
                SftpPassword = saved;
                RememberSftp = true;
                StatusText = "Saved SFTP password loaded from credential store";
            }
        }
        catch { }
    }

    private void SavePassword()
    {
        if (RememberSftp && !string.IsNullOrEmpty(SftpPassword))
            CredentialService.SavePassword(SftpPassword);
        else
            CredentialService.DeletePassword();
    }

    [RelayCommand]
    private void ForgetPassword()
    {
        CredentialService.DeletePassword();
        SftpPassword = "";
        RememberSftp = false;
        StatusText = "Saved SFTP password removed";
    }

    [RelayCommand]
    private async Task TriggerScheduledNowAsync()
    {
        await RunSyncAsync(ScheduleDryRun);
    }

    [RelayCommand]
    private async Task UpdateAppAsync()
    {
        if (IsRunning) return;

        StatusText = "Checking for updates...";
        var (hasUpdate, sha, msg) = await UpdateService.CheckForUpdateAsync();

        if (!hasUpdate)
        {
            StatusText = "App is up to date.";
            MessageBox.Show("You are running the latest version.", "No updates",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Update available!\n\nLatest: {sha} - {msg.Trim()[..Math.Min(100, msg.Trim().Length)]}\n\nDownload and install update?\nThe app will restart automatically.",
            "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (result != MessageBoxResult.Yes) return;

        IsRunning = true;
        try
        {
            var status = await UpdateService.DownloadAndBuildAsync(
                new Progress<string>(m => StatusText = m));

            if (status == "OK")
            {
                StatusText = "Restarting with update...";
                UpdateService.LaunchUpdaterAndExit();
            }
            else
            {
                StatusText = "Update failed";
                MessageBox.Show(status, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            StatusText = "Update failed";
            MessageBox.Show(ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsRunning = false;
        }
    }
}

// ── Row Models ──────────────────────────────────────────────────

public partial class VmRow : ObservableObject
{
    public VmRow(VmPolicy vm)
    {
        VmId = vm.VmId;
        _name = vm.Name;
        _enabled = vm.Enabled;
        _importance = vm.Importance;
        _copies = vm.Copies;
        _maxAge = vm.MaxAge;
    }

    public int VmId { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _importance = 1;
    [ObservableProperty] private int _copies = 1;
    [ObservableProperty] private int _maxAge = 7;

    public void CycleImportance()
    {
        Importance = Importance >= 3 ? 1 : Importance + 1;
        Copies = Importance;
    }

    [ObservableProperty] private int _sourceCount;
    [ObservableProperty] private int _externalCount;
    [ObservableProperty] private string _newestExternal = "";
    [ObservableProperty] private string _healthStatus = "NOT SCANNED";
    [ObservableProperty] private string _healthDetails = "";
}

public class HistoryRow
{
    public HistoryRow(HistoryEntry e)
    {
        Timestamp = e.Timestamp.Replace('T', ' ');
        Operation = e.Operation;
        Result = e.Result;
        Actions = e.Actions;
        SourceCount = e.SourceCount;
        ExternalCount = e.ExternalCount;
        Warnings = e.Warnings;
        Details = e.Details;
        VmSnapshotJson = e.VmSnapshotJson;

        // Compute age
        if (DateTime.TryParse(e.Timestamp, out var then))
        {
            var delta = DateTime.Now - then;
            Age = delta.TotalDays >= 1 ? $"{(int)delta.TotalDays}d {delta.Hours}h" : $"{(int)delta.TotalMinutes} min";
        }
    }

    public string Timestamp { get; }
    public string Operation { get; }
    public string Result { get; }
    public string ResultBadge => Result switch
    {
        string r when r.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase) => "Good",
        string r when r.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                      r.Contains("ABORTED", StringComparison.OrdinalIgnoreCase) => "Bad",
        string r when r.Contains("WARNING", StringComparison.OrdinalIgnoreCase) => "Warning",
        _ => "Good"
    };
    public int Actions { get; }
    public int SourceCount { get; }
    public int ExternalCount { get; }
    public int Warnings { get; }
    public string Details { get; }
    public string Age { get; } = "";
    public string VmSnapshotJson { get; }
}

public class VmSnapshotRow
{
    public int VmId { get; set; }
    public string Name { get; set; } = "";
    public int Importance { get; set; }
    public int RequiredCopies { get; set; }
    public int SourceCopies { get; set; }
    public int ExternalCopies { get; set; }
    public string NewestExternal { get; set; } = "";
    public string Health { get; set; } = "UNKNOWN";
    public string Details { get; set; } = "";
}
