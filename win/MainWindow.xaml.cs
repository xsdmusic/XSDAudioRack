using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using XSDAudioRack.Audio;
using XSDAudioRack.License;
using XSDAudioRack.Ui;
using XSDAudioRack.Vst3;

// WPF / WinForms 类型歧义（UseWindowsForms 隐式引入了 System.Windows.Forms）
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace XSDAudioRack;

/// <summary>
/// 主界面：复刻苹果版三栏布局（插件链 / 电平+主控 / 设备栏），
/// Send A/B 旋钮条、虚拟输入 C/D（对应苹果版 AUX 位置）、主题切换、全局静音快捷键。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AudioEngine _engine = new();
    private readonly DispatcherTimer _meterTimer = new();
    private readonly DispatcherTimer _licenseTimer = new();

    private string[] _asioDrivers = Array.Empty<string>();

    // UI 控件
    private MacVerticalMeter? _inMeter, _outMeter;
    private MacVerticalFader? _masterFader;
    private MacMeteredFader? _faderC, _faderD;

    // 状态
    private bool _initialized;          // 防止初始化期间的回调写自动保存
    private bool _suppressComboEvents;  // 程序填充下拉框时屏蔽 SelectionChanged
    private bool _showDevicePanel = true;
    private string _auxCName = "伴奏", _auxDName = "音效";   // 虚拟输入 C/D 显示名（双击可改）
    private string? _presetDriver;      // 预设里保存的 ASIO 声卡
    private string? _muteHotkey;        // 如 "Ctrl+Shift+M"
    private bool _capturingHotkey;
    private HwndSource? _hwnd;

    public MainWindow()
    {
        InitializeComponent();

        // 未激活且在三天试用内：标题提示剩余天数与激活联系方式
        try
        {
            var (valid, _, _) = LicenseManager.CheckLocalLicense(DateTime.UtcNow);
            if (!valid)
            {
                var (trialOk, trialEnd) = LicenseManager.CheckTrial(DateTime.UtcNow);
                if (trialOk)
                {
                    int left = Math.Max(1, (int)Math.Ceiling((trialEnd - DateTime.UtcNow).TotalDays));
                    Title = $"XSD Audio Rack（试用剩 {left} 天 · 试用结束后支付宝 ¥9.9 扫码激活终身使用）";
                }
            }
        }
        catch { }

        LoadPresetIntoEngine();
        // 启动时即创建 WDM2VST 虚拟输入/总输出插件实例（若已安装），
        // 保证保存预置时它们的设置始终可被捕获，不依赖引擎是否启动过
        try { _engine.EnsureWdm2vstPlugins(); } catch { }
        BuildMeters();
        BuildMasterFader();
        BuildSendStrip(1);
        BuildSendStrip(2);
        BuildAuxRow(3);
        BuildAuxRow(4);
        BuildPostSlot();
        RebuildChainUi();
        RefreshAsioDrivers();
        UpdateStartStopUi();
        UpdateMuteUi();
        UpdateHotkeyUi();
        UpdateSendMuteUi();
        ApplyDevicePanelVisibility();

        _meterTimer.Interval = TimeSpan.FromMilliseconds(50);
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();

        _licenseTimer.Interval = TimeSpan.FromSeconds(60);
        _licenseTimer.Tick += (_, _) => CheckLicense();
        _licenseTimer.Start();

        _initialized = true;
    }

    // ---------------- 预设 ----------------

    private void LoadPresetIntoEngine()
    {
        var preset = RackPreset.Load();
        if (preset == null) return;
        try
        {
            var failed = preset.RestoreInto(_engine);
            if (failed.Count > 0)
                InfoText.Text = "插件恢复失败：" + string.Join("、", failed);
        }
        catch { }
        _presetDriver = preset.AsioDriverName;
        _showDevicePanel = preset.ShowDevicePanel;
        _muteHotkey = preset.MuteHotkey;
        if (!string.IsNullOrWhiteSpace(preset.AuxCName)) _auxCName = preset.AuxCName;
        if (!string.IsNullOrWhiteSpace(preset.AuxDName)) _auxDName = preset.AuxDName;
        // Send 总音量滑条回填（_initialized=false 时不触发回写）
        SendMasterSlider.Value = DbConv.ToDb(_engine.SendMasterVolume);
        if (preset.Theme == ThemeManager.Dark)
            ApplyTheme(ThemeManager.Dark);
    }

    private void SavePreset()
    {
        if (!_initialized) return;
        try
        {
            var preset = RackPreset.Capture(_engine, _showDevicePanel, _muteHotkey);
            preset.AsioDriverName = _engine.AsioDriverName ?? _presetDriver; // 引擎停止时引擎字段为空，用记住的驱动
            preset.AuxCName = _auxCName;
            preset.AuxDName = _auxDName;
            preset.Save();
        }
        catch { }
    }

    /// <summary>从预设对象恢复并刷新全部 UI（autosave 启动 / 文件加载共用）。</summary>
    private void RestorePresetIntoUi(RackPreset preset)
    {
        _engine.Stop();

        // 清空当前会话的链与 Send 插件，避免文件加载时追加到旧链上
        foreach (var p in _engine.Chain.ToList())
        {
            _engine.Chain.Remove(p);
            p.Dispose();
        }
        _engine.SendPluginA?.Dispose(); _engine.SendPluginA = null;
        _engine.SendPluginB?.Dispose(); _engine.SendPluginB = null;

        preset.RestoreInto(_engine);
        _presetDriver = preset.AsioDriverName;
        if (!string.IsNullOrWhiteSpace(preset.AuxCName)) _auxCName = preset.AuxCName;
        if (!string.IsNullOrWhiteSpace(preset.AuxDName)) _auxDName = preset.AuxDName;

        RebuildChainUi();
        BuildSendStrip(1);
        BuildSendStrip(2);
        BuildAuxRow(3);
        BuildAuxRow(4);
        BuildPostSlot();
        UpdateSendMuteUi();
        if (_faderC != null) _faderC.Db = DbConv.ToDb(_engine.SendVolumeC);
        if (_faderD != null) _faderD.Db = DbConv.ToDb(_engine.SendVolumeD);
        if (_masterFader != null) _masterFader.Db = DbConv.ToDb(_engine.MasterGain);
        SendMasterSlider.Value = DbConv.ToDb(_engine.SendMasterVolume);
        UpdateMuteUi();
        // 静音快捷键 / 侧栏显示 / 主题也跟随预设
        _muteHotkey = preset.MuteHotkey;
        UpdateHotkeyUi();
        _showDevicePanel = preset.ShowDevicePanel;
        ApplyDevicePanelVisibility();
        if (!string.IsNullOrWhiteSpace(preset.Theme) && preset.Theme != ThemeManager.Current)
            ApplyTheme(preset.Theme);
        RefreshAsioDrivers();
        UpdateStartStopUi();
    }

    // ---------------- 效果器链 ----------------

    private void RebuildChainUi()
    {
        ChainList.Children.Clear();
        ChainCount.Text = $"{_engine.Chain.Count} 个插件";
        ChainEmptyHint.Visibility = _engine.Chain.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _engine.Chain.Count; i++)
        {
            int idx = i;
            var plugin = _engine.Chain[i];

            var row = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            row.SetResourceReference(Border.BackgroundProperty, "CardBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            // 序号
            var num = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            num.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
            grid.Children.Add(num);

            // 上/下移
            var chevrons = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var upBtn = MakeChevronButton("˄", () => MovePlugin(idx, -1), idx > 0);
            var downBtn = MakeChevronButton("˅", () => MovePlugin(idx, +1), idx < _engine.Chain.Count - 1);
            chevrons.Children.Add(upBtn);
            chevrons.Children.Add(downBtn);
            grid.Children.Add(chevrons);
            Grid.SetColumn(chevrons, 1);

            // 插件名
            var name = new TextBlock
            {
                Text = plugin.Name,
                FontWeight = FontWeights.Medium,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 6, 0),
                ToolTip = plugin.Name,
            };
            grid.Children.Add(name);
            Grid.SetColumn(name, 2);

            // Open
            var openBtn = new Button
            {
                Content = "Open",
                Style = (Style)FindResource("SmallButton"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "打开插件界面",
            };
            openBtn.Click += (_, _) => OpenEditor(_engine.Chain[idx]);
            grid.Children.Add(openBtn);
            Grid.SetColumn(openBtn, 3);

            // 旁通开关
            var sw = new CheckBox
            {
                Style = (Style)FindResource("SwitchToggle"),
                IsChecked = !plugin.Bypassed,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = plugin.Bypassed ? "启用插件" : "旁通插件",
            };
            sw.Click += (_, _) =>
            {
                _engine.Chain[idx].Bypassed = !(_engine.Chain[idx].Bypassed);
                sw.ToolTip = _engine.Chain[idx].Bypassed ? "启用插件" : "旁通插件";
                SavePreset();
            };
            grid.Children.Add(sw);
            Grid.SetColumn(sw, 4);

            // 删除
            var delBtn = MakeIconButton("✕", "移除插件");
            delBtn.Click += (_, _) => ModifyChain(() =>
            {
                var p = _engine.Chain[idx];
                _engine.Chain.RemoveAt(idx);
                p.Dispose();
            });
            grid.Children.Add(delBtn);
            Grid.SetColumn(delBtn, 5);

            row.Child = grid;
            ChainList.Children.Add(row);
        }
    }

    private Button MakeChevronButton(string glyph, Action action, bool enabled)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 18,
            Height = 14,
            Padding = new Thickness(0),
            FontSize = 9,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsEnabled = enabled,
        };
        b.SetResourceReference(Button.ForegroundProperty, "SecondaryBrush");
        b.Click += (_, _) => action();
        return b;
    }

    private Button MakeIconButton(string glyph, string toolTip)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            FontSize = 13,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = toolTip,
        };
        b.SetResourceReference(Button.ForegroundProperty, "SecondaryBrush");
        return b;
    }

    private void MovePlugin(int index, int delta)
    {
        int to = index + delta;
        if (to < 0 || to >= _engine.Chain.Count) return;
        (_engine.Chain[index], _engine.Chain[to]) = (_engine.Chain[to], _engine.Chain[index]);
        RebuildChainUi();
        SavePreset();
    }

    /// <summary>修改链/送出插件须先停引擎（插件需重新 Activate），完成后再启动。</summary>
    private void ModifyChain(Action change)
    {
        bool wasRunning = _engine.IsRunning;
        string? driver = _engine.AsioDriverName;
        if (wasRunning) _engine.Stop();
        change();
        RebuildChainUi();
        BuildSendStrip(1);
        BuildSendStrip(2);
        if (wasRunning && driver != null)
        {
            try { StartEngine(driver); } catch (Exception ex) { ShowEngineError(ex); }
        }
        UpdateStartStopUi();
        SavePreset();
    }

    private void OpenEditor(Vst3Plugin plugin)
    {
        try { plugin.OpenEditor(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开「{plugin.Name}」的界面：{ex.Message}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnMasterOut_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.MasterOutPlugin == null)
        {
            MessageBox.Show(this, "总输出插件未加载（WDM2VST 未检测到或引擎未启动）。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenEditor(_engine.MasterOutPlugin);
    }

    // ---------------- 输出前插件（限制器插槽） ----------------

    /// <summary>构建输出前插件插入栏：所有声音之后、总推子之前。</summary>
    private void BuildPostSlot()
    {
        PostSlot.Children.Clear();
        var p = _engine.PostPlugin;
        if (p == null)
        {
            var add = new Button
            {
                Content = "＋ 限制器",
                FontSize = 11,
                Height = 24,
                Padding = new Thickness(10, 0, 10, 0),
            };
            add.ToolTip = "在所有混音之后、总推子之前插入一个效果器（通常放限制器，防止总输出爆音）";
            add.Click += (_, _) =>
            {
                var info = PluginPickerWindow.Pick(this, allowNone: false);
                if (info == null) return;
                ModifyPost(() =>
                {
                    try { _engine.PostPlugin = Vst3Plugin.Create(info); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"无法加载「{info.Name}」：{ex.Message}",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });
            };
            PostSlot.Children.Add(add);
            return;
        }

        // 已加载：名字 + 打开 + 移除
        var name = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 0, 8, 0),
            Height = 24,
        };
        name.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        name.SetResourceReference(Border.BorderBrushProperty, "SepBrush");
        name.BorderThickness = new Thickness(1);
        name.Child = new TextBlock
        {
            Text = p.Name,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.ToolTip = "输出前效果器（所有声音之后、总推子之前）：" + p.Name;
        name.MouseLeftButtonDown += (_, _) => OpenEditor(p);
        PostSlot.Children.Add(name);

        var openBtn = new Button { Content = "打开", FontSize = 11, Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
        openBtn.ToolTip = "打开输出前效果器的界面";
        openBtn.Click += (_, _) => OpenEditor(p);
        PostSlot.Children.Add(openBtn);

        var removeBtn = new Button { Content = "×", FontSize = 11, Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
        removeBtn.ToolTip = "移除输出前效果器";
        removeBtn.Click += (_, _) => ModifyPost(() =>
        {
            _engine.PostPlugin?.Dispose();
            _engine.PostPlugin = null;
        });
        PostSlot.Children.Add(removeBtn);
    }

    /// <summary>修改输出前插件（引擎运行中则自动重启引擎使其生效）。</summary>
    private void ModifyPost(Action change)
    {
        bool wasRunning = _engine.IsRunning;
        string? driver = _engine.AsioDriverName;
        if (wasRunning) _engine.Stop();
        change();
        BuildPostSlot();
        if (wasRunning && driver != null)
        {
            try { StartEngine(driver); } catch (Exception ex) { ShowEngineError(ex); }
        }
        UpdateStartStopUi();
        SavePreset();
    }

    // ---------------- Send 总静音 ----------------

    private void BtnSendMute_Click(object sender, RoutedEventArgs e)
    {
        _engine.SendMuted = !_engine.SendMuted;
        UpdateSendMuteUi();
        SavePreset();
    }

    private void UpdateSendMuteUi()
    {
        BtnSendMute.Content = _engine.SendMuted ? "已静音" : "静音";
        BtnSendMute.Style = (Style)FindResource(_engine.SendMuted ? "DangerButton" : typeof(Button));
    }

    private void BtnAddPlugin_Click(object sender, RoutedEventArgs e)
    {
        var info = PluginPickerWindow.Pick(this, allowNone: false);
        if (info == null) return;
        Vst3Plugin? plugin = null;
        ModifyChain(() =>
        {
            try
            {
                plugin = Vst3Plugin.Create(info);
                if (_engine.Chain.Count >= AudioEngine.MainChainMax)
                {
                    plugin.Dispose();
                    plugin = null;
                }
                else _engine.Chain.Add(plugin);
            }
            catch (Exception ex)
            {
                plugin = null;
                MessageBox.Show(this, $"无法加载「{info.Name}」：{ex.Message}",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
        if (plugin != null) OpenEditor(plugin);
    }

    // ---------------- Send A / B（苹果版 SendStrip：旋钮 + 名称条 + Open + 开关 + 删除）----------------

    private void BuildSendStrip(int which)
    {
        var host = which == 1 ? SendARow : SendBRow;
        host.Children.Clear();

        var plugin = which == 1 ? _engine.SendPluginA : _engine.SendPluginB;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        // 旋钮
        var knob = new MacKnob(34) { VerticalAlignment = VerticalAlignment.Center };
        knob.Db = DbConv.ToDb(which == 1 ? _engine.SendVolumeA : _engine.SendVolumeB);
        knob.ValueChanged += db =>
        {
            if (which == 1) _engine.SendVolumeA = (float)DbConv.ToLinear(db);
            else _engine.SendVolumeB = (float)DbConv.ToLinear(db);
            SavePreset();
        };
        grid.Children.Add(knob);

        // 名称条 / 虚线加载条
        FrameworkElement pill;
        if (plugin != null)
        {
            var namePill = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 0, 10, 0),
                Height = 38,
                VerticalAlignment = VerticalAlignment.Center,
            };
            namePill.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            namePill.Child = new TextBlock
            {
                Text = plugin.Name,
                FontWeight = FontWeights.Medium,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = plugin.Name,
            };
            pill = namePill;
        }
        else
        {
            var loadBtn = new Button
            {
                Height = 38,
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(0),
                ToolTip = "点击选择送出效果器",
                Cursor = Cursors.Hand,
            };
            loadBtn.SetResourceReference(Button.BackgroundProperty, "CardBrush");
            var g = new Grid();
            var rect = MakeDashedRect(8);
            g.Children.Add(rect);
            var tb = new TextBlock
            {
                Text = "＋ 点击加载插件",
                FontSize = 12,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
            g.Children.Add(tb);
            loadBtn.Content = g;
            loadBtn.Click += (_, _) =>
            {
                var info = PluginPickerWindow.Pick(this, allowNone: true);
                if (info == null) return;
                ModifyChain(() =>
                {
                    if (which == 1)
                    {
                        _engine.SendPluginA?.Dispose();
                        _engine.SendPluginA = SafeCreate(info);
                    }
                    else
                    {
                        _engine.SendPluginB?.Dispose();
                        _engine.SendPluginB = SafeCreate(info);
                    }
                });
            };
            pill = loadBtn;
        }
        grid.Children.Add(pill);
        Grid.SetColumn(pill, 1);

        if (plugin != null)
        {
            int idx = which; // 捕获
            // Open
            var openBtn = new Button
            {
                Content = "Open",
                Style = (Style)FindResource("SmallButton"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            openBtn.Click += (_, _) =>
            {
                var p = idx == 1 ? _engine.SendPluginA : _engine.SendPluginB;
                if (p != null) OpenEditor(p);
            };
            grid.Children.Add(openBtn);
            Grid.SetColumn(openBtn, 2);

            // 开关（旁通）
            var sw = new CheckBox
            {
                Style = (Style)FindResource("SwitchToggle"),
                IsChecked = !plugin.Bypassed,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = plugin.Bypassed ? "开启 Send" : "关闭 Send",
            };
            sw.Click += (_, _) =>
            {
                var p = idx == 1 ? _engine.SendPluginA : _engine.SendPluginB;
                if (p != null) p.Bypassed = !(p.Bypassed);
                SavePreset();
            };
            grid.Children.Add(sw);
            Grid.SetColumn(sw, 3);

            // 删除
            var delBtn = MakeIconButton("✕", "移除 Send 插件");
            delBtn.VerticalAlignment = VerticalAlignment.Center;
            delBtn.Click += (_, _) => ModifyChain(() =>
            {
                if (idx == 1) { _engine.SendPluginA?.Dispose(); _engine.SendPluginA = null; }
                else { _engine.SendPluginB?.Dispose(); _engine.SendPluginB = null; }
            });
            grid.Children.Add(delBtn);
            Grid.SetColumn(delBtn, 4);
        }

        host.Children.Add(grid);
    }

    private Vst3Plugin? SafeCreate(Vst3PluginInfo info)
    {
        try { return Vst3Plugin.Create(info); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法加载「{info.Name}」：{ex.Message}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    // ---------------- Send Volume 总推子 ----------------

    private void SendMasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) { if (_engine != null) _engine.SendMasterVolume = (float)DbConv.ToLinear(e.NewValue); return; }
        double db = SendMasterSlider.Value;
        _engine.SendMasterVolume = (float)DbConv.ToLinear(db);
        SendMasterDb.Text = (db >= 0 ? "+" : "") + db.ToString("0.0") + " dB";
        SavePreset();
    }

    // ---------------- 虚拟输入 C / D（苹果版 AUX 行样式）----------------

    private void BuildAuxRow(int which)
    {
        var host = which == 3 ? AuxRowC : AuxRowD;
        host.Children.Clear();
        host.Height = 42;

        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(185) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 胶囊标签（白底 + 灰实线边，双击可改名）
        var capsule = new Grid();
        var capRect = new Rectangle { RadiusX = 6, RadiusY = 6, StrokeThickness = 1 };
        capRect.SetResourceReference(Rectangle.StrokeProperty, "SepBrush");
        capRect.SetResourceReference(Rectangle.FillProperty, "FieldBrush");
        capsule.Children.Add(capRect);
        var capText = new TextBlock
        {
            Text = which == 3 ? _auxCName : _auxDName,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        capText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        capsule.Children.Add(capText);
        capsule.ToolTip = (which == 3
            ? "虚拟输入 C：把 Ultra PLAY 05/06 上播放的系统声音混进机架（WDM2VST Ultra 自动采集）"
            : "虚拟输入 D：把 Ultra PLAY 07/08 上播放的系统声音混进机架（WDM2VST Ultra 自动采集）") + "\n双击标签可改名";
        capsule.MouseLeftButtonDown += (_, me) =>
        {
            if (me.ClickCount == 2) BeginAuxRename(which, capsule, capText);
        };
        host.Children.Add(capsule);

        // 通道框（白底 + 灰虚线，显示固定虚拟设备）
        var channel = new Grid();
        var chRect = MakeDashedRect(8);
        channel.Children.Add(chRect);
        var chText = new TextBlock
        {
            Text = (which == 3 ? Wdm2VstBridge.PlayC : Wdm2VstBridge.PlayD) + " 的系统声音",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        chText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        channel.Children.Add(chText);
        channel.ToolTip = "由 WDM2VST Ultra 自动采集，无需设置；在 Windows 声音设置里把要混入的应用输出到该设备即可";
        host.Children.Add(channel);
        Grid.SetColumn(channel, 2);

        // 打开采集插件界面：自动绑定失败时可手动选择端口
        var openBtn = new Button
        {
            Content = "打开",
            FontSize = 12,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
        };
        openBtn.ToolTip = "打开 WDM2VST Ultra 采集插件的界面，手动确认/选择端口（" +
                          (which == 3 ? Wdm2VstBridge.PlayC : Wdm2VstBridge.PlayD) + "）";
        openBtn.Click += (_, _) =>
        {
            var p = which == 3 ? _engine.SendPluginC : _engine.SendPluginD;
            if (p == null)
            {
                MessageBox.Show(this, "虚拟输入插件未加载（WDM2VST 未检测到或引擎未启动），无法打开界面。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenEditor(p);
        };
        host.Children.Add(openBtn);
        Grid.SetColumn(openBtn, 4);

        // 电平推子
        var fader = new MacMeteredFader();
        fader.Db = DbConv.ToDb(which == 3 ? _engine.SendVolumeC : _engine.SendVolumeD);
        fader.ValueChanged += db =>
        {
            if (which == 3) _engine.SendVolumeC = (float)DbConv.ToLinear(db);
            else _engine.SendVolumeD = (float)DbConv.ToLinear(db);
            SavePreset();
        };
        host.Children.Add(fader);
        Grid.SetColumn(fader, 6);

        if (which == 3) _faderC = fader; else _faderD = fader;
    }

    /// <summary>双击 C/D 胶囊标签 → 就地变输入框改名，回车/失焦提交，Esc 取消。</summary>
    private void BeginAuxRename(int which, Grid capsule, TextBlock label)
    {
        if (capsule.Children.OfType<TextBox>().Any()) return; // 已在编辑
        bool isC = which == 3;
        string old = isC ? _auxCName : _auxDName;
        var box = new TextBox
        {
            Text = old,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(3, 1, 3, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            TextAlignment = TextAlignment.Center,
        };
        capsule.Children.Remove(label);
        capsule.Children.Add(box);
        box.Focus();
        box.SelectAll();

        void EndEdit(bool commit)
        {
            if (!capsule.Children.Contains(box)) return;
            capsule.Children.Remove(box);
            capsule.Children.Add(label);
            if (commit)
            {
                var t = box.Text.Trim();
                if (t.Length == 0) t = old;
                if (t.Length > 8) t = t[..8];
                if (isC) _auxCName = t; else _auxDName = t;
                label.Text = t;
                SavePreset();
            }
        }

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter) EndEdit(true);
            else if (ke.Key == Key.Escape) EndEdit(false);
        };
        box.LostFocus += (_, _) => EndEdit(true);
    }

    private static Rectangle MakeDashedRect(double radius)
    {
        var r = new Rectangle
        {
            RadiusX = radius,
            RadiusY = radius,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
        };
        r.SetResourceReference(Rectangle.StrokeProperty, "SepBrush");
        r.SetResourceReference(Rectangle.FillProperty, "FieldBrush");
        return r;
    }

    // ---------------- 主控 / 电平表 ----------------

    private void BuildMeters()
    {
        _inMeter = new MacVerticalMeter("IN") { Margin = new Thickness(4, 0, 4, 0) };
        _outMeter = new MacVerticalMeter("OUT") { Margin = new Thickness(4, 0, 4, 0) };
        MetersHost.Children.Add(_inMeter);
        MetersHost.Children.Add(_outMeter);
    }

    private void BuildMasterFader()
    {
        _masterFader = new MacVerticalFader();
        _masterFader.Db = DbConv.ToDb(_engine.MasterGain);
        _masterFader.ValueChanged += db =>
        {
            _engine.MasterGain = (float)DbConv.ToLinear(db);
            SavePreset();
        };
        MasterFaderHost.Children.Add(_masterFader);
    }

    private void UpdateMeters()
    {
        _inMeter?.SetLevel(_engine.InputLevel);
        _outMeter?.SetLevel(Math.Max(_engine.OutputLevelL, _engine.OutputLevelR));
        _faderC?.SetLevel(_engine.LevelC);
        _faderD?.SetLevel(_engine.LevelD);

        if (!string.IsNullOrEmpty(_engine.Wdm2vstStatus) && Wdm2vstHint.Text != _engine.Wdm2vstStatus)
            Wdm2vstHint.Text = _engine.Wdm2vstStatus;
        BtnMasterOut.Visibility = _engine.MasterOutPlugin != null ? Visibility.Visible : Visibility.Collapsed;

        if (_engine.IsRunning)
        {
            string info = $"{_engine.SampleRate / 1000.0:0.#} kHz · {_engine.FramesPerBuffer} 帧";
            if (_engine.IsRecording) info += " · ● 录音中";
            InfoText.Text = info;
        }
        else if (InfoText.Text.Length > 0 && !_engine.IsRunning && InfoText.Text.Contains("kHz"))
        {
            InfoText.Text = "";
        }
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        _engine.MicMuted = !_engine.MicMuted;
        UpdateMuteUi();
        SavePreset();
    }

    private void UpdateMuteUi()
    {
        BtnMute.Content = _engine.MicMuted ? "🔇" : "🎙";
        BtnMute.Style = _engine.MicMuted
            ? (Style)FindResource("DangerButton")
            : (Style)FindResource(typeof(Button));
        BtnMute.ToolTip = _engine.MicMuted ? "开麦（恢复话筒输入）" : "闭麦（静音话筒输入）";
    }

    // ---------------- 引擎启停 / 录音 ----------------

    private string? SelectedDriver =>
        AsioComboSelectedIndexValid() ? _asioDrivers[AsioDriverCombo.SelectedIndex] : null;

    private bool AsioComboSelectedIndexValid() =>
        AsioDriverCombo.SelectedIndex >= 0 && AsioDriverCombo.SelectedIndex < _asioDrivers.Length;

    /// <summary>按界面当前选择启动引擎。</summary>
    private void StartEngine(string driver)
    {
        _engine.InputChannelOffset = Math.Max(0, InputChannelCombo.SelectedIndex);
        _engine.OutputChannelOffset = Math.Max(0, OutputChannelCombo.SelectedIndex);
        _engine.DesiredSampleRate = SelectedSampleRate;
        _engine.Start(driver);
        _presetDriver = driver;   // 记住最近一次使用的驱动，供预设保存
        BufferText.Text = $"{_engine.FramesPerBuffer} 帧";
    }

    private int SelectedSampleRate
    {
        get
        {
            if (SampleRateCombo.SelectedItem is ComboBoxItem item && item.Tag is int r) return r;
            return 0;
        }
    }

    /// <summary>设备 / 通道 / 采样率变化时，运行中则自动重启引擎。</summary>
    private void RestartEngineIfRunning()
    {
        if (!_engine.IsRunning) { SavePreset(); return; }
        var driver = SelectedDriver ?? _engine.AsioDriverName;
        _engine.Stop();
        if (driver != null)
        {
            try { StartEngine(driver); }
            catch (Exception ex) { ShowEngineError(ex); }
        }
        UpdateStartStopUi();
        SavePreset();
    }

    private void BtnStartStop_Click(object? sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            UpdateStartStopUi();
            SavePreset();
            return;
        }

        var driver = SelectedDriver;
        if (driver == null)
        {
            MessageBox.Show(this, "请先选择 ASIO 声卡。", "提示");
            return;
        }
        try { StartEngine(driver); }
        catch (Exception ex) { ShowEngineError(ex); }
        UpdateStartStopUi();
        SavePreset();
    }

    private void ShowEngineError(Exception ex)
    {
        StatusText.Text = "错误";
        StatusText.Foreground = (Brush)FindResource("RedBrush");
        MessageBox.Show(this, "音频引擎启动失败：\n" + ex.Message +
            "\n\n请检查：ASIO 声卡是否被其他软件占用、通道选择是否超出范围。",
            "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void UpdateStartStopUi()
    {
        bool running = _engine.IsRunning;
        BtnStartStop.Content = running ? "■" : "▶";
        BtnStartStop.Style = (Style)FindResource(running ? "DangerButton" : "AccentButton");
        BtnStartStop.ToolTip = running ? "停止（空格）" : "启动（空格）";
        StatusText.Text = running ? "Running" : "Stopped";
        if (!running)
        {
            StatusText.Foreground = (Brush)FindResource("SecondaryBrush");
            if (InfoText.Text.Contains("kHz")) InfoText.Text = "";
        }
        else
        {
            StatusText.Foreground = (Brush)FindResource("SecondaryBrush");
        }
        StatusDot.SetResourceReference(Ellipse.FillProperty, running ? "GreenBrush" : "TertiaryBrush");
    }

    private void BtnRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsRunning)
        {
            MessageBox.Show(this, "请先启动引擎再录音。", "提示");
            return;
        }
        if (_engine.IsRecording)
        {
            var path = _engine.StopRecording();
            BtnRecord.Content = "● 录音";
            BtnRecord.Style = (Style)FindResource("AccentButton");
            BtnRecord.ToolTip = "开始录音";
            if (path != null) InfoText.Text = "录音已保存：" + System.IO.Path.GetFileName(path);
        }
        else
        {
            var path = _engine.StartRecording();
            if (path == null)
                MessageBox.Show(this, "无法开始录音：" + _engine.LastError, "提示");
            else
            {
                BtnRecord.Content = "■ 停止";
                BtnRecord.Style = (Style)FindResource("DangerButton");
                BtnRecord.ToolTip = "停止录音";
            }
        }
    }

    private void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = _engine.RecordingsDir, UseShellExecute = true }); }
        catch { }
    }

    // ---------------- 设备栏 ----------------

    private void RefreshAsioDrivers()
    {
        _suppressComboEvents = true;
        try
        {
            AsioDriverCombo.Items.Clear();
            _asioDrivers = AudioEngine.GetAsioDrivers();
            if (_asioDrivers.Length == 0)
            {
                AsioDriverCombo.Items.Add("（未找到 ASIO 声卡）");
                AsioDriverCombo.SelectedIndex = 0;
                AsioDriverCombo.IsEnabled = false;
                OutputDriverCombo.Items.Clear();
                OutputDriverCombo.Items.Add("—");
                OutputDriverCombo.SelectedIndex = 0;
                return;
            }
            AsioDriverCombo.IsEnabled = true;
            foreach (var d in _asioDrivers) AsioDriverCombo.Items.Add(d);
            int idx = Array.IndexOf(_asioDrivers, _engine.AsioDriverName ?? _presetDriver);
            AsioDriverCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        finally
        {
            _suppressComboEvents = false;
        }
        RefreshChannelsAndFormats();
    }

    /// <summary>枚举所选驱动的通道对与采样率（引擎运行中跳过，避免驱动重入）。</summary>
    private void RefreshChannelsAndFormats()
    {
        if (!AsioComboSelectedIndexValid()) return;
        string driver = _asioDrivers[AsioDriverCombo.SelectedIndex];

        // 镜像输出设备显示
        OutputDriverCombo.Items.Clear();
        OutputDriverCombo.Items.Add(driver);
        OutputDriverCombo.SelectedIndex = 0;

        _suppressComboEvents = true;
        try
        {
            try
            {
                var (ins, outs) = AudioEngine.GetChannelNames(driver);
                InputChannelCombo.Items.Clear();
                for (int i = 0; i + 1 < ins.Length; i += 2)
                {
                    string n1 = string.IsNullOrWhiteSpace(ins[i]) ? $"{i + 1}" : ins[i];
                    string n2 = string.IsNullOrWhiteSpace(ins[i + 1]) ? $"{i + 2}" : ins[i + 1];
                    InputChannelCombo.Items.Add($"{i + 1}-{i + 2} · {n1}/{n2}");
                }
                OutputChannelCombo.Items.Clear();
                for (int i = 0; i + 1 < outs.Length; i += 2)
                {
                    string n1 = string.IsNullOrWhiteSpace(outs[i]) ? $"{i + 1}" : outs[i];
                    string n2 = string.IsNullOrWhiteSpace(outs[i + 1]) ? $"{i + 2}" : outs[i + 1];
                    OutputChannelCombo.Items.Add($"{i + 1}-{i + 2} · {n1}/{n2}");
                }
                InputChannelCombo.SelectedIndex = Math.Min(_engine.InputChannelOffset,
                    Math.Max(0, InputChannelCombo.Items.Count - 1));
                OutputChannelCombo.SelectedIndex = Math.Min(_engine.OutputChannelOffset,
                    Math.Max(0, OutputChannelCombo.Items.Count - 1));

                // 采样率
                SampleRateCombo.Items.Clear();
                SampleRateCombo.Items.Add(new ComboBoxItem { Content = "自动", Tag = 0 });
                var rates = _engine.IsRunning
                    ? new List<int>()
                    : AudioEngine.GetSupportedSampleRates(driver);
                foreach (var r in rates)
                {
                    double k = r / 1000.0;
                    SampleRateCombo.Items.Add(new ComboBoxItem
                    {
                        Content = k >= 1 ? $"{k:0.#} kHz" : $"{r} Hz",
                        Tag = r,
                    });
                }
                int sel = 0;
                for (int i = 0; i < SampleRateCombo.Items.Count; i++)
                    if (SampleRateCombo.Items[i] is ComboBoxItem ci && ci.Tag is int rate && rate == _engine.DesiredSampleRate)
                    { sel = i; break; }
                SampleRateCombo.SelectedIndex = sel;
            }
            catch (Exception ex)
            {
                InputChannelCombo.Items.Clear();
                InputChannelCombo.Items.Add("（通道枚举失败）");
                InputChannelCombo.SelectedIndex = 0;
                OutputChannelCombo.Items.Clear();
                OutputChannelCombo.Items.Add("（通道枚举失败）");
                OutputChannelCombo.SelectedIndex = 0;
                InfoText.Text = "通道枚举失败：" + ex.Message;
            }
        }
        finally
        {
            _suppressComboEvents = false;
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        try { _engine.EnsureWdm2vstPlugins(); } catch { }   // 新装 WDM2VST 后点刷新即可生效
        RefreshAsioDrivers();
        if (!_engine.IsRunning)
            RefreshChannelsAndFormats();
    }

    private void AsioDriver_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (AsioDriverCombo.SelectedIndex >= 0)
        {
            _engine.InputChannelOffset = 0;
            _engine.OutputChannelOffset = 0;
        }
        if (!_engine.IsRunning)
        {
            RefreshChannelsAndFormats();
        }
        else
        {
            // 换声卡：立即重启
            RestartEngineIfRunning();
        }
        SavePreset();
    }

    private void InputChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        _engine.InputChannelOffset = Math.Max(0, InputChannelCombo.SelectedIndex);
        RestartEngineIfRunning();
    }

    private void OutputChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        _engine.OutputChannelOffset = Math.Max(0, OutputChannelCombo.SelectedIndex);
        RestartEngineIfRunning();
    }

    private void SampleRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        _engine.DesiredSampleRate = SelectedSampleRate;
        RestartEngineIfRunning();
    }

    // ---------------- 预设文件（加载 / 保存）----------------

    private void BtnLoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "加载预设",
            Filter = "XSD 机架预设|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var preset = JsonSerializer.Deserialize<RackPreset>(File.ReadAllText(dlg.FileName));
            if (preset == null) return;
            RestorePresetIntoUi(preset);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "加载预设失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnSavePreset_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存预设",
            Filter = "XSD 机架预设|*.json",
            FileName = "XSDRackPreset.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // 保存前确保虚拟输入/总输出插件实例存在（引擎没启动过也能存到它们的设置）
            _engine.EnsureWdm2vstPlugins();
            var preset = RackPreset.Capture(_engine, _showDevicePanel, _muteHotkey);
            preset.AsioDriverName = _engine.AsioDriverName ?? _presetDriver;
            preset.AuxCName = _auxCName;
            preset.AuxDName = _auxDName;
            File.WriteAllText(dlg.FileName,
                JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "保存预设失败：" + ex.Message, "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- 主题 / 侧栏 ----------------

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
        => ApplyTheme(ThemeManager.Current == ThemeManager.Dark ? ThemeManager.Light : ThemeManager.Dark);

    private void ApplyTheme(string theme)
    {
        ThemeManager.Apply(theme);
        BtnTheme.Content = theme == ThemeManager.Dark ? "☀️" : "🌙";
        SavePreset();
    }

    private void BtnSidebar_Click(object sender, RoutedEventArgs e)
    {
        _showDevicePanel = !_showDevicePanel;
        ApplyDevicePanelVisibility();
        SavePreset();
    }

    private void ApplyDevicePanelVisibility()
    {
        // 隐藏右栏：中栏宽度不变（565），窗口整体收窄，避免右侧留白
        DeviceCol.Width = new GridLength(_showDevicePanel ? 350 : 0);
        DeviceSep.Visibility = _showDevicePanel ? Visibility.Visible : Visibility.Collapsed;
        BtnSidebar.Opacity = _showDevicePanel ? 1.0 : 0.5;
        Width = _showDevicePanel ? 1010 : 660;
    }

    // ---------------- 全局静音快捷键 ----------------

    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = (HwndSource)PresentationSource.FromVisual(this);
        _hwnd.AddHook(WndProc);
        RegisterMuteHotkey();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt64() == 1)
        {
            _engine.MicMuted = !_engine.MicMuted;
            UpdateMuteUi();
            SavePreset();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RegisterMuteHotkey()
    {
        if (_hwnd == null) return;
        UnregisterHotKey(_hwnd.Handle, 1);
        if (string.IsNullOrEmpty(_muteHotkey)) return;

        uint mods = 0;
        uint vk = 0;
        foreach (var part in _muteHotkey.Split('+'))
        {
            switch (part)
            {
                case "Ctrl": mods |= ModControl; break;
                case "Alt": mods |= ModAlt; break;
                case "Shift": mods |= ModShift; break;
                case "Win": mods |= ModWin; break;
                default:
                    if (Enum.TryParse<Key>(part, out var key))
                        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        if (vk == 0) return;
        if (!RegisterHotKey(_hwnd.Handle, 1, mods, vk))
            InfoText.Text = "快捷键注册失败（可能被其他软件占用）";
    }

    private void UpdateHotkeyUi()
    {
        HotkeyButton.Content = _capturingHotkey
            ? "按下组合键…（Esc 取消）"
            : (string.IsNullOrEmpty(_muteHotkey) ? "点击录制…" : _muteHotkey);
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        UpdateHotkeyUi();
    }

    private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = false;
        _muteHotkey = null;
        if (_hwnd != null) UnregisterHotKey(_hwnd.Handle, 1);
        UpdateHotkeyUi();
        SavePreset();
    }

    private void HandleHotkeyCapture(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _capturingHotkey = false;
            UpdateHotkeyUi();
            e.Handled = true;
            return;
        }
        // 忽略纯修饰键
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return;

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            InfoText.Text = "快捷键需包含 Ctrl / Alt / Shift";
            return;
        }

        string name = "";
        if (mods.HasFlag(ModifierKeys.Control)) name += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt)) name += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift)) name += "Shift+";
        if (mods.HasFlag(ModifierKeys.Windows)) name += "Win+";
        name += key.ToString();

        _muteHotkey = name;
        _capturingHotkey = false;
        RegisterMuteHotkey();
        UpdateHotkeyUi();
        SavePreset();
        e.Handled = true;
    }

    // ---------------- 键盘 ----------------

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey) { HandleHotkeyCapture(e); return; }
        if (e.Key == Key.Space && e.OriginalSource is not TextBox && e.OriginalSource is not ComboBox)
        {
            BtnStartStop_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ---------------- 授权 ----------------

    private void CheckLicense()
    {
        var (valid, message, _) = LicenseManager.CheckLocalLicense(DateTime.UtcNow);
        if (valid) return;
        var (trialOk, _) = LicenseManager.CheckTrial(DateTime.UtcNow);
        if (trialOk) return;   // 三天试用期内：继续正常使用

        // 授权失效：停引擎、停录音，重新弹激活窗
        _engine.Stop();
        UpdateStartStopUi();
        _licenseTimer.Stop();

        var win = new ActivationWindow(message);
        if (win.ShowDialog() != true)
        {
            Application.Current.Shutdown();
            return;
        }
        _licenseTimer.Start();
    }

    // ---------------- 退出 ----------------

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _meterTimer.Stop();
        _licenseTimer.Stop();
        if (_hwnd != null) UnregisterHotKey(_hwnd.Handle, 1);
        SavePreset();
        _engine.Dispose();
    }
}
