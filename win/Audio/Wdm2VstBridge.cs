using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using XSDAudioRack.Vst3;

using MMDeviceEnumerator = NAudio.CoreAudioApi.MMDeviceEnumerator;

namespace XSDAudioRack.Audio;

/// <summary>
/// WDM2VST Ultra 桥接：自动创建其 VST3 插件实例，并把实例"钉"到指定的
/// Ultra PLAY / Ultra REC 虚拟端点上（不弹插件界面）。
///
/// 绑定原理：这两个插件是 JUCE 写的，设备选择存在其状态的
/// &lt;DEVICESETUP&gt; XML 里（audioInputDeviceName / audioOutputDeviceName /
/// loopbackName / loopbackDeviceId / ipcChannelId / isDeviceLocked 等属性）。
/// 做法：加载实例 → SaveState 读出当前 XML → 把其中的设备名 / 设备 ID /
/// 通道号替换成目标端点 → LoadState 写回。
/// ipcChannelId 的原始值会随自适应偏移换算，不假设 0/1 起始。
/// 过程全部写入日志（%APPDATA%\XSDAudioRack\wdm2vst.log），便于排查。
/// </summary>
public static class Wdm2VstBridge
{
    // 采集插件（WDM2VST Ultra）：把系统声音采进机架
    public const string CapturePlugin = "WDM2VST Ultra.vst3";
    // 送出插件（VST2WDM Ultra）：把机架声音送到虚拟录音端点
    public const string OutputPlugin = "VST2WDM Ultra.vst3";

    // Send C / D 对应的虚拟播放端点
    public const string PlayC = "Ultra PLAY 05/06";
    public const string PlayD = "Ultra PLAY 07/08";
    // 总输出（主音量后）送到的虚拟录音端点
    public const string RecMasterOut = "Ultra REC 01/02";

    public static string Vst3Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Common Files", "VST3");

