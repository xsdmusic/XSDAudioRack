using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using Rectangle = System.Windows.Shapes.Rectangle;

namespace XSDAudioRack.Ui;

/// <summary>dB ↔ 线性增益换算（-60 dB 视为静音）。</summary>
public static class DbConv
{
    public static double ToDb(double linear)
        => linear <= 0.001 ? -60 : Math.Clamp(20 * Math.Log10(linear), -60, 12);

    public static double ToLinear(double db)
        => db <= -60 ? 0.001 : Math.Pow(10, db / 20);
}

/// <summary>
/// 苹果版 VerticalLevelMeter 的复刻：竖条电平表（峰值保持 + 峰值 dB 读数 + 底部标题）。
/// SetLevel(linear 0..1+) 由 UI 定时器调用。
/// </summary>
public sealed class MacVerticalMeter : Grid
{
    private readonly TextBlock _peakText = new() { FontSize = 9, FontFamily = new FontFamily("Consolas") };
    private readonly Rectangle _level = new();
    private readonly Rectangle _peakLine = new() { Height = 2, RadiusX = 1, RadiusY = 1 };
    private readonly Grid _barInner = new();
    private double _peakDb = -60;
    private double _lastRatio;

    public MacVerticalMeter(string title, double width = 22)
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(13) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });

        _peakText.TextAlignment = TextAlignment.Center;
        _peakText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        Children.Add(_peakText);
        SetRow(_peakText, 0);

        var track = new Border
        {
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = width,
        };
        track.SetResourceReference(Border.BackgroundProperty, "FieldBrush");
        track.SetResourceReference(Border.BorderBrushProperty, "SepBrush");
        track.BorderThickness = new Thickness(0.5);
        _barInner = new Grid();
        track.Child = _barInner;

        _level.Fill = (Brush)Application.Current.FindResource("MeterVertBrush");
        _level.RadiusX = 6; _level.RadiusY = 6;
        _level.VerticalAlignment = VerticalAlignment.Bottom;
        _level.HorizontalAlignment = HorizontalAlignment.Stretch;
        _barInner.Children.Add(_level);

        _peakLine.SetResourceReference(Shape.FillProperty, "PeakLineBrush");
        _peakLine.VerticalAlignment = VerticalAlignment.Bottom;
        _barInner.Children.Add(_peakLine);

        Children.Add(track);
        SetRow(track, 1);

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        Children.Add(titleText);
        SetRow(titleText, 2);
    }

    /// <summary>linear: 0..1+ 线性峰值电平。</summary>
    public void SetLevel(float linear)
    {
        double db = DbConv.ToDb(linear);
        _peakDb = Math.Max(_peakDb - 0.4, db); // 峰值缓慢回落
        _peakText.Text = _peakDb <= -59.9 ? "-60" : _peakDb.ToString("0.0");

        double h = _barInner.ActualHeight;
        if (h > 5)
        {
            double ratio = Math.Clamp((db + 60) / 60, 0, 1);
            _lastRatio = ratio;
            _level.Height = h * ratio;
            double pr = Math.Clamp((_peakDb + 60) / 60, 0, 1);
            _peakLine.Margin = new Thickness(0, 0, 0, Math.Max(0, h * pr - 1));
        }
    }
}

/// <summary>
/// 苹果版 VerticalVolumeSlider 的复刻：主音量竖推子。
/// -60..+12 dB，-30 dB 处 0.3 分割；3 dB 刻度、15 dB 主刻度、0 dB 强调；双击归零；滚轮 ±1 dB。
/// </summary>
public sealed class MacVerticalFader : Grid
{
    public const double MinDb = -60, MaxDb = 12, SplitDb = -30;
    private const double SplitRatio = 0.3, WheelStepDb = 1.0;

    public event Action<double>? ValueChanged;

