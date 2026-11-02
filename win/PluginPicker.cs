using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XSDAudioRack.Vst3;

using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace XSDAudioRack;

/// <summary>VST3 插件选择窗：复刻苹果版（搜索框 + 行列表 + 计数 + Cancel）。</summary>
public sealed class PluginPickerWindow : Window
{
    private readonly List<Vst3PluginInfo> _items;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _count = new();
    private Vst3PluginInfo? _picked;
    private bool _nonePicked;

    private PluginPickerWindow(List<Vst3PluginInfo> items, bool allowNone, string? current)
    {
        _items = items;

        Title = "选择 VST3 效果器";
        Width = 560;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(Window.BackgroundProperty, "WinBrush");
        SetResourceReference(Window.ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");

        var dock = new DockPanel();

        // ── 标题 ──
        var header = new TextBlock
        {
            Text = "选择 VST3 效果器",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(20, 18, 20, 12),
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        // ── 搜索框 ──
        var search = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(20, 0, 20, 10),
        };
        search.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        var searchPanel = new StackPanel { Orientation = Orientation.Horizontal };
        searchPanel.Children.Add(new TextBlock { Text = "🔍", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 380,
        };
        box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
        searchPanel.Children.Add(box);
        search.Child = searchPanel;
        DockPanel.SetDock(search, Dock.Top);
        dock.Children.Add(search);

        // ── 「不使用」选项 ──
        if (allowNone)
        {
            var noneBtn = new Button
            {
                Content = "（不使用效果器）",
                Height = 34,
                FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(20, 0, 20, 8),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            noneBtn.SetResourceReference(Button.ForegroundProperty, "SecondaryBrush");
            noneBtn.Click += (_, _) => { _nonePicked = true; DialogResult = true; };
            DockPanel.SetDock(noneBtn, Dock.Top);
            dock.Children.Add(noneBtn);
        }

        // ── 底部：计数 + Cancel ──
        var footer = new DockPanel { Margin = new Thickness(20, 12, 20, 16) };
        _count.FontSize = 12;
        _count.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        DockPanel.SetDock(_count, Dock.Left);
        footer.Children.Add(_count);

        var cancel = new Button
        {
            Content = "Cancel",
            Width = 80,
            Height = 30,
            FontSize = 13,
        };
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        // ── 行列表 ──
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(12, 0, 12, 0),
        };
        scroll.Content = _rows;
        dock.Children.Add(scroll);

        Content = dock;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        void Refresh()
        {
            string keyword = box.Text.Trim();
            _rows.Children.Clear();
            var filtered = string.IsNullOrEmpty(keyword)
                ? items
                : items.Where(i => i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var info in filtered)
            {
                var item = info;
                var row = new Button
                {
                    Content = " " + info.Name,
                    Height = 36,
                    FontSize = 14,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 2),
                    BorderThickness = new Thickness(0),
                    Background = info.Name == current
                        ? (Brush)Application.Current.FindResource("CardBrush")
                        : Brushes.Transparent,
                    Cursor = Cursors.Hand,
                };
                row.Click += (_, _) => { _picked = item; DialogResult = true; };
                _rows.Children.Add(row);
            }
            _rows.Children.Add(new Border { Height = 8 });
            _count.Text = $"{filtered.Count} 个插件";
        }
        box.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>弹出选择。返回 null 且未选「不使用」= 用户取消。</summary>
    public static Vst3PluginInfo? Pick(Window owner, bool allowNone)
    {
        List<Vst3PluginInfo> items;
        try { items = Vst3Scanner.Scan(); }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "扫描 VST3 插件失败：" + ex.Message, "提示");
            return null;
        }
        if (items.Count == 0)
        {
            MessageBox.Show(owner,
                "没有找到可用的 VST3 效果器。\n\n请把插件安装到：\nC:\\Program Files\\Common Files\\VST3",
                "提示");
            return null;
        }
        var win = new PluginPickerWindow(items, allowNone, null);
        win.Owner = owner;
        return win.ShowDialog() == true ? (win._nonePicked ? null : win._picked) : null;
    }
}

/// <summary>简单文本输入对话框。返回 null = 取消。</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _tb = new();
    private string? _result;

    private InputDialog(string title, string initial)
    {
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(Window.BackgroundProperty, "WinBrush");
        SetResourceReference(Window.ForegroundProperty, "TextBrush");

        var stack = new StackPanel { Margin = new Thickness(12) };
        _tb.Text = initial;
        _tb.FontSize = 14;
        _tb.Padding = new Thickness(6, 4, 6, 4);
        stack.Children.Add(_tb);
        var ok = new Button { Content = "确定", Height = 34, Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.Bold };
        ok.Click += (_, _) => { _result = _tb.Text; DialogResult = true; };
        _tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _result = _tb.Text; DialogResult = true; }
        };
        stack.Children.Add(ok);
        Content = stack;
    }

    public static string? Ask(Window owner, string title, string initial)
    {
        var win = new InputDialog(title, initial) { Owner = owner };
        return win.ShowDialog() == true ? win._result : null;
    }
}
