using System.Windows;
using System.Windows.Threading;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        FileLog.Write("=== WarbandStudio 启动 ===");
        DispatcherUnhandledException += (_, args) =>
        {
            FileLog.Write("UI 未处理异常", args.Exception);
            MessageBox.Show("出错了，详情见日志：\n" + FileLog.Path + "\n\n" + args.Exception.Message,
                "WarbandStudio", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) FileLog.Write("后台未处理异常", ex);
        };
        base.OnStartup(e);
    }
}
