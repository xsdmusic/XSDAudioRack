using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XSDAudioRack.Vst3;

namespace XSDAudioRack.Audio;

/// <summary>机架状态自动保存/恢复（JSON）。</summary>
public sealed class RackPreset
{
    public class ChainItem
    {
        public string? Name { get; set; }
        public string? ModulePath { get; set; }
        public bool Bypassed { get; set; }
        public byte[]? State { get; set; }
    }

    public List<ChainItem> Chain { get; set; } = new();
    public ChainItem? SendPluginA { get; set; }
    public ChainItem? SendPluginB { get; set; }
    public double SendVolumeA { get; set; } = 1.0;
    public double SendVolumeB { get; set; } = 1.0;

    // 虚拟输入 C/D（WDM2VST 采集）与总输出送出：实例由引擎自动创建，只存音量和状态
    public double SendVolumeC { get; set; } = 1.0;
    public double SendVolumeD { get; set; } = 1.0;
    public byte[]? SendPluginCState { get; set; }
    public byte[]? SendPluginDState { get; set; }
    public byte[]? MasterOutState { get; set; }
    public ChainItem? PostPlugin { get; set; }     // 输出前插件（限制器）
    public bool SendMuted { get; set; }             // Send A/B 总静音
    public string? AuxCName { get; set; }   // 虚拟输入 C 显示名（默认"伴奏"，双击可改）
    public string? AuxDName { get; set; }   // 虚拟输入 D 显示名（默认"音效"，双击可改）

    public double MasterVolume { get; set; } = 1.0;
    public bool Muted { get; set; }
    public string? AsioDriverName { get; set; }

    // 设备 / 外观 / 快捷键
    public int InputChannelOffset { get; set; }
    public int OutputChannelOffset { get; set; }
    public int SampleRate { get; set; }          // 0 = 自动
    public double SendMasterVolume { get; set; } = 1.0;
    public string? Theme { get; set; }          // "Light" / "Dark"
    public bool ShowDevicePanel { get; set; } = true;
    public string? MuteHotkey { get; set; }      // 如 "Ctrl+Shift+M"

    private static string SavePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XSDAudioRack");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "autosave.json");
        }
    }

    private static ChainItem? ToItem(Vst3Plugin? p)
    {
        if (p == null) return null;
        return new ChainItem
        {
            Name = p.Name,
            ModulePath = p.ModulePath,
            Bypassed = p.Bypassed,
            State = p.SaveState(),
        };
    }

    public static RackPreset Capture(AudioEngine engine, bool showDevicePanel = true, string? muteHotkey = null)
    {
        var preset = new RackPreset
        {
            SendPluginA = ToItem(engine.SendPluginA),
            SendPluginB = ToItem(engine.SendPluginB),
            SendVolumeA = engine.SendVolumeA,
            SendVolumeB = engine.SendVolumeB,
            SendVolumeC = engine.SendVolumeC,
            SendVolumeD = engine.SendVolumeD,
            SendPluginCState = engine.SendPluginC?.SaveState(),
            SendPluginDState = engine.SendPluginD?.SaveState(),
            MasterOutState = engine.MasterOutPlugin?.SaveState(),
            PostPlugin = ToItem(engine.PostPlugin),
            SendMuted = engine.SendMuted,
            MasterVolume = engine.MasterGain,
            Muted = engine.MicMuted,   // 字段名保留 Muted（兼容旧预设文件），语义已改为闭麦
            AsioDriverName = engine.AsioDriverName,
            InputChannelOffset = engine.InputChannelOffset,
            OutputChannelOffset = engine.OutputChannelOffset,
            SampleRate = engine.DesiredSampleRate,
            SendMasterVolume = engine.SendMasterVolume,
            Theme = ThemeManager.Current,
            ShowDevicePanel = showDevicePanel,
            MuteHotkey = muteHotkey,
        };
        foreach (var p in engine.Chain)
            preset.Chain.Add(ToItem(p)!);
        return preset;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SavePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = false }));
        }
        catch { }
    }

    public static RackPreset? Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return null;
            return JsonSerializer.Deserialize<RackPreset>(File.ReadAllText(SavePath));
        }
        catch { return null; }
    }

    /// <summary>按预设恢复插件到引擎（引擎须处于停止状态）。返回失败的插件名。</summary>
    public List<string> RestoreInto(AudioEngine engine)
    {
        var failed = new List<string>();

        Vst3Plugin? Restore(ChainItem? item)
        {
            if (item?.ModulePath == null || item.Name == null) return null;
            try
            {
                var p = Vst3Plugin.Create(new Vst3PluginInfo
                {
                    ModulePath = item.ModulePath,
                    Name = item.Name,
                    Vendor = "",
                });
                p.Bypassed = item.Bypassed;
                if (item.State is { Length: > 0 } state)
                    p.LoadState(state);
                return p;
            }
            catch
            {
                failed.Add(item.Name);
                return null;
            }
        }

        foreach (var item in Chain)
        {
            if (engine.Chain.Count >= AudioEngine.MainChainMax) break;
            var p = Restore(item);
            if (p != null) engine.Chain.Add(p);
        }
        engine.SendPluginA = Restore(SendPluginA);
        engine.SendPluginB = Restore(SendPluginB);
        engine.PostPlugin = Restore(PostPlugin);
        engine.SendMuted = SendMuted;
        engine.SendVolumeA = (float)SendVolumeA;
        engine.SendVolumeB = (float)SendVolumeB;
        engine.SendVolumeC = (float)SendVolumeC;
        engine.SendVolumeD = (float)SendVolumeD;
        // WDM2VST 实例由引擎启动时自动创建，状态先挂起，创建后回灌
        engine.PendingStateC = SendPluginCState;
        engine.PendingStateD = SendPluginDState;
        engine.PendingStateMasterOut = MasterOutState;
        engine.MasterGain = (float)MasterVolume;
        engine.MicMuted = Muted;
        engine.InputChannelOffset = InputChannelOffset;
        engine.OutputChannelOffset = OutputChannelOffset;
        engine.DesiredSampleRate = SampleRate;
        engine.SendMasterVolume = (float)SendMasterVolume;

        return failed;
    }
}
