using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XSDAudioRack.Vst3;

/// <summary>
/// 一个已加载、可处理的 VST3 效果器实例：负责组件/控制器/处理器生命周期、
/// 实时处理、状态保存恢复、编辑器窗口。
/// </summary>
public sealed class Vst3Plugin : IDisposable
{
    private readonly Vst3Module _module;
    private readonly PluginComponent _component;
    private readonly PluginAudioProcessor _processor;
    private readonly PluginEditController? _controller;

    private readonly HostApplication _hostApp = new();
    public HostComponentHandler Handler { get; } = new();
    private readonly HostPlugFrame _plugFrame = new();
    private readonly HostParameterChanges _paramChanges = new();
    private readonly HostStream _stateStream = new();

    public string Name { get; }
    public string Vendor { get; }
    public string Version { get; }
    public string ModulePath { get; }
    public bool Bypassed { get; set; }

    private int _inputChannels = 2, _outputChannels = 2;
    private int _frames;
    private double _sampleRate;
    private bool _active;

    // 原生处理缓冲
    private IntPtr _processData, _inputBus, _outputBus, _inPtrs, _outPtrs;
    private IntPtr[] _inBufs = Array.Empty<IntPtr>();
    private IntPtr[] _outBufs = Array.Empty<IntPtr>();

    private Form? _editor;
    private bool _disposed;

    /// <summary>按扫描到的插件信息创建实例。</summary>
    public static Vst3Plugin Create(Vst3PluginInfo info)
    {
        var module = Vst3ModuleCache.Get(info.ModulePath)
                     ?? throw new InvalidOperationException($"无法加载插件模块：{info.ModulePath}");
        var f = module.Factory;
        int count = f.CountClasses();
        for (int i = 0; i < count; i++)
        {
            var (cid, category, name) = f.GetClassInfo(i);
            if (category != Vst3.kAudioEffectClass || name != info.Name) continue;
            string vendor = info.Vendor, version = "";
            var info2 = f.GetClassInfo2(i);
            if (info2 != null && info2.Value.Name == info.Name)
                version = info2.Value.Version;
            return new Vst3Plugin(module, cid, info.Name, vendor, version, info.ModulePath);
        }
        throw new InvalidOperationException($"模块里找不到插件类：{info.Name}");
    }

