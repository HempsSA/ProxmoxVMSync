using DarkSync.Models;

namespace DarkSync.Services;

/// <summary>
/// Headless executor for <c>DarkSync.exe --run-scheduled [--dry-run] [--force]</c>.
/// Used by the Windows Task Scheduler entry so backups run when the app is closed.
/// Exit codes: 0 = success (or already ran today), 1 = failure/aborted, 2 = skipped
/// (another run already in progress).
/// </summary>
public static class ScheduledRunner
{
    public static bool IsHeadlessArgs(string[] args) =>
        args.Any(a => a.Equals("--run-scheduled", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("--scheduled", StringComparison.OrdinalIgnoreCase)
                   || a.Equals("/scheduled", StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args)
    {
        var force = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
        var dryOverride = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            ? true
            : (bool?)null;

        SchedulerLog.Write("Headless run started. " + string.Join(" ", args));

        using var runMutex = new Mutex(false, "DarkSyncProxmoxArchive_Run");
        if (!runMutex.WaitOne(0))
        {
            SchedulerLog.Write("Skipped: another DarkSync run is already in progress.");
            return 2;
        }

        try
        {
            HistoryService.Initialize();
            var config = ConfigService.Load();
            var password = CredentialService.LoadPassword() ?? "";
            var dry = dryOverride ?? config.ScheduleDryRun;
            var today = DateTime.Now.Date.ToString("yyyy-MM-dd");

            if (!force && config.ScheduleLastRunDate == today)
            {
                SchedulerLog.Write($"Skipped: already ran today ({today}). Use --force to override.");
                return 0;
            }

            config.ScheduleLastAttempt = DateTime.Now.ToString("o");

            try
            {
                SyncService.ValidateArchive(config, requireWrite: !dry);
            }
            catch (DestinationUnavailableException ex)
            {
                return await FailAsync(config,
                    dry ? "Dry Run" : "Sync / Copy",
                    "Aborted - destination unavailable", ex.Message, notify: true);
            }
            catch (Exception ex)
            {
                return await FailAsync(config,
                    dry ? "Dry Run" : "Sync / Copy",
                    "Failed - invalid configuration", $"{ex.GetType().Name}: {ex.Message}", notify: true);
            }

            try
            {
                var lastProgressLog = DateTime.MinValue;
                var result = await SyncService.ExecuteAsync(config, password, dry,
                    CancellationToken.None,
                    new Progress<(int Current, int Total, string Message)>(p =>
                    {
                        // Throttle: log at most every 30s so scheduler.log stays readable.
                        var now = DateTime.UtcNow;
                        if (now - lastProgressLog >= TimeSpan.FromSeconds(30))
                        {
                            lastProgressLog = now;
                            SchedulerLog.Write("Progress: " + p.Message);
                        }
                    }));

                var summary = $"{(dry ? "Dry run" : "Sync")} complete: {result.Results.Count} operation(s); warnings: {result.Warnings.Count}";
                SchedulerLog.Write($"Success. {summary}");

                HistoryService.Add(dry ? "Dry Run (scheduled)" : "Sync / Copy (scheduled)",
                    dry ? "Dry run completed" : "Success", summary,
                    result.SourceCount, result.ExternalCount, result.Results.Count, result.Warnings.Count);

                if (!dry)
                {
                    try
                    {
                        await NtfyService.SendAsync(config, "DarkSync Sync Complete",
                            $"{summary}\nSource backups: {result.SourceCount}\nExternal backups: {result.ExternalCount}\nWarnings: {result.Warnings.Count}", true);
                    }
                    catch (Exception ex)
                    {
                        SchedulerLog.Write($"ntfy success notification failed: {ex.Message}");
                    }
                }

                config.ScheduleLastRunDate = today;
                config.ScheduleConsecutiveFailures = 0;
                config.ScheduleLastResult = $"{DateTime.Now:HH:mm} Success: {summary}";
                ConfigService.Save(config);
                return 0;
            }
            catch (Exception ex)
            {
                return await FailAsync(config,
                    dry ? "Dry Run" : "Sync / Copy",
                    "Failed", ex.ToString(), notify: true);
            }
        }
        finally
        {
            try { runMutex.ReleaseMutex(); } catch { }
        }
    }

    private static async Task<int> FailAsync(Config config, string operation, string result, string details, bool notify)
    {
        SchedulerLog.Write($"{result}: {FirstLine(details)}");
        try
        {
            HistoryService.Add(operation + " (scheduled)", result,
                details.Length > 4000 ? details[..4000] : details);
        }
        catch (Exception ex)
        {
            SchedulerLog.Write($"History write failed: {ex.Message}");
        }

        if (notify)
        {
            try { await NtfyService.SendAsync(config, "DarkSync Backup Failed", details.Length > 3000 ? details[..3000] : details, false); }
            catch (Exception ex) { SchedulerLog.Write($"ntfy failure notification failed: {ex.Message}"); }
        }

        try
        {
            config.ScheduleConsecutiveFailures++;
            config.ScheduleLastResult = $"{DateTime.Now:HH:mm} {result}: {FirstLine(details)}";
            ConfigService.Save(config);
        }
        catch (Exception ex)
        {
            SchedulerLog.Write($"Config save failed: {ex.Message}");
        }
        return 1;
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return text.Trim();
    }
}
