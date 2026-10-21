using System.Windows;
using System.Windows.Threading;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        FileLog.Write("=== WarbandStudio 启动 ===");
        try
        {
            var version = typeof(App).Assembly.GetName().Version!;
            InstallationCheck.Verify(AppContext.BaseDirectory, $"{version.Major}.{version.Minor}.{version.Build}",
#if DEBUG
                required: false
#else
                required: true
#endif
            );
            FileLog.Write("启动文件校验：通过");
            var settings=AppSettings.Load();
            InstallationAudit.Run(AppContext.BaseDirectory,$"{version.Major}.{version.Minor}.{version.Build}",
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WarbandStudio","installation-audit"),
                new Dictionary<string,string>{["RPFM"]=WarbandStudio.Rpfm.RpfmCli.LocateCli()??"",["schema"]=WarbandStudio.Rpfm.RpfmCli.LocateSchema()??"",
                    ["旧版全局命名前缀（仅记录）"]=settings.GroupKeyPrefix,["最近 Pack（仅记录）"]=settings.LastPack,["最近工程（仅记录）"]=string.Join(";",settings.RecentProjects)});
        }
        catch (Exception ex)
        {
            FileLog.Write("启动文件校验失败",ex);
            MessageBox.Show(ex.Message + "\n\n日志：" + FileLog.Path,"程序文件校验失败",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
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
