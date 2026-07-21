using System.Windows;

namespace PakAssetStudio;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 应用品牌强调色（青绿），覆盖系统强调色
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x35, 0xD0, 0xA5),
            Wpf.Ui.Appearance.ApplicationTheme.Dark,
            false);
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "未处理错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