    internal Vst3Plugin(Vst3Module module, byte[] cid, string name, string vendor, string version, string modulePath)
    {
        _module = module;
        Name = name; Vendor = vendor; Version = version; ModulePath = modulePath;

        var f = module.Factory;
        var compPtr = f.CreateInstance(cid, Vst3.IID_IComponent);
        if (compPtr == IntPtr.Zero)
            throw new InvalidOperationException($"无法创建插件实例：{name}");
        _component = new PluginComponent(compPtr);

        if (_component.Initialize(_hostApp.Unknown) != Vst3.kResultOk)
            throw new InvalidOperationException($"插件初始化失败：{name}");

        // 控制器获取（宿主标准流程 + 健壮回退链）：
        // ① getControllerClassId → 工厂创建；② 失败则直接 QI 组件本身（单组件插件，Waves 等即如此）；
        // ③ 再从工厂类列表找同名 Component Controller Class；④ 最后同组件 ID 回退。
        // 任何一步拿到的指针都必须先通过 QI 验证，防止个别厂商工厂返回错误接口导致后面调用越界崩溃。
        var ctrlCid = _component.GetControllerClassId();
        IntPtr ctrlPtr = IntPtr.Zero;
        string ctrlPath;
        if (ctrlCid != null)
        {
            var (r, p) = f.CreateInstanceEx(ctrlCid, Vst3.IID_IEditController);
            var v = p == IntPtr.Zero ? IntPtr.Zero : QueryInterface(p, Vst3.IID_IEditController);
            if (v != IntPtr.Zero) { ctrlPtr = v; ctrlPath = $"控制器类ID直建 result={r}"; }
            else ctrlPath = $"控制器类ID直建失败 result={r}";
        }
        else ctrlPath = "getControllerClassId 未提供";
        if (ctrlPtr == IntPtr.Zero)
        {
            // 单组件插件：组件自身实现 IEditController，直接 QI（QI 不可能骗人）
            var qiPtr = QueryInterface(compPtr, Vst3.IID_IEditController);
            if (qiPtr != IntPtr.Zero) { ctrlPtr = qiPtr; ctrlPath += " → 组件QI成功（单组件）"; }
        }
        if (ctrlPtr == IntPtr.Zero)
        {
            // 工厂类列表里找同名控制器类（部分厂商 getControllerClassId 不可用）
            int n = f.CountClasses();
            for (int i = 0; i < n; i++)
            {
                var (ccid, cat, cname) = f.GetClassInfo(i);
                if (cat != Vst3.kComponentControllerClass || cname != name) continue;
                var (r3, p3) = f.CreateInstanceEx(ccid, Vst3.IID_IEditController);
                var v3 = p3 == IntPtr.Zero ? IntPtr.Zero : QueryInterface(p3, Vst3.IID_IEditController);
                if (v3 != IntPtr.Zero)
                {
                    ctrlPtr = v3;
                    ctrlPath += $" → 工厂同名控制器类成功 result={r3}";
                }
                break;
            }
        }
        if (ctrlPtr == IntPtr.Zero)
        {
            var (r2, p2) = f.CreateInstanceEx(cid, Vst3.IID_IEditController);
            var v2 = p2 == IntPtr.Zero ? IntPtr.Zero : QueryInterface(p2, Vst3.IID_IEditController);
            if (v2 != IntPtr.Zero) { ctrlPtr = v2; ctrlPath += $" → 同组件ID回退成功 result={r2}"; }
        }
        if (ctrlPtr != IntPtr.Zero)
        {
            _controller = new PluginEditController(ctrlPtr);
            var initR = _controller.Initialize(_hostApp.Unknown);
            var handlerR = _controller.SetComponentHandler(Handler.Unknown);
            // "连接"握手（VST3 规范）：把组件状态同步给控制器。
            // 部分插件在收到 setComponentState 之前不导出参数和编辑器视图。
            var compState = SaveState();
            int connectR = -1;
            if (compState != null && compState.Length > 0)
            {
                _stateStream.SetData(compState);
                connectR = _controller.SetComponentState(_stateStream);
            }
            var pc = _controller.GetParameterCount();
            // IConnectionPoint 连接握手（VST3 宿主规范步骤）：JUCE 等分离式控制器只有在
            // connect 之后才能拿到处理器实例——之前缺了这一步，导致参数数为 0、编辑器打不开。
            int connR1 = -1, connR2 = -1;
            var compCP = QueryInterface(compPtr, Vst3.IID_IConnectionPoint);
            var ctrlCP = QueryInterface(ctrlPtr, Vst3.IID_IConnectionPoint);
            if (compCP != IntPtr.Zero && ctrlCP != IntPtr.Zero)
            {
                connR1 = CallConnect(compCP, ctrlCP); // 组件 ← 控制器
                connR2 = CallConnect(ctrlCP, compCP); // 控制器 ← 组件（JUCE 在此拿到处理器）
            }
            var pc2 = _controller.GetParameterCount();
            DiagLog.Write($"插件「{name}」：控制器获取（{ctrlPath}），" +
                          $"initialize={initR}，setComponentHandler={handlerR}，" +
                          $"setComponentState={connectR}，握手前参数数={pc}，" +
                          $"连接点握手 comp={connR1} ctrl={connR2}，握手后参数数={pc2}");
        }
        else
        {
            DiagLog.Write($"插件「{name}」：编辑器控制器获取失败（{ctrlPath}，所有路径均未通过 QI 验证），" +
                          "插件按无编辑器处理（不影响音频处理）");
        }

        // 音频处理器接口：从组件对象 queryInterface（IAudioProcessor 不是独立可实例化的类）
        var procPtr = QueryInterface(compPtr, Vst3.IID_IAudioProcessor);
        if (procPtr == IntPtr.Zero)
            throw new InvalidOperationException($"插件不支持音频处理：{name}");
        _processor = new PluginAudioProcessor(procPtr);
    }

