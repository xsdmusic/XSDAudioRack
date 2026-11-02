using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace XSDAudioRack.Vst3;

/// <summary>
/// VST3 C API 底层互操作：常量、IID、vtable 委托、宿主侧 COM 对象、插件侧接口包装。
/// 所有 vtable 顺序与结构体布局严格照官方 SDK 头文件（vst3sdk 3.7.x）编写。
/// </summary>
internal static unsafe class Vst3
{
    // ---- 结果码（Windows/COM）----
    public const int kNoInterface = unchecked((int)0x80004002);
    public const int kResultOk = 0;
    public const int kResultFalse = 1;
    public const int kNotImplemented = unchecked((int)0x80004001);
    public const int kInvalidArgument = unchecked((int)0x80070057);
    public const int kInternalError = unchecked((int)0x80004005);

    public const int kRealtime = 0;          // ProcessModes::kRealtime
    public const int kSample32 = 0;          // SymbolicSampleSizes::kSample32
    public const int kAudio = 0;             // MediaTypes::kAudio
    public const int kInput = 0;             // BusDirections::kInput
    public const int kOutput = 1;            // BusDirections::kOutput
    public const int kMain = 0;              // BusTypes::kMain
    public const byte kTrue = 1, kFalse = 0;
    public const ulong kStereoArrangement = 3; // 1<<0 | 1<<1

    public const string kAudioEffectClass = "Audio Module Class";
    public const string kComponentControllerClass = "Component Controller Class";
    public const string kEditor = "editor";
    public const string kPlatformHWND = "HWND";

    // ---- TUID（Windows COM 兼容字节布局，照 funknown.h 的 INLINE_UID）----
    public static byte[] Uid(uint l1, uint l2, uint l3, uint l4)
    {
        var b = new byte[16];
        b[0] = (byte)l1; b[1] = (byte)(l1 >> 8); b[2] = (byte)(l1 >> 16); b[3] = (byte)(l1 >> 24);
        b[4] = (byte)(l2 >> 16); b[5] = (byte)(l2 >> 24); b[6] = (byte)l2; b[7] = (byte)(l2 >> 8);
        b[8] = (byte)(l3 >> 24); b[9] = (byte)(l3 >> 16); b[10] = (byte)(l3 >> 8); b[11] = (byte)l3;
        b[12] = (byte)(l4 >> 24); b[13] = (byte)(l4 >> 16); b[14] = (byte)(l4 >> 8); b[15] = (byte)l4;
        return b;
    }