    private readonly TextBlock _valueText = new()
    {
        FontSize = 13,
        FontFamily = new FontFamily("Consolas"),
        TextAlignment = TextAlignment.Center,
        Height = 18,
    };
    private readonly Canvas _canvas = new();
    private readonly Rectangle _track = new() { Width = 6, RadiusX = 3, RadiusY = 3 };
    private readonly Rectangle _fill = new() { Width = 6, RadiusX = 3, RadiusY = 3 };
    private readonly Border _thumb = new()
    {
        Width = 22, Height = 15, CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
    };

    private double _db;
    private bool _dragging;

    public double Db
    {
        get => _db;
        set { _db = Math.Clamp(value, MinDb, MaxDb); Refresh(); }
    }

    public MacVerticalFader()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _valueText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        Children.Add(_valueText);
        SetRow(_valueText, 0);

        _canvas.ClipToBounds = true;
        Children.Add(_canvas);
        SetRow(_canvas, 1);

        _track.VerticalAlignment = VerticalAlignment.Stretch;
        _track.HorizontalAlignment = HorizontalAlignment.Center;
        _track.SetResourceReference(Shape.FillProperty, "FieldBrush");
        _track.SetResourceReference(Shape.StrokeProperty, "SepBrush");
        _track.StrokeThickness = 0.5;
        _canvas.Children.Add(_track);

        _fill.VerticalAlignment = VerticalAlignment.Bottom;
        _fill.HorizontalAlignment = HorizontalAlignment.Center;
        _fill.SetResourceReference(Shape.FillProperty, "AccentBrush");
        _canvas.Children.Add(_fill);

        _thumb.SetResourceReference(Border.BackgroundProperty, "KnobBrush");
        _thumb.SetResourceReference(Border.BorderBrushProperty, "SepBrush");
        _canvas.Children.Add(_thumb);

        _canvas.SizeChanged += (_, _) => Refresh();
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;
        _canvas.MouseWheel += OnWheel;
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Db = 0; ValueChanged?.Invoke(_db); }
        };
        Refresh();
    }

    private static double RatioFor(double db)
    {
        if (db <= SplitDb)
            return Math.Clamp((db - MinDb) / (SplitDb - MinDb), 0, 1) * SplitRatio;
        return SplitRatio + Math.Clamp((db - SplitDb) / (MaxDb - SplitDb), 0, 1) * (1 - SplitRatio);
    }

    private static double DbFor(double ratio)
    {
        if (ratio <= SplitRatio)
            return MinDb + ratio / SplitRatio * (SplitDb - MinDb);
        return SplitDb + (ratio - SplitRatio) / (1 - SplitRatio) * (MaxDb - SplitDb);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _canvas.CaptureMouse();
        UpdateFromY(e.GetPosition(_canvas).Y);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragging) UpdateFromY(e.GetPosition(_canvas).Y);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _canvas.ReleaseMouseCapture();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Db = _db + (e.Delta > 0 ? WheelStepDb : -WheelStepDb);
        ValueChanged?.Invoke(_db);
        e.Handled = true;
    }

    private void UpdateFromY(double y)
    {
        double h = _canvas.ActualHeight;
        if (h <= 0) return;
        Db = DbFor(1 - Math.Clamp(y / h, 0, 1));
        ValueChanged?.Invoke(_db);
    }

    private void Refresh()
    {
        double h = _canvas.ActualHeight;
        _valueText.Text = _db <= -59.9 ? "-60" : _db > 0 ? "+" + _db.ToString("0.0") : _db.ToString("0.0");
        if (h > 10)
        {
            // 刻度（6 → -60，每 3 dB；15 的倍数为主刻度，0 dB 强调）
            for (int i = _canvas.Children.Count - 1; i >= 3; i--)
                _canvas.Children.RemoveAt(i);

            for (double t = 6; t >= -60; t -= 3)
            {
                bool isZero = Math.Abs(t) < 0.01;
                bool isMajor = Math.Abs(t % 15) < 0.01;
                double y = h * (1 - RatioFor(t));
                var left = new Rectangle
                {
                    Width = isZero ? 14 : isMajor ? 8 : 5,
                    Height = isZero ? 1.5 : 1,
                };
                left.SetResourceReference(Shape.FillProperty, isZero ? "TextBrush" : "SepBrush");
                Canvas.SetLeft(left, (_canvas.ActualWidth - 34) / 2 - left.Width);
                Canvas.SetTop(left, y);
                var right = new Rectangle
                {
                    Width = isZero ? 14 : isMajor ? 8 : 5,
                    Height = isZero ? 1.5 : 1,
                };
                right.SetResourceReference(Shape.FillProperty, isZero ? "TextBrush" : "SepBrush");
                Canvas.SetLeft(right, (_canvas.ActualWidth - 34) / 2 + 34);
                Canvas.SetTop(right, y);
                _canvas.Children.Add(left);
                _canvas.Children.Add(right);
            }

            double r = RatioFor(_db);
            _fill.Height = h * r;
            Canvas.SetLeft(_track, (_canvas.ActualWidth - 6) / 2);
            Canvas.SetLeft(_fill, (_canvas.ActualWidth - 6) / 2);
            Canvas.SetLeft(_thumb, (_canvas.ActualWidth - 22) / 2);
            Canvas.SetTop(_thumb, Math.Clamp(h * (1 - r) - _thumb.Height / 2, 0, h - _thumb.Height));
        }
    }
}

