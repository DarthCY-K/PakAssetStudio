using System.Windows;

namespace PakAssetStudio;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 先加载多语言，再应用品牌强调色（青绿），覆盖系统强调色
        Services.LocalizationService.Instance.Initialize();
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x35, 0xD0, 0xA5),
            Wpf.Ui.Appearance.ApplicationTheme.Dark,
            false);
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, Services.LocalizationService.Text("Crash_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
