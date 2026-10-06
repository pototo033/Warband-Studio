using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WarbandStudio.Ui.Views;

/// <summary>
/// 使用教程（"PPT 式"指引）：盖在主窗口上，一步指一个地方 + 说清那块是干嘛的，点「确定」下一步。
/// 覆盖层与主窗口同尺寸同位置，高亮框用"目标相对主窗口"的坐标定位（不用屏幕坐标，缩放/多屏都不怕）。
/// 同一个窗口也当"面板问号"的说明卡用（单步模式：只显示一条 + 「知道了」）。
/// </summary>
public partial class TourWindow : Window
{
    /// <summary>一步：指哪个元素（主窗口里的 x:Name，空 = 居中不指）、标题、说明。</summary>
    public sealed record Step(string Target, string Title, string Body);

    private readonly Window _owner;
    private readonly List<Step> _steps;
    private readonly bool _single;          // 单步模式（面板问号）：没有上一步/跳过，按钮写"知道了"
    private int _at;

    public TourWindow(Window owner, List<Step> steps, bool single = false)
    {
        InitializeComponent();
        _owner = owner; _steps = steps; _single = single;
        if (single)
        {
            BtnPrev.Visibility = Visibility.Collapsed;
            BtnSkip.Visibility = Visibility.Collapsed;
            BtnNext.Content = "知道了";
        }
        Owner = owner;
        Loaded += (_, _) => { SyncBounds(); ShowStep(0); };
        owner.LocationChanged += (_, _) => SyncBounds();
        owner.SizeChanged += (_, _) => SyncBounds();
    }

    /// <summary>覆盖层跟主窗口的**客户区**对齐：窗口 Left/Top 是外框（含标题栏/边框）→ 会整体偏下偏右 ✗，
    /// 用 PointToScreen(0,0) 取客户区原点的屏幕坐标再换算成 DIP（DPI 缩放也对得上）。</summary>
    private void SyncBounds()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(_owner);
            var origin = _owner.PointToScreen(new Point(0, 0));
            Left = origin.X / dpi.DpiScaleX;
            Top = origin.Y / dpi.DpiScaleY;
            Width = _owner.ActualWidth;          // 客户区大小（DIP）
            Height = _owner.ActualHeight;
        }
        catch { }
    }

    private void ShowStep(int i)
    {
        if (i < 0 || i >= _steps.Count) return;
        _at = i;
        var st = _steps[i];
        Title.Text = st.Title;
        Body.Text = st.Body;
        StepNo.Text = _single ? "" : $"{i + 1} / {_steps.Count}";

        // 高亮框：按目标元素相对主窗口的位置定位（找不到就居中放卡片、不画框）
        var target = string.IsNullOrWhiteSpace(st.Target) ? null : _owner.FindName(st.Target) as FrameworkElement;
        if (target is null || !target.IsVisible || target.ActualWidth < 2)
        {
            Spot.Visibility = Visibility.Collapsed;
            Card.HorizontalAlignment = HorizontalAlignment.Center;
            Card.VerticalAlignment = VerticalAlignment.Center;
            Card.Margin = new Thickness(0);
            return;
        }
        Spot.Visibility = Visibility.Visible;
        try
        {
            var p = target.TransformToAncestor(_owner).Transform(new Point(0, 0));
            var pad = 3.0;
            Spot.Width = target.ActualWidth + pad * 2;
            Spot.Height = target.ActualHeight + pad * 2;
            Spot.Margin = new Thickness(p.X - pad, p.Y - pad, 0, 0);

            // 卡片定位：**先量出真实高度**（不量的话长文案会压住高亮框 ✗），再按 下 / 上 / 右 依次试，
            // 都放不下就放到框**内**左下角（这样高亮框的四条边还看得见，不会整块被盖住）
            Card.HorizontalAlignment = HorizontalAlignment.Left;
            Card.VerticalAlignment = VerticalAlignment.Top;
            Card.Measure(new Size(430, double.PositiveInfinity));
            Card.UpdateLayout();
            var cardH = Card.ActualHeight > 10 ? Card.ActualHeight : 240;
            var gap = 12.0;
            double sl = p.X - pad, stp = p.Y - pad;
            double sr = sl + Spot.Width, sb = stp + Spot.Height;
            var l = Math.Min(Math.Max(8, sl), Math.Max(8, Width - 438));
            double t;
            if (sb + gap + cardH <= Height - 8) t = sb + gap;                       // ① 框下面
            else if (stp - gap - cardH >= 8) t = stp - gap - cardH;                 // ② 框上面
            else if (sr + gap + 438 <= Width - 8)                                   // ③ 框右边
            { l = sr + gap; t = Math.Min(Math.Max(8, stp), Math.Max(8, Height - cardH - 8)); }
            else                                                                    // ④ 框内左下角（四条边留出来）
            { l = Math.Min(Math.Max(8, sl + 12), Math.Max(8, Width - 438)); t = Math.Max(stp + 12, sb - cardH - 12); }
            Card.Margin = new Thickness(l, t, 0, 0);
        }
        catch { Spot.Visibility = Visibility.Collapsed; }
    }

    private void OnPrev(object sender, RoutedEventArgs e) => ShowStep(_at - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_single || _at >= _steps.Count - 1) { Close(); return; }
        ShowStep(_at + 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Close();
}
