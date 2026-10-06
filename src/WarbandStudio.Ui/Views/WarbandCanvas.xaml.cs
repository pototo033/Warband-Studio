using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace WarbandStudio.Ui.Views;

/// <summary>
/// 战帮画布（WebView2 承载）：页面就是 <c>Web/canvas.html</c>，坐标模型与游戏 1:1
/// （卡 40×87、步长 46、格 50、原点在面板 (322,48)）。
/// C# 侧只做两件事：把 <c>icons</c> 目录映射成虚拟站点（卡图用），以及把数据 JSON 推进去。
/// </summary>
public partial class WarbandCanvas : UserControl
{
    /// <summary>画布上的一次编辑（原始 JSON，交给 VM 落成"待导出"记录）。</summary>
    public event Action<string>? EditRequested;

    private bool _ready;
    private string? _pendingJson;

    public WarbandCanvas()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        // 被折叠过的 WebView2 再显示出来时偶尔是白的（它内部窗口不会自己重算尺寸）——
        // 抖一下外边距逼它走一次布局，等价于"手动拉一下窗口"的老办法
        IsVisibleChanged += (_, _) => { if (IsVisible) Nudge(); };
    }

    /// <summary>逼 WebView2 重算一次尺寸/重绘（分栏拖动、被折叠后再显示时都要用）。</summary>
    public void NudgeSize() => Nudge();

    private void Nudge()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var m = Margin;
                Margin = new Thickness(m.Left, m.Top, m.Right, m.Bottom + 1);
                _ = Dispatcher.InvokeAsync(() => Margin = m, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception e) { Services.FileLog.Write("画布重排失败", e); }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async Task InitAsync()
    {
        if (_ready) return;
        try
        {
            await View.EnsureCoreWebView2Async();
            // 卡图站点 != 素材目录：解析出来的图（包内 / 游戏 / 随附素材）都会抽到缓存目录，
            // 页面统一用 http://icons.local/<兵>.png 取（以前映射到素材目录，游戏里抽出来的图全是 404）
            var iconsDir = IconCacheDir();
            View.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "icons.local", iconsDir, CoreWebView2HostResourceAccessKind.Allow);
            Services.FileLog.Write("画布：卡图站点 icons.local → " + iconsDir);
            // 皮肤素材（页签背景图 / UI 区的图）——旧工坊那套背景 UI 用
            var skinDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "skincache");
            Directory.CreateDirectory(skinDir);
            View.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "skins.local", skinDir, CoreWebView2HostResourceAccessKind.Allow);
            Services.FileLog.Write("画布：皮肤站点 skins.local → " + skinDir);
            View.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                string msg;
                try { msg = e.WebMessageAsJson; } catch { msg = "(非 JSON 消息)"; }
                Services.FileLog.Write("画布自报：" + msg);
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    switch (type)
                    {
                        case "rendered":
                        case "filter":
                        case "pan":
                        case "domCheck":      // 启动自检（哪些浮层缺元素）
                        case "deselect":      // 点空白取消选择：选择状态只在页面里，后端没有对应动作
                        case "band":          // 框选了几个组（自报计数）
                        case "selectAll":     // Ctrl+A 选了几个组（自报计数）
                        case "snap":          // 吸附开关状态（自报）
                        case "jsError":       // 画布脚本报错：上面那行"画布自报"已经进日志，别再当编辑
                            break;                                  // 自报类，只记日志
                        default:
                            EditRequested?.Invoke(msg);             // 编辑类，一律交给 VM
                            break;
                    }
                }
                catch { /* 不是 JSON 就算了 */ }
            };
            var html = Path.Combine(AppContext.BaseDirectory, "Web", "canvas.html");
            View.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "canvas.local", Path.GetDirectoryName(html)!, CoreWebView2HostResourceAccessKind.Allow);
            View.Source = new Uri("http://canvas.local/" + Path.GetFileName(html));
            _ready = true;
            if (_pendingJson is not null) { var j = _pendingJson; _pendingJson = null; Render(j); }
        }
        catch (Exception e)
        {
            Services.FileLog.Write("画布初始化失败", e);
        }
    }

    /// <summary>把数据推给画布页面（键：groups[{x,y,category,units[]}], icons{unit→url}, pages[]）。</summary>
    public void Render(string json)
    {
        if (!_ready) { _pendingJson = json; return; }
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try { await View.ExecuteScriptAsync("renderWarband(" + json + ")"); }
            catch (Exception e) { Services.FileLog.Write("画布渲染失败", e); }
        });
    }

    /// <summary>卡图缓存目录（解析出来的兵牌都抽到这里；页面用 icons.local 取）。</summary>
    private static string IconCacheDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "iconcache");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>右上选种族/派系 → 让画布按它过滤（并自动切到该种族的页签）。</summary>
    public void SendFilter(string? race, string? faction)
    {
        if (!_ready) return;
        var payload = System.Text.Json.JsonSerializer.Serialize(new { type = "filterBy", race = race ?? "", faction = faction ?? "" });
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try { await View.ExecuteScriptAsync("canvasFilterBy(" + payload + ")"); }
            catch (Exception e) { Services.FileLog.Write("画布过滤失败", e); }
        });
    }

    /// <summary>兵种库双击一张牌 → 让画布新开一组（返回的坐标由页面回传）。</summary>
    public void SendAddUnit(string unit, string? iconUrl)
    {
        if (!_ready) return;
        var payload = System.Text.Json.JsonSerializer.Serialize(new { type = "addUnit", unit, icon = iconUrl ?? "" });
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try { await View.ExecuteScriptAsync("canvasAddUnit(" + payload + ")"); }
            catch (Exception e) { Services.FileLog.Write("画布加兵失败", e); }
        });
    }
}
