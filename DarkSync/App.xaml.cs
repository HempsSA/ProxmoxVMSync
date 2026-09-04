using System.Windows;
using DarkSync.Services;

namespace DarkSync;

public partial class App : System.Windows.Application
{
    // Held for the lifetime of the process. The previous code used a method-local
    // `using var mutex`, which released the mutex as soon as Startup returned,
    // so the single-instance guard never actually worked.
    private static System.Threading.Mutex? _singleInstanceMutex;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var args = e.Args ?? [];

        // Headless mode for the Windows Task Scheduler entry:
        // DarkSync.exe --run-scheduled [--dry-run] [--force]
        if (ScheduledRunner.IsHeadlessArgs(args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunHeadlessAsync(args);
            return;
        }

        // Apply persisted theme before MainWindow is created
        ThemeService.Apply(this, ThemeService.Current);

        // Prevent multiple GUI instances (mutex is now held for process lifetime)
        _singleInstanceMutex = new System.Threading.Mutex(true, "DarkSyncProxmoxArchive_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DarkSync is already running.", "DarkSync Proxmox Archive", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        MainWindow = new Views.MainWindow();
        MainWindow.Show();
    }

    private async Task RunHeadlessAsync(string[] args)
    {
        int code;
        try
        {
            code = await Task.Run(() => ScheduledRunner.RunAsync(args));
        }
        catch (Exception ex)
        {
            try { SchedulerLog.Write("Headless run crashed: " + ex); } catch { }
            code = 1;
        }
        Shutdown(code);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }
}
