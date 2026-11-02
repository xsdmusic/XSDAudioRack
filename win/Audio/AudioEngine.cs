using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.Asio;
using XSDAudioRack.Vst3;

namespace XSDAudioRack.Audio;

/// <summary>静默源：只为 InitRecordAndPlayback 提供格式，实际输出由 AudioAvailable 直写。</summary>
internal sealed class FormatProvider : IWaveProvider
{
    public WaveFormat WaveFormat { get; }
    public FormatProvider(int sampleRate, int channels) =>
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    public int Read(byte[] buffer, int offset, int count) => 0;
}

/// <summary>
/// ASIO 全双工引擎：ASIO 输入 → VST3 插件链 → Send A/B → 虚拟输入 C/D 混音 → 主输出，
/// 总音量之后再经 VST2WDM Ultra 把最终混音送到虚拟录音端点（Ultra REC 01/02），
/// 同时供录音与电平表。
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int MainChainMax = 8;

    private AsioOut? _asio;
    private string? _asioDriverName;
    private int _sampleRate = 48000;
    private int _frames;

    // 设备选择（启动前设置，改动后需重启引擎生效）
    public int InputChannelOffset;   // 输入通道对起始下标（0 = 1/2 通道）
    public int OutputChannelOffset;  // 输出通道对起始下标（0 = 1/2 通道）
    public int DesiredSampleRate;    // 0 = 自动（先 48k 再 44.1k）

    // 信号链（须在引擎停止时修改）
    public readonly List<Vst3Plugin> Chain = new();
    public Vst3Plugin? SendPluginA, SendPluginB;
    public float SendVolumeA = 1f, SendVolumeB = 1f; // 线性
    public float SendMasterVolume = 1f;              // Send 总音量（A/B 共用，Mac 版同款）

    // 虚拟声卡（WDM2VST Ultra）自动接入：
    //   C/D = 采集插件（WDM2VST Ultra），分别绑定 Ultra PLAY 05/06、07/08，
    //         把系统声音混进机架，替代原 AUX 环回；
    //   MasterOutPlugin = 送出插件（VST2WDM Ultra），绑定 Ultra REC 01/02，
    //         在主音量之后把最终混音送出去给 OBS 等软件采集。
    public Vst3Plugin? SendPluginC, SendPluginD;
    public Vst3Plugin? MasterOutPlugin;
    /// <summary>输出前插件（通常是限制器）：所有混音之后、总推子之前，防总输出爆音。</summary>
    public Vst3Plugin? PostPlugin;
    /// <summary>Send A/B 总静音（静音掉两个 Send 效果器的输出）。</summary>
    public bool SendMuted;
    public float SendVolumeC = 1f, SendVolumeD = 1f; // 线性，插件输出之后乘
    public string Wdm2vstStatus = "";

    // 预设里保存、待插件创建后回灌的状态（WDM2VST 实例由引擎惰性创建）
    internal byte[]? PendingStateC, PendingStateD, PendingStateMasterOut;

    // 主控
    public float MasterGain = 1f;
    /// <summary>闭麦：静音话筒输入通道（总输出里的 C/D 虚拟输入音乐不受影响）。</summary>
    public bool MicMuted;

    // 电平（volatile，UI 轮询）
    public volatile float InputLevel, OutputLevelL, OutputLevelR;
    public volatile float LevelC, LevelD; // 虚拟输入 C/D（插件输出，乘音量前）

    // 录音
    private WaveFileWriter? _writer;
    public bool IsRecording => _writer != null;
    public string RecordingsDir { get; }

    public string? LastError { get; private set; }

    public AudioEngine()
    {
        RecordingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XSDAudioRack录音");
        try { Directory.CreateDirectory(RecordingsDir); } catch { }
    }

    public static string[] GetAsioDrivers()
    {
        try { return AsioOut.GetDriverNames(); }
        catch { return Array.Empty<string>(); }
    }

    public string? AsioDriverName => _asioDriverName;
    public int SampleRate => _sampleRate;
    public int FramesPerBuffer => _frames;
    public bool IsRunning => _asio != null;

    // ---------------- 启动 / 停止 ----------------

    public void Start(string asioDriverName)
    {
        if (_asio != null && _asioDriverName == asioDriverName) return;
        Stop();
        LastError = null;

        try
        {
            EnsureWdm2vstPlugins(); // 在启动线程做（要加载插件 DLL），不占音频回调
            StartAsio(asioDriverName);
            _asioDriverName = asioDriverName;
        }
        catch (Exception ex)
        {
            Stop();
            LastError = ex.Message;
            throw;
        }
    }

    private void StartAsio(string driverName)
    {
        // 采样率尝试序列：优先用户指定，再 48k / 44.1k 兜底
        var rates = new List<int>();
        if (DesiredSampleRate > 0) rates.Add(DesiredSampleRate);
        foreach (var r in new[] { 48000, 44100 })
            if (!rates.Contains(r)) rates.Add(r);

        AsioOut? asio = null;
        Exception? last = null;
        foreach (var rate in rates)
        {
            try
            {
                var attempt = new AsioOut(driverName);
                try
                {
                    // 通道选择须在 InitRecordAndPlayback 之前设置（SetChannelOffset 会校验越界）
                    // 注意：本版 NAudio 的 AsioOut 输出偏移属性名为 ChannelOffset
                    attempt.ChannelOffset = OutputChannelOffset * 2;
                    attempt.InputChannelOffset = InputChannelOffset * 2;
                    attempt.InitRecordAndPlayback(new FormatProvider(rate, 2), 2, 0);
                }
                catch
                {
                    try { attempt.Dispose(); } catch { }
                    throw;
                }
                asio = attempt;
                _sampleRate = rate;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                try { asio?.Dispose(); } catch { }
                asio = null;
            }
        }
        if (asio == null)
        {
            if (last != null && (InputChannelOffset > 0 || OutputChannelOffset > 0))
                throw new InvalidOperationException("通道选择超出声卡支持范围：" + last.Message);
            throw new InvalidOperationException("ASIO 初始化失败：" + last?.Message);
        }

        _frames = asio.FramesPerBuffer;
        EnsureBuffers(_frames);
        _diagBlocks = 8; // 下一批音频块记录原始数据，用于排查通路

        asio.AudioAvailable += OnAsioAudio;
        asio.Play();
        _asio = asio;

        var owf = asio.OutputWaveFormat;
        DiagLog.Write($"引擎启动：驱动={driverName}，采样率={_sampleRate}，每块帧数={_frames}，" +
                      $"输入通道对={InputChannelOffset}，输出通道对={OutputChannelOffset}");
        DiagLog.Write($"NAudio 输出格式：{owf.Encoding}，{owf.BitsPerSample}bit，{owf.Channels}声道，{owf.SampleRate}Hz");
    }

    /// <summary>枚举 ASIO 驱动的输入/输出通道名（下标即通道号-1）。引擎运行中请勿调用。</summary>
    public static (string[] inputNames, string[] outputNames) GetChannelNames(string driverName)
    {
        var a = new AsioOut(driverName);
        try
        {
            string[] ins = new string[a.DriverInputChannelCount];
            for (int i = 0; i < ins.Length; i++)
                ins[i] = a.AsioInputChannelName(i);
            string[] outs = new string[a.DriverOutputChannelCount];
            for (int i = 0; i < outs.Length; i++)
                outs[i] = a.AsioOutputChannelName(i);
            return (ins, outs);
        }
        finally
        {
            try { a.Dispose(); } catch { }
        }
    }

    /// <summary>查询驱动支持的常见采样率（引擎运行中请勿调用）。</summary>
    public static List<int> GetSupportedSampleRates(string driverName)
    {
        var supported = new List<int>();
        var a = new AsioOut(driverName);
        try
        {
            foreach (var r in new[] { 44100, 48000, 88200, 96000, 176400, 192000 })
                if (a.IsSampleRateSupported(r)) supported.Add(r);
        }
        catch { }
        finally
        {
            try { a.Dispose(); } catch { }
        }
        return supported;
    }

    public void Stop()
    {
        StopRecording();
        if (_asio != null)
        {
            try { _asio.Stop(); _asio.Dispose(); } catch { }
            _asio = null;
        }
        // 注意：_asioDriverName 保留（表示"上次使用的驱动"），清空会导致预设丢失声卡设置
        foreach (var p in AllPlugins()) p.Deactivate();
        InputLevel = OutputLevelL = OutputLevelR = 0;
        LevelC = LevelD = 0;
    }

    private IEnumerable<Vst3Plugin> AllPlugins()
    {
        foreach (var p in Chain) yield return p;
        if (SendPluginA != null) yield return SendPluginA;
        if (SendPluginB != null) yield return SendPluginB;
        if (SendPluginC != null) yield return SendPluginC;
        if (SendPluginD != null) yield return SendPluginD;
        if (MasterOutPlugin != null) yield return MasterOutPlugin;
        if (PostPlugin != null) yield return PostPlugin;
    }

    // ---------------- 虚拟声卡（WDM2VST Ultra） ----------------

    /// <summary>惰性创建并绑定 C/D 采集插件与总输出送出插件。引擎启动前调用一次即可；
    /// 公开供主窗口在启动/保存预置时提前创建，保证插件状态随时可被捕获。</summary>
    public void EnsureWdm2vstPlugins()
    {
        if (SendPluginC != null && SendPluginD != null && MasterOutPlugin != null) return;
        try
        {
            if (!Wdm2VstBridge.IsInstalled)
            {
                Wdm2vstStatus = "WDM2VST：未检测到，虚拟输入 C/D 与虚拟输出停用（安装 WDM2VST Ultra 后重新启动引擎）";
                DiagLog.Write($"WDM2VST 未检测到（本次引擎启动跳过 C/D）。查找位置：{Wdm2VstBridge.Vst3Dir} 下的 " +
                              $"'{Wdm2VstBridge.CapturePlugin}'（单文件或 Contents\\x86_64-win 捆绑包）与 " +
                              $"'{Wdm2VstBridge.OutputPlugin}'");
                return;
            }

            SendPluginC ??= Wdm2VstBridge.TryCreatePinned(Wdm2VstBridge.CapturePlugin, Wdm2VstBridge.PlayC);
            SendPluginD ??= Wdm2VstBridge.TryCreatePinned(Wdm2VstBridge.CapturePlugin, Wdm2VstBridge.PlayD);
            MasterOutPlugin ??= Wdm2VstBridge.TryCreatePinned(Wdm2VstBridge.OutputPlugin, Wdm2VstBridge.RecMasterOut);

            // 回灌预设里保存的插件状态（若本会话新建了实例）
            if (PendingStateC != null) { SendPluginC?.LoadState(PendingStateC); PendingStateC = null; }
            if (PendingStateD != null) { SendPluginD?.LoadState(PendingStateD); PendingStateD = null; }
            if (PendingStateMasterOut != null)
            {
                MasterOutPlugin?.LoadState(PendingStateMasterOut);
                PendingStateMasterOut = null;
            }

            if (SendPluginC == null || SendPluginD == null || MasterOutPlugin == null)
                Wdm2vstStatus = "WDM2VST：部分组件加载失败，详见日志 " + Wdm2VstBridge.LogPath;
            else
                Wdm2vstStatus = $"WDM2VST：C ← {Wdm2VstBridge.PlayC}，D ← {Wdm2VstBridge.PlayD}，总输出 → {Wdm2VstBridge.RecMasterOut}";
        }
        catch (Exception ex)
        {
            Wdm2vstStatus = "WDM2VST 加载失败：" + ex.Message;
        }
    }

    // ---------------- ASIO 实时回调 ----------------

    private float[] _inL = Array.Empty<float>(), _inR = Array.Empty<float>();
    private float[] _bufA1 = Array.Empty<float>(), _bufA2 = Array.Empty<float>();
    private float[] _bufB1 = Array.Empty<float>(), _bufB2 = Array.Empty<float>();
    private float[] _mixL = Array.Empty<float>(), _mixR = Array.Empty<float>();
    private float[] _zeroL = Array.Empty<float>(), _zeroR = Array.Empty<float>();
    private float[] _recInterleaved = Array.Empty<float>();

    private void EnsureBuffers(int frames)
    {
        void Ensure(ref float[] a)
        {
            if (a.Length < frames) a = new float[frames];
        }
        Ensure(ref _inL); Ensure(ref _inR);
        Ensure(ref _bufA1); Ensure(ref _bufA2);
        Ensure(ref _bufB1); Ensure(ref _bufB2);
        Ensure(ref _mixL); Ensure(ref _mixR);
        Ensure(ref _zeroL); Ensure(ref _zeroR);
        // 交织录音缓冲是 LRLR 双声道，需要 frames*2 长度（之前只分 frames 导致越界闪退）
        if (_recInterleaved.Length < frames * 2) _recInterleaved = new float[frames * 2];
        foreach (var p in AllPlugins())
        {
            try { p.Activate(frames, _sampleRate); }
            catch (Exception ex) { LastError = $"插件 {p.Name} 激活失败：{ex.Message}"; }
        }
    }

    private unsafe void OnAsioAudio(object? sender, AsioAudioAvailableEventArgs e)
    {
        int frames = e.SamplesPerBuffer;
        if (frames <= 0 || e.OutputBuffers.Length < 2) return;
        EnsureBuffers(frames);

        // 1) ASIO 输入 → float
        ReadInput(e, _inL, _inR, frames);

        // 1.5) 闭麦：直接静音话筒输入通道（下游的链/Send/录音里话筒全部无声，C/D 音乐不受影响）
        if (MicMuted)
        {
            Array.Clear(_inL, 0, frames);
            Array.Clear(_inR, 0, frames);
        }

        float peakIn = 0;
        for (int i = 0; i < frames; i++)
        {
            float a = Math.Abs(_inL[i]), b = Math.Abs(_inR[i]);
            if (a > peakIn) peakIn = a;
            if (b > peakIn) peakIn = b;
        }
        InputLevel = peakIn;

        if (_diagBlocks > 0) { _diagBlocks--; DiagDumpInputBlock(e, frames, peakIn); }

        // 2) 插件链
        var edits = new List<(uint, double)>();
        var curL = _inL; var curR = _inR;
        var nextL = _bufA1; var nextR = _bufA2;
        foreach (var p in Chain)
        {
            TakeEdits(p, edits);
            p.Process(curL, curR, nextL, nextR, frames, edits);
            (curL, nextL) = (nextL, curL);
            (curR, nextR) = (nextR, curR);
            edits.Clear();
        }

        // 3) 主混音：主链输出 + Send A/B 输出，逐样本累加（绝不能把整块求和成一个标量）
        var mixL = _mixL; var mixR = _mixR;
        for (int i = 0; i < frames; i++) { mixL[i] = curL[i]; mixR[i] = curR[i]; }
        float sendMaster = SendMuted ? 0 : SendMasterVolume;
        if (SendPluginA != null)
        {
            float v = SendVolumeA * sendMaster;
            for (int i = 0; i < frames; i++) { _bufB1[i] = curL[i] * v; _bufB2[i] = curR[i] * v; }
            TakeEdits(SendPluginA, edits);
            SendPluginA.Process(_bufB1, _bufB2, _bufA1, _bufA2, frames, edits);
            edits.Clear();
            for (int i = 0; i < frames; i++) { mixL[i] += _bufA1[i]; mixR[i] += _bufA2[i]; }
        }
        if (SendPluginB != null)
        {
            float v = SendVolumeB * sendMaster;
            for (int i = 0; i < frames; i++) { _bufB1[i] = curL[i] * v; _bufB2[i] = curR[i] * v; }
            TakeEdits(SendPluginB, edits);
            SendPluginB.Process(_bufB1, _bufB2, _bufA1, _bufA2, frames, edits);
            edits.Clear();
            for (int i = 0; i < frames; i++) { mixL[i] += _bufA1[i]; mixR[i] += _bufA2[i]; }
        }

        // 4) 虚拟输入 C / D（WDM2VST Ultra 采集 Ultra PLAY 05/06、07/08 的系统声音）
        //    输入喂零防回授，音量在插件输出之后乘
        if (SendPluginC != null)
        {
            Array.Clear(_zeroL, 0, frames); Array.Clear(_zeroR, 0, frames);
            TakeEdits(SendPluginC, edits);
            SendPluginC.Process(_zeroL, _zeroR, _bufA1, _bufA2, frames, edits);
            edits.Clear();
            float v = SendVolumeC;
            float pk = 0;
            for (int i = 0; i < frames; i++)
            {
                float c = _bufA1[i] * v, d = _bufA2[i] * v;
                mixL[i] += c; mixR[i] += d;
                float a2 = Math.Abs(c), b2 = Math.Abs(d);
                if (a2 > pk) pk = a2;
                if (b2 > pk) pk = b2;
            }
            LevelC = pk;
        }
        if (SendPluginD != null)
        {
            Array.Clear(_zeroL, 0, frames); Array.Clear(_zeroR, 0, frames);
            TakeEdits(SendPluginD, edits);
            SendPluginD.Process(_zeroL, _zeroR, _bufA1, _bufA2, frames, edits);
            edits.Clear();
            float v = SendVolumeD;
            float pk = 0;
            for (int i = 0; i < frames; i++)
            {
                float c = _bufA1[i] * v, d = _bufA2[i] * v;
                mixL[i] += c; mixR[i] += d;
                float a2 = Math.Abs(c), b2 = Math.Abs(d);
                if (a2 > pk) pk = a2;
                if (b2 > pk) pk = b2;
            }
            LevelD = pk;
        }

        // 4.5) 输出前插件（通常是限制器）：所有混音之后、总推子之前，防止总输出爆音
        if (PostPlugin != null)
        {
            TakeEdits(PostPlugin, edits);
            PostPlugin.Process(mixL, mixR, _bufA1, _bufA2, frames, edits);
            edits.Clear();
            mixL = _bufA1; mixR = _bufA2;
        }

        // 5) 主音量（逐样本）。闭麦在输入端处理，主推子不再承担静音职责
        float g = MasterGain;
        float peakL = 0, peakR = 0;
        for (int i = 0; i < frames; i++)
        {
            float l = mixL[i] * g, r = mixR[i] * g;
            if (l > 1) l = 1; else if (l < -1) l = -1;
            if (r > 1) r = 1; else if (r < -1) r = -1;
            _inL[i] = l;
            _inR[i] = r;
            float a = Math.Abs(l), b = Math.Abs(r);
            if (a > peakL) peakL = a;
            if (b > peakR) peakR = b;
        }
        OutputLevelL = peakL;
        OutputLevelR = peakR;

        // 5.5) 总输出送出（VST2WDM Ultra → Ultra REC 01/02）：在主音量之后，
        //      把最终混音送到虚拟录音端点供 OBS 等软件采集；
        //      插件返回值不采用，ASIO 输出与录音不受它影响。
        if (MasterOutPlugin != null)
        {
            TakeEdits(MasterOutPlugin, edits);
            MasterOutPlugin.Process(_inL, _inR, _bufA1, _bufA2, frames, edits);
            edits.Clear();
        }

        // 6) 录音（交织成 LRLR 写 WAV；_inL/_inR 已存最终混音）
        if (_writer != null)
        {
            for (int i = 0, j = 0; i < frames; i++)
            {
                _recInterleaved[j++] = _inL[i];
                _recInterleaved[j++] = _inR[i];
            }
            _writer.WriteSamples(_recInterleaved, 0, frames * 2);
        }

        // 7) 写 ASIO 输出
        WriteOutput(e, _inL, _inR, frames);
        e.WrittenToOutputBuffers = true;

        void WriteOutput(AsioAudioAvailableEventArgs ev, float[] l, float[] r, int n)
        {
            // 输出采样格式从 OutputWaveFormat 推断（args 里只带输入类型）
            var type = AsioSampleType.Float32LSB;
            if (_asio != null)
            {
                var owf = _asio.OutputWaveFormat;
                if (owf.Encoding == WaveFormatEncoding.IeeeFloat) type = AsioSampleType.Float32LSB;
                else if (owf.BitsPerSample == 32) type = AsioSampleType.Int32LSB;
                else if (owf.BitsPerSample == 24) type = AsioSampleType.Int24LSB;
                else if (owf.BitsPerSample == 16) type = AsioSampleType.Int16LSB;
            }

            unsafe
            {
                var pL = (void*)ev.OutputBuffers[0];
                var pR = ev.OutputBuffers.Length > 1 ? (void*)ev.OutputBuffers[1] : pL;
                switch (type)
                {
                    case AsioSampleType.Float32LSB:
                    {
                        var dL = (float*)pL; var dR = (float*)pR;
                        for (int i = 0; i < n; i++) { dL[i] = l[i]; dR[i] = r[i]; }
                        break;
                    }
                    case AsioSampleType.Int32LSB:
                    {
                        var dL = (int*)pL; var dR = (int*)pR;
                        for (int i = 0; i < n; i++)
                        {
                            dL[i] = (int)(l[i] * int.MaxValue);
                            dR[i] = (int)(r[i] * int.MaxValue);
                        }
                        break;
                    }
                    case AsioSampleType.Int16LSB:
                    {
                        var dL = (short*)pL; var dR = (short*)pR;
                        for (int i = 0; i < n; i++)
                        {
                            dL[i] = (short)(l[i] * short.MaxValue);
                            dR[i] = (short)(r[i] * short.MaxValue);
                        }
                        break;
                    }
                    case AsioSampleType.Int24LSB:
                    {
                        var dL = (byte*)pL; var dR = (byte*)pR;
                        for (int i = 0; i < n; i++)
                        {
                            int vL = (int)(l[i] * 8388607f);
                            int vR = (int)(r[i] * 8388607f);
                            dL[i * 3] = (byte)vL; dL[i * 3 + 1] = (byte)(vL >> 8); dL[i * 3 + 2] = (byte)(vL >> 16);
                            dR[i * 3] = (byte)vR; dR[i * 3 + 1] = (byte)(vR >> 8); dR[i * 3 + 2] = (byte)(vR >> 16);
                        }
                        break;
                    }
                }
            }
        }
    }

    private void TakeEdits(Vst3Plugin p, List<(uint, double)> list)
    {
        list.AddRange(p.Handler.TakePendingEdits());
    }

    private int _diagBlocks;

    /// <summary>把音频块开头的原始字节按 int32 / float 两种视角转储，用于判断实际采样格式。</summary>
    private static unsafe void DiagDumpInputBlock(AsioAudioAvailableEventArgs e, int frames, float peakIn)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"音频块诊断：报告类型={e.AsioSampleType}，输入通道数={e.InputBuffers.Length}，" +
                  $"输出通道数={e.OutputBuffers.Length}，帧数={frames}，转换后峰值={peakIn:0.00000}");
        for (int ch = 0; ch < Math.Min(2, e.InputBuffers.Length); ch++)
        {
            var pb = (byte*)e.InputBuffers[ch];
            sb.Append($"\n  输入{ch} 前16字节(hex)：");
            for (int i = 0; i < 16; i++) sb.Append(pb[i].ToString("X2")).Append(' ');
            var pi = (int*)e.InputBuffers[ch];
            sb.Append($"\n  输入{ch} 前4样本(int32视角)：{pi[0]}, {pi[1]}, {pi[2]}, {pi[3]}");
            var pf = (float*)e.InputBuffers[ch];
            sb.Append($"\n  输入{ch} 前4样本(float视角)：{pf[0]:0.000000}, {pf[1]:0.000000}, {pf[2]:0.000000}, {pf[3]:0.000000}");
        }
        DiagLog.WriteBlock("----", sb.ToString());
    }

    private static unsafe void ReadInput(AsioAudioAvailableEventArgs e, float[] l, float[] r, int frames)
    {
        if (e.InputBuffers.Length == 0)
        {
            Array.Clear(l, 0, frames);
            Array.Clear(r, 0, frames);
            return;
        }
        var pL = (void*)e.InputBuffers[0];
        var pR = e.InputBuffers.Length > 1 ? (void*)e.InputBuffers[1] : pL;
        switch (e.AsioSampleType)
        {
            case AsioSampleType.Float32LSB:
                {
                    var sL = (float*)pL; var sR = (float*)pR;
                    for (int i = 0; i < frames; i++) { l[i] = sL[i]; r[i] = sR[i]; }
                    break;
                }
            case AsioSampleType.Int32LSB:
                {
                    var sL = (int*)pL; var sR = (int*)pR;
                    const float k = 1f / int.MaxValue;
                    for (int i = 0; i < frames; i++) { l[i] = sL[i] * k; r[i] = sR[i] * k; }
                    break;
                }
            case AsioSampleType.Int16LSB:
                {
                    var sL = (short*)pL; var sR = (short*)pR;
                    const float k = 1f / short.MaxValue;
                    for (int i = 0; i < frames; i++) { l[i] = sL[i] * k; r[i] = sR[i] * k; }
                    break;
                }
            case AsioSampleType.Int24LSB:
                {
                    var sL = (byte*)pL; var sR = (byte*)pR;
                    const float k = 1f / 8388608f;
                    for (int i = 0; i < frames; i++)
                    {
                        int vL = sL[i * 3] | (sL[i * 3 + 1] << 8) | ((sbyte)sL[i * 3 + 2] << 16);
                        int vR = sR[i * 3] | (sR[i * 3 + 1] << 8) | ((sbyte)sR[i * 3 + 2] << 16);
                        l[i] = vL * k; r[i] = vR * k;
                    }
                    break;
                }
            default:
                Array.Clear(l, 0, frames);
                Array.Clear(r, 0, frames);
                break;
        }
    }

    // ---------------- 录音 ----------------

    public string? StartRecording()
    {
        if (_writer != null) return null;
        try
        {
            var name = "录音_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav";
            var path = Path.Combine(RecordingsDir, name);
            _writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2));
            return path;
        }
        catch (Exception ex)
        {
            LastError = "无法开始录音：" + ex.Message;
            return null;
        }
    }

    public string? StopRecording()
    {
        if (_writer == null) return null;
        var w = _writer;
        _writer = null;
        try { w.Dispose(); } catch { }
        return w.Filename;
    }

    public void Dispose()
    {
        Stop();
        SendPluginC?.Dispose();
        SendPluginD?.Dispose();
        MasterOutPlugin?.Dispose();
        SendPluginC = SendPluginD = MasterOutPlugin = null;
        PostPlugin?.Dispose();
        PostPlugin = null;
    }
}
