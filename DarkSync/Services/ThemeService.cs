using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DarkSync.Services;

public static class ThemeService
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DarkSync", "theme.txt");

    private static object Resolve(Application app, string key)
        => app.TryFindResource(key) ?? Brushes.Transparent;

    public static string Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return File.ReadAllText(SettingsPath).Trim();
        }
        catch { }
        return Light;
    }

    public static void Save(string theme)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, theme == Dark ? Dark : Light);
        }
        catch { }
    }

    public static string Current => Load();

    public static void Apply(Application app, string theme)
    {
        var uri = theme == Dark
            ? new Uri("pack://application:,,,/DarkSync;component/Themes/Dark.xaml")
            : new Uri("pack://application:,,,/DarkSync;component/Themes/Light.xaml");

        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });

        // Override WPF system-color keys that control templates bind to.
        // Default templates (TextBox, ComboBox, ListBox, DataGrid rows, menus,
        // GroupBox, TabControl...) paint their parts from SystemColors; redirecting
        // these keys makes every template use the active theme so no white
        // panels leak through in dark mode.
        app.Resources[SystemColors.WindowBrushKey] = Resolve(app, "WindowBrush");
        app.Resources[SystemColors.WindowTextBrushKey] = Resolve(app, "ForegroundBrush");
        app.Resources[SystemColors.ControlBrushKey] = Resolve(app, "SurfaceBrush");
        app.Resources[SystemColors.ControlTextBrushKey] = Resolve(app, "ForegroundBrush");
        app.Resources[SystemColors.ControlLightBrushKey] = Resolve(app, "SurfaceAltBrush");
        app.Resources[SystemColors.ControlDarkBrushKey] = Resolve(app, "BorderBrush");
        app.Resources[SystemColors.ControlLightLightBrushKey] = Resolve(app, "SurfaceAltBrush");
        app.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Resolve(app, "SurfaceAltBrush");
        app.Resources[SystemColors.HighlightBrushKey] = Resolve(app, "AccentBlue");
        app.Resources[SystemColors.HighlightTextBrushKey] = Resolve(app, "ForegroundBrush");
        app.Resources[SystemColors.MenuBrushKey] = Resolve(app, "SurfaceBrush");
        app.Resources[SystemColors.MenuTextBrushKey] = Resolve(app, "ForegroundBrush");
        app.Resources[SystemColors.ControlDarkDarkBrushKey] = Resolve(app, "BorderBrush");
        app.Resources[SystemColors.ActiveCaptionBrushKey] = Resolve(app, "SurfaceBrush");
        app.Resources[SystemColors.InactiveCaptionBrushKey] = Resolve(app, "SurfaceBrush");
        app.Resources[SystemColors.ActiveCaptionTextBrushKey] = Resolve(app, "ForegroundBrush");
        app.Resources[SystemColors.InactiveCaptionTextBrushKey] = Resolve(app, "ForegroundBrush");
        app.Resources[SystemColors.AppWorkspaceBrushKey] = Resolve(app, "WindowBrush");
        app.Resources[SystemColors.DesktopBrushKey] = Resolve(app, "WindowBrush");
        app.Resources[SystemColors.ScrollBarBrushKey] = Resolve(app, "SurfaceAltBrush");

        app.Resources[typeof(Button)] = ButtonStyle();
        app.Resources[typeof(DataGrid)] = DataGridStyle();
        app.Resources[typeof(TextBox)] = TextBoxStyle();
        app.Resources[typeof(PasswordBox)] = PasswordBoxStyle();
        var comboTemplate = (ControlTemplate)app.TryFindResource("ThemedComboBoxTemplate")!;
        app.Resources[typeof(ComboBox)] = new Style(typeof(ComboBox))
        {
            Setters =
            {
                new Setter(ComboBox.TemplateProperty, comboTemplate),
                new Setter(ComboBox.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")),
                new Setter(ComboBox.BorderThicknessProperty, new Thickness(1)),
                new Setter(ComboBox.PaddingProperty, new Thickness(4, 3, 4, 3))
            }
        };
        app.Resources[typeof(ComboBoxItem)] = new Style(typeof(ComboBoxItem))
        {
            Setters =
            {
                new Setter(ComboBoxItem.BackgroundProperty, new DynamicResourceExtension("SurfaceAltBrush")),
                new Setter(ComboBoxItem.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")),
                new Setter(ComboBoxItem.PaddingProperty, new Thickness(6, 3, 6, 3))
            }
        };
        app.Resources[typeof(ListBoxItem)] = new Style(typeof(ListBoxItem))
        {
            Setters =
            {
                new Setter(ListBoxItem.BackgroundProperty, new DynamicResourceExtension("SurfaceAltBrush")),
                new Setter(ListBoxItem.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush"))
            }
        };
        app.Resources[typeof(DataGridCell)] = new Style(typeof(DataGridCell))
        {
            Setters =
            {
                new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent),
                new Setter(DataGridCell.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")),
                new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)),
                new Setter(DataGridCell.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                new Setter(DataGridCell.PaddingProperty, new Thickness(2))
            }
        };
        var checkTemplate = (ControlTemplate)app.TryFindResource("ThemedCheckBoxTemplate")!;
        app.Resources[typeof(CheckBox)] = new Style(typeof(CheckBox))
        {
            Setters =
            {
                new Setter(CheckBox.TemplateProperty, checkTemplate),
                new Setter(CheckBox.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush"))
            }
        };
        app.Resources[typeof(Label)] = new Style(typeof(Label))
        {
            Setters =
            {
                new Setter(Label.FontWeightProperty, FontWeights.SemiBold),
                new Setter(Label.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush"))
            }
        };
        var tabControlTemplate = (ControlTemplate)app.TryFindResource("ThemedTabControlTemplate")!;
        app.Resources[typeof(TabControl)] = new Style(typeof(TabControl))
        {
            Setters =
            {
                new Setter(TabControl.TemplateProperty, tabControlTemplate),
                new Setter(TabControl.FontWeightProperty, FontWeights.SemiBold),
                new Setter(TabControl.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush"))
            }
        };
        app.Resources[typeof(GroupBox)] = BoxStyle();
        app.Resources[typeof(TextBlock)] = new Style(typeof(TextBlock))
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")) }
        };
        var tabItemTemplate = (ControlTemplate)app.TryFindResource("ThemedTabItemTemplate")!;
        app.Resources[typeof(TabItem)] = new Style(typeof(TabItem))
        {
            Setters =
            {
                new Setter(TabItem.TemplateProperty, tabItemTemplate),
                new Setter(TabItem.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")),
                new Setter(TabItem.PaddingProperty, new Thickness(8, 4, 8, 4))
            }
        };
        app.Resources[typeof(Menu)] = new Style(typeof(Menu))
        {
            Setters =
            {
                new Setter(Menu.BackgroundProperty, new DynamicResourceExtension("SurfaceBrush")),
                new Setter(Menu.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")),
                new Setter(Menu.BorderBrushProperty, new DynamicResourceExtension("BorderBrush"))
            }
        };
        app.Resources[typeof(MenuItem)] = new Style(typeof(MenuItem))
        {
            Setters =
            {
                new Setter(MenuItem.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush"))
            }
        };
        app.Resources[typeof(ContextMenu)] = new Style(typeof(ContextMenu))
        {
            Setters =
            {
                new Setter(ContextMenu.BackgroundProperty, new DynamicResourceExtension("SurfaceBrush")),
                new Setter(ContextMenu.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")),
                new Setter(ContextMenu.BorderBrushProperty, new DynamicResourceExtension("BorderBrush"))
            }
        };
        app.Resources[typeof(ToolTip)] = new Style(typeof(ToolTip))
        {
            Setters =
            {
                new Setter(ToolTip.BackgroundProperty, new DynamicResourceExtension("SurfaceBrush")),
                new Setter(ToolTip.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")),
                new Setter(ToolTip.BorderBrushProperty, new DynamicResourceExtension("BorderBrush"))
            }
        };
    }

    private static Style ButtonStyle()
    {
        var s = new Style(typeof(Button));
        s.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 6, 12, 6)));
        s.Setters.Add(new Setter(Button.FontWeightProperty, FontWeights.SemiBold));
        s.Setters.Add(new Setter(Button.MarginProperty, new Thickness(2)));
        s.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
        s.Setters.Add(new Setter(Button.BorderBrushProperty, new DynamicResourceExtension("BorderBrush")));
        s.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension("NeutralBtn")));
        s.Setters.Add(new Setter(Button.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")));
        return s;
    }

    private static Style DataGridStyle()
    {
        var s = new Style(typeof(DataGrid));
        s.Setters.Add(new Setter(DataGrid.AutoGenerateColumnsProperty, false));
        s.Setters.Add(new Setter(DataGrid.IsReadOnlyProperty, true));
        s.Setters.Add(new Setter(DataGrid.CanUserAddRowsProperty, false));
        s.Setters.Add(new Setter(DataGrid.AlternatingRowBackgroundProperty, new DynamicResourceExtension("AlternatingRowBrush")));
        s.Setters.Add(new Setter(DataGrid.RowHeaderWidthProperty, 0.0));
        s.Setters.Add(new Setter(DataGrid.FontSizeProperty, 13.0));
        s.Setters.Add(new Setter(DataGrid.BackgroundProperty, new DynamicResourceExtension("WindowBrush")));
        s.Setters.Add(new Setter(DataGrid.BorderBrushProperty, new DynamicResourceExtension("BorderBrush")));
        s.Setters.Add(new Setter(DataGrid.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")));
        s.Setters.Add(new Setter(DataGrid.HorizontalGridLinesBrushProperty, new DynamicResourceExtension("BorderBrush")));
        s.Setters.Add(new Setter(DataGrid.ColumnHeaderStyleProperty, ColumnHeaderStyle()));
        return s;
    }

    private static Style ColumnHeaderStyle()
    {
        var s = new Style(typeof(DataGridColumnHeader));
        s.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, new DynamicResourceExtension("GridColumnHeaderBg")));
        s.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")));
        s.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
        s.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(6, 4, 6, 4)));
        s.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, new DynamicResourceExtension("BorderBrush")));
        s.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        return s;
    }

    private static Style TextBoxStyle()
    {
        var s = new Style(typeof(TextBox));
        s.Setters.Add(new Setter(TextBox.BackgroundProperty, new DynamicResourceExtension("InputBgBrush")));
        s.Setters.Add(new Setter(TextBox.BorderBrushProperty, new DynamicResourceExtension("InputBorderBrush")));
        s.Setters.Add(new Setter(TextBox.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")));
        s.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(1)));
        s.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(4, 3, 4, 3)));
        s.Setters.Add(new Setter(TextBox.CaretBrushProperty, new DynamicResourceExtension("ForegroundBrush")));
        s.Setters.Add(new Setter(TextBox.SelectionBrushProperty, new SolidColorBrush(Color.FromRgb(59, 130, 246))));
        return s;
    }

    private static Style PasswordBoxStyle()
    {
        var s = new Style(typeof(PasswordBox));
        s.Setters.Add(new Setter(PasswordBox.BackgroundProperty, new DynamicResourceExtension("InputBgBrush")));
        s.Setters.Add(new Setter(PasswordBox.BorderBrushProperty, new DynamicResourceExtension("InputBorderBrush")));
        s.Setters.Add(new Setter(PasswordBox.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")));
        s.Setters.Add(new Setter(PasswordBox.BorderThicknessProperty, new Thickness(1)));
        s.Setters.Add(new Setter(PasswordBox.PaddingProperty, new Thickness(4, 3, 4, 3)));
        s.Setters.Add(new Setter(PasswordBox.CaretBrushProperty, new DynamicResourceExtension("ForegroundBrush")));
        return s;
    }

    private static Style BoxStyle()
    {
        var s = new Style(typeof(GroupBox));
        s.Setters.Add(new Setter(GroupBox.BackgroundProperty, new DynamicResourceExtension("WindowBrush")));
        s.Setters.Add(new Setter(GroupBox.BorderBrushProperty, new DynamicResourceExtension("BorderBrush")));
        s.Setters.Add(new Setter(GroupBox.ForegroundProperty, new DynamicResourceExtension("ForegroundBrush")));
        return s;
    }
}
