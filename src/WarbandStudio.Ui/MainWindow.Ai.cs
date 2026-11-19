using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WarbandStudio.Ui.Services.Ai;

namespace WarbandStudio.Ui;

public partial class MainWindow
{
    private AiPipeHost? _aiHost;
    private bool _aiExecuting;
    private bool _aiClosedHooked;

    private Task<object?> DispatchAiAsync(Func<Task<object?>> action) => Dispatcher.InvokeAsync(async () =>
    {
        var enabled = IsEnabled;
        _aiExecuting = true;
        IsEnabled = false;
        try { return await action(); }
        finally { _aiExecuting = false; IsEnabled = enabled; }
    }).Task.Unwrap();

    private void OnAiInterface(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "AI 接口 · MCP", Owner = this, Width = 740, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(BackgroundProperty, "Bg.Window");
        dialog.SetResourceReference(ForegroundProperty, "Text.Primary");
        var body = new StackPanel { Margin = new Thickness(18) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        body.Children.Add(status);
        var tabs = new TabControl();
        var current = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 245, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var separate = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 245, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        tabs.Items.Add(new TabItem { Header = "当前工坊窗口", Content = current });
        tabs.Items.Add(new TabItem { Header = "独立会话", Content = separate });
        body.Children.Add(tabs);
        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WarbandStudio.exe");
        string Configuration(string[] arguments) => JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object> { ["warband-studio"] = new { command = executable, args = arguments } }
        }, new JsonSerializerOptions { WriteIndented = true });
        separate.Text = Configuration(["--mcp-stdio"]);
        void Refresh()
        {
            status.Text = _aiHost is null
                ? "启用后，AI 可通过 MCP 查询和编辑当前窗口。修改会显示在画布和表视图中，保存使用现有备份及 RPFM 校验。"
                : "当前窗口接口已启用。复制配置到 MCP 客户端后连接。关闭工坊后，此窗口的连接配置失效。";
            current.Text = _aiHost is null ? "请先点击「启用当前窗口接口」。" : Configuration(["--mcp-stdio", "--connect", _aiHost.PipeName]);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        var enable = new Button { Content = "启用当前窗口接口", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        enable.Click += (_, _) =>
        {
            if (_aiHost is null)
            {
                _aiHost = new AiPipeHost(new AiSession(_vm, DispatchAiAsync));
                _aiHost.Start();
                if (!_aiClosedHooked)
                {
                    _aiClosedHooked = true;
                    Closed += async (_, _) => { if (_aiHost is { } host) await host.DisposeAsync(); };
                }
            }
            Refresh();
        };
        buttons.Children.Add(enable);
        var stop = new Button { Content = "关闭当前窗口接口", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        stop.Click += async (_, _) =>
        {
            var host = _aiHost;
            _aiHost = null;
            enable.IsEnabled = false;
            stop.IsEnabled = false;
            try { if (host is not null) await host.DisposeAsync(); }
            finally { enable.IsEnabled = true; stop.IsEnabled = true; Refresh(); }
        };
        buttons.Children.Add(stop);
        var copy = new Button { Content = "复制所选配置", Padding = new Thickness(10, 5, 10, 5) };
        copy.Click += (_, _) => Clipboard.SetText(tabs.SelectedIndex == 1 ? separate.Text : current.Text);
        buttons.Children.Add(copy);
        body.Children.Add(buttons);
        body.Children.Add(new TextBlock
        {
            Text = "独立会话由 MCP 客户端启动，使用同一套编辑功能。工具支持工程、兵组、路线、页签、成本、授权、表编辑、撤销重做、诊断和保存。批量编辑作为一次撤销记录。",
            TextWrapping = TextWrapping.Wrap
        });
        Refresh();
        dialog.Content = body;
        dialog.ShowDialog();
    }
}
