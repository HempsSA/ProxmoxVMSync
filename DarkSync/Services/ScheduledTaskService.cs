using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace DarkSync.Services;

/// <summary>
/// Registers DarkSync with the Windows Task Scheduler so the daily job runs even
/// when the app is closed, the user is logged off, or the machine was asleep
/// (wake-to-run + start-when-available). Implemented on top of schtasks.exe so no
/// extra dependencies are required.
/// </summary>
public static class ScheduledTaskService
{
    public const string TaskName = "DarkSync Proxmox Archive";

    public static string ExePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "DarkSync.exe");

    public static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParse(text?.Trim(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out time);

    public static async Task<(bool Installed, string Details)> GetStatusAsync()
    {
        var (code, output) = await RunSchtasksAsync($"/query /tn \"{TaskName}\" /fo LIST /v");
        if (code != 0)
            return (false, "Not installed");

        var next = "";
        var lastResult = "";
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Next Run Time:", StringComparison.OrdinalIgnoreCase))
                next = t["Next Run Time:".Length..].Trim();
            else if (t.StartsWith("Last Result:", StringComparison.OrdinalIgnoreCase))
                lastResult = t["Last Result:".Length..].Trim();
        }
        var details = "Installed";
        if (!string.IsNullOrEmpty(next) && !next.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            details += $"; next run: {next}";
        if (!string.IsNullOrEmpty(lastResult))
            details += $"; last result: {lastResult}";
        return (true, details);
    }

    public static Task<(bool Ok, string Message)> RegisterAsync(TimeOnly time, bool dryRun)
    {
        return RegisterAsync(time.ToString("HH:mm"), dryRun);
    }

    public static async Task<(bool Ok, string Message)> RegisterAsync(string timeText, bool dryRun)
    {
        if (!TryParseTime(timeText, out var time))
            return (false, $"Invalid time '{timeText}'. Use HH:mm, e.g. 02:00.");

        var exe = ExePath;
        if (!File.Exists(exe))
            return (false, $"Application exe not found: {exe}");

        // Daily trigger fires at the next occurrence; StartBoundary just needs a valid date.
        var start = DateTime.Today.Add(time.ToTimeSpan()).ToString("yyyy-MM-ddTHH:mm:ss");
        var args = "--run-scheduled" + (dryRun ? " --dry-run" : "");
        var xml = BuildTaskXml(exe, args, start);            var tmp = Path.Combine(Path.GetTempPath(), $"darksync_task_{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tmp, xml, Encoding.Unicode);
            var (code, output) = await RunSchtasksAsync($"/create /tn \"{TaskName}\" /xml \"{tmp}\" /f");
            if (code != 0)
            {
                var hint = output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                    ? " Run DarkSync as administrator and try again."
                    : "";
                return (false, $"Could not register task.{hint} {FirstLine(output)}".Trim());
            }
            return (true, $"Windows task installed: daily at {time:HH:mm} " +
                          $"({(dryRun ? "Dry Run" : "Sync / Copy")}). It runs even when the app is closed.");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public static async Task<(bool Ok, string Message)> UnregisterAsync()
    {
        var (code, output) = await RunSchtasksAsync($"/delete /tn \"{TaskName}\" /f");
        if (code != 0)
            return (false, $"Could not remove task. {FirstLine(output)}".Trim());
        return (true, "Windows task removed. Only the in-app timer (app must stay open) remains.");
    }

    private static string BuildTaskXml(string exePath, string arguments, string startBoundary)
    {
        var cmd = SecurityElement.Escape(exePath) ?? exePath;
        var args = SecurityElement.Escape(arguments) ?? arguments;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>DarkSync Proxmox Archive daily backup. Runs even when the app is closed.</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{startBoundary}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay>
                    <DaysInterval>1</DaysInterval>
                  </ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <WakeToRun>true</WakeToRun>
                <ExecutionTimeLimit>PT4H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{cmd}</Command>
                  <Arguments>{args}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static async Task<(int ExitCode, string Output)> RunSchtasksAsync(string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode, (stdout + "\n" + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return "";
    }
}
