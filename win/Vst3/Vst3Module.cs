using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace XSDAudioRack.Vst3;

/// <summary>一个已加载的 VST3 模块（.vst3 DLL）及其工厂。</summary>
internal sealed class Vst3Module : IDisposable
{
    private IntPtr _dll;
    private PluginFactory? _factory;
    private bool _initDone;

    public string Path { get; }
    public PluginFactory Factory => _factory ?? throw new InvalidOperationException("模块未初始化");
    public string FactoryVendor { get; private set; } = "";

    private Vst3Module(string path) { Path = path; }

    public static Vst3Module? TryLoad(string modulePath)
    {
        var m = new Vst3Module(modulePath);
        try
        {
            m.Load();
            return m;
        }
        catch
        {
            m.Dispose();
            return null;
        }
    }

    private void Load()
    {
        _dll = NativeLibrary.Load(Path);
        if (_dll == IntPtr.Zero)
            throw new DllNotFoundException("无法加载模块 " + Path);

        // InitDll 可选，先调一次（官方宿主在 Windows 上如此做）
        if (NativeLibrary.TryGetExport(_dll, "InitDll", out var initDll))
        {
            var init = Marshal.GetDelegateForFunctionPointer<Fn0>(initDll);
            init();
            _initDone = true;
        }

        if (!NativeLibrary.TryGetExport(_dll, "GetPluginFactory", out var getFactory))
            throw new EntryPointNotFoundException("缺 GetPluginFactory 导出");

        var factoryPtr = Marshal.GetDelegateForFunctionPointer<FnPtr>(getFactory)();
        if (factoryPtr == IntPtr.Zero)
            throw new InvalidOperationException("GetPluginFactory 返回空");

        _factory = new PluginFactory(factoryPtr);
        FactoryVendor = _factory.GetFactoryInfo().Vendor;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        if (_dll != IntPtr.Zero)
        {
            if (_initDone && NativeLibrary.TryGetExport(_dll, "ExitDll", out var exitDll))
            {
                try { Marshal.GetDelegateForFunctionPointer<Fn0>(exitDll)(); }
                catch { }
            }
            try { NativeLibrary.Free(_dll); } catch { }
            _dll = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void Fn0();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr FnPtr();
}

/// <summary>
/// 枚举到的 VST3 插件类信息（模块懒加载：列表只扫描，不加载 DLL）。
/// </summary>
public sealed class Vst3PluginInfo
{
    public required string ModulePath { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; init; } = "";
    public string SubCategories { get; init; } = "";
    public bool IsInstrument => SubCategories.StartsWith("Instrument", StringComparison.OrdinalIgnoreCase)
                                || SubCategories.Contains("|Instrument", StringComparison.OrdinalIgnoreCase);
    public override string ToString() =>
        string.IsNullOrEmpty(Vendor) ? Name : $"{Name}（{Vendor}）";
}

public static class Vst3Scanner
{
    /// <summary>扫描系统 VST3 目录（不加载插件 DLL，只读取目录与缓存）。</summary>
    public static List<Vst3PluginInfo> Scan()
    {
        var dirs = new List<string>();
        foreach (var pf in new[] {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var d = System.IO.Path.Combine(pf, "Common Files", "VST3");
            if (Directory.Exists(d)) dirs.Add(d);
        }

        var result = new List<Vst3PluginInfo>();
        foreach (var dir in dirs)
            ScanDirectory(dir, result);
        return result;
    }

    private static void ScanDirectory(string dir, List<Vst3PluginInfo> result)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }

        foreach (var entry in entries)
        {
            var name = System.IO.Path.GetFileName(entry);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            if (Directory.Exists(entry))
            {
                if (name.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                {
                    // bundle 结构：Name.vst3/Contents/x86_64-win/*.vst3
                    var binDir = System.IO.Path.Combine(entry, "Contents", "x86_64-win");
                    if (Directory.Exists(binDir))
                    {
                        foreach (var bin in Directory.EnumerateFiles(binDir))
                        {
                            if (System.IO.Path.GetExtension(bin).Equals(".vst3", StringComparison.OrdinalIgnoreCase))
                            {
                                ListClasses(bin, result);
                            }
                        }
                    }
                }
                else
                {
                    // 厂商子文件夹（FabFilter、ValhallaDSP、WDM2VST Ultra 等）：递归扫描
                    ScanDirectory(entry, result);
                }
            }
            else if (System.IO.Path.GetExtension(entry).Equals(".vst3", StringComparison.OrdinalIgnoreCase))
            {
                // 单文件 DLL 型插件
                ListClasses(entry, result);
            }
        }
    }

    private static void ListClasses(string modulePath, List<Vst3PluginInfo> result)
    {
        // 用模块缓存：加载过的模块整场保留（很多插件不支持反复加载/卸载）
        var module = Vst3ModuleCache.Get(modulePath);
        if (module == null) return;

        try
        {
            var f = module.Factory;
            int count = f.CountClasses();
            for (int i = 0; i < count; i++)
            {
                var info2 = f.GetClassInfo2(i);
                string name; string sub = ""; string vendor = module.FactoryVendor;
                if (info2 != null)
                {
                    (_, name, sub, vendor, _) = info2.Value;
                    if (string.IsNullOrEmpty(vendor)) vendor = module.FactoryVendor;
                }
                else
                {
                    var (_, category, n) = f.GetClassInfo(i);
                    if (category != Vst3.kAudioEffectClass) continue;
                    name = n;
                }
                if (!IsAudioEffect(info2?.SubCategories ?? sub)) continue;
                result.Add(new Vst3PluginInfo
                {
                    ModulePath = modulePath,
                    Name = name,
                    Vendor = vendor,
                    SubCategories = info2?.SubCategories ?? sub,
                });
            }
        }
        catch
        {
            // 插件枚举失败：跳过该模块
        }
    }

    private static bool IsAudioEffect(string subCategories)
    {
        // 排除纯乐器（合成器等）和分析器，保留可用作插入效果器的插件
        var cats = subCategories.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (cats.Length == 0) return true;
        if (cats[0].Equals("Instrument", StringComparison.OrdinalIgnoreCase)) return false;
        if (cats[0].Equals("Analyzer", StringComparison.OrdinalIgnoreCase)) return false;
        if (cats[0].Equals("Spatial", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

/// <summary>扫描一次的模块缓存：VST3 模块加载后整场保留，不再卸载。</summary>
internal static class Vst3ModuleCache
{
    private static readonly Dictionary<string, Vst3Module> Modules = new(StringComparer.OrdinalIgnoreCase);

    public static Vst3Module? Get(string path)
    {
        lock (Modules)
        {
            if (Modules.TryGetValue(path, out var m)) return m;
            var loaded = Vst3Module.TryLoad(path);
            if (loaded == null) return null;
            Modules[path] = loaded;
            return loaded;
        }
    }
}