/// <summary>
/// 苹果版 MeteredFader 的复刻：横版带电平推子（白底虚线轨道 + 实时电平 + 刻度 + 推拉键 + 跟随数值）。
/// 用于虚拟输入 C/D。双击归零，滚轮 0.5 dB，拖动灵敏度 0.3 dB/px。
/// </summary>
public sealed class MacMeteredFader : Grid
{
    private const double MinDb = -60, MaxDb = 12, WheelStepDb = 0.5, DragSensitivity = 0.3;
    private static readonly double[] Ticks = { 12, 0, -15, -30, -45, -60 };

    public event Action<double>? ValueChanged;

    private readonly Rectangle _track = new() { RadiusX = 6, RadiusY = 6 };
    private readonly Rectangle _level = new() { RadiusX = 3, RadiusY = 3 };
    private readonly Border _knob = new()
    {
        Width = 14, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly TextBlock _valueText = new()
    {
        FontSize = 10, FontFamily = new FontFamily("Consolas"),
        VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
        IsHitTestVisible = false,
    };

    private double _db;
    private double _dragStartDb;
    private double _downX;
    private bool _dragging;

    public double Db
    {
        get => _db;
        set { _db = Quantize(Math.Clamp(value, MinDb, MaxDb)); Refresh(); }
    }

    public MacMeteredFader()
    {
        _track.SetResourceReference(Shape.FillProperty, "FieldBrush");
        _track.SetResourceReference(Shape.StrokeProperty, "SepBrush");
        _track.StrokeThickness = 1;
        _track.StrokeDashArray = new DoubleCollection { 3, 2 };
        Children.Add(_track);

        _level.Fill = (Brush)Application.Current.FindResource("MeterHorizBrush");
        _level.VerticalAlignment = VerticalAlignment.Center;
        _level.HorizontalAlignment = HorizontalAlignment.Left;
        _level.Margin = new Thickness(2, 0, 0, 0);
        Children.Add(_level);

        _knob.SetResourceReference(Border.BackgroundProperty, "KnobBrush");
        _knob.SetResourceReference(Border.BorderBrushProperty, "SepBrush");
        Children.Add(_knob);

        _valueText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        _valueText.SetResourceReference(TextBlock.BackgroundProperty, "FieldBrush");
        Children.Add(_valueText);

        SizeChanged += (_, _) => Refresh();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Db = 0; ValueChanged?.Invoke(_db); }
        };
        Refresh();
    }

    private static double Quantize(double v) => Math.Round(v * 2) / 2;

    private double Ratio(double db) => Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0, 1);

    /// <summary>linear: 0..1+ 实时电平。</summary>
    public void SetLevel(float linear)
    {
        double db = DbConv.ToDb(linear);
        double w = ActualWidth;
        if (w > 10) _level.Width = Math.Max(0, (w - 4) * Ratio(db));
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStartDb = _db;
        _downX = e.GetPosition(this).X;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        // 与 Mac 相同：相对位移 × 灵敏度（0.3 dB/px）
        double delta = (e.GetPosition(this).X - _downX) * DragSensitivity;
        Db = _dragStartDb + delta;
        ValueChanged?.Invoke(_db);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Db = _db + (e.Delta > 0 ? WheelStepDb : -WheelStepDb);
        ValueChanged?.Invoke(_db);
        e.Handled = true;
    }

    private void Refresh()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;

        // 轨道 + 电平高度
        _track.Width = w; _track.Height = h;
        _level.Height = h - 8;
        double knobX = Math.Clamp((w - _knob.Width) * Ratio(_db), 2, w - _knob.Width - 2);
        _knob.Height = h - 6;
        _knob.Margin = new Thickness(knobX, 0, 0, 0);
        _valueText.Margin = new Thickness(Math.Max(0, knobX + _knob.Width / 2 - 18), 1, 0, 0);
        _valueText.Text = _db <= -59.9 ? "-60" : (_db > 0 ? "+" : "") + _db.ToString("0.0");

        // 刻度
        for (int i = Children.Count - 1; i >= 4; i--)
            Children.RemoveAt(i);
        foreach (double t in Ticks)
        {
            bool isZero = Math.Abs(t) < 0.01;
            var tick = new Rectangle { Width = isZero ? 1.5 : 1, Height = isZero ? 8 : 4 };
            tick.SetResourceReference(Shape.FillProperty, isZero ? "TextBrush" : "SepBrush");
            tick.VerticalAlignment = VerticalAlignment.Center;
            tick.HorizontalAlignment = HorizontalAlignment.Left;
            tick.Margin = new Thickness(w * Ratio(t) - (isZero ? 0.75 : 0.5), 0, 0, 0);
            Children.Add(tick);
        }
    }
}

