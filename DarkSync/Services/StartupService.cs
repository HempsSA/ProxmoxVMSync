using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace DarkSync.Services;    /// <summary>
    /// Registers DarkSync in Windows Task Scheduler with a Logon trigger and
    /// RunLevel = HighestAvailable so the application starts as Administrator
    /// when the user logs in — with NO UAC prompt.  The Task Scheduler service
    /// (running as SYSTEM) creates the elevated process directly.
    /// Implemented on top of schtasks.exe so no extra dependencies are required.
    /// </summary>
public static class StartupService
{
    public const string TaskName = "DarkSync Proxmox Archive – Startup";

    public static string ExePath =>
        Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "DarkSync.exe");

    public static async Task<bool> IsRegisteredAsync()
    {
        var (code, _) = await RunSchtasksAsync($"/query /tn \"{TaskName}\" /fo LIST");
        return code == 0;
    }

    public static async Task<(bool Ok, string Message)> RegisterAsync()
    {
        var exe = ExePath;
        if (!File.Exists(exe))
            return (false, $"Application exe not found: {exe}");

        // Logon trigger – fires once when the current user logs on.
        // HighestAvailable: the scheduler service creates the process elevated
        // directly (no UAC prompt) when the logged-on user is an admin.
        var xml = BuildTaskXml(exe);
        var tmp = Path.Combine(Path.GetTempPath(), $"darksync_startup_{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tmp, xml, Encoding.Unicode);
            var (code, output) = await RunSchtasksAsync($"/create /tn \"{TaskName}\" /xml \"{tmp}\" /f");
            if (code != 0)
            {
                var hint = output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                    ? " Run DarkSync as administrator and try again."
                    : "";
                return (false, $"Could not register startup task.{hint} {FirstLine(output)}".Trim());
            }
            return (true, "DarkSync will now start with Windows (as Administrator) when you log in.");
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
            return (false, $"Could not remove startup task. {FirstLine(output)}".Trim());
        return (true, "DarkSync will no longer start automatically with Windows.");
    }

    private static string BuildTaskXml(string exePath)
    {
        var cmd = SecurityElement.Escape(exePath) ?? exePath;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Start DarkSync Proxmox Archive as Administrator at user logon.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{cmd}</Command>
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
