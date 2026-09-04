using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    private bool _syncCompletedSuccessfully;

    [ObservableProperty] private string _sourcesText = "";
    [ObservableProperty] private string _sftpPassword = "";
    [ObservableProperty] private bool _rememberSftp;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private string _archiveId = "";
    [ObservableProperty] private string _retention = "Keep all";
    public string[] RetentionOptions { get; } = ["Keep all", "Move to recycle folder", "Delete permanently"];
    [ObservableProperty] private int _minFreeGb = 5;
    public ObservableCollection<VmRow> VmRows { get; } = [];

    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private string _scheduleTime = "02:00";
    [ObservableProperty] private bool _scheduleDryRun;
    [ObservableProperty] private string _scheduleStatusText = "Scheduler not configured";
    public string[] ScheduleModes { get; } = ["Sync / Copy", "Dry Run"];

    [ObservableProperty] private bool _ntfyEnabled;
    [ObservableProperty] private string _ntfyServer = "https://ntfy.sh";
    [ObservableProperty] private string _ntfyTopic = "";
    [ObservableProperty] private string _ntfyToken = "";
    [ObservableProperty] private string _ntfyPriority = "high";
    public string[] NtfyPriorities { get; } = ["min", "low", "default", "high", "urgent"];
    [ObservableProperty] private bool _ntfyOnSuccess = true;
    [ObservableProperty] private bool _ntfyOnFailure = true;

    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];
    [ObservableProperty] private string _historySummary = "No history loaded";
    public ObservableCollection<VmSnapshotRow> HistoryVmRows { get; } = [];
    private List<HistoryRow> _allHistoryRows = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HistorySummary))]
    private string _historyFilter = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ThemeToggleLabel))]
    private bool _isDarkTheme = ThemeService.Current == ThemeService.Dark;
    public string ThemeToggleLabel => IsDarkTheme ? "☀ Light" : "🌙 Dark";

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isSftpTestRunning;

    [RelayCommand] private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        ThemeService.Apply(Application.Current, IsDarkTheme ? ThemeService.Dark : ThemeService.Light);
        ThemeService.Save(IsDarkTheme ? ThemeService.Dark : ThemeService.Light);
    }

    public MainViewModel()
    {
        HistoryService.Initialize();
        _config = ConfigService.Load();
        LoadFromConfig();
        LoadSavedPassword();
        StartScheduler();
        RefreshHistory();
        UpdateScheduleStatus();
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
        foreach (var vm in _config.Vms) VmRows.Add(new VmRow(vm));
    }

    private Config CollectConfig()
    {
        var sources = new List<Source>();
        foreach (var line in SourcesText.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('|', 3);
            sources.Add(new Source
            {
                Name = parts.Length > 1 ? parts[0].Trim() : $"PVE{sources.Count + 1}",
                Path = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim(),
                Enabled = true,
                KeyFile = parts.Length > 2 ? parts[2].Trim() : ""
            });
        }
        var vms = VmRows.Select(r => new VmPolicy
        {
            VmId = r.VmId, Name = r.Name, Enabled = r.Enabled,
            Importance = r.Importance, Copies = r.Copies, MaxAge = r.MaxAge
        }).ToList();
        return new Config
        {
            Sources = sources, Destination = Destination.Trim(), ArchiveId = ArchiveId.Trim(),
            Vms = vms, MinFreeGb = MinFreeGb, Retention = Retention, RecycleDays = _config.RecycleDays,
            NtfyEnabled = NtfyEnabled, NtfyServer = NtfyServer, NtfyTopic = NtfyTopic, NtfyToken = NtfyToken,
            NtfyPriority = NtfyPriority, NtfyOnSuccess = NtfyOnSuccess, NtfyOnFailure = NtfyOnFailure,
            ScheduleEnabled = ScheduleEnabled, ScheduleTime = ScheduleTime, ScheduleDryRun = ScheduleDryRun,
            ScheduleLastRunDate = _config.ScheduleLastRunDate
        };
    }

    [RelayCommand] private void Save()
    {
        try { _config = CollectConfig(); ConfigService.Save(_config); SavePassword(); StatusText = "Configuration saved"; UpdateScheduleStatus(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    [RelayCommand] private void ExportSettings()
    {
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export DarkSync Settings", Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", FileName = $"darksync_settings_{DateTime.Now:yyyyMMdd}.json" };
            if (dlg.ShowDialog() != true) return;
            _config = CollectConfig();
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
            StatusText = $"Settings exported to {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    [RelayCommand] private void ImportSettings()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import DarkSync Settings", Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;
            var imported = JsonSerializer.Deserialize<Config>(File.ReadAllText(dlg.FileName));
            if (imported == null) { MessageBox.Show("Invalid settings file.", "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (MessageBox.Show($"Replace all current settings with {Path.GetFileName(dlg.FileName)}?", "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _config = imported; LoadFromConfig(); SavePassword(); StatusText = $"Settings imported from {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    [RelayCommand] private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "External archive", InitialDirectory = Destination };
        if (dlg.ShowDialog() == true) Destination = dlg.FolderName;
    }

    [RelayCommand] private void InitializeArchive()
    {
        try
        {
            var dir = ScanService.NormalizePath(Destination); Directory.CreateDirectory(dir);
            var aid = string.IsNullOrWhiteSpace(ArchiveId) ? $"PROXMOX-{Guid.NewGuid().ToString("N")[..8].ToUpper()}" : ArchiveId;
            File.WriteAllText(Path.Combine(dir, ".darksync_archive_id"), JsonSerializer.Serialize(new { archive_id = aid, created = DateTime.Now.ToString("o") }, new JsonSerializerOptions { WriteIndented = true }));
            ArchiveId = aid; Save();
            MessageBox.Show($"Archive initialized.\nArchive ID: {aid}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Initialization failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    [RelayCommand] private void AddVms()
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox("Comma or space separated VM IDs:", "Add VM IDs", "");
        if (string.IsNullOrWhiteSpace(input)) return;
        var existing = new HashSet<int>(VmRows.Select(r => r.VmId));
        foreach (var id in System.Text.RegularExpressions.Regex.Matches(input, @"\d+").Select(m => int.Parse(m.Value)).Where(id => !existing.Contains(id)).OrderBy(id => id))
            VmRows.Add(new VmRow(new VmPolicy { VmId = id }));
    }

    [RelayCommand] private void RemoveSelectedVms(System.Collections.IList selected)
    {
        if (selected == null) return;
        foreach (var row in selected.Cast<object>().OfType<VmRow>().ToList()) VmRows.Remove(row);
    }

    [RelayCommand] private async Task TestSftpAsync()
    {
        if (IsRunning || IsSftpTestRunning) return; Save();
        var sftpSources = _config.Sources.Where(s => s.Enabled && s.Path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)).ToList();
        if (sftpSources.Count == 0) { MessageBox.Show("No enabled SFTP sources found.", "Test SFTP", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        IsSftpTestRunning = true; _sftpTestCts = new CancellationTokenSource(); StatusText = "Testing SFTP connections...";
        try
        {
            var results = new List<(string Name, string Path, bool Ok, string Message)>();
            foreach (var src in sftpSources)
            {
                if (_sftpTestCts.Token.IsCancellationRequested) break;
                try
                {
                    var (host, port, user, root) = SftpService.ParseUri(src.Path);
                    using var client = SftpService.CreateClient(host, port, user, SftpPassword, src.KeyFile); client.Connect();
                    using var sftp = SftpService.CreateSftpClient(host, port, user, SftpPassword, src.KeyFile); sftp.Connect();
                    var entries = sftp.ListDirectory(root).ToList();
                    results.Add((src.Name, src.Path, true, $"Connected; {entries.Count(e => e.IsRegularFile)} files, {entries.Count(e => e.IsDirectory && e.Name != "." && e.Name != "..")} folders, {entries.Count(e => e.IsRegularFile && e.Name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))} .zst files"));
                    sftp.Disconnect(); client.Disconnect();
                }
                catch (Exception ex) { results.Add((src.Name, src.Path, false, ex.Message)); }
            }
            var passed = results.Count(r => r.Ok); var failed = results.Count - passed;
            StatusText = $"SFTP test complete: {passed} passed, {failed} failed";
            MessageBox.Show(string.Join("\n\n", results.Select(r => $"{(r.Ok ? "PASS" : "FAIL")} - {r.Name}\n{r.Path}\n{r.Message}")), failed == 0 ? "SFTP test passed" : "SFTP test results", MessageBoxButton.OK, failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            HistoryService.Add("SFTP Test", failed == 0 ? "Success" : "Completed with warnings", string.Join("; ", results.Select(r => $"{r.Name}: {r.Message}"))); RefreshHistory();
        }
        catch (Exception ex) { StatusText = "SFTP test failed"; HistoryService.Add("SFTP Test", "Failed", ex.Message); RefreshHistory(); MessageBox.Show(ex.Message, "SFTP test failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsSftpTestRunning = false; _sftpTestCts?.Dispose(); _sftpTestCts = null; }
    }

    private async Task RunSyncAsync(bool dry = false, bool isScheduled = false)
    {
        if (IsRunning) return;
        _syncCompletedSuccessfully = false;
        try { _config = CollectConfig(); ConfigService.Save(_config); SyncService.ValidateArchive(_config, requireWrite: !dry); }
        catch (DestinationUnavailableException ex) { StatusText = "Sync aborted: " + ex.Message; HistoryService.Add(dry ? "Dry Run" : "Sync / Copy", "Aborted", ex.Message); RefreshHistory(); try { await NtfyService.SendAsync(_config, "DarkSync Sync Aborted", ex.Message, false); } catch { } if (isScheduled) LogScheduler($"Aborted: {ex.Message}"); return; }
        catch (Exception ex) { if (!isScheduled) MessageBox.Show(ex.Message, "Invalid configuration", MessageBoxButton.OK, MessageBoxImage.Error); else LogScheduler($"Validation error: {ex.Message}"); return; }
        if (!dry && !isScheduled && MessageBox.Show($"Copy required backups now?\n\nOld-copy handling: {Retention}", "Run sync", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsRunning = true; _syncCts = new CancellationTokenSource(); StatusText = "Scanning and planning..."; if (isScheduled) LogScheduler("Sync started");
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => SyncService.ExecuteAsync(_config, SftpPassword, dry, _syncCts.Token, new Progress<(int, int, string)>(p => Application.Current.Dispatcher.Invoke(() => StatusText = p.Item3))));
            _syncCompletedSuccessfully = true; if (isScheduled) LogScheduler($"Sync OK: {result.Results.Count} ops");
            foreach (var (vmid, h) in result.Health) { var row = VmRows.FirstOrDefault(r => r.VmId == vmid); if (row != null) { row.SourceCount = h.SourceCount; row.ExternalCount = h.ExternalCount; row.NewestExternal = h.Newest?.ToString("yyyy-MM-dd HH:mm") ?? "None"; row.HealthStatus = h.Status; row.HealthDetails = h.Message; } }
            StatusText = $"{(dry ? "Dry run" : "Sync")} complete: {result.Results.Count} operation(s); warnings: {result.Warnings.Count}";
            var vmSnap = JsonSerializer.Serialize(result.Health.ToDictionary(h => h.Key.ToString(), h => new { vmid = h.Key, name = _config.Vms.FirstOrDefault(v => v.VmId == h.Key)?.Name ?? "", importance = _config.Vms.FirstOrDefault(v => v.VmId == h.Key)?.Importance ?? 1, required_copies = _config.Vms.FirstOrDefault(v => v.VmId == h.Key)?.Copies ?? 1, source_copies = h.Value.SourceCount, external_copies = h.Value.ExternalCount, newest_external = h.Value.Newest?.ToString("o") ?? "", health = h.Value.Status, details = h.Value.Message }));
            HistoryService.Add(dry ? "Dry Run" : "Sync / Copy", dry ? "Dry run completed" : "Success", StatusText, result.SourceCount, result.ExternalCount, result.Results.Count, result.Warnings.Count, vmSnap); RefreshHistory();
            if (!dry) { try { await NtfyService.SendAsync(_config, "DarkSync Sync Complete", $"{StatusText}\nSource: {result.SourceCount}\nExternal: {result.ExternalCount}\nWarnings: {result.Warnings.Count}", true); } catch { } }
            if (!isScheduled && result.Results.Count > 0) { var lines = result.Results.Take(30).Select(r => $"{r.Item1} VM {r.Item2}: {r.Item3}"); MessageBox.Show(StatusText + "\n\n" + string.Join("\n", lines), "Results", MessageBoxButton.OK, MessageBoxImage.Information); }
        }
        catch (OperationCanceledException) { StatusText = "Operation cancelled."; if (isScheduled) LogScheduler("Cancelled"); }
        catch (Exception ex) { StatusText = "Failed; see error log"; if (isScheduled) LogScheduler($"Failed: {ex.Message}"); try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "darksync_proxmox_error.log"), ex.ToString()); } catch { } HistoryService.Add("Operation", "Failed", ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message); RefreshHistory(); try { await NtfyService.SendAsync(_config, "DarkSync Backup Failed", ex.Message.Length > 3000 ? ex.Message[..3000] : ex.Message, false); } catch { } if (!isScheduled) MessageBox.Show(ex.Message.Length > 4000 ? ex.Message[..4000] : ex.Message, "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsRunning = false; _syncCts?.Dispose(); _syncCts = null; }
    }

    [RelayCommand] private Task DryRunAsync() => RunSyncAsync(dry: true);
    [RelayCommand] private Task SyncAsync() => RunSyncAsync(dry: false);
    [RelayCommand] private void Cancel() { _syncCts?.Cancel(); _sftpTestCts?.Cancel(); }

    [RelayCommand] private async Task TestNtfyAsync()
    {
        try { var cfg = CollectConfig(); cfg.NtfyEnabled = true; await NtfyService.SendAsync(cfg, "DarkSync Test", $"DarkSync ntfy test.\nMachine: {Environment.MachineName}\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", true); MessageBox.Show("Test notification sent.", "ntfy test", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "ntfy test failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    [RelayCommand] private void RefreshHistory() { var entries = HistoryService.LoadAll(); _allHistoryRows.Clear(); foreach (var e in entries) _allHistoryRows.Add(new HistoryRow(e)); ApplyHistoryFilter(); }
    partial void OnHistoryFilterChanged(string value) => ApplyHistoryFilter();
    private void ApplyHistoryFilter()
    {
        HistoryRows.Clear(); var filter = HistoryFilter?.Trim();
        foreach (var r in string.IsNullOrEmpty(filter) ? _allHistoryRows : _allHistoryRows.Where(r => r.Operation?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true || r.Result?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true || r.Timestamp?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true || r.Details?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)) HistoryRows.Add(r);
        HistorySummary = $"{HistoryRows.Count} of {_allHistoryRows.Count} record(s); newest first";
    }

    [RelayCommand] private void ClearHistory() { if (MessageBox.Show("Delete all history?", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { HistoryService.Clear(); RefreshHistory(); HistoryVmRows.Clear(); } }

    public void ShowHistoryVms(HistoryRow? selected)
    {
        HistoryVmRows.Clear(); if (selected == null) return;
        try { var vms = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(selected.VmSnapshotJson); if (vms == null) return; foreach (var vm in vms) HistoryVmRows.Add(new VmSnapshotRow { VmId = vm.TryGetValue("vmid", out var vid) ? vid.GetInt32() : 0, Name = vm.TryGetValue("name", out var vn) ? vn.GetString() ?? "" : "", Importance = vm.TryGetValue("importance", out var vi) ? vi.GetInt32() : 1, RequiredCopies = vm.TryGetValue("required_copies", out var vc) ? vc.GetInt32() : 1, SourceCopies = vm.TryGetValue("source_copies", out var vs) ? vs.GetInt32() : 0, ExternalCopies = vm.TryGetValue("external_copies", out var ve) ? ve.GetInt32() : 0, NewestExternal = vm.TryGetValue("newest_external", out var vn2) ? (vn2.GetString()?.Replace('T', ' ') ?? "") : "", Health = vm.TryGetValue("health", out var vh) ? vh.GetString() ?? "UNKNOWN" : "UNKNOWN", Details = vm.TryGetValue("details", out var vd) ? vd.GetString() ?? "" : "" }); } catch { }
    }

    // ── Scheduler ────────────────────────────────────────────────

    private void StartScheduler()
    {
        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _schedulerTimer.Tick += async (_, _) => { try { await CheckScheduleAsync(); } catch (Exception ex) { LogScheduler($"Tick exception: {ex.Message}"); } };
        _schedulerTimer.Start();
        LogScheduler($"Timer started. Enabled={ScheduleEnabled}, Time='{ScheduleTime}'");
    }

    private async Task CheckScheduleAsync()
    {
        LogScheduler($"Tick: enabled={ScheduleEnabled}, time='{ScheduleTime}', running={_scheduleRunning}");
        if (_scheduleRunning || !ScheduleEnabled) return;
        if (!TimeOnly.TryParse(ScheduleTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var scheduled)) { LogScheduler($"Bad time: '{ScheduleTime}'"); return; }
        var now = DateTime.Now; var today = now.Date.ToString("yyyy-MM-dd");
        var due = new TimeOnly(now.Hour, now.Minute) >= scheduled;
        _config = CollectConfig();
        LogScheduler($"now={now:HH:mm}, scheduled={scheduled:HH:mm}, due={due}, lastRun='{_config.ScheduleLastRunDate}'");
        if (_config.ScheduleLastRunDate == today) { LogScheduler("Already ran today."); return; }
        if (!due) { LogScheduler("Not due yet."); return; }
        _scheduleRunning = true;
        try
        {
            StatusText = $"[Scheduler] Starting {(ScheduleDryRun ? "Dry Run" : "Sync")} at {now:HH:mm:ss}...";
            LogScheduler(">>> TRIGGERED");
            await RunSyncAsync(dry: ScheduleDryRun, isScheduled: true);
            if (_syncCompletedSuccessfully) { _config.ScheduleLastRunDate = today; ConfigService.Save(_config); LogScheduler($"Marked run for {today}"); }
            else LogScheduler("Sync did not complete.");
        }
        catch (Exception ex) { StatusText = $"Scheduled run failed: {ex.Message}"; LogScheduler($"Exception: {ex.Message}"); }
        finally { _scheduleRunning = false; UpdateScheduleStatus(); }
    }

    private static void LogScheduler(string message)
    {
        try { var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DarkSync"); Directory.CreateDirectory(dir); File.AppendAllText(Path.Combine(dir, "scheduler.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n"); } catch { }
    }

    partial void OnScheduleEnabledChanged(bool value) => UpdateScheduleStatus();
    partial void OnScheduleTimeChanged(string value) => UpdateScheduleStatus();
    partial void OnScheduleDryRunChanged(bool value) => UpdateScheduleStatus();

    private void UpdateScheduleStatus()
    {
        if (!ScheduleEnabled) { ScheduleStatusText = "Disabled"; return; }
        var last = string.IsNullOrEmpty(_config.ScheduleLastRunDate) ? "never" : _config.ScheduleLastRunDate;
        ScheduleStatusText = $"Enabled: {(ScheduleDryRun ? "Dry Run" : "Sync / Copy")} daily at {ScheduleTime}. Last run: {last}.";
    }

    // ── Credentials ──────────────────────────────────────────────

    private void LoadSavedPassword() { try { var saved = CredentialService.LoadPassword(); if (saved != null) { SftpPassword = saved; RememberSftp = true; StatusText = "Saved SFTP password loaded"; } } catch { } }
    private void SavePassword() { if (RememberSftp && !string.IsNullOrEmpty(SftpPassword)) CredentialService.SavePassword(SftpPassword); else CredentialService.DeletePassword(); }
    [RelayCommand] private void ForgetPassword() { CredentialService.DeletePassword(); SftpPassword = ""; RememberSftp = false; StatusText = "Saved SFTP password removed"; }
    [RelayCommand]
    private async Task TriggerScheduledNowAsync()
    {
        await RunSyncAsync(dry: ScheduleDryRun, isScheduled: true);
        UpdateScheduleStatus();
    }

    [RelayCommand] private async Task UpdateAppAsync()
    {
        if (IsRunning) return; StatusText = "Checking for updates...";
        var (hasUpdate, sha, msg) = await UpdateService.CheckForUpdateAsync();
        if (!hasUpdate) { StatusText = "App is up to date."; MessageBox.Show("You are running the latest version.", "No updates", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (MessageBox.Show($"Update available!\n\n{sha} - {msg.Trim()[..Math.Min(100, msg.Trim().Length)]}\n\nDownload and install?", "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        IsRunning = true;
        try { var status = await UpdateService.DownloadAndBuildAsync(new Progress<string>(m => StatusText = m)); if (status == "OK") { StatusText = "Restarting..."; UpdateService.LaunchUpdaterAndExit(); } else { StatusText = "Update failed"; MessageBox.Show(status, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error); } }
        catch (Exception ex) { StatusText = "Update failed"; MessageBox.Show(ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsRunning = false; }
    }
}

public partial class VmRow : ObservableObject
{
    public VmRow(VmPolicy vm) { VmId = vm.VmId; _name = vm.Name; _enabled = vm.Enabled; _importance = vm.Importance; _copies = vm.Copies; _maxAge = vm.MaxAge; }
    public int VmId { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _importance = 1;
    [ObservableProperty] private int _copies = 1;
    [ObservableProperty] private int _maxAge = 7;
    public void CycleImportance() { Importance = Importance >= 3 ? 1 : Importance + 1; Copies = Importance; }
    [ObservableProperty] private int _sourceCount;
    [ObservableProperty] private int _externalCount;
    [ObservableProperty] private string _newestExternal = "";
    [ObservableProperty] private string _healthStatus = "NOT SCANNED";
    [ObservableProperty] private string _healthDetails = "";
}

public class HistoryRow
{
    public HistoryRow(HistoryEntry e) { Timestamp = e.Timestamp.Replace('T', ' '); Operation = e.Operation; Result = e.Result; Actions = e.Actions; SourceCount = e.SourceCount; ExternalCount = e.ExternalCount; Warnings = e.Warnings; Details = e.Details; VmSnapshotJson = e.VmSnapshotJson; if (DateTime.TryParse(e.Timestamp, out var then)) { var d = DateTime.Now - then; Age = d.TotalDays >= 1 ? $"{(int)d.TotalDays}d {d.Hours}h" : $"{(int)d.TotalMinutes} min"; } }
    public string Timestamp { get; } public string Operation { get; } public string Result { get; }
    public string ResultBadge => Result switch { string r when r.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase) => "Good", string r when r.Contains("FAILED", StringComparison.OrdinalIgnoreCase) || r.Contains("ABORTED", StringComparison.OrdinalIgnoreCase) => "Bad", string r when r.Contains("WARNING", StringComparison.OrdinalIgnoreCase) => "Warning", _ => "Good" };
    public int Actions { get; } public int SourceCount { get; } public int ExternalCount { get; } public int Warnings { get; } public string Details { get; } public string Age { get; } = ""; public string VmSnapshotJson { get; }
}

public class VmSnapshotRow { public int VmId { get; set; } public string Name { get; set; } = ""; public int Importance { get; set; } public int RequiredCopies { get; set; } public int SourceCopies { get; set; } public int ExternalCopies { get; set; } public string NewestExternal { get; set; } = ""; public string Health { get; set; } = "UNKNOWN"; public string Details { get; set; } = ""; }