    private static unsafe IntPtr QueryInterface(IntPtr obj, byte[] iid)
    {
        var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj)));
        IntPtr result = IntPtr.Zero;
        var pIid = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(iid, 0, pIid, 16);
            qi(obj, pIid, &result);
        }
        finally { Marshal.FreeHGlobal(pIid); }
        return result;
    }

    /// <summary>IConnectionPoint::connect（vtable 槽位 3）。</summary>
    private static unsafe int CallConnect(IntPtr self, IntPtr other)
    {
        var fn = Marshal.GetDelegateForFunctionPointer<ConnectPointFn>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), 3 * 8));
        return fn(self, other);
    }

    // ---------------- 激活 / 停用 ----------------

    /// <summary>配置并激活插件（引擎启动或参数变化时调用，须在音频线程停止时）。</summary>
    public void Activate(int maxFrames, double sampleRate)
    {
        if (_active && _frames == maxFrames && _sampleRate == sampleRate) return;
        if (_active) Deactivate();

        _frames = maxFrames;
        _sampleRate = sampleRate;

        // 总线布局：尝试立体声进/出，失败则用插件默认
        _processor.SetBusArrangements(Vst3.kStereoArrangement, 1, Vst3.kStereoArrangement, 1);

        int inCount = _component.GetBusCount(Vst3.kAudio, Vst3.kInput);
        int outCount = _component.GetBusCount(Vst3.kAudio, Vst3.kOutput);
        if (inCount > 0)
        {
            if (_component.GetBusInfo(Vst3.kAudio, Vst3.kInput, 0) is { } bi)
            {
                _inputChannels = Math.Max(1, bi.ChannelCount);
                _component.ActivateBus(Vst3.kAudio, Vst3.kInput, 0, true);
            }
        }
        else _inputChannels = 0;
        if (outCount > 0)
        {
            if (_component.GetBusInfo(Vst3.kAudio, Vst3.kOutput, 0) is { } bo)
            {
                _outputChannels = Math.Max(1, bo.ChannelCount);
                _component.ActivateBus(Vst3.kAudio, Vst3.kOutput, 0, true);
            }
        }
        else _outputChannels = 0;

        if (_inputChannels == 0 && _outputChannels == 0)
            throw new InvalidOperationException($"{Name} 没有可用的音频总线");

        _processor.SetupProcessing(Vst3.kRealtime, Vst3.kSample32, maxFrames, sampleRate);

        AllocateBuffers(maxFrames);

        _component.SetActive(true);
        _processor.SetProcessing(true);
        _active = true;
    }

    public void Deactivate()
    {
        if (!_active) return;
        _processor.SetProcessing(false);
        _component.SetActive(false);
        _active = false;
    }

    private unsafe void AllocateBuffers(int frames)
    {
        FreeBuffers();
        _processData = Marshal.AllocHGlobal(Vst3.SizeOfProcessData);
        _inputBus = Marshal.AllocHGlobal(Vst3.SizeOfAudioBusBuffers);
        _outputBus = Marshal.AllocHGlobal(Vst3.SizeOfAudioBusBuffers);

        _inBufs = new IntPtr[Math.Max(1, _inputChannels)];
        _outBufs = new IntPtr[Math.Max(1, _outputChannels)];
        for (int i = 0; i < _inBufs.Length; i++)
            _inBufs[i] = Marshal.AllocHGlobal(frames * sizeof(float));
        for (int i = 0; i < _outBufs.Length; i++)
            _outBufs[i] = Marshal.AllocHGlobal(frames * sizeof(float));

        _inPtrs = Marshal.AllocHGlobal(_inBufs.Length * sizeof(IntPtr));
        _outPtrs = Marshal.AllocHGlobal(_outBufs.Length * sizeof(IntPtr));
        for (int i = 0; i < _inBufs.Length; i++) Marshal.WriteIntPtr(_inPtrs, i * 8, _inBufs[i]);
        for (int i = 0; i < _outBufs.Length; i++) Marshal.WriteIntPtr(_outPtrs, i * 8, _outBufs[i]);

        // AudioBusBuffers
        Marshal.WriteInt32(_inputBus, Vst3.AB_NumChannels, _inputChannels);
        Marshal.WriteInt64(_inputBus, Vst3.AB_SilenceFlags, 0);
        Marshal.WriteIntPtr(_inputBus, Vst3.AB_ChannelBuffers64, _inPtrs);
        Marshal.WriteInt32(_outputBus, Vst3.AB_NumChannels, _outputChannels);
        Marshal.WriteInt64(_outputBus, Vst3.AB_SilenceFlags, 0);
        Marshal.WriteIntPtr(_outputBus, Vst3.AB_ChannelBuffers64, _outPtrs);

        // ProcessData
        for (int off = 0; off < Vst3.SizeOfProcessData; off += 8) Marshal.WriteInt64(_processData, off, 0);
        Marshal.WriteInt32(_processData, Vst3.PD_ProcessMode, Vst3.kRealtime);
        Marshal.WriteInt32(_processData, Vst3.PD_SymbolicSampleSize, Vst3.kSample32);
        Marshal.WriteInt32(_processData, Vst3.PD_NumInputs, _inputChannels > 0 ? 1 : 0);
        Marshal.WriteInt32(_processData, Vst3.PD_NumOutputs, _outputChannels > 0 ? 1 : 0);
        Marshal.WriteIntPtr(_processData, Vst3.PD_Inputs, _inputChannels > 0 ? _inputBus : IntPtr.Zero);
        Marshal.WriteIntPtr(_processData, Vst3.PD_Outputs, _outputChannels > 0 ? _outputBus : IntPtr.Zero);
        Marshal.WriteIntPtr(_processData, Vst3.PD_InputParameterChanges, _paramChanges.Unknown);
        Marshal.WriteIntPtr(_processData, Vst3.PD_OutputParameterChanges, IntPtr.Zero);
        Marshal.WriteIntPtr(_processData, Vst3.PD_InputEvents, IntPtr.Zero);
        Marshal.WriteIntPtr(_processData, Vst3.PD_OutputEvents, IntPtr.Zero);
        Marshal.WriteIntPtr(_processData, Vst3.PD_ProcessContext, IntPtr.Zero);
    }

    private void FreeBuffers()
    {
        foreach (var p in new[] { _processData, _inputBus, _outputBus, _inPtrs, _outPtrs })
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        _processData = _inputBus = _outputBus = _inPtrs = _outPtrs = IntPtr.Zero;
        foreach (var p in _inBufs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        foreach (var p in _outBufs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        _inBufs = _outBufs = Array.Empty<IntPtr>();
    }

    // ---------------- 实时处理 ----------------

    /// <summary>音频线程调用：inL/inR 进，outL/outR 出。须先 Activate。</summary>
    public unsafe void Process(ReadOnlySpan<float> inL, ReadOnlySpan<float> inR,
                               Span<float> outL, Span<float> outR, int frames,
                               List<(uint Id, double Value)> pendingEdits)
    {
        if (!_active || Bypassed)
        {
            inL[..frames].CopyTo(outL);
            inR[..frames].CopyTo(outR);
            return;
        }

        _paramChanges.BeginBlock(pendingEdits);
        Marshal.WriteInt32(_processData, Vst3.PD_NumSamples, frames);

        // 填输入
        if (_inputChannels > 0)
        {
            var p0 = (float*)_inBufs[0];
            for (int i = 0; i < frames; i++) p0[i] = inL[i];
            if (_inputChannels > 1)
            {
                var p1 = (float*)_inBufs[1];
                for (int i = 0; i < frames; i++) p1[i] = inR[i];
            }
        }

        _processor.Process(_processData);

        // 收输出
        if (_outputChannels == 0)
        {
            outL[..frames].Clear();
            outR[..frames].Clear();
        }
        else
        {
            var p0 = (float*)_outBufs[0];
            if (_outputChannels > 1)
            {
                var p1 = (float*)_outBufs[1];
                for (int i = 0; i < frames; i++)
                {
                    outL[i] = p0[i];
                    outR[i] = p1[i];
                }
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    outL[i] = p0[i];
                    outR[i] = p0[i];
                }
            }
        }
    }

    // ---------------- 状态保存 / 恢复 ----------------

    /// <summary>读取组件完整状态（含全部参数），用于预设保存。</summary>
    public byte[]? SaveState()
    {
        try
        {
            _stateStream.SetData(Array.Empty<byte>());
            if (_component.GetState(_stateStream) != Vst3.kResultOk)
                return null;
            var data = _stateStream.ToArray();
            return data.Length > 0 ? data : null;
        }
        catch { return null; }
    }

    /// <summary>恢复组件状态并同步到控制器（编辑器界面会刷新）。</summary>
    public bool LoadState(byte[] data)
    {
        try
        {
            _stateStream.SetData(data);
            if (_component.SetState(_stateStream) != Vst3.kResultOk)
                return false;
            if (_controller != null)
            {
                _stateStream.Rewind();
                _controller.SetComponentState(_stateStream);
            }
            return true;
        }
        catch { return false; }
    }

    // ---------------- 编辑器窗口 ----------------

    // 通用参数编辑器（无 GUI 插件）所需的参数访问
    internal int ParameterCount => _controller?.GetParameterCount() ?? 0;
    internal (uint Id, string Title, string Units, int StepCount, double Default, int Flags)? ParameterInfo(int index) =>
        _controller?.GetParameterInfo(index);
    internal double GetParam(uint id) => _controller?.GetParamNormalized(id) ?? 0;
    internal void SetParam(uint id, double value) => _controller?.SetParamNormalized(id, value);

    /// <summary>打开插件编辑器窗口（须在 UI 线程调用）。插件自带界面用插件界面，
    /// 无 GUI 插件（如 Airwindows 系，createView 返回空）退回宿主通用参数编辑器。</summary>
    public void OpenEditor()
    {
        if (_editor != null && !_editor.IsDisposed)
        {
            _editor.Activate();
            return;
        }
        if (_controller == null)
            throw new InvalidOperationException($"{Name} 没有编辑器（控制器创建失败，详见 engine.log）");
        DiagLog.Write($"插件「{Name}」：请求打开编辑器，调用 createView…");
        var viewPtr = _controller.CreateView(Vst3.kEditor);
        if (viewPtr == IntPtr.Zero)
        {
            // 插件不提供自定义界面：打开通用参数编辑器
            var count = _controller.GetParameterCount();
            if (count <= 0)
                throw new InvalidOperationException($"{Name} 没有编辑器，也没有可调参数");
            DiagLog.Write($"插件「{Name}」：createView 返回空（插件无自带界面），打开通用参数编辑器（{count} 个参数）");
            var generic = new GenericParameterEditor(this);
            generic.FormClosing += (_, _) => CloseEditor();
            generic.Show();
            _editor = generic;
            return;
        }
        DiagLog.Write($"插件「{Name}」：createView 成功（0x{viewPtr.ToString("X")}），检查平台支持…");
        var view = new PluginPlugView(viewPtr);
        if (!view.IsPlatformTypeSupported(Vst3.kPlatformHWND))
            throw new InvalidOperationException($"{Name} 编辑器不支持 HWND 宿主");

        var form = new PluginEditorWindow(this, view, _plugFrame);
        _plugFrame.ResizeRequested += form.OnPluginResize;
        DiagLog.Write($"插件「{Name}」：显示编辑器窗口…");
        form.Show();
        _editor = form;
        DiagLog.Write($"插件「{Name}」：编辑器窗口已显示");
    }

    internal void CloseEditor()
    {
        if (_editor != null && !_editor.IsDisposed)
            _editor.Close();
        _editor = null;
    }

    private sealed class PluginEditorWindow : Form
    {
        private readonly PluginPlugView _view;
        private readonly HostPlugFrame _frame;
        private readonly Vst3Plugin _plugin;
        private bool _attached;

        public PluginEditorWindow(Vst3Plugin plugin, PluginPlugView view, HostPlugFrame frame)
        {
            _plugin = plugin; _view = view; _frame = frame;
            Text = $"{plugin.Name}" + (string.IsNullOrEmpty(plugin.Vendor) ? "" : $" — {plugin.Vendor}");
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = System.Drawing.Color.FromArgb(20, 20, 28);

            var (w, h) = view.GetSize();
            ClientSize = new System.Drawing.Size(Math.Max(200, w), Math.Max(150, h));
            FormClosing += (_, e) =>
            {
                if (_attached)
                {
                    _attached = false;
                    try { _view.Removed(); } catch { }
                    frame.ResizeRequested -= OnPluginResize;
                }
                _view.Dispose();
                _plugin.CloseEditor();
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_attached)
            {
                _view.SetFrame(_frame.Unknown);
                if (_view.Attached(Handle, Vst3.kPlatformHWND) == Vst3.kResultOk)
                {
                    _attached = true;
                    var (w, h) = _view.GetSize();
                    ClientSize = new System.Drawing.Size(Math.Max(200, w), Math.Max(150, h));
                }
            }
        }

        public void OnPluginResize(int w, int h)
        {
            if (IsHandleCreated)
                BeginInvoke(new Action(() =>
                {
                    ClientSize = new System.Drawing.Size(Math.Max(200, w), Math.Max(150, h));
                    try { _view.OnSize(w, h); } catch { }
                }));
        }

        protected override void WndProc(ref Message m)
        {
            // 16 = WM_ERASEBKGND，避免宿主背景闪烁盖住插件画面
            if (m.Msg == 0x14 && _attached) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }
    }

    // ---------------- 释放 ----------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseEditor();
        Deactivate();
        FreeBuffers();
        try { _component.Terminate(); } catch { }
        if (_controller != null)
        {
            try { _controller.Terminate(); } catch { }
            _controller.Dispose();
        }
        _processor.Dispose();
        _component.Dispose();
    }
}
