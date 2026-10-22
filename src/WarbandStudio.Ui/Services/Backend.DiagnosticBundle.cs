using System.IO;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    public void ExportDiagnosticBundle(string destination, string context)
    {
        lock (StateSync)
        {
            var current = _pack?.PackPath ?? throw new InvalidOperationException("先打开一个包。");
            var state = CaptureUpdateRecovery();
            state.Packs.RemoveAll(p => p.OriginalPath != current && !p.IsUnitPack);
            var included = state.Packs.Select(p => p.OriginalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in PermissionReferencePacks(current))
                if (included.Add(path)) state.Packs.Add(new RecoveryPack { OriginalPath = path, DisplayName = Path.GetFileName(path), IsUnitPack = true });
            var vanilla = VanillaDbPackPath is { } v ? new FileInfo(v) : null;
            context = $"工坊版本：{Updater.CurrentVersion}\n导出时间：{DateTimeOffset.Now:O}\n" +
                $"游戏目录：{settings.GameDir}\n原版 db.pack：{vanilla?.Length} 字节，{vanilla?.LastWriteTimeUtc:O}\n" +
                $"当前 Pack：{current}\n当前编辑：{EditSummary}\n" + context;
            DiagnosticBundle.Export(destination, state, FileLog.Path, context, RpfmCli.LocateSchema());
            Log?.Invoke("已导出编辑诊断包（含日志、底本、外部引用和撤销记录）：" + destination);
        }
    }
}