    /// <summary>解析插件实际 DLL 路径：兼容单文件 DLL、.vst3 捆绑包、厂商子文件夹三种安装形式
    ///（WDM2VST Ultra 实际装在 VST3 根目录下的 "WDM2VST Ultra" 子文件夹里）。</summary>
    private static string ResolveModule(string pluginFile)
    {
        var root = Path.Combine(Vst3Dir, pluginFile);
        if (File.Exists(root)) return root;
        // 捆绑包结构：Name.vst3\Contents\x86_64-win\Name.vst3
        var bundle = Path.Combine(Vst3Dir, pluginFile, "Contents", "x86_64-win", pluginFile);
        if (File.Exists(bundle)) return bundle;
        // 任意一级子文件夹（厂商目录）
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Vst3Dir))
            {
                var p = Path.Combine(d, pluginFile);
                if (File.Exists(p)) return p;
                var pb = Path.Combine(d, pluginFile, "Contents", "x86_64-win", pluginFile);
                if (File.Exists(pb)) return pb;
            }
        }
        catch { }
        return root;
    }

    public static bool IsInstalled =>
        File.Exists(ResolveModule(CapturePlugin)) &&
        File.Exists(ResolveModule(OutputPlugin));

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XSDAudioRack", "wdm2vst.log");

    // ---------------- 实例创建 ----------------

    /// <summary>加载插件模块并创建一个效果器实例，绑定到 targetDevice。失败返回 null。</summary>
    public static Vst3Plugin? TryCreatePinned(string pluginFile, string targetDevice)
    {
        var modulePath = ResolveModule(pluginFile);
        if (!File.Exists(modulePath))
        {
            Log($"找不到插件文件：{modulePath}");
            return null;
        }
        var module = Vst3ModuleCache.Get(modulePath);
        if (module == null)
        {
            Log($"插件模块加载失败：{modulePath}");
            return null;
        }

        byte[]? cid = null;
        string name = "", vendor = "";
        try
        {
            var f = module.Factory;
            for (int i = 0; i < f.CountClasses(); i++)
            {
                var (c, category, n) = f.GetClassInfo(i);
                if (category != global::XSDAudioRack.Vst3.Vst3.kAudioEffectClass) continue;
                cid = c;
                name = n;
                var info2 = f.GetClassInfo2(i);
                if (info2 != null) vendor = info2.Value.Vendor;
                break;
            }
        }
        catch (Exception ex)
        {
            Log($"枚举插件类失败：{ex.Message}");
            return null;
        }
        if (cid == null)
        {
            Log($"模块里没有效果器类：{pluginFile}");
            return null;
        }

        var plugin = new Vst3Plugin(module, cid, name, vendor, "", modulePath);
        Log($"已加载 {name}（{pluginFile}），尝试绑定 {targetDevice} ...");
        bool pinned = TryPinDevice(plugin, targetDevice);
        Log(pinned ? $"√ {name} 已绑定 {targetDevice}"
                   : $"× {name} 绑定 {targetDevice} 失败，将沿用插件默认设备分配");
        return plugin;
    }

    // ---------------- 设备绑定 ----------------

    /// <summary>把已加载的插件实例绑定到目标 Ultra 端点（补丁 DEVICESETUP 状态）。</summary>
    public static bool TryPinDevice(Vst3Plugin plugin, string targetDevice)
    {
        try
        {
            var state = plugin.SaveState();
            if (state is not { Length: > 0 })
            {
                Log($"[{plugin.Name}] SaveState 返回空，无法绑定（插件可能要求先激活一次）");
                return false;
            }

            string text;
            try { text = Encoding.UTF8.GetString(state); }
            catch
            {
                Log($"[{plugin.Name}] 状态不是 UTF-8 文本（可能是二进制块），无法绑定");
                return false;
            }

            if (!text.Contains("<DEVICESETUP"))
            {
                Log($"[{plugin.Name}] 状态里没有 DEVICESETUP 节点");
                return false;
            }

            // 目标端点的真实 WASAPI 设备 ID（用于替换 loopbackDeviceId 之类属性）
            var dev = FindEndpoint(targetDevice);
            if (dev == null)
                Log($"警告：枚举不到端点「{targetDevice}」（驱动未装或端点未激活），只按名字绑定");

            // 原状态里的端点序号（推断 ipcChannelId 的基准）
            var mOrig = Regex.Match(text, @"Ultra (?:PLAY|REC) (\d+)/(\d+)");
            int origPair = mOrig.Success ? ParsePairIndex(mOrig.Groups[1].Value) : -1;
            int targetPair = ParsePairIndex(FirstChannelNumber(targetDevice));

            string patched = Regex.Replace(text, @"<DEVICESETUP\b[^>]*>",
                m => PatchTag(m.Value, targetDevice, dev?.Id, targetPair, origPair));

            var bytes = Encoding.UTF8.GetBytes(patched);
            if (!plugin.LoadState(bytes))
            {
                Log($"[{plugin.Name}] LoadState 写回失败");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"[{plugin.Name}] 绑定异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>补丁单个 &lt;DEVICESETUP …&gt; 标签的属性。</summary>
    private static string PatchTag(string tag, string targetDevice, string? deviceId, int targetPair, int origPair)
    {
        var attrs = new List<(string Name, string Value)>();
        foreach (Match m in Regex.Matches(tag, @"([\w:]+)\s*=\s*""([^""]*)"""))
            attrs.Add((m.Groups[1].Value, m.Groups[2].Value));

        bool sawDeviceName = false, sawRate = false;

        var result = tag;
        foreach (var (name, value) in attrs)
        {
            string nameL = name.ToLowerInvariant();
            string? replace = null;

            if (nameL == "audiodevicerate")
            {
                replace = "48000";
                sawRate = true;
            }
            else if (nameL.Contains("channel") && int.TryParse(value, out int ch))
            {
                // ipcChannelId 之类通道号：按端点序号差自适应换算
                replace = (origPair >= 0 ? ch + (targetPair - origPair) : ch + targetPair).ToString();
            }
            else if (nameL.Contains("ipc") || nameL.Contains("mode"))
            {
                // IPC 节名 / 模式等内部标识：不动
            }
            else if (nameL.Contains("locked"))
            {
                replace = "1"; // 锁定设备，防止被自动重新分配
            }
            else if (nameL.Contains("name"))
            {
                // 设备名属性（audioInputDeviceName / audioOutputDeviceName / loopbackName / …）
                // 空值 = 未选设备，也一并替换，避免后面追加成重复属性
                if (value.Length == 0 || value.Contains("Ultra") || nameL.Contains("loopback") ||
                    nameL.Contains("audioinput") || nameL.Contains("audiooutput") || nameL.Contains("audiodevice"))
                {
                    replace = targetDevice;
                    sawDeviceName = true;
                }
            }
            else if (nameL.Contains("id") && value.Length > 10 && !int.TryParse(value, out _))
            {
                // WASAPI 设备 ID（loopbackDeviceId 之类）：换成目标端点的真实 ID
                if (deviceId != null) replace = deviceId;
            }

            if (replace != null)
                result = result.Replace($"{name}=\"{value}\"", $"{name}=\"{replace}\"");
        }

        // 属性缺失时的兜底：直接补设备名与采样率
        if (!sawDeviceName)
            result = result.Insert(result.Length - 1,
                $" audioOutputDeviceName=\"{targetDevice}\" audioInputDeviceName=\"{targetDevice}\"");
        if (!sawRate)
            result = result.Insert(result.Length - 1, " audioDeviceRate=\"48000\"");
        return result;
    }

    // ---------------- 辅助 ----------------

    /// <summary>按友好名在系统端点里找设备（先 Render 后 Capture）。</summary>
    private static (string Id, string Name)? FindEndpoint(string friendlyPart)
    {
        try
        {
            var e = new MMDeviceEnumerator();
            foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
            {
                foreach (var d in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    if (d.FriendlyName.Contains(friendlyPart, StringComparison.OrdinalIgnoreCase))
                        return (d.ID, d.FriendlyName);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>"Ultra PLAY 05/06" → 5；用于解析端点名里的第一个通道号。</summary>
    private static string FirstChannelNumber(string deviceName)
    {
        var m = Regex.Match(deviceName, @"(\d+)\s*/");
        return m.Success ? m.Groups[1].Value : "1";
    }

    /// <summary>通道号 → 端点组序号（05 → 2，即第 3 组，0 基）。</summary>
    private static int ParsePairIndex(string firstChannel)
    {
        if (int.TryParse(firstChannel, out int n) && n >= 1)
            return (n - 1) / 2;
        return 0;
    }

    public static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