    public static readonly byte[] IID_FUnknown = Uid(0x00000000, 0x00000000, 0xC0000000, 0x00000046);
    public static readonly byte[] IID_IPluginFactory = Uid(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F);
    public static readonly byte[] IID_IPluginFactory2 = Uid(0x0007B650, 0xF24B4C0B, 0xA464EDB9, 0xF00B2ABB);
    public static readonly byte[] IID_IComponent = Uid(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    public static readonly byte[] IID_IAudioProcessor = Uid(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    public static readonly byte[] IID_IEditController = Uid(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    public static readonly byte[] IID_IComponentHandler = Uid(0x93A0BEA3, 0x0BD045DB, 0x8E890B0C, 0xC1E46AC6);
    public static readonly byte[] IID_IParameterChanges = Uid(0xA4779663, 0x0BB64A56, 0xB44384A8, 0x466FEB9D);
    public static readonly byte[] IID_IParamValueQueue = Uid(0x01263A18, 0xED074F6F, 0x98C9D356, 0x4686F9BA);
    public static readonly byte[] IID_IBStream = Uid(0xC3BF6EA2, 0x30994752, 0x9B6BF990, 0x1EE33E9B);
    public static readonly byte[] IID_IPlugView = Uid(0x5BC32507, 0xD06049EA, 0xA6151B52, 0x2B755B29);
    public static readonly byte[] IID_IPlugFrame = Uid(0x367FAF01, 0xAFA94693, 0x8D4DA2A0, 0xED0882A3);
    public static readonly byte[] IID_IHostApplication = Uid(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5);
    public static readonly byte[] IID_IConnectionPoint = Uid(0x70A4156F, 0x6E6E4026, 0x989148BF, 0xAA60D8D1);
    public static readonly byte[] IID_IMessage = Uid(0x936F033B, 0xC6C047DB, 0xBB0882F8, 0x13C1E613);
    public static readonly byte[] IID_IAttributeList = Uid(0x1E5F0AEB, 0xCC7F4533, 0xA2544011, 0x38AD5EE4);

    // ---- 结构体尺寸（x64，pack(16) 即自然对齐）----
    public const int SizeOfPClassInfo = 116;      // cid16 + cardinality4 + category32 + name64
    public const int SizeOfPClassInfo2 = 440;     // 同上 + classFlags4 + subCategories128 + vendor64 + version64 + sdkVersion64
    public const int SizeOfBusInfo = 276;         // mediaType4 + direction4 + channelCount4 + name(128*2) + busType4 + flags4
    public const int SizeOfProcessSetup = 24;     // 3*int32 + pad + double
    public const int SizeOfProcessData = 80;      // 5*int32 + pad + 7*ptr
    public const int SizeOfAudioBusBuffers = 24;  // int32 + pad + uint64 + ptr

    // ProcessData 字段偏移
    public const int PD_ProcessMode = 0, PD_SymbolicSampleSize = 4, PD_NumSamples = 8,
        PD_NumInputs = 12, PD_NumOutputs = 16, PD_Inputs = 24, PD_Outputs = 32,
        PD_InputParameterChanges = 40, PD_OutputParameterChanges = 48,
        PD_ProcessContext = 56, PD_InputEvents = 64, PD_OutputEvents = 72;
    // AudioBusBuffers 字段偏移
    public const int AB_NumChannels = 0, AB_SilenceFlags = 8, AB_ChannelBuffers64 = 16;

    public static bool IidEqual(IntPtr a, byte[] b)
    {
        for (int i = 0; i < 16; i++)
            if (Marshal.ReadByte(a, i) != b[i]) return false;
        return true;
    }

    public static string ReadAnsi(IntPtr p, int max)
    {
        var bytes = new List<byte>(max);
        for (int i = 0; i < max; i++)
        {
            var b = Marshal.ReadByte(p, i);
            if (b == 0) break;
            bytes.Add(b);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}

// ================= vtable 委托签名 =================
// vtable 前 3 个槽固定：queryInterface / addRef / release

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal unsafe delegate int QueryInterfaceFn(IntPtr self, IntPtr iid, IntPtr* objPtr);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate uint RefCountFn(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetFactoryInfoFn(IntPtr self, IntPtr info);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int CountClassesFn(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetClassInfoFn(IntPtr self, int index, IntPtr info);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal unsafe delegate int CreateInstanceFn(IntPtr self, IntPtr cid, IntPtr iid, IntPtr* objPtr);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetClassInfo2Fn(IntPtr self, int index, IntPtr info);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int InitializeFn(IntPtr self, IntPtr context);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int TerminateFn(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetControllerClassIdFn(IntPtr self, IntPtr classId);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetIoModeFn(IntPtr self, int mode);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetBusCountFn(IntPtr self, int type, int dir);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetBusInfoFn(IntPtr self, int type, int dir, int index, IntPtr bus);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int ActivateBusFn(IntPtr self, int type, int dir, int index, byte state);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetActiveFn(IntPtr self, byte state);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetStateFn(IntPtr self, IntPtr stream);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetStateFn(IntPtr self, IntPtr stream);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetBusArrangementsFn(IntPtr self, IntPtr inputs, int numIns, IntPtr outputs, int numOuts);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetBusArrangementFn(IntPtr self, int dir, int index, IntPtr arr);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int CanProcessSampleSizeFn(IntPtr self, int symbolicSampleSize);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate uint GetLatencySamplesFn(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetupProcessingFn(IntPtr self, IntPtr setup);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetProcessingFn(IntPtr self, byte state);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int ProcessFn(IntPtr self, IntPtr data);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate uint GetTailSamplesFn(IntPtr self);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetParameterCountFn(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetParameterInfoFn(IntPtr self, int index, IntPtr info);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetParamStringByValueFn(IntPtr self, uint id, double value, IntPtr str128);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetParamValueByStringFn(IntPtr self, uint id, IntPtr str, IntPtr valuePtr);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate double NormalizedParamToPlainFn(IntPtr self, uint id, double value);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate double PlainParamToNormalizedFn(IntPtr self, uint id, double plain);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate double GetParamNormalizedFn(IntPtr self, uint id);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetParamNormalizedFn(IntPtr self, uint id, double value);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetComponentHandlerFn(IntPtr self, IntPtr handler);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate IntPtr CreateViewFn(IntPtr self, IntPtr name);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int IsPlatformTypeSupportedFn(IntPtr self, IntPtr type);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int AttachedFn(IntPtr self, IntPtr parent, IntPtr type);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int RemovedFn(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int OnWheelFn(IntPtr self, float distance);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int OnKeyFn(IntPtr self, ushort key, short keyCode, short modifiers);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetSizeFn(IntPtr self, IntPtr rect);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int OnSizeFn(IntPtr self, IntPtr rect);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int OnFocusFn(IntPtr self, byte state);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetFrameFn(IntPtr self, IntPtr frame);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int CanResizeFn(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int CheckSizeConstraintFn(IntPtr self, IntPtr rect);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int BeginEndEditFn(IntPtr self, uint id);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int PerformEditFn(IntPtr self, uint id, double value);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int RestartComponentFn(IntPtr self, int flags);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate IntPtr GetParameterDataFn(IntPtr self, int index);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate IntPtr AddParameterDataFn(IntPtr self, IntPtr id, IntPtr indexPtr);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate uint GetParameterIdFn(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetPointFn(IntPtr self, int index, IntPtr sampleOffset, IntPtr value);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int AddPointFn(IntPtr self, int sampleOffset, double value, IntPtr indexPtr);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int ReadFn(IntPtr self, IntPtr buffer, int numBytes, IntPtr numBytesRead);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int WriteFn(IntPtr self, IntPtr buffer, int numBytes, IntPtr numBytesWritten);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SeekFn(IntPtr self, long pos, int mode, IntPtr result);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int TellFn(IntPtr self, IntPtr pos);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int ResizeViewFn(IntPtr self, IntPtr view, IntPtr rect);

// IConnectionPoint::connect（槽位 3）
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int ConnectPointFn(IntPtr self, IntPtr other);

// IMessage（槽位 3-5）
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate IntPtr GetMessageIDFn(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void SetMessageIDFn(IntPtr self, IntPtr id);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate IntPtr GetAttributesFn(IntPtr self);

// IAttributeList（槽位 3-10）
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetIntFn(IntPtr self, IntPtr id, long value);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetIntFn(IntPtr self, IntPtr id, IntPtr valuePtr);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetFloatFn(IntPtr self, IntPtr id, double value);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetFloatFn(IntPtr self, IntPtr id, IntPtr valuePtr);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetStringFn(IntPtr self, IntPtr id, IntPtr str);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetStringFn(IntPtr self, IntPtr id, IntPtr outStr, uint sizeInBytes);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int SetBinaryFn(IntPtr self, IntPtr id, IntPtr data, uint sizeInBytes);
[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetBinaryFn(IntPtr self, IntPtr id, IntPtr outDataPtr, IntPtr outSizePtr);[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetNameFn(IntPtr self, IntPtr name);

// ================= 宿主侧 COM 对象基类 =================

/// <summary>
/// 宿主实现的 COM 对象：一块 8 字节内存（首字段指向 vtable）。
/// 子类在构造函数里 Bind 各槽位。对象随宿主进程存活，release 只减计数不销毁。
/// </summary>
public abstract unsafe class HostComObject
{
    private IntPtr _objectPtr;
    private readonly object?[] _delegates;
    private int _refCount = 1;
    private bool _destroyed;

    protected IntPtr Vtable { get; }
    public IntPtr Unknown => _destroyed ? IntPtr.Zero : _objectPtr;

    protected abstract byte[] InterfaceIid { get; }

    /// <summary>消息类临时对象（IMessage/IAttributeList）为 true：引用归零即销毁，防泄漏。</summary>
    protected virtual bool IsTransient => false;

    protected HostComObject(int slotCount)
    {
        _delegates = new object[slotCount];
        Vtable = Marshal.AllocHGlobal(slotCount * 8);
        _objectPtr = Marshal.AllocHGlobal(8);
        Marshal.WriteIntPtr(_objectPtr, Vtable);

        Bind(0, (QueryInterfaceFn)QueryInterfaceImpl);
        Bind(1, (RefCountFn)AddRefImpl);
        Bind(2, (RefCountFn)ReleaseImpl);
    }

    protected void Bind(int slot, Delegate d)
    {
        _delegates[slot] = d;
        Marshal.WriteIntPtr(Vtable, slot * 8, Marshal.GetFunctionPointerForDelegate(d));
    }

    private int QueryInterfaceImpl(IntPtr self, IntPtr iid, IntPtr* objPtr)
    {
        if (_destroyed) { *objPtr = IntPtr.Zero; return Vst3.kNoInterface; }
        if (Vst3.IidEqual(iid, Vst3.IID_FUnknown) || Vst3.IidEqual(iid, InterfaceIid))
        {
            AddRefImpl(self);
            *objPtr = _objectPtr;
            return Vst3.kResultOk;
        }
        *objPtr = IntPtr.Zero;
        return Vst3.kNoInterface;
    }

    internal uint AddRefImpl(IntPtr self) => (uint)Interlocked.Increment(ref _refCount);

    private uint ReleaseImpl(IntPtr self)
    {
        uint r = (uint)Interlocked.Decrement(ref _refCount);
        if (r == 0 && IsTransient) Destroy();
        return r;
    }

    private void Destroy()
    {
        if (_destroyed) return;
        _destroyed = true;
        try { OnDestroy(); } catch { }
        Marshal.FreeHGlobal(Vtable);
        Marshal.FreeHGlobal(_objectPtr);
        _objectPtr = IntPtr.Zero;
    }

    /// <summary>销毁前释放子类资源的钩子（如固定句柄）。</summary>
    protected virtual void OnDestroy() { }
}

// ================= 宿主侧各接口实现 =================

/// <summary>IBStream：内存流（getState/setState 用）。</summary>
internal sealed class HostStream : HostComObject
{
    private readonly MemoryStream _ms = new();
    public HostStream() : base(7)
    {
        Bind(3, (ReadFn)ReadImpl);
        Bind(4, (WriteFn)WriteImpl);
        Bind(5, (SeekFn)SeekImpl);
        Bind(6, (TellFn)TellImpl);
    }
    protected override byte[] InterfaceIid => Vst3.IID_IBStream;

    public byte[] ToArray() => _ms.ToArray();
    public void SetData(byte[] data) { _ms.SetLength(0); _ms.Write(data, 0, data.Length); _ms.Position = 0; }
    public void Rewind() => _ms.Position = 0;

    private unsafe int ReadImpl(IntPtr self, IntPtr buffer, int numBytes, IntPtr numBytesRead)
    {
        var tmp = new byte[numBytes];
        int read = _ms.Read(tmp, 0, numBytes);
        if (read > 0) Marshal.Copy(tmp, 0, buffer, read);
        if (numBytesRead != IntPtr.Zero) Marshal.WriteInt32(numBytesRead, read);
        return Vst3.kResultOk;
    }

    private unsafe int WriteImpl(IntPtr self, IntPtr buffer, int numBytes, IntPtr numBytesWritten)
    {
        var tmp = new byte[numBytes];
        Marshal.Copy(buffer, tmp, 0, numBytes);
        _ms.Write(tmp, 0, numBytes);
        if (numBytesWritten != IntPtr.Zero) Marshal.WriteInt32(numBytesWritten, numBytes);
        return Vst3.kResultOk;
    }

    private unsafe int SeekImpl(IntPtr self, long pos, int mode, IntPtr result)
    {
        long newPos = mode switch
        {
            0 => pos,                       // kIBSeekSet
            1 => _ms.Position + pos,        // kIBSeekCur
            _ => _ms.Length + pos,          // kIBSeekEnd
        };
        _ms.Position = Math.Max(0, newPos);
        if (result != IntPtr.Zero) Marshal.WriteInt64(result, _ms.Position);
        return Vst3.kResultOk;
    }

    private unsafe int TellImpl(IntPtr self, IntPtr pos)
    {
        if (pos != IntPtr.Zero) Marshal.WriteInt64(pos, _ms.Position);
        return Vst3.kResultOk;
    }
}

/// <summary>IParamValueQueue：单个参数在本块内的变化点。</summary>
internal sealed class HostParamValueQueue : HostComObject
{
    public uint ParamId;
    private readonly List<(int Offset, double Value)> _points = new();

    public HostParamValueQueue() : base(7)
    {
        Bind(3, (GetParameterIdFn)GetParameterIdImpl);
        Bind(4, (GetParameterCountFn)GetPointCountImpl);
        Bind(5, (GetPointFn)GetPointImpl);
        Bind(6, (AddPointFn)AddPointImpl);
    }
    protected override byte[] InterfaceIid => Vst3.IID_IParamValueQueue;

    public void Reset(uint id) { ParamId = id; _points.Clear(); }
    public void Push(int offset, double value) => _points.Add((offset, value));

    private uint GetParameterIdImpl(IntPtr self) => ParamId;
    private int GetPointCountImpl(IntPtr self) => _points.Count;

    private unsafe int GetPointImpl(IntPtr self, int index, IntPtr sampleOffset, IntPtr value)
    {
        if (index < 0 || index >= _points.Count) return Vst3.kInvalidArgument;
        if (sampleOffset != IntPtr.Zero) Marshal.WriteInt32(sampleOffset, _points[index].Offset);
        if (value != IntPtr.Zero) Marshal.WriteInt64(value, BitConverter.DoubleToInt64Bits(_points[index].Value));
        return Vst3.kResultOk;
    }

    private unsafe int AddPointImpl(IntPtr self, int sampleOffset, double value, IntPtr indexPtr)
    {
        _points.Add((sampleOffset, value));
        if (indexPtr != IntPtr.Zero) Marshal.WriteInt32(indexPtr, _points.Count - 1);
        return Vst3.kResultOk;
    }
}

/// <summary>IParameterChanges：一个处理块内所有参数变化。</summary>
internal sealed class HostParameterChanges : HostComObject
{
    private const int MaxQueues = 32;
    private readonly HostParamValueQueue[] _queues;
    private int _activeCount;

    public HostParameterChanges() : base(7)
    {
        _queues = new HostParamValueQueue[MaxQueues];
        for (int i = 0; i < MaxQueues; i++) _queues[i] = new HostParamValueQueue();
        Bind(3, (GetParameterCountFn)GetParameterCountImpl);
        Bind(4, (GetParameterDataFn)GetParameterDataImpl);
        Bind(5, (AddParameterDataFn)AddParameterDataImpl);
    }
    protected override byte[] InterfaceIid => Vst3.IID_IParameterChanges;

    /// <summary>音频线程每块前调用：把 UI 传来的待处理编辑装进队列。</summary>
    public void BeginBlock(List<(uint Id, double Value)> edits)
    {
        _activeCount = 0;
        int i = 0;
        foreach (var (id, value) in edits)
        {
            if (i >= MaxQueues) break;
            // 同一参数只保留最后一次
            bool dup = false;
            for (int j = 0; j < i; j++)
                if (_queues[j].ParamId == id) { dup = true; break; }
            if (dup) continue;
            _queues[i].Reset(id);
            _queues[i].Push(0, value);
            i++;
        }
        _activeCount = i;
    }

    private int GetParameterCountImpl(IntPtr self) => _activeCount;

    private IntPtr GetParameterDataImpl(IntPtr self, int index) =>
        index >= 0 && index < _activeCount ? _queues[index].Unknown : IntPtr.Zero;

    private unsafe IntPtr AddParameterDataImpl(IntPtr self, IntPtr id, IntPtr indexPtr)
    {
        uint pid = (uint)Marshal.ReadInt32(id);
        foreach (var q in _queues)
            if (q.ParamId == pid) return q.Unknown;
        if (_activeCount < MaxQueues)
        {
            _queues[_activeCount].Reset(pid);
            if (indexPtr != IntPtr.Zero) Marshal.WriteInt32(indexPtr, _activeCount);
            return _queues[_activeCount++].Unknown;
        }
        return IntPtr.Zero;
    }
}

/// <summary>IComponentHandler：插件 UI 通过它把参数编辑发给宿主。</summary>
public sealed class HostComponentHandler : HostComObject
{
    /// <summary>待写入下一处理块的编辑（performEdit 持续覆盖同 id，音频线程取走后清空）。</summary>
    public readonly Dictionary<uint, double> PendingEdits = new();
    public event Action<uint, double>? EditPerformed;
    public event Action<int>? RestartRequested;

    public HostComponentHandler() : base(7)
    {
        Bind(3, (BeginEndEditFn)BeginEditImpl);
        Bind(4, (PerformEditFn)PerformEditImpl);
        Bind(5, (BeginEndEditFn)EndEditImpl);
        Bind(6, (RestartComponentFn)RestartComponentImpl);
    }
    protected override byte[] InterfaceIid => Vst3.IID_IComponentHandler;

    /// <summary>音频线程：取走全部待处理编辑。</summary>
    public List<(uint Id, double Value)> TakePendingEdits()
    {
        var list = new List<(uint, double)>(PendingEdits.Count);
        lock (PendingEdits)
        {
            foreach (var kv in PendingEdits) list.Add((kv.Key, kv.Value));
            PendingEdits.Clear();
        }
        return list;
    }

    private int BeginEditImpl(IntPtr self, uint id) => Vst3.kResultOk;
    private int EndEditImpl(IntPtr self, uint id) => Vst3.kResultOk;

    private int PerformEditImpl(IntPtr self, uint id, double value)
    {
        lock (PendingEdits)
        {
            PendingEdits[id] = value;
        }
        EditPerformed?.Invoke(id, value);
        return Vst3.kResultOk;
    }

    private int RestartComponentImpl(IntPtr self, int flags)
    {
        RestartRequested?.Invoke(flags);
        return Vst3.kResultOk;
    }
}

/// <summary>IPlugFrame：插件编辑器请求宿主调整视图大小。</summary>
internal sealed class HostPlugFrame : HostComObject
{
    public event Action<int, int>? ResizeRequested;

    public HostPlugFrame() : base(4)
    {
        Bind(3, (ResizeViewFn)ResizeViewImpl);
    }
    protected override byte[] InterfaceIid => Vst3.IID_IPlugFrame;

    private unsafe int ResizeViewImpl(IntPtr self, IntPtr view, IntPtr rect)
    {
        int w = Marshal.ReadInt32(rect, 8) - Marshal.ReadInt32(rect, 0);
        int h = Marshal.ReadInt32(rect, 12) - Marshal.ReadInt32(rect, 4);
        ResizeRequested?.Invoke(w, h);
        return Vst3.kResultOk;
    }
}

/// <summary>IAttributeList：IMessage 的属性表（插件向消息里写/读数据用）。</summary>
internal sealed unsafe class HostAttributeList : HostComObject
{
    protected override byte[] InterfaceIid => Vst3.IID_IAttributeList;
    protected override bool IsTransient => true;

    private sealed class Bin
    {
        public byte[] Data;
        public GCHandle Pin;
        public Bin(byte[] data) { Data = data; Pin = GCHandle.Alloc(data, GCHandleType.Pinned); }
    }

    private readonly Dictionary<string, object> _map = new();

    public HostAttributeList() : base(11)
    {
        Bind(3, (SetIntFn)SetIntImpl);
        Bind(4, (GetIntFn)GetIntImpl);
        Bind(5, (SetFloatFn)SetFloatImpl);
        Bind(6, (GetFloatFn)GetFloatImpl);
        Bind(7, (SetStringFn)SetStringImpl);
        Bind(8, (GetStringFn)GetStringImpl);
        Bind(9, (SetBinaryFn)SetBinaryImpl);
        Bind(10, (GetBinaryFn)GetBinaryImpl);
    }

    private static string Key(IntPtr id) => Marshal.PtrToStringAnsi(id) ?? "";

    private int SetIntImpl(IntPtr self, IntPtr id, long value)
    {
        lock (_map) _map[Key(id)] = value;
        return Vst3.kResultOk;
    }

    private unsafe int GetIntImpl(IntPtr self, IntPtr id, IntPtr valuePtr)
    {
        lock (_map)
        {
            if (_map.TryGetValue(Key(id), out var v) && v is long l && valuePtr != IntPtr.Zero)
            {
                *(long*)valuePtr = l;
                return Vst3.kResultOk;
            }
        }
        return Vst3.kResultFalse;
    }

    private int SetFloatImpl(IntPtr self, IntPtr id, double value)
    {
        lock (_map) _map[Key(id)] = value;
        return Vst3.kResultOk;
    }

    private unsafe int GetFloatImpl(IntPtr self, IntPtr id, IntPtr valuePtr)
    {
        lock (_map)
        {
            if (_map.TryGetValue(Key(id), out var v) && v is double d && valuePtr != IntPtr.Zero)
            {
                *(double*)valuePtr = d;
                return Vst3.kResultOk;
            }
        }
        return Vst3.kResultFalse;
    }

    private unsafe int SetStringImpl(IntPtr self, IntPtr id, IntPtr str)
    {
        lock (_map) _map[Key(id)] = str == IntPtr.Zero ? "" : Marshal.PtrToStringUni(str) ?? "";
        return Vst3.kResultOk;
    }

    private unsafe int GetStringImpl(IntPtr self, IntPtr id, IntPtr outStr, uint sizeInBytes)
    {
        lock (_map)
        {
            if (_map.TryGetValue(Key(id), out var v) && v is string s && outStr != IntPtr.Zero)
            {
                fixed (char* p = s)
                    Buffer.MemoryCopy(p, (void*)outStr, sizeInBytes, Math.Min((s.Length + 1) * 2, (int)sizeInBytes));
                return Vst3.kResultOk;
            }
        }
        return Vst3.kResultFalse;
    }

    private int SetBinaryImpl(IntPtr self, IntPtr id, IntPtr data, uint sizeInBytes)
    {
        var bytes = new byte[sizeInBytes];
        if (sizeInBytes > 0) Marshal.Copy(data, bytes, 0, (int)sizeInBytes);
        lock (_map)
        {
            var key = Key(id);
            if (_map.TryGetValue(key, out var old) && old is Bin b) b.Pin.Free();
            _map[key] = new Bin(bytes);
        }
        return Vst3.kResultOk;
    }

    private unsafe int GetBinaryImpl(IntPtr self, IntPtr id, IntPtr outDataPtr, IntPtr outSizePtr)
    {
        lock (_map)
        {
            if (_map.TryGetValue(Key(id), out var v) && v is Bin b)
            {
                if (outDataPtr != IntPtr.Zero) *(IntPtr*)outDataPtr = b.Pin.AddrOfPinnedObject();
                if (outSizePtr != IntPtr.Zero) *(uint*)outSizePtr = (uint)b.Data.Length;
                return Vst3.kResultOk;
            }
        }
        return Vst3.kResultFalse;
    }

    protected override void OnDestroy()
    {
        foreach (var v in _map.Values) if (v is Bin b) b.Pin.Free();
        _map.Clear();
    }
}

/// <summary>IMessage：组件与控制器之间传递消息的对象（电平表等实时数据用）。</summary>
internal sealed class HostMessage : HostComObject
{
    protected override byte[] InterfaceIid => Vst3.IID_IMessage;
    protected override bool IsTransient => true;

    private readonly HostAttributeList _attributes = new();
    private IntPtr _id = IntPtr.Zero;

    public HostMessage() : base(6)
    {
        Bind(3, (GetMessageIDFn)GetIdImpl);
        Bind(4, (SetMessageIDFn)SetIdImpl);
        Bind(5, (GetAttributesFn)GetAttrsImpl);
    }

    private IntPtr GetIdImpl(IntPtr self) => _id;

    private void SetIdImpl(IntPtr self, IntPtr id)
    {
        if (_id != IntPtr.Zero) Marshal.FreeHGlobal(_id);
        _id = id == IntPtr.Zero ? IntPtr.Zero : Marshal.StringToHGlobalAnsi(Marshal.PtrToStringAnsi(id) ?? "");
    }

    private IntPtr GetAttrsImpl(IntPtr self)
    {
        _attributes.AddRefImpl(self);
        return _attributes.Unknown;
    }

    protected override void OnDestroy()
    {
        if (_id != IntPtr.Zero) Marshal.FreeHGlobal(_id);
        _id = IntPtr.Zero;
    }
}

/// <summary>IHostApplication：传给插件 initialize 的宿主上下文。</summary>
internal sealed unsafe class HostApplication : HostComObject
{
    public HostApplication() : base(5)
    {
        Bind(3, (GetNameFn)GetNameImpl);
        Bind(4, (CreateInstanceFn)CreateInstanceImpl);
    }
    protected override byte[] InterfaceIid => Vst3.IID_IHostApplication;

    private unsafe int GetNameImpl(IntPtr self, IntPtr name)
    {
        if (name != IntPtr.Zero)
        {
            var s = "XSDAudioRack\0";
            fixed (char* p = s)
                Buffer.MemoryCopy(p, (void*)name, 256, s.Length * 2);
        }
        return Vst3.kResultOk;
    }

    private unsafe int CreateInstanceImpl(IntPtr self, IntPtr cid, IntPtr iid, IntPtr* objPtr)
    {
        if (objPtr != null) *objPtr = IntPtr.Zero;
        // IMessage / IAttributeList：插件（尤其 FabFilter 等）传实时数据的必需宿主接口
        if (iid != IntPtr.Zero && (Vst3.IidEqual(iid, Vst3.IID_IMessage) || (cid != IntPtr.Zero && Vst3.IidEqual(cid, Vst3.IID_IMessage))))
        {
            var m = new HostMessage();
            if (objPtr != null) *objPtr = m.Unknown;
            return Vst3.kResultOk;
        }
        if (iid != IntPtr.Zero && (Vst3.IidEqual(iid, Vst3.IID_IAttributeList) || (cid != IntPtr.Zero && Vst3.IidEqual(cid, Vst3.IID_IAttributeList))))
        {
            var a = new HostAttributeList();
            if (objPtr != null) *objPtr = a.Unknown;
            return Vst3.kResultOk;
        }
        return Vst3.kNoInterface;
    }
}

// ================= 插件侧接口包装 =================

/// <summary>插件侧 COM 接口智能包装：按槽位调用虚函数，Dispose 时 release。</summary>
internal abstract unsafe class PluginInterface : IDisposable
{
    public IntPtr Ptr;
    private readonly IntPtr _vtable;
    private readonly Delegate?[] _cache;
    private bool _released;

    protected PluginInterface(IntPtr ptr, int slotCount)
    {
        Ptr = ptr;
        _vtable = Marshal.ReadIntPtr(ptr);
        _cache = new Delegate?[slotCount];
    }

    protected T Fn<T>(int slot) where T : Delegate
    {
        var d = _cache[slot];
        if (d == null)
        {
            d = Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(_vtable, slot * 8));
            _cache[slot] = d;
        }
        return (T)d;
    }

    public void Dispose()
    {
        if (!_released && Ptr != IntPtr.Zero)
        {
            _released = true;
            Fn<RefCountFn>(2)(Ptr);
            Ptr = IntPtr.Zero;
        }
    }
}

internal sealed unsafe class PluginFactory : PluginInterface
{
    public PluginFactory(IntPtr ptr) : base(ptr, 7) { }

    public (string Vendor, string Url) GetFactoryInfo()
    {
        var mem = Marshal.AllocHGlobal(452);
        try
        {
            NativeMemory.Clear((void*)mem, 452);
            Fn<GetFactoryInfoFn>(3)(Ptr, mem);
            return (Vst3.ReadAnsi(mem, 64), Vst3.ReadAnsi(mem + 64, 256));
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public int CountClasses() => Fn<CountClassesFn>(4)(Ptr);

    public (byte[] Cid, string Category, string Name) GetClassInfo(int index)
    {
        var mem = Marshal.AllocHGlobal(Vst3.SizeOfPClassInfo);
        try
        {
            NativeMemory.Clear((void*)mem, Vst3.SizeOfPClassInfo);
            Fn<GetClassInfoFn>(5)(Ptr, index, mem);
            var cid = new byte[16];
            Marshal.Copy(mem, cid, 0, 16);
            var category = Vst3.ReadAnsi(mem + 20, 32);
            var name = Vst3.ReadAnsi(mem + 52, 64);
            return (cid, category, name);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public IntPtr CreateInstance(byte[] cid, byte[] iid) => CreateInstanceEx(cid, iid).Ptr;

    /// <summary>创建实例并返回 (tresult, 指针)，便于诊断失败原因。</summary>
    public unsafe (int Result, IntPtr Ptr) CreateInstanceEx(byte[] cid, byte[] iid)
    {
        var obj = IntPtr.Zero;
        var pCid = Marshal.AllocHGlobal(16);
        var pIid = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(cid, 0, pCid, 16);
            Marshal.Copy(iid, 0, pIid, 16);
            var r = Fn<CreateInstanceFn>(6)(Ptr, pCid, pIid, &obj);
            return (r, obj);
        }
        finally
        {
            Marshal.FreeHGlobal(pCid);
            Marshal.FreeHGlobal(pIid);
        }
    }

    /// <summary>queryInterface(IPluginFactory2) 后取 PClassInfo2（含版本/子类别），失败返回 null。</summary>
    public (byte[] Cid, string Name, string SubCategories, string Vendor, string Version)? GetClassInfo2(int index)
    {
        var factory2 = IntPtr.Zero;
        var qi = Fn<QueryInterfaceFn>(0);
        fixed (byte* iid = Vst3.IID_IPluginFactory2)
        {
            if (qi(Ptr, (IntPtr)iid, &factory2) != Vst3.kResultOk || factory2 == IntPtr.Zero)
                return null;
        }
        try
        {
            var mem = Marshal.AllocHGlobal(Vst3.SizeOfPClassInfo2);
            try
            {
                NativeMemory.Clear((void*)mem, Vst3.SizeOfPClassInfo2);
                var vt = Marshal.ReadIntPtr(factory2);
                var getClassInfo2 = Marshal.GetDelegateForFunctionPointer<GetClassInfo2Fn>(
                    Marshal.ReadIntPtr(vt, 7 * 8));
                if (getClassInfo2(factory2, index, mem) != Vst3.kResultOk)
                    return null;
                var cid = new byte[16];
                Marshal.Copy(mem, cid, 0, 16);
                return (cid,
                    Vst3.ReadAnsi(mem + 52, 64),        // name
                    Vst3.ReadAnsi(mem + 120, 128),      // subCategories
                    Vst3.ReadAnsi(mem + 248, 64),       // vendor
                    Vst3.ReadAnsi(mem + 312, 64));      // version
            }
            finally { Marshal.FreeHGlobal(mem); }
        }
        finally
        {
            var rel = Marshal.GetDelegateForFunctionPointer<RefCountFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory2), 2 * 8));
            rel(factory2);
        }
    }
}

/// <summary>Vst::IComponent（14 槽）。</summary>
internal sealed unsafe class PluginComponent : PluginInterface
{
    public PluginComponent(IntPtr ptr) : base(ptr, 14) { }

    public byte[]? GetControllerClassId()
    {
        var mem = Marshal.AllocHGlobal(16);
        try
        {
            NativeMemory.Clear((void*)mem, 16);
            var r = Fn<GetControllerClassIdFn>(5)(Ptr, mem);
            if (r != Vst3.kResultOk) return null;
            var cid = new byte[16];
            Marshal.Copy(mem, cid, 0, 16);
            return cid;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public int GetBusCount(int mediaType, int dir) => Fn<GetBusCountFn>(7)(Ptr, mediaType, dir);

    public (int ChannelCount, string Name, bool DefaultActive)? GetBusInfo(int mediaType, int dir, int index)
    {
        var mem = Marshal.AllocHGlobal(Vst3.SizeOfBusInfo);
        try
        {
            NativeMemory.Clear((void*)mem, Vst3.SizeOfBusInfo);
            if (Fn<GetBusInfoFn>(8)(Ptr, mediaType, dir, index, mem) != Vst3.kResultOk)
                return null;
            int channelCount = Marshal.ReadInt32(mem, 8);
            string name = System.Text.Encoding.Unicode.GetString(
                ReadBytes(mem + 12, 256)).TrimEnd('\0');
            int flags = Marshal.ReadInt32(mem, 272);
            return (channelCount, name, (flags & 1) != 0);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public int ActivateBus(int mediaType, int dir, int index, bool state) =>
        Fn<ActivateBusFn>(10)(Ptr, mediaType, dir, index, state ? Vst3.kTrue : Vst3.kFalse);

    public int SetActive(bool state) => Fn<SetActiveFn>(11)(Ptr, state ? Vst3.kTrue : Vst3.kFalse);
    public int SetState(HostStream stream) => Fn<SetStateFn>(12)(Ptr, stream.Unknown);
    public int GetState(HostStream stream) => Fn<GetStateFn>(13)(Ptr, stream.Unknown);
    public int Initialize(IntPtr context) => Fn<InitializeFn>(3)(Ptr, context);
    public int Terminate() => Fn<TerminateFn>(4)(Ptr);

    private static byte[] ReadBytes(IntPtr p, int count)
    {
        var b = new byte[count];
        Marshal.Copy(p, b, 0, count);
        return b;
    }
}

/// <summary>Vst::IAudioProcessor（11 槽，组件对象 queryInterface 而来）。</summary>
internal sealed unsafe class PluginAudioProcessor : PluginInterface
{
    public PluginAudioProcessor(IntPtr ptr) : base(ptr, 11) { }

    public int SetBusArrangements(ulong input, int numIns, ulong output, int numOuts)
    {
        var mem = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(mem, 0, (long)input);
            Marshal.WriteInt64(mem, 8, (long)output);
            return Fn<SetBusArrangementsFn>(3)(Ptr, mem, numIns, mem + 8, numOuts);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public int SetupProcessing(int processMode, int symbolicSampleSize, int maxSamplesPerBlock, double sampleRate)
    {
        var mem = Marshal.AllocHGlobal(Vst3.SizeOfProcessSetup);
        try
        {
            Marshal.WriteInt32(mem, 0, processMode);
            Marshal.WriteInt32(mem, 4, symbolicSampleSize);
            Marshal.WriteInt32(mem, 8, maxSamplesPerBlock);
            Marshal.WriteInt64(mem, 16, BitConverter.DoubleToInt64Bits(sampleRate));
            return Fn<SetupProcessingFn>(7)(Ptr, mem);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public int SetProcessing(bool state) => Fn<SetProcessingFn>(8)(Ptr, state ? Vst3.kTrue : Vst3.kFalse);
    public int Process(IntPtr processData) => Fn<ProcessFn>(9)(Ptr, processData);
}

/// <summary>Vst::IEditController（18 槽）。</summary>
internal sealed unsafe class PluginEditController : PluginInterface
{
    public PluginEditController(IntPtr ptr) : base(ptr, 18) { }

    public int Initialize(IntPtr context) => Fn<InitializeFn>(3)(Ptr, context);
    public int Terminate() => Fn<TerminateFn>(4)(Ptr);
    public int SetComponentState(HostStream stream) => Fn<SetStateFn>(5)(Ptr, stream.Unknown);
    public int SetState(HostStream stream) => Fn<SetStateFn>(6)(Ptr, stream.Unknown);
    public int GetState(HostStream stream) => Fn<GetStateFn>(7)(Ptr, stream.Unknown);
    public int SetComponentHandler(IntPtr handler) => Fn<SetComponentHandlerFn>(16)(Ptr, handler);

    public int GetParameterCount() => Fn<GetParameterCountFn>(8)(Ptr);

    /// <summary>读取参数信息。ParameterInfo 布局（x64）：
    /// id(0) / title[128]char16(4) / shortTitle(260) / units(516) / stepCount(772)
    /// / default double(780) / unitId(788) / flags(792)，共 796 字节。</summary>
    public (uint Id, string Title, string Units, int StepCount, double Default, int Flags)? GetParameterInfo(int index)
    {
        var mem = Marshal.AllocHGlobal(800);
        try
        {
            NativeMemory.Clear((void*)mem, 800);
            if (Fn<GetParameterInfoFn>(9)(Ptr, index, mem) != Vst3.kResultOk) return null;
            var id = (uint)Marshal.ReadInt32(mem, 0);
            var title = System.Text.Encoding.Unicode.GetString(ReadBytes(mem + 4, 256)).TrimEnd('\0');
            var units = System.Text.Encoding.Unicode.GetString(ReadBytes(mem + 516, 256)).TrimEnd('\0');
            var stepCount = Marshal.ReadInt32(mem, 772);
            var def = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(mem, 780));
            var flags = Marshal.ReadInt32(mem, 792);
            return (id, title, units, stepCount, def, flags);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public double GetParamNormalized(uint id) => Fn<GetParamNormalizedFn>(14)(Ptr, id);
    public int SetParamNormalized(uint id, double value) => Fn<SetParamNormalizedFn>(15)(Ptr, id, value);

    private static byte[] ReadBytes(IntPtr p, int count)
    {
        var b = new byte[count];
        Marshal.Copy(p, b, 0, count);
        return b;
    }

    public IntPtr CreateView(string name)
    {
        // FIDString 是 UTF-8（char8*）
        var pAnsi = Marshal.StringToHGlobalAnsi(name);
        try
        {
            return Fn<CreateViewFn>(17)(Ptr, pAnsi);
        }
        finally { Marshal.FreeHGlobal(pAnsi); }
    }
}

/// <summary>IPlugView（15 槽）：插件编辑器视图。</summary>
internal sealed unsafe class PluginPlugView : PluginInterface
{
    public PluginPlugView(IntPtr ptr) : base(ptr, 15) { }

    public bool IsPlatformTypeSupported(string type)
    {
        var p = Marshal.StringToHGlobalAnsi(type);
        try { return Fn<IsPlatformTypeSupportedFn>(3)(Ptr, p) == Vst3.kResultOk; }
        finally { Marshal.FreeHGlobal(p); }
    }

    public int Attached(IntPtr parent, string type)
    {
        var p = Marshal.StringToHGlobalAnsi(type);
        try { return Fn<AttachedFn>(4)(Ptr, parent, p); }
        finally { Marshal.FreeHGlobal(p); }
    }

    public int Removed() => Fn<RemovedFn>(5)(Ptr);

    public (int W, int H) GetSize()
    {
        var mem = Marshal.AllocHGlobal(16);
        try
        {
            NativeMemory.Clear((void*)mem, 16);
            Fn<GetSizeFn>(9)(Ptr, mem);
            int l = Marshal.ReadInt32(mem, 0), t = Marshal.ReadInt32(mem, 4);
            int r = Marshal.ReadInt32(mem, 8), b = Marshal.ReadInt32(mem, 12);
            return (r - l, b - t);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public int OnSize(int w, int h)
    {
        var mem = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(mem, 0, 0);
            Marshal.WriteInt32(mem, 4, 0);
            Marshal.WriteInt32(mem, 8, w);
            Marshal.WriteInt32(mem, 12, h);
            return Fn<OnSizeFn>(10)(Ptr, mem);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    public int SetFrame(IntPtr plugFrame) => Fn<SetFrameFn>(12)(Ptr, plugFrame);
}