/// <summary>
/// 苹果版 SendVolumeKnob 的复刻：圆形小旋钮，中心显示 dB 值；
/// 上下拖动调节（约 400px 走完全量程），双击回 -6 dB，滚轮 ±1 dB。
/// </summary>
public sealed class MacKnob : Grid
{
    private const double MinDb = -60, MaxDb = 12;

    public event Action<double>? ValueChanged;

    private readonly Ellipse _circle = new();
    private readonly TextBlock _valueText = new()
    {
        FontSize = 10, FontFamily = new FontFamily("Consolas"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private double _db;
    private double _dragStartDb, _dragStartY;
    private bool _dragging;

    public double Db
    {
        get => _db;
        set { _db = Math.Clamp(value, MinDb, MaxDb); Refresh(); }
    }

    public MacKnob(double size = 34)
    {
        Width = size; Height = size;

        _circle.SetResourceReference(Shape.FillProperty, "CardBrush");
        _circle.SetResourceReference(Shape.StrokeProperty, "SepBrush");
        _circle.StrokeThickness = 1;
        Children.Add(_circle);

        _valueText.SetResourceReference(TextBlock.ForegroundProperty, "BrandBrush");
        Children.Add(_valueText);

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseWheel += OnWheel;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Db = -6; ValueChanged?.Invoke(_db); }
        };
        Refresh();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _dragStartDb = _db;
        _dragStartY = e.GetPosition(this).Y;
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        double dy = e.GetPosition(this).Y - _dragStartY;
        Db = _dragStartDb - dy / 400.0 * (MaxDb - MinDb);
        ValueChanged?.Invoke(_db);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Db = _db + (e.Delta > 0 ? 1 : -1);
        ValueChanged?.Invoke(_db);
        e.Handled = true;
    }

    private void Refresh()
    {
        _valueText.Text = _db <= -59.9 ? "-60" : (_db > 0 ? "+" : "") + _db.ToString("0");
    }
}
