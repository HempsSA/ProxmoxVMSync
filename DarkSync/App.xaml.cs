using System.Windows;
using DarkSync.Services;

namespace DarkSync;

public partial class App : System.Windows.Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Apply persisted theme before MainWindow is created
        ThemeService.Apply(this, ThemeService.Current);

        // Prevent multiple instances
        using var mutex = new System.Threading.Mutex(true, "DarkSyncProxmoxArchive_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DarkSync is already running.", "DarkSync Proxmox Archive", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
    }
}
