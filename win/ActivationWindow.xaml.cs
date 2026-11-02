using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using XSDAudioRack.License;

namespace XSDAudioRack;

/// <summary>
/// 激活窗口：打开即创建 ¥9.9 终身订单并显示支付宝付款二维码，
/// 付款成功（服务器自动发码）→ 本地验签 → 激活进软件。
/// 另保留手动激活码入口（服务器不可用/客服兜底）。
/// 激活成功 → DialogResult = true；用户选择退出 → DialogResult = false（程序随后关闭）。
/// </summary>
public partial class ActivationWindow : Window
{
    private readonly string _machineCode;
    private bool _activated;

    private string? _orderId;
    private readonly DispatcherTimer _pollTimer;
    private bool _polling;
    private DateTime _pollStartedUtc;
    private CancellationTokenSource? _pollCts;

    public ActivationWindow(string? reason)
    {
        InitializeComponent();
        _machineCode = LicenseManager.ComputeMachineCode();
        TbMachineCode.Text = LicenseManager.FormatGroups(_machineCode, 4);
        if (!string.IsNullOrEmpty(reason))
            TxtReason.Text = reason;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += PollTimer_Tick;

        Loaded += async (_, _) => await StartLifeOrderAsync();
        Closed += (_, _) => { _pollTimer.Stop(); _pollCts?.Cancel(); };
    }

    public new bool Activated => _activated;

    // ================= 扫码付款流程 =================

    private async Task StartLifeOrderAsync()
    {
        if (_activated) return;
        BtnRetryQr.Visibility = Visibility.Collapsed;
        TxtQrState.Text = "正在获取付款二维码…";
        QrLoading.Visibility = Visibility.Visible;
        ImgQr.Visibility = Visibility.Collapsed;

        _pollTimer.Stop();
        _pollCts?.Cancel();

        try
        {
            _pollCts = new CancellationTokenSource();
            var ct = _pollCts.Token;
            var order = await PayClient.CreateLifeOrderAsync(_machineCode, ct);
            _orderId = order.OrderId;

            ImgQr.Source = RenderQr(order.QrCode ?? "");
            ImgQr.Visibility = Visibility.Visible;
            QrLoading.Visibility = Visibility.Collapsed;

            TxtPayStatus.Text = order.Mock
                ? "【联调模式】打开手机支付宝扫一扫，付款后自动激活"
                : "打开手机支付宝扫一扫，付款后自动激活";

            _pollStartedUtc = DateTime.UtcNow;
            _pollTimer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _orderId = null;
            TxtQrState.Text = "获取付款二维码失败：\n" + ex.Message;
            BtnRetryQr.Visibility = Visibility.Visible;
            QrLoading.Visibility = Visibility.Visible;
            TxtPayStatus.Text = $"检查网络后重试；也可以微信联系 {PayClient.ContactWeChat} 手动激活。";
        }
    }

    private static BitmapImage RenderQr(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var img = new BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_polling || _activated || _orderId is null) return;
        _polling = true;
        try
        {
            // 二维码有效期 15 分钟，超时停轮询引导重下
            if (DateTime.UtcNow - _pollStartedUtc > TimeSpan.FromMinutes(15))
            {
                _pollTimer.Stop();
                TxtPayStatus.Text = "二维码已过期，请点击二维码下方「重新获取」。";
                TxtQrState.Text = "二维码已过期";
                BtnRetryQr.Visibility = Visibility.Visible;
                QrLoading.Visibility = Visibility.Visible;
                ImgQr.Visibility = Visibility.Collapsed;
                return;
            }

            var status = await PayClient.GetOrderAsync(_orderId, _pollCts?.Token ?? CancellationToken.None);
            if (status.Status == "paid" && !string.IsNullOrWhiteSpace(status.ActivationCode))
            {
                _pollTimer.Stop();
                CompleteActivation(status.ActivationCode);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* 网络抖动：下个周期重试 */ }
        finally
        {
            _polling = false;
        }
    }

    private void CompleteActivation(string code)
    {
        var (err, expiry) = LicenseManager.ValidateActivationCode(code, _machineCode, DateTime.UtcNow);
        if (err != LicenseManager.LicenseError.None)
        {
            TxtResult.Text = err == LicenseManager.LicenseError.MachineMismatch
                ? "服务器返回的激活码与本机不匹配，请截图联系客服处理。"
                : "服务器返回的激活码无效，请联系客服处理。";
            return;
        }

        LicenseManager.SaveLocalLicense(code);
        TxtPayStatus.Text = "";
        TxtResult.Text = $"✅ 激活成功！终身授权已生效（本机专用）。";
        TxtResult.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4c, 0xc3, 0x8a));
        _activated = true;

        var okBtn = new Button
        {
            Content = "开始使用",
            Height = 40,
            Margin = new Thickness(0, 14, 0, 0),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x4c, 0xc3, 0x8a)),
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        okBtn.Click += (_, _) => DialogResult = true;
        ((StackPanel)((ScrollViewer)Content).Content).Children.Insert(
            ((StackPanel)((ScrollViewer)Content).Content).Children.Count - 1, okBtn);
    }

    private async void BtnRetryQr_Click(object sender, RoutedEventArgs e) => await StartLifeOrderAsync();

    private void LnkSite_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) =>
        OpenUrl(e.Uri.AbsoluteUri);

    // ================= 手动激活（兜底） =================

    private void LnkManual_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e) =>
        PanelManual.Visibility = PanelManual.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private void BtnCopyCode_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TbMachineCode.Text); TxtResult.Text = "机器码已复制，发给客服即可获取激活码。"; }
        catch { }
    }

    private void BtnActivate_Click(object sender, RoutedEventArgs e)
    {
        var code = TbActivation.Text;
        if (string.IsNullOrWhiteSpace(code))
        {
            TxtResult.Text = "请先粘贴激活码。";
            return;
        }

        try
        {
            var (err, expiry) = LicenseManager.ValidateActivationCode(code, _machineCode, DateTime.UtcNow);
            switch (err)
            {
                case LicenseManager.LicenseError.None:
                    LicenseManager.SaveLocalLicense(code);
                    TxtResult.Text = $"✅ 激活成功！授权到 {expiry:yyyy-MM-dd}。";
                    TxtResult.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x4c, 0xc3, 0x8a));
                    _activated = true;
                    BtnActivate.IsEnabled = false;
                    var okBtn = new Button
                    {
                        Content = "开始使用",
                        Height = 40,
                        Margin = new Thickness(0, 14, 0, 0),
                        Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x4c, 0xc3, 0x8a)),
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                    };
                    okBtn.Click += (_, _) => DialogResult = true;
                    ((StackPanel)((ScrollViewer)Content).Content).Children.Insert(
                        ((StackPanel)((ScrollViewer)Content).Content).Children.Count - 1, okBtn);
                    break;
                case LicenseManager.LicenseError.Expired:
                    TxtResult.Text = $"这个激活码已于 {expiry:yyyy-MM-dd} 到期，请联系客服续期。";
                    break;
                case LicenseManager.LicenseError.MachineMismatch:
                    TxtResult.Text = "这个激活码是别的电脑的，与本机机器码不匹配。";
                    break;
                case LicenseManager.LicenseError.Malformed:
                    TxtResult.Text = "激活码格式不对，请完整复制（含短横线也可以）。";
                    break;
                default:
                    TxtResult.Text = "激活码无效（内容被篡改或输错）。";
                    break;
            }
        }
        catch (Exception ex)
        {
            TxtResult.Text = "激活失败：" + ex.Message;
        }
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_activated) DialogResult = false;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
