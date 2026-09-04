using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DarkSync.ViewModels;

namespace DarkSync.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
    }

    private void SetupTray()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open DarkSync", null, (_, _) => RestoreFromTray());
            menu.Items.Add("Run Scheduled Job Now", null, (_, _) =>
            {
                ViewModel.TriggerScheduledNowCommand!.Execute(null);
            });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit DarkSync", null, (_, _) => ExitApplication());

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "DarkSync Proxmox Archive",
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch
        {
            _trayIcon = null;
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _trayIcon != null)
        {
            Hide();
            _trayIcon.ShowBalloonTip(3000, "DarkSync", "Running in the system tray. Scheduler remains active.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            _trayIcon?.Dispose();
            return;
        }

        e.Cancel = true;
        if (_trayIcon != null)
        {
            Hide();
            _trayIcon.ShowBalloonTip(3000, "DarkSync", "Running in the system tray. Scheduler remains active.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        else
        {
            WindowState = WindowState.Minimized;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Maximized;
        ShowInTaskbar = true;
        Activate();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/DarkSync;component/DarkSync_Proxmox_Archive.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
                return new System.Drawing.Icon(stream);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void ExitApplication()
    {
        if (ViewModel.IsRunning || ViewModel.IsSftpTestRunning)
        {
            System.Windows.MessageBox.Show("Cancel or allow the current operation to finish before exiting.",
                "Operation active", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _allowClose = true;
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
            vm.SftpPassword = pb.Password;
    }

    private void NtfyTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is PasswordBox pb)
            vm.NtfyToken = pb.Password;
    }

    private void HistoryTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid dg && dg.SelectedItem is HistoryRow row)
            ViewModel.ShowHistoryVms(row);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveSelectedVmsCommand.Execute(VmGrid.SelectedItems);
    }

    private void ImportanceBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn &&
            btn.DataContext is VmRow row)
            row.CycleImportance();
    }
}

public class BoolToModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is true ? "Dry Run" : "Sync / Copy";

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value?.ToString() == "Dry Run";
}

public class ImportanceToBrushConverter : IValueConverter
{
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(59, 130, 246));   // #3B82F6
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(34, 197, 94));   // #22C55E
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(239, 68, 68));     // #EF4444
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(107, 114, 128));  // #6B7280

    static ImportanceToBrushConverter()
    {
        Blue.Freeze(); Green.Freeze(); Red.Freeze(); Gray.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is int i ? i switch { 1 => Blue, 2 => Green, 3 => Red, _ => Gray } : Gray;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public class ImportanceToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is int i ? i switch { 1 => "1 - Standard", 2 => "2 - Important", 3 => "3 - Critical", _ => $"{i}" } : "";

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
