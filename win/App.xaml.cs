using System;
using System.Threading.Tasks;
using System.Windows;
using XSDAudioRack.License;

using Application = System.Windows.Application;

namespace XSDAudioRack;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常兜底：任何未处理异常都写 engine.log；界面线程异常弹窗提示，不闪退
        DispatcherUnhandledException += (_, ex) =>
        {
            try { DiagLog.Write($"[界面线程未处理异常]\n{ex.Exception}"); } catch { }
            try
            {
                MessageBox.Show($"发生了一个错误（程序已继续运行）：\n\n{ex.Exception.Message}\n\n详细信息已写入 engine.log。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { DiagLog.Write($"[致命未处理异常]\n{ex.ExceptionObject}"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            try { DiagLog.Write($"[后台任务异常]\n{ex.Exception}"); } catch { }
        };

        base.OnStartup(e);

        // 提前应用上次使用的主题（激活窗口也要跟着主题走）
        try { ThemeManager.Apply(XSDAudioRack.Audio.RackPreset.Load()?.Theme); }
        catch { ThemeManager.Apply(null); }

        // 时钟回拨 → 直接判为需重新激活
        if (LicenseManager.IsClockRolledBack(DateTime.UtcNow))
            LicenseManager.ClearLocalLicense();

        var (valid, message, _) = LicenseManager.CheckLocalLicense(DateTime.UtcNow);
        if (!valid)
        {
            // 未激活：先看三天试用是否还有效
            var (trialOk, trialEnd) = LicenseManager.CheckTrial(DateTime.UtcNow);
            if (!trialOk)
            {
                var reason = string.IsNullOrEmpty(message) || message == "未激活"
                    ? $"三天试用期已结束（{trialEnd:yyyy-MM-dd}）。正式使用请联系新声代音乐服务（微信 277878676）激活，授权一年。"
                    : $"{message}。三天试用期也已结束（{trialEnd:yyyy-MM-dd}），请联系新声代音乐服务（微信 277878676）激活，授权一年。";
                var win = new ActivationWindow(reason);
                if (win.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
            }
            // 试用有效：正常进入主窗口（标题会提示剩余天数）
        }
    }
}
