using System.IO;

namespace DarkSync.Services;

/// <summary>
/// Shared scheduler log shared by the in-app timer and the headless
/// <c>--run-scheduled</c> executor. Lives in %AppData% so dev and published
/// copies of the app write to the same place.
/// </summary>
public static class SchedulerLog
{
    private static readonly object Lock = new();

    public static string LogPath
    {
        get
        {
            var dir = ConfigService.AppDataDir;
            return Path.Combine(dir, "scheduler.log");
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(ConfigService.AppDataDir);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
        }
        catch { }
    }
}
