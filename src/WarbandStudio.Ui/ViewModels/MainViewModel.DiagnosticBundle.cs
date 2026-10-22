using System.IO;
using Microsoft.Win32;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public RelayCommand ExportDiagnosticBundleCommand { get; }
    public event Action<string>? SaveFailureReported;

    private void ReportSaveFailure(string title, Exception error)
    {
        FileLog.Write(title, error);
        Status = title + "：" + error.Message;
        DiagnosticsLines.Clear();
        foreach (var line in Status.Split('\n')) DiagnosticsLines.Add(line.TrimEnd());
        SaveFailureReported?.Invoke(Status);
    }

    private async Task ExportDiagnosticBundleAsync()
    {
        if (IsBusy || !PackOpen) return;
        if (FlushTableEdits is not null) await FlushTableEdits();
        var dialog = new SaveFileDialog
        {
            Title = "导出编辑与日志（包含当前 Pack 底本和外部兵种引用）",
            Filter = "编辑诊断包 (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(CurrentPackPath) + "_编辑诊断_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip",
        };
        if (dialog.ShowDialog() != true) return;
        var context = $"界面状态：{Status}\n当前文档：{ActiveCenterTab?.GetType().Name}\n" +
            "诊断明细：\n" + string.Join('\n', DiagnosticsLines) + "\n本次会话日志：\n" + string.Join('\n', LogLines);
        ++_autoSaveGen;
        IsBusy = true;
        // 快照包含底本文件；复制期间暂停界面操作，防止状态与底本属于两次不同编辑。
        _updatePreparing = true;
        UpdateSessionLockChanged?.Invoke(true);
        try
        {
            Status = "正在导出编辑诊断 ZIP（含日志和引用包）…";
            await Task.Run(() => _backend.ExportDiagnosticBundle(dialog.FileName, context));
            Status = "已导出编辑与日志：" + dialog.FileName;
        }
        catch (Exception ex) { FileLog.Write("编辑诊断导出失败", ex); Status = "编辑诊断导出失败：" + ex.Message; }
        finally { _updatePreparing = false; UpdateSessionLockChanged?.Invoke(false); IsBusy = false; }
    }
}
