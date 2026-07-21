using System.Windows;

namespace PakAssetStudio;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "未处理错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
