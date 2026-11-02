import Foundation
import AVFoundation
import CoreAudio
import AudioToolbox
import CoreAudioKit
import Combine
import AppKit
import KeyboardShortcuts

// MARK: - 设备模型

struct AudioDevice: Identifiable, Hashable {
    let id: AudioDeviceID
    let name: String
    let inputChannelCount: Int
    let outputChannelCount: Int
}

struct InputChannelOption: Identifiable, Hashable {
    let id = UUID()
    let name: String
    let channelMap: [Int32]
}

struct OutputChannelOption: Identifiable, Hashable {
    let id = UUID()
    let name: String
    let startChannel: Int32
    let channelCount: Int32

    var channelMap: [Int32] {
        (0..<channelCount).map { startChannel + $0 }
    }
}

struct AUEffectInfo: Identifiable, Hashable {
    let id = UUID()
    let name: String
    let componentDesc: AudioComponentDescription
    let architectures: [String]
    var isIntelOnly: Bool {
        !architectures.contains("arm64") && architectures.contains("x86_64")
    }
    static func == (lhs: AUEffectInfo, rhs: AUEffectInfo) -> Bool { lhs.id == rhs.id }
    func hash(into hasher: inout Hasher) { hasher.combine(id) }
}

final class LoadedPlugin: Identifiable {
    let id = UUID()
    var name: String
    var componentDesc: AudioComponentDescription
    var avAudioUnit: AVAudioUnit
    var bypassed: Bool = false
    var windows: NSWindow?

    init(name: String,
         componentDesc: AudioComponentDescription,
         avAudioUnit: AVAudioUnit) {
        self.name = name
        self.componentDesc = componentDesc
        self.avAudioUnit = avAudioUnit
    }
}

// MARK: - 预设

struct PluginPreset: Codable {
    var name: String
    var componentType: UInt32
    var componentSubType: UInt32
    var componentManufacturer: UInt32
    var bypassed: Bool
    var fullState: Data?
}

struct RackPreset: Codable {
    var name: String
    var inputDeviceName: String?
    var outputDeviceName: String?
    var inputChannelMap: [Int32]?
    var outputChannelStart: Int32?
    var sampleRate: Double
    var bufferSize: Int
    var masterGainDB: Double
    var plugins: [PluginPreset]

    // Send 相关
    var sendPluginA: PluginPreset?
    var sendPluginB: PluginPreset?
    var sendVolumeA: Double?
    var sendVolumeB: Double?
    var sendMasterVolume: Double?

    // AUX 输入（音乐/音效软件进声）
    var aux1Name: String?
    var aux1DeviceName: String?
    var aux1ChannelMap: [Int32]?
    var aux1GainDB: Double?
    var aux2Name: String?
    var aux2DeviceName: String?
    var aux2ChannelMap: [Int32]?
    var aux2GainDB: Double?

    // 插入效果器（所有声音之后、总推子之前）
    var insertPlugin: PluginPreset?

    // 第二路输出（监听/推流）
    var monitorDeviceName: String?
    var monitorChannelStart: Int32?
}

// MARK: - Ring Buffer

final class AudioRingBuffer: @unchecked Sendable {
    private var buffer: [Float] = []
    private let lock = NSLock()
    private let capacity: Int

    init(capacity: Int) { self.capacity = capacity }

    func append(_ samples: [Float]) {
        lock.lock(); defer { lock.unlock() }
        buffer.append(contentsOf: samples)
        if buffer.count > capacity {
            buffer.removeFirst(buffer.count - capacity)
        }
    }

    /// 零拷贝版本：实时音频线程用，避免每帧构造数组
    func append(_ ptr: UnsafePointer<Float>, count n: Int) {
        lock.lock(); defer { lock.unlock() }
        buffer.append(contentsOf: UnsafeBufferPointer(start: ptr, count: n))
        if buffer.count > capacity {
            buffer.removeFirst(buffer.count - capacity)
        }
    }

    func readInto(_ dest: UnsafeMutablePointer<Float>, count: Int) -> Int {
        lock.lock(); defer { lock.unlock() }
        let n = min(buffer.count, count)
        for i in 0..<n {
            dest[i] = buffer[i]
        }
        if n > 0 { buffer.removeFirst(n) }
        return n
    }

    func clear() {
        lock.lock(); defer { lock.unlock() }
        buffer.removeAll(keepingCapacity: true)
    }

    var count: Int {
        lock.lock(); defer { lock.unlock() }
        return buffer.count
    }
}

// MARK: - 录音器

final class AudioRecorder: @unchecked Sendable {

    private var file: ExtAudioFileRef?
    private var isRecording = false
    private var writeTimer: DispatchSourceTimer?
    private let writeQueue = DispatchQueue(label: "com.xsd.recorder.write", qos: .utility)

    private var buffer: [Float] = []
    private let lock = NSLock()
    private let capacity: Int = 48000 * 2 * 10

    private let sampleRate: Double = 48000
    private let channels: UInt32 = 2

    private(set) var currentFileURL: URL?

    static var recordingsDirectory: URL {
        let dir = FileManager.default
            .urls(for: .musicDirectory, in: .userDomainMask)
            .first!
            .appendingPathComponent("XSDAudioRack", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    func start() throws {
        guard !isRecording else { return }

        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        let name = "Recording_\(formatter.string(from: Date())).wav"
        let url = Self.recordingsDirectory.appendingPathComponent(name)

        // 文件格式：交错
        var fileASBD = AudioStreamBasicDescription(
            mSampleRate: sampleRate,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 4 * channels,
            mFramesPerPacket: 1,
            mBytesPerFrame: 4 * channels,
            mChannelsPerFrame: channels,
            mBitsPerChannel: 32,
            mReserved: 0
        )

        var fileRef: ExtAudioFileRef?
        var status = ExtAudioFileCreateWithURL(
            url as CFURL,
            kAudioFileWAVEType,
            &fileASBD,
            nil,
            AudioFileFlags.eraseFile.rawValue,
            &fileRef
        )
        guard status == noErr, let fileRef = fileRef else {
            throw NSError(domain: "Recorder", code: Int(status),
                          userInfo: [NSLocalizedDescriptionKey: "无法创建录音文件: \(status)"])
        }

        // 客户端格式：交错（和写入数据一致）
        var clientASBD = fileASBD
        status = ExtAudioFileSetProperty(fileRef,
                                         kExtAudioFileProperty_ClientDataFormat,
                                         UInt32(MemoryLayout<AudioStreamBasicDescription>.size),
                                         &clientASBD)
        guard status == noErr else {
            ExtAudioFileDispose(fileRef)
            throw NSError(domain: "Recorder", code: Int(status),
                          userInfo: [NSLocalizedDescriptionKey: "设置录音格式失败: \(status)"])
        }

        self.file = fileRef
        self.currentFileURL = url
        self.isRecording = true

        lock.lock()
        buffer.removeAll(keepingCapacity: true)
        lock.unlock()

        let timer = DispatchSource.makeTimerSource(queue: writeQueue)
        timer.schedule(deadline: .now() + 0.05, repeating: 0.05)
        timer.setEventHandler { [weak self] in
            self?.flushBuffer()
        }
        timer.resume()
        self.writeTimer = timer

        print("🎙 开始录音: \(url.path)")
    }

    func stop() {
        guard isRecording else { return }
        isRecording = false

        writeTimer?.cancel()
        writeTimer = nil

        writeQueue.sync {
            flushBuffer()
        }

        if let file = file {
            ExtAudioFileDispose(file)
        }
        file = nil
        print("🎙 录音已停止: \(currentFileURL?.path ?? "")")
    }

    func appendFromAudioThread(_ samples: UnsafePointer<Float>, count: Int) {
        guard isRecording else { return }
        lock.lock()
        for i in 0..<count {
            buffer.append(samples[i])
        }
        if buffer.count > capacity {
            buffer.removeFirst(buffer.count - capacity)
        }
        lock.unlock()
    }

    private func flushBuffer() {
        lock.lock()
        let samples = buffer
        buffer.removeAll(keepingCapacity: true)
        lock.unlock()

        guard !samples.isEmpty, let file = file else { return }

        let frames = samples.count / Int(channels)
        guard frames > 0 else { return }

        let ablSize = MemoryLayout<AudioBufferList>.size
        let rawABL = UnsafeMutableRawPointer.allocate(byteCount: ablSize, alignment: 8)
        defer { rawABL.deallocate() }

        let abl = rawABL.assumingMemoryBound(to: AudioBufferList.self)
        let ablPtr = UnsafeMutableAudioBufferListPointer(abl)
        ablPtr.count = 1

        samples.withUnsafeBufferPointer { ptr in
            ablPtr[0] = AudioBuffer(
                mNumberChannels: channels,
                mDataByteSize: UInt32(samples.count * 4),
                mData: UnsafeMutableRawPointer(mutating: ptr.baseAddress)
            )

            let framesToWrite = UInt32(frames)
            let status = ExtAudioFileWrite(file, framesToWrite, ablPtr.unsafeMutablePointer)
            if status != noErr {
                print("⚠️ 录音写盘失败: \(status)")
            }
        }
    }
}

// MARK: - 引擎状态

final class AudioEngineState: @unchecked Sendable {
    var inputAU: AudioUnit?
    var outputAU: AudioUnit?
    var engine: AVAudioEngine?
    var sourceNode: AVAudioSourceNode?

    var channels: Int = 2
    var sampleRate: Double = 48000
    var inputChannelMap: [Int32] = [0, 0]
    var outputChannelCount: Int = 2

    var plugins: [LoadedPlugin] = []
    var outputChannelOption: OutputChannelOption?
    var monitorChannelOption: OutputChannelOption?
    
    // ───── Send A / Send B 的插件和音量 ─────
    var sendPluginA: AVAudioUnitEffect?
    var sendPluginB: AVAudioUnitEffect?
    var sendMixerA: AVAudioMixerNode?
    var sendMixerB: AVAudioMixerNode?
    var sendMasterMixer: AVAudioMixerNode?
    var branchMixer: AVAudioMixerNode?
    
    var outputMuted: Bool = false
    var inputMuted: Bool = false

    let ring = AudioRingBuffer(capacity: 65536)

    // ───── 插入效果器（所有声音之后、总推子之前）─────
    var insertPlugin: AVAudioUnitEffect?

    // ───── 主输出链末端 mixer（总推子在这里，位于插入效果器之后）─────
    var masterMixer: AVAudioMixerNode?

    // ───── AUX 源节点（把 AUX 环缓冲作为引擎输入源，进插入效果器和总推子）─────
    var auxSourceNode: AVAudioSourceNode?

    // ───── 第二路输出（监听/推流）─────
    var monitorAU: AudioUnit?
    let monitorRing = AudioRingBuffer(capacity: 65536)

    // ───── AUX 输入（两路立体声，混进主输出）─────
    var auxInputAU1: AudioUnit?
    var auxInputAU2: AudioUnit?
    let auxRing1 = AudioRingBuffer(capacity: 65536)
    let auxRing2 = AudioRingBuffer(capacity: 65536)
    var auxGain1: Float = 1.0   // 线性增益（混音时施加）
    var auxGain2: Float = 1.0
    var auxRMS1: Float = 0      // 施加增益后的电平（推子位置看到的）
    var auxRMS2: Float = 0
    var auxCtx1: AuxContext?
    var auxCtx2: AuxContext?

    var inputRMS: Float = 0
    var outputRMS: Float = 0

    var sourceTemp: UnsafeMutablePointer<Float>
    var sourceTrash: UnsafeMutablePointer<Float>
    let sourceMaxFrames: Int = 8192

    var renderTempL: UnsafeMutablePointer<Float>
    var renderTempR: UnsafeMutablePointer<Float>

    // 实时线程预分配缓冲（避免每帧分配内存导致偶发卡顿）
    var monitorScratch: UnsafeMutablePointer<Float>   // 帧数*2 交错（主输出→第二路）
    var monitorTmp: UnsafeMutablePointer<Float>       // 帧数*2 交错（第二路回调读缓冲）
    var monitorTrash: UnsafeMutablePointer<Float>     // 丢帧暂存
    var auxScratch: UnsafeMutablePointer<Float>       // 帧数*2 交错（AUX 混入）
    var auxTrash: UnsafeMutablePointer<Float>         // 丢帧暂存
    var inputScratch: UnsafeMutablePointer<Float>     // 帧数*2 非交错（麦克风输入）

    nonisolated init() {
        sourceTemp = .allocate(capacity: sourceMaxFrames * 2)
        sourceTemp.initialize(repeating: 0, count: sourceMaxFrames * 2)
        sourceTrash = .allocate(capacity: sourceMaxFrames * 2)
        sourceTrash.initialize(repeating: 0, count: sourceMaxFrames * 2)
        renderTempL = .allocate(capacity: sourceMaxFrames)
        renderTempL.initialize(repeating: 0, count: sourceMaxFrames)
        renderTempR = .allocate(capacity: sourceMaxFrames)
        renderTempR.initialize(repeating: 0, count: sourceMaxFrames)
        monitorScratch = .allocate(capacity: sourceMaxFrames * 2)
        monitorScratch.initialize(repeating: 0, count: sourceMaxFrames * 2)
        monitorTmp = .allocate(capacity: sourceMaxFrames * 2)
        monitorTmp.initialize(repeating: 0, count: sourceMaxFrames * 2)
        monitorTrash = .allocate(capacity: 65536 + 64)
        monitorTrash.initialize(repeating: 0, count: 65536 + 64)
        auxScratch = .allocate(capacity: sourceMaxFrames * 2)
        auxScratch.initialize(repeating: 0, count: sourceMaxFrames * 2)
        auxTrash = .allocate(capacity: 65536 + 64)
        auxTrash.initialize(repeating: 0, count: 65536 + 64)
        inputScratch = .allocate(capacity: sourceMaxFrames * 2)
        inputScratch.initialize(repeating: 0, count: sourceMaxFrames * 2)
    }

    deinit {
        sourceTemp.deallocate()
        sourceTrash.deallocate()
        renderTempL.deallocate()
        renderTempR.deallocate()
        monitorScratch.deallocate()
        monitorTmp.deallocate()
        monitorTrash.deallocate()
        auxScratch.deallocate()
        auxTrash.deallocate()
        inputScratch.deallocate()
    }

    func clear() {
        ring.clear()
        monitorRing.clear()
        inputAU = nil
        outputAU = nil
        monitorAU = nil
        engine = nil
        sourceNode = nil
        auxSourceNode = nil
        insertPlugin = nil
        masterMixer = nil
        plugins.removeAll()
    }
}

/// AUX 输入回调上下文（AUHAL refCon）
final class AuxContext {
    unowned let state: AudioEngineState
    let tag: Int          // 1 / 2
    var unit: AudioUnit?

    init(state: AudioEngineState, tag: Int) {
        self.state = state
        self.tag = tag
    }
}

// MARK: - 管理器
// MARK: - 全局快捷键定义

extension KeyboardShortcuts.Name {
    static let toggleOutputMute = Self("toggleOutputMute",
                                        default: .init(.space, modifiers: [.option]))
}

@MainActor
class AudioDeviceManager: NSObject, ObservableObject {

    @Published var devices: [AudioDevice] = []
    @Published var selectedInputDevice: AudioDevice?
    @Published var selectedOutputDevice: AudioDevice?
    @Published var selectedInputChannel: InputChannelOption?
    @Published var selectedOutputChannel: OutputChannelOption?
    @Published var inputChannelOptions: [InputChannelOption] = []
    @Published var outputChannelOptions: [OutputChannelOption] = []
    @Published var isRunning: Bool = false
    @Published var pluginChain: [LoadedPlugin] = []

    @Published var inputLevel: Double = -60.0
    @Published var outputLevel: Double = -60.0
    @Published var inputPeak: Double = -60.0
    @Published var outputPeak: Double = -60.0

    // ───── AUX 输入（音乐/音效软件进声）─────
    @Published var aux1Name: String = ""   // 自定义名称（空 = 显示 AUX1）
    @Published var aux2Name: String = ""
    @Published var aux1Device: AudioDevice?
    @Published var aux1Channel: InputChannelOption?
    @Published var aux2Device: AudioDevice?
    @Published var aux2Channel: InputChannelOption?
    @Published var aux1GainDB: Double = 0.0 {
        didSet { applyAuxGains() }
    }
    @Published var aux2GainDB: Double = 0.0 {
        didSet { applyAuxGains() }
    }
    @Published var aux1Level: Double = -60.0
    @Published var aux2Level: Double = -60.0

    // ───── 第二路输出（监听/推流）─────
    @Published var monitorDevice: AudioDevice?
    @Published var monitorChannel: OutputChannelOption?

    // ───── 插入效果器 ─────
    @Published var insertPluginName: String? = nil
    @Published var insertBypassed: Bool = false

    // ───── 闭麦（静音麦克风输入）─────
    @Published var inputMuted: Bool = false

    private var inputPeakTime: Date = .distantPast
    private var outputPeakTime: Date = .distantPast
    
    @Published var availableSampleRates: [Double] = []
    @Published var selectedSampleRate: Double = 48000
    @Published var availableBufferSizes: [Int] = []
    @Published var selectedBufferSize: Int = 128

    @Published var isRecording: Bool = false

    @Published var masterGainDB: Double = 0.0 {
        didSet { applyMasterGain() }
    }
    @Published var outputMuted: Bool = false
    
    // ───── Send 链 ─────
    @Published var sendPluginNameA: String? = nil
    @Published var sendPluginNameB: String? = nil
    @Published var sendBypassedA: Bool = false
    @Published var sendBypassedB: Bool = false
    @Published var sendVolumeA: Double = -6.0 {   // dB
        didSet { applySendVolumes() }
    }
    @Published var sendVolumeB: Double = -6.0 {   // dB
        didSet { applySendVolumes() }
    }
    @Published var sendMasterVolume: Double = -9.0 {
        didSet { applySendVolumes() }
    }

    nonisolated let state: AudioEngineState
    nonisolated let recorder: AudioRecorder

    private var levelTimer: Timer?
    private var pluginWindows: [UUID: NSWindow] = [:]

    // ───── 授权 ─────
    @Published var licenseValid = false
    @Published var licenseMessage = ""
    @Published var licenseNeedsAttention = false
    private var licenseTimer: Timer?

    /// 启动时和运行中每分钟检查授权；失效则停引擎并弹激活窗
    func checkLicenseNow() {
        let lic = LicenseManager.checkLocalLicense()
        licenseValid = lic.valid
        licenseMessage = lic.message
        licenseNeedsAttention = !lic.valid
        if !lic.valid && isRunning {
            stop()
        }
    }

    nonisolated(unsafe) static var ringRef: AudioRingBuffer?
    nonisolated(unsafe) static var stateRef: AudioEngineState?
    nonisolated(unsafe) static var recorderRef: AudioRecorder?

    private static let autoSaveURL: URL = {
        let dir = FileManager.default
            .urls(for: .applicationSupportDirectory, in: .userDomainMask)
            .first!
            .appendingPathComponent("XSDAudioRack", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("autosave.json")
    }()

    override init() {
        self.state = AudioEngineState()
        self.recorder = AudioRecorder()
        super.init()
        refreshDevices()
        setupGlobalHotkey()
        checkLicenseNow()
        licenseTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            self?.checkLicenseNow()
        }
    }

    func refreshDevices() {
        devices = Self.getDevices()
    }

    // MARK: - 全局快捷键

    private func setupGlobalHotkey() {
        KeyboardShortcuts.onKeyUp(for: .toggleOutputMute) { [weak self] in
            guard let self = self else { return }
            DispatchQueue.main.async {
                // 全局快捷键现在控制「闭麦」（与总推子下的小话筒按钮联动）
                self.setInputMuted(!self.inputMuted)
                print("⌨️ 全局快捷键触发，闭麦 = \(self.inputMuted)")
            }
        }
    }

    // MARK: - 通道选项

    func updateInputChannels(for device: AudioDevice?) {
        guard let device = device, device.inputChannelCount > 0 else {
            inputChannelOptions = []
            selectedInputChannel = nil
            return
        }
        var options: [InputChannelOption] = []
        for i in 0..<device.inputChannelCount {
            options.append(InputChannelOption(
                name: "Input \(i + 1) (mono → L+R)",
                channelMap: [Int32(i), Int32(i)]
            ))
        }
        let pairs = device.inputChannelCount / 2
        for p in 0..<pairs {
            let a = p * 2
            options.append(InputChannelOption(
                name: "Input \(a + 1)-\(a + 2) (stereo)",
                channelMap: [Int32(a), Int32(a + 1)]
            ))
        }
        inputChannelOptions = options
        if selectedInputChannel == nil ||
            !inputChannelOptions.contains(where: { $0 == selectedInputChannel }) {
            selectedInputChannel = inputChannelOptions.first
        }
    }

    /// AUX 输入的立体声通道对选项（列出该设备所有输入通道两两成对）
    static func auxChannelOptions(for device: AudioDevice) -> [InputChannelOption] {
        guard device.inputChannelCount > 0 else { return [] }
        var options: [InputChannelOption] = []
        let pairs = device.inputChannelCount / 2
        for p in 0..<pairs {
            let a = p * 2
            options.append(InputChannelOption(
                name: "输入 \(a + 1)-\(a + 2)（立体声）",
                channelMap: [Int32(a), Int32(a + 1)]
            ))
        }
        // 奇数通道时补一个单声道复制
        if device.inputChannelCount % 2 == 1 {
            let last = device.inputChannelCount - 1
            options.append(InputChannelOption(
                name: "输入 \(last + 1)（单声道→立体声）",
                channelMap: [Int32(last), Int32(last)]
            ))
        }
        return options
    }

    func updateOutputChannels(for device: AudioDevice?) {
        guard let device = device, device.outputChannelCount >= 2 else {
            outputChannelOptions = []
            selectedOutputChannel = nil
            return
        }
        var options: [OutputChannelOption] = []
        let pairs = device.outputChannelCount / 2
        for p in 0..<pairs {
            let a = p * 2
            options.append(OutputChannelOption(
                name: "Output \(a + 1)-\(a + 2)",
                startChannel: Int32(a),
                channelCount: 2
            ))
        }
        outputChannelOptions = options
        if selectedOutputChannel == nil ||
            !outputChannelOptions.contains(where: { $0 == selectedOutputChannel }) {
            selectedOutputChannel = outputChannelOptions.first
        }
    }

    /// 第二路输出的立体声通道对选项（该设备所有输出通道两两成对）
    static func monitorChannelOptions(for device: AudioDevice) -> [OutputChannelOption] {
        guard device.outputChannelCount >= 2 else { return [] }
        var options: [OutputChannelOption] = []
        let pairs = device.outputChannelCount / 2
        for p in 0..<pairs {
            let a = p * 2
            options.append(OutputChannelOption(
                name: "Output \(a + 1)-\(a + 2)",
                startChannel: Int32(a),
                channelCount: 2
            ))
        }
        return options
    }

    // MARK: - 采样率 / buffer

    func updateDeviceFormats(for device: AudioDevice?) {
        guard let device = device else {
            availableSampleRates = []
            availableBufferSizes = []
            return
        }
        availableSampleRates = Self.getAvailableSampleRates(deviceID: device.id)
        let curRate = Self.getDeviceSampleRate(deviceID: device.id) ?? 48000
        if availableSampleRates.contains(curRate) {
            selectedSampleRate = curRate
        } else if let first = availableSampleRates.first {
            selectedSampleRate = first
        }

        availableBufferSizes = Self.getAvailableBufferSizes(deviceID: device.id)
        let curBuf = Self.getDeviceBufferSize(deviceID: device.id) ?? 128
        if availableBufferSizes.contains(curBuf) {
            selectedBufferSize = curBuf
        } else if let first = availableBufferSizes.first {
            selectedBufferSize = first
        }
    }

    // MARK: - AU 扫描

    // ───── 插件列表缓存 ─────
    /// 缓存的 AU 效果器列表（后台扫描，避免每次打开选择器都在主线程同步扫全系统 AU 卡住界面——
    /// 表现为点一下没反应、要猛点好几下）
    @Published var availableEffects: [AUEffectInfo] = []
    private var isScanningEffects = false

    /// 需要时后台重扫插件列表（打开选择器前调用；已有缓存则跳过，force 强制重扫）
    func refreshAvailableEffects(force: Bool = false) {
        if isScanningEffects { return }
        if !force, !availableEffects.isEmpty { return }
        isScanningEffects = true
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let list = Self.scanAvailableAUEffects()
            DispatchQueue.main.async {
                self?.availableEffects = list
                self?.isScanningEffects = false
            }
        }
    }

    static func scanAvailableAUEffects() -> [AUEffectInfo] {
        var desc = AudioComponentDescription(
            componentType: kAudioUnitType_Effect,
            componentSubType: 0,
            componentManufacturer: 0,
            componentFlags: 0,
            componentFlagsMask: 0
        )
        var list: [AUEffectInfo] = []
        var current = AudioComponentFindNext(nil, &desc)
        while let comp = current {
            var cd = AudioComponentDescription()
            AudioComponentGetDescription(comp, &cd)

            var unmanagedName: Unmanaged<CFString>?
            var displayName = "Unknown AU"
            if AudioComponentCopyName(comp, &unmanagedName) == noErr,
               let cfName = unmanagedName?.takeRetainedValue() {
                displayName = cfName as String
            }

            let archs = Self.getArchitectures(component: comp)
            list.append(AUEffectInfo(name: displayName,
                                     componentDesc: cd,
                                     architectures: archs))
            current = AudioComponentFindNext(comp, &desc)
        }
        return list.sorted {
            $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
    }

    private static func getArchitectures(component: AudioComponent) -> [String] {
        var configInfo: Unmanaged<CFDictionary>?
        let status = AudioComponentCopyConfigurationInfo(component, &configInfo)
        guard status == noErr, let dict = configInfo?.takeRetainedValue() as? [String: Any] else {
            return []
        }
        let key = kAudioUnitConfigurationInfo_AvailableArchitectures as String
        if let archs = dict[key] as? [String] {
            return archs
        }
        return []
    }

    // MARK: - 插件加载

    func addPlugin(_ info: AUEffectInfo) {
        let wasRunning = isRunning
        if wasRunning { stop() }

        print("🔄 异步实例化插件: \(info.name)")

        AVAudioUnit.instantiate(with: info.componentDesc, options: []) { [weak self] avUnit, error in
            guard let self = self else { return }

            guard error == nil, let avUnit = avUnit else {
                print("❌ 实例化失败: \(error?.localizedDescription ?? "unknown")")
                return
            }

            print("✅ 实例化成功: \(avUnit.name)")

            DispatchQueue.main.async {
                let plugin = LoadedPlugin(name: info.name,
                                          componentDesc: info.componentDesc,
                                          avAudioUnit: avUnit)
                self.state.plugins.append(plugin)
                self.pluginChain.append(plugin)

                if wasRunning {
                    do { try self.start() }
                    catch { print("⚠️ 重启引擎失败: \(error)") }
                }
                self.autoSave()
            }
        }
    }

    func removePlugin(id: UUID) {
        guard let idx = pluginChain.firstIndex(where: { $0.id == id }) else { return }
        let plugin = pluginChain[idx]

        pluginWindows[id]?.close()
        pluginWindows.removeValue(forKey: id)

        pluginChain.remove(at: idx)
        state.plugins.removeAll { $0.id == id }

        if isRunning {
            stop()
            try? start()
        }
        autoSave()
    }

    func toggleBypass(id: UUID) {
        guard let plugin = pluginChain.first(where: { $0.id == id }) else { return }
        plugin.bypassed.toggle()
        pluginChain = pluginChain
        plugin.avAudioUnit.auAudioUnit.shouldBypassEffect = plugin.bypassed
        autoSave()
    }

    func movePluginUp(id: UUID) {
        guard let idx = pluginChain.firstIndex(where: { $0.id == id }), idx > 0 else { return }
        pluginChain.swapAt(idx, idx - 1)
        state.plugins = pluginChain
        rebuildPluginChain()
        autoSave()
    }

    func movePluginDown(id: UUID) {
        guard let idx = pluginChain.firstIndex(where: { $0.id == id }),
              idx < pluginChain.count - 1 else { return }
        pluginChain.swapAt(idx, idx + 1)
        state.plugins = pluginChain
        rebuildPluginChain()
        autoSave()
    }

    private func rebuildPluginChain() {
        guard isRunning else { return }
        stop()
        try? start()
    }

    // MARK: - 打开插件 UI

    /// 插件拿不出界面时给出提示（而不是默默没反应），便于定位"打不开"的原因
    private func showPluginUINotAvailable(_ name: String) {
        let alert = NSAlert()
        alert.messageText = "无法打开插件界面"
        alert.informativeText = "「\(name)」没有提供可显示的窗口界面，但插件本身仍在正常处理声音。"
        alert.addButton(withTitle: "好")
        alert.runModal()
    }

    /// 创建自适应插件视图尺寸的窗口。
    /// 之前写死 640×420：插件视图偏小会出白板（如 Apogee MEQ-5）、偏大会被裁切（如 Antares Auto-Key）。
    /// 这里按视图 fittingSize 适配；不少插件要异步布局完才定稿尺寸，所以延迟校准几次。
    private func makePluginWindow(viewController vc: NSViewController, title: String) -> NSWindow {
        let window = NSWindow(contentViewController: vc)
        window.title = title
        window.isReleasedWhenClosed = false

        func desiredSize() -> NSSize {
            let fit = vc.view.fittingSize
            let frame = vc.view.frame
            let w = max(320, fit.width > 10 ? fit.width : (frame.width > 10 ? frame.width : 640))
            let h = max(240, fit.height > 10 ? fit.height : (frame.height > 10 ? frame.height : 420))
            return NSSize(width: w, height: h)
        }
        func applySize(onlyIfVisible: Bool) {
            if onlyIfVisible && !window.isVisible { return }
            let target = desiredSize()
            let content = window.contentView?.frame.size ?? .zero
            if abs(content.width - target.width) > 1 || abs(content.height - target.height) > 1 {
                window.setContentSize(target)
            }
        }

        applySize(onlyIfVisible: false)
        // 插件视图加载/布局是异步的，之后几次再校准（用户也可手动拉伸窗口）
        for delay in [0.05, 0.3, 1.0, 2.5] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak window] in
                guard window != nil else { return }
                applySize(onlyIfVisible: true)
            }
        }
        return window
    }

    func openPluginUI(id: UUID) {
        guard let plugin = pluginChain.first(where: { $0.id == id }) else { return }

        if let win = pluginWindows[id] {
            win.makeKeyAndOrderFront(nil)
            return
        }

        let auAudioUnit = plugin.avAudioUnit.auAudioUnit
        let selector = NSSelectorFromString("requestViewControllerWithCompletionHandler:")
        guard auAudioUnit.responds(to: selector) else { return }

        let completionHandler: @convention(block) (NSViewController?) -> Void = { [weak self] vc in
            guard let self = self else { return }
            guard let vc = vc else {
                self.showPluginUINotAvailable(plugin.name)
                return
            }

            DispatchQueue.main.async {
                NSApp.setActivationPolicy(.regular)
                NSApp.activate(ignoringOtherApps: true)

                let window = self.makePluginWindow(viewController: vc, title: plugin.name)
                window.center()
                window.makeKeyAndOrderFront(nil)

                self.pluginWindows[id] = window
                plugin.windows = window
            }
        }

        let imp = auAudioUnit.method(for: selector)
        typealias Func = @convention(c) (AnyObject, Selector, AnyObject) -> Void
        let funcPtr = unsafeBitCast(imp, to: Func.self)
        funcPtr(auAudioUnit, selector, completionHandler as AnyObject)
    }

    // MARK: - 录音

    func startRecording() {
        do {
            try recorder.start()
            isRecording = true
        } catch {
            print("⚠️ 开始录音失败: \(error)")
        }
    }

    func stopRecording() {
        recorder.stop()
        isRecording = false
    }

    func openRecordingsFolder() {
        NSWorkspace.shared.open(AudioRecorder.recordingsDirectory)
    }
    
    // MARK: - Send 插件管理

    func setSendPluginA(_ info: AUEffectInfo?) {
        let wasRunning = isRunning
        if wasRunning { stop() }

        // 移除旧插件
        if let old = state.sendPluginA {
            state.engine?.detach(old)
            state.sendPluginA = nil
        }
        sendPluginNameA = nil
        sendBypassedA = false

        guard let info = info else {
            if wasRunning { try? start() }
            return
        }

        // 创建新插件
        AVAudioUnit.instantiate(with: info.componentDesc, options: []) { [weak self] avUnit, error in
            guard let self = self else { return }
            guard error == nil, let avUnit = avUnit as? AVAudioUnitEffect else {
                print("❌ Send A 实例化失败")
                return
            }
            DispatchQueue.main.async {
                self.state.sendPluginA = avUnit
                self.sendPluginNameA = info.name
                if wasRunning { try? self.start() }
                self.autoSave()
            }
        }
    }

    func setSendPluginB(_ info: AUEffectInfo?) {
        let wasRunning = isRunning
        if wasRunning { stop() }

        if let old = state.sendPluginB {
            state.engine?.detach(old)
            state.sendPluginB = nil
        }
        sendPluginNameB = nil
        sendBypassedB = false

        guard let info = info else {
            if wasRunning { try? start() }
            return
        }

        AVAudioUnit.instantiate(with: info.componentDesc, options: []) { [weak self] avUnit, error in
            guard let self = self else { return }
            guard error == nil, let avUnit = avUnit as? AVAudioUnitEffect else {
                print("❌ Send B 实例化失败")
                return
            }
            DispatchQueue.main.async {
                self.state.sendPluginB = avUnit
                self.sendPluginNameB = info.name
                if wasRunning { try? self.start() }
                self.autoSave()
            }
        }
    }

    func toggleSendBypassA() {
        sendBypassedA.toggle()
        state.sendPluginA?.bypass = sendBypassedA
        autoSave()
    }

    func toggleSendBypassB() {
        sendBypassedB.toggle()
        state.sendPluginB?.bypass = sendBypassedB
        autoSave()
    }

    func openSendPluginUI(id: String) {
        let unit: AVAudioUnitEffect? = (id == "A") ? state.sendPluginA : state.sendPluginB
        guard let unit = unit else { return }

        let auAudioUnit = unit.auAudioUnit
        let selector = NSSelectorFromString("requestViewControllerWithCompletionHandler:")
        guard auAudioUnit.responds(to: selector) else { return }

        let completionHandler: @convention(block) (NSViewController?) -> Void = { [weak self] vc in
            guard let self = self else { return }
            guard let vc = vc else {
                self.showPluginUINotAvailable("Send \(id) 插件")
                return
            }
            DispatchQueue.main.async {
                NSApp.setActivationPolicy(.regular)
                NSApp.activate(ignoringOtherApps: true)
                let window = self.makePluginWindow(viewController: vc, title: "Send \(id)")
                window.center()
                window.makeKeyAndOrderFront(nil)
                self.pluginWindows[UUID()] = window
            }
        }

        let imp = auAudioUnit.method(for: selector)
        typealias Func = @convention(c) (AnyObject, Selector, AnyObject) -> Void
        let funcPtr = unsafeBitCast(imp, to: Func.self)
        funcPtr(auAudioUnit, selector, completionHandler as AnyObject)
    }
    
    func setOutputMuted(_ muted: Bool) {
        outputMuted = muted
        state.outputMuted = muted
    }

    // MARK: - 闭麦（静音麦克风输入）

    func setInputMuted(_ muted: Bool) {
        inputMuted = muted
        state.inputMuted = muted
    }

    // MARK: - 插入效果器（所有声音之后、总推子之前）

    func setInsertPlugin(_ info: AUEffectInfo?) {
        let wasRunning = isRunning
        if wasRunning { stop() }

        if let old = state.insertPlugin {
            state.engine?.detach(old)
            state.insertPlugin = nil
        }
        insertPluginName = nil
        insertBypassed = false

        guard let info = info else {
            if wasRunning { try? start() }
            return
        }

        AVAudioUnit.instantiate(with: info.componentDesc, options: []) { [weak self] avUnit, error in
            guard let self = self else { return }
            guard error == nil, let avUnit = avUnit as? AVAudioUnitEffect else {
                print("❌ 插入效果器实例化失败")
                return
            }
            DispatchQueue.main.async {
                self.state.insertPlugin = avUnit
                self.insertPluginName = info.name
                if wasRunning { try? self.start() }
                self.autoSave()
            }
        }
    }

    func toggleInsertBypass() {
        insertBypassed.toggle()
        state.insertPlugin?.auAudioUnit.shouldBypassEffect = insertBypassed
        autoSave()
    }

    func openInsertPluginUI() {
        guard let unit = state.insertPlugin else { return }

        let auAudioUnit = unit.auAudioUnit
        let selector = NSSelectorFromString("requestViewControllerWithCompletionHandler:")
        guard auAudioUnit.responds(to: selector) else { return }

        let completionHandler: @convention(block) (NSViewController?) -> Void = { [weak self] vc in
            guard let self = self else { return }
            guard let vc = vc else {
                self.showPluginUINotAvailable(self.insertPluginName ?? "插入效果器")
                return
            }
            DispatchQueue.main.async {
                NSApp.setActivationPolicy(.regular)
                NSApp.activate(ignoringOtherApps: true)
                let window = self.makePluginWindow(viewController: vc, title: self.insertPluginName ?? "插入效果器")
                window.center()
                window.makeKeyAndOrderFront(nil)
                self.pluginWindows[UUID()] = window
            }
        }

        let imp = auAudioUnit.method(for: selector)
        typealias Func = @convention(c) (AnyObject, Selector, AnyObject) -> Void
        let funcPtr = unsafeBitCast(imp, to: Func.self)
        funcPtr(auAudioUnit, selector, completionHandler as AnyObject)
    }

    // MARK: - 主音量

    /// 总推子现在位于插入效果器之后（masterMixer 上），AUX/Send/麦克风全部受控
    private func applyMasterGain() {
        let linearGain = pow(10, masterGainDB / 20.0)
        state.masterMixer?.outputVolume = Float(linearGain)
    }

    private func applyAuxGains() {
        state.auxGain1 = Float(pow(10, aux1GainDB / 20.0))
        state.auxGain2 = Float(pow(10, aux2GainDB / 20.0))
    }
    
    private func applySendVolumes() {
        let linearA = pow(10, sendVolumeA / 20.0)
        let linearB = pow(10, sendVolumeB / 20.0)
        let linearMaster = pow(10, sendMasterVolume / 20.0)
        state.sendMixerA?.outputVolume = Float(linearA)
        state.sendMixerB?.outputVolume = Float(linearB)
        state.sendMasterMixer?.outputVolume = Float(linearMaster)
        autoSave()
    }

    // MARK: - 启动 / 停止

    func start() throws {
        // 授权检查：未激活/到期不允许启动引擎
        let lic = LicenseManager.checkLocalLicense()
        guard lic.valid else {
            licenseMessage = lic.message
            licenseNeedsAttention = true
            throw NSError(domain: "License", code: -1,
                          userInfo: [NSLocalizedDescriptionKey: lic.message])
        }

        guard let inDev = selectedInputDevice, let outDev = selectedOutputDevice else {
            throw NSError(domain: "Audio", code: -1,
                          userInfo: [NSLocalizedDescriptionKey: "请选择输入和输出设备"])
        }
        guard let inCh = selectedInputChannel, let outCh = selectedOutputChannel else {
            throw NSError(domain: "Audio", code: -2,
                          userInfo: [NSLocalizedDescriptionKey: "请选择输入和输出通道"])
        }

        stop()

        state.sampleRate = selectedSampleRate
        state.channels = 2
        state.inputChannelMap = inCh.channelMap
        state.outputChannelCount = outDev.outputChannelCount
        state.outputChannelOption = outCh
        state.outputMuted = outputMuted
        state.monitorChannelOption = monitorChannel
        state.inputMuted = inputMuted

        Self.setDeviceSampleRate(deviceID: inDev.id, rate: selectedSampleRate)
        Self.setDeviceSampleRate(deviceID: outDev.id, rate: selectedSampleRate)
        Self.setDeviceBufferSize(deviceID: inDev.id, size: selectedBufferSize)
        if outDev.id != inDev.id {
            Self.setDeviceBufferSize(deviceID: outDev.id, size: selectedBufferSize)
        }

        // ─────── AVAudioEngine 手动渲染模式 ───────
        let engine = AVAudioEngine()

        guard let renderFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: selectedSampleRate,
            channels: 2,
            interleaved: false
        ) else {
            throw NSError(domain: "Engine", code: -1,
                          userInfo: [NSLocalizedDescriptionKey: "无法创建渲染格式"])
        }

        try engine.enableManualRenderingMode(.realtime,
                                             format: renderFormat,
                                             maximumFrameCount: 4096)

        AudioDeviceManager.ringRef = state.ring
        AudioDeviceManager.stateRef = state
        AudioDeviceManager.recorderRef = recorder

        let sourceNode = AVAudioSourceNode(format: renderFormat) { _, _, frameCount, audioBufferList -> OSStatus in
            let abl = UnsafeMutableAudioBufferListPointer(audioBufferList)
            let frames = Int(frameCount)
            let totalSamples = frames * 2
            let targetSamples = frames * 4

            guard let ring = AudioDeviceManager.ringRef,
                  let state = AudioDeviceManager.stateRef else {
                for buffer in abl {
                    if let data = buffer.mData {
                        memset(data, 0, Int(buffer.mDataByteSize))
                    }
                }
                return noErr
            }

            let available = ring.count

            if available > targetSamples + totalSamples {
                let drop = available - targetSamples
                let dropCount = min(drop, state.sourceMaxFrames * 2)
                _ = ring.readInto(state.sourceTrash, count: dropCount)
            } else if available < totalSamples {
                for buffer in abl {
                    if let data = buffer.mData {
                        memset(data, 0, Int(buffer.mDataByteSize))
                    }
                }
                return noErr
            }

            let n = ring.readInto(state.sourceTemp, count: totalSamples)
            if n < totalSamples {
                for i in n..<totalSamples {
                    state.sourceTemp[i] = 0
                }
            }

            if abl.count >= 2 {
                if let dataL = abl[0].mData?.assumingMemoryBound(to: Float.self),
                   let dataR = abl[1].mData?.assumingMemoryBound(to: Float.self) {
                    for i in 0..<frames {
                        dataL[i] = state.sourceTemp[i]
                        dataR[i] = state.sourceTemp[frames + i]
                    }
                }
            }
            return noErr
        }
        engine.attach(sourceNode)
        state.sourceNode = sourceNode

        // ───── 主插件链 ─────
        var prev: AVAudioNode = sourceNode
        for plugin in state.plugins {
            engine.attach(plugin.avAudioUnit)
            engine.connect(prev, to: plugin.avAudioUnit, format: renderFormat)
            prev = plugin.avAudioUnit
        }

        // ───── 主插件链输出 → branchMixer ─────
        let branchMixer = AVAudioMixerNode()
        engine.attach(branchMixer)
        engine.connect(prev, to: branchMixer, format: renderFormat)
        state.branchMixer = branchMixer

        // ───── Send Master（合并两路 Send）─────
        let sendMasterMixer = AVAudioMixerNode()
        engine.attach(sendMasterMixer)
        state.sendMasterMixer = sendMasterMixer

        // ───── Send A 的 mixer ─────
        let sendMixerA = AVAudioMixerNode()
        engine.attach(sendMixerA)
        engine.connect(sendMixerA, to: sendMasterMixer, format: renderFormat)
        state.sendMixerA = sendMixerA
        
        if let pluginA = state.sendPluginA {
            engine.attach(pluginA)
            engine.connect(pluginA, to: sendMixerA, format: renderFormat)
        }
        // ───── Send B 的 mixer ─────
        let sendMixerB = AVAudioMixerNode()
        engine.attach(sendMixerB)
        engine.connect(sendMixerB, to: sendMasterMixer, format: renderFormat)
        state.sendMixerB = sendMixerB
        
        if let pluginB = state.sendPluginB {
            engine.attach(pluginB)
            engine.connect(pluginB, to: sendMixerB, format: renderFormat)
        }

        

        // ───── ⭐️ branchMixer 输出 fan-out 到 3 个目的地 ─────

        // 主路专用 mixer（汇聚 麦克风主链 + Send 回流 + AUX 源）
        let mainBusMixer = AVAudioMixerNode()
        engine.attach(mainBusMixer)

        // 总推子 mixer（位于插入效果器之后，控制所有声音）
        let masterMixer = AVAudioMixerNode()
        engine.attach(masterMixer)
        state.masterMixer = masterMixer

        // ───── AUX 源节点（把 AUX 环缓冲送进引擎，从而经过插入效果器和总推子）─────
        let auxSourceNode = AVAudioSourceNode(format: renderFormat) { _, _, frameCount, audioBufferList -> OSStatus in
            let ablPtr = UnsafeMutableAudioBufferListPointer(audioBufferList)
            let frames = Int(frameCount)

            func fillSilence() {
                for buffer in ablPtr {
                    if let data = buffer.mData {
                        memset(data, 0, Int(buffer.mDataByteSize))
                    }
                }
            }

            guard let state = AudioDeviceManager.stateRef, ablPtr.count >= 2,
                  let dataL = ablPtr[0].mData?.assumingMemoryBound(to: Float.self),
                  let dataR = ablPtr[1].mData?.assumingMemoryBound(to: Float.self) else {
                fillSilence()
                return noErr
            }
            fillSilence()

            // AUX 1/2 混入主路（增益在混音时施加；欠载补零，过载丢最旧）
            state.auxRMS1 = mixAuxRing(state.auxRing1, into: dataL, and: dataR,
                                       frames: frames, gain: state.auxGain1,
                                       scratch: state.auxScratch, trash: state.auxTrash)
            state.auxRMS2 = mixAuxRing(state.auxRing2, into: dataL, and: dataR,
                                       frames: frames, gain: state.auxGain2,
                                       scratch: state.auxScratch, trash: state.auxTrash)
            return noErr
        }
        engine.attach(auxSourceNode)
        state.auxSourceNode = auxSourceNode

        // fan-out 到 主路 + Send A + Send B（必须先连：这里显式占用 mainBusMixer 的输入 bus 0；
        // Send 路如果挂了插件，就先经过插件再进 sendMixer）
        let sendATarget: AVAudioNode = state.sendPluginA ?? sendMixerA
        let sendBTarget: AVAudioNode = state.sendPluginB ?? sendMixerB

        engine.connect(branchMixer,
                       to: [AVAudioConnectionPoint(node: mainBusMixer, bus: 0),
                            AVAudioConnectionPoint(node: sendATarget, bus: 0),
                            AVAudioConnectionPoint(node: sendBTarget, bus: 0)],
                       fromBus: 0,
                       format: renderFormat)

        // Send 路回流 + AUX 源汇入主路 mixer（在 fan-out 之后连接，引擎自动分配 bus 1/2；
        // 若顺序颠倒，fan-out 的显式 bus 0 会顶掉 Send 回流，导致两路 Send 没声音）
        engine.connect(sendMasterMixer, to: mainBusMixer, format: renderFormat)
        engine.connect(auxSourceNode, to: mainBusMixer, format: renderFormat)

        // ───── 主路 → (插入效果器) → 总推子 → 引擎输出 ─────
        if let insertAU = state.insertPlugin {
            engine.attach(insertAU)
            engine.connect(mainBusMixer, to: insertAU, format: renderFormat)
            engine.connect(insertAU, to: masterMixer, format: renderFormat)
        } else {
            engine.connect(mainBusMixer, to: masterMixer, format: renderFormat)
        }
        engine.connect(masterMixer, to: engine.mainMixerNode, format: renderFormat)

        applySendVolumes()
        engine.prepare()
        try engine.start()
        state.engine = engine

        // ─────── 双 AUHAL ───────
        let inputAU = try makeInputAU(deviceID: inDev.id,
                                      sampleRate: selectedSampleRate,
                                      channels: 2,
                                      channelMap: inCh.channelMap)
        let outputAU = try makeOutputAU(deviceID: outDev.id,
                                        sampleRate: selectedSampleRate,
                                        channels: outDev.outputChannelCount,
                                        channelMap: nil,
                                        callback: outputRenderCallback)
        state.inputAU = inputAU
        state.outputAU = outputAU

        var status = AudioUnitInitialize(inputAU)
        guard status == noErr else {
            throw NSError(domain: "AU", code: Int(status),
                          userInfo: [NSLocalizedDescriptionKey: "输入 AU 初始化失败: \(status)"])
        }
        status = AudioUnitInitialize(outputAU)
        guard status == noErr else {
            throw NSError(domain: "AU", code: Int(status),
                          userInfo: [NSLocalizedDescriptionKey: "输出 AU 初始化失败: \(status)"])
        }

        status = AudioOutputUnitStart(inputAU)
        guard status == noErr else {
            throw NSError(domain: "AU", code: Int(status),
                          userInfo: [NSLocalizedDescriptionKey: "输入 AU 启动失败: \(status)"])
        }
        status = AudioOutputUnitStart(outputAU)
        guard status == noErr else {
            throw NSError(domain: "AU", code: Int(status),
                          userInfo: [NSLocalizedDescriptionKey: "输出 AU 启动失败: \(status)"])
        }

        // ─────── 第二路输出（监听/推流）：整路混音同时送到这里 ───────
        state.monitorAU = nil
        state.monitorRing.clear()
        if let mDev = monitorDevice, let mCh = monitorChannel, mDev.outputChannelCount >= 2 {
            do {
                Self.setDeviceSampleRate(deviceID: mDev.id, rate: selectedSampleRate)
                let monitorAU = try makeOutputAU(deviceID: mDev.id,
                                                 sampleRate: selectedSampleRate,
                                                 channels: mDev.outputChannelCount,
                                                 channelMap: nil,
                                                 callback: monitorRenderCallback)
                var mStatus = AudioUnitInitialize(monitorAU)
                guard mStatus == noErr else {
                    throw NSError(domain: "AU", code: Int(mStatus),
                                  userInfo: [NSLocalizedDescriptionKey: "第二路输出初始化失败: \(mStatus)"])
                }
                mStatus = AudioOutputUnitStart(monitorAU)
                guard mStatus == noErr else {
                    AudioUnitUninitialize(monitorAU)
                    throw NSError(domain: "AU", code: Int(mStatus),
                                  userInfo: [NSLocalizedDescriptionKey: "第二路输出启动失败: \(mStatus)"])
                }
                state.monitorAU = monitorAU
                print("✅ 第二路输出已启动: \(mDev.name) · \(mCh.name)")
            } catch {
                state.monitorAU = nil
                print("⚠️ 第二路输出不可用: \(error.localizedDescription)")
            }
        }

        // ─────── AUX 输入 1 / 2（音乐/音效软件进声，立体声直混）───────
        for (tag, dev, ch) in [(1, aux1Device, aux1Channel), (2, aux2Device, aux2Channel)] {
            if let auxDev = dev, let auxCh = ch, auxDev.inputChannelCount > 0 {
                do {
                    let ctx = try makeAuxAU(tag: tag,
                                            deviceID: auxDev.id,
                                            sampleRate: selectedSampleRate,
                                            channelMap: auxCh.channelMap)
                    guard let auxUnit = ctx.unit else {
                        throw NSError(domain: "AU", code: -1,
                                      userInfo: [NSLocalizedDescriptionKey: "AUX\(tag) 创建失败"])
                    }
                    var initStatus = AudioUnitInitialize(auxUnit)
                    guard initStatus == noErr else {
                        throw NSError(domain: "AU", code: Int(initStatus),
                                      userInfo: [NSLocalizedDescriptionKey: "AUX\(tag) 初始化失败: \(initStatus)"])
                    }
                    initStatus = AudioOutputUnitStart(auxUnit)
                    guard initStatus == noErr else {
                        AudioUnitUninitialize(auxUnit)
                        throw NSError(domain: "AU", code: Int(initStatus),
                                      userInfo: [NSLocalizedDescriptionKey: "AUX\(tag) 启动失败: \(initStatus)"])
                    }
                    if tag == 1 { state.auxInputAU1 = ctx.unit; state.auxCtx1 = ctx }
                    else { state.auxInputAU2 = ctx.unit; state.auxCtx2 = ctx }
                } catch {
                    print("⚠️ AUX\(tag) 不可用: \(error.localizedDescription)")
                }
            }
        }

        isRunning = true
        applyMasterGain()
        applyAuxGains()
        startLevelTimer()
        print("✅ 引擎已启动 \(selectedSampleRate)Hz / \(selectedBufferSize)帧 / \(state.plugins.count) 插件 / 输出 \(outDev.outputChannelCount)ch")
    }

    func stop() {
        stopLevelTimer()
        if isRecording {
            stopRecording()
        }
        if let au = state.inputAU {
            AudioOutputUnitStop(au)
            AudioUnitUninitialize(au)
            AudioComponentInstanceDispose(au)
        }
        if let au = state.outputAU {
            AudioOutputUnitStop(au)
            AudioUnitUninitialize(au)
            AudioComponentInstanceDispose(au)
        }
        if let au = state.monitorAU {
            AudioOutputUnitStop(au)
            AudioUnitUninitialize(au)
            AudioComponentInstanceDispose(au)
        }
        for ctx in [state.auxCtx1, state.auxCtx2] {
            if let unit = ctx?.unit {
                AudioOutputUnitStop(unit)
                AudioUnitUninitialize(unit)
                AudioComponentInstanceDispose(unit)
                ctx?.unit = nil
            }
        }
        state.engine?.stop()
        state.engine = nil
        state.sourceNode = nil
        state.auxSourceNode = nil
        state.inputAU = nil
        state.outputAU = nil
        state.monitorAU = nil
        state.masterMixer = nil
        state.auxInputAU1 = nil
        state.auxInputAU2 = nil
        state.auxCtx1 = nil
        state.auxCtx2 = nil

        // 清理 Send 链
        state.sendMixerA = nil
        state.sendMixerB = nil
        state.sendMasterMixer = nil
        state.branchMixer = nil

        state.ring.clear()
        state.auxRing1.clear()
        state.auxRing2.clear()
        state.monitorRing.clear()
        state.auxRMS1 = 0
        state.auxRMS2 = 0
        isRunning = false
                inputLevel = -60
                outputLevel = -60
                inputPeak = -60
                outputPeak = -60
                aux1Level = -60
                aux2Level = -60
        print("🛑 引擎已停止")
    }

    func reloadEngineIfNeeded() {
        guard isRunning else { return }
        stop()
        try? start()
    }

    // MARK: - 电平定时器

    private func startLevelTimer() {
        levelTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            let now = Date()
            let holdSeconds: TimeInterval = 2.0
            let fallPerTick: Double = 1.5   // 每 tick(50ms) 回落的 dB 数

            let inNow = Self.rmsToDB(rms: self.state.inputRMS)
            let outNow = Self.rmsToDB(rms: self.state.outputRMS)
            let aux1Now = Self.rmsToDB(rms: self.state.auxRMS1)
            let aux2Now = Self.rmsToDB(rms: self.state.auxRMS2)

            self.inputLevel = inNow
            self.outputLevel = outNow
            self.aux1Level = aux1Now
            self.aux2Level = aux2Now

            // 输入峰值
            if inNow >= self.inputPeak {
                self.inputPeak = inNow
                self.inputPeakTime = now
            } else if now.timeIntervalSince(self.inputPeakTime) > holdSeconds {
                self.inputPeak = max(inNow, self.inputPeak - fallPerTick)
            }

            // 输出峰值
            if outNow >= self.outputPeak {
                self.outputPeak = outNow
                self.outputPeakTime = now
            } else if now.timeIntervalSince(self.outputPeakTime) > holdSeconds {
                self.outputPeak = max(outNow, self.outputPeak - fallPerTick)
            }
        }
    }

    private func stopLevelTimer() {
        levelTimer?.invalidate()
        levelTimer = nil
    }

    nonisolated private static func rmsToDB(rms: Float) -> Double {
        if rms < 1e-8 { return -60.0 }
        return 20 * log10(Double(rms))
    }

    // MARK: - 输入 AUHAL

    private func makeInputAU(deviceID: AudioDeviceID,
                             sampleRate: Double,
                             channels: Int,
                             channelMap: [Int32]) throws -> AudioUnit {
        var desc = AudioComponentDescription(
            componentType: kAudioUnitType_Output,
            componentSubType: kAudioUnitSubType_HALOutput,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0, componentFlagsMask: 0
        )
        guard let comp = AudioComponentFindNext(nil, &desc) else { throw NSError(domain: "AU", code: -1) }
        var au: AudioUnit?
        AudioComponentInstanceNew(comp, &au)
        guard let unit = au else { throw NSError(domain: "AU", code: -1) }

        var enable: UInt32 = 1
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                             kAudioUnitScope_Input, 1, &enable,
                             UInt32(MemoryLayout<UInt32>.size))
        var disable: UInt32 = 0
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                             kAudioUnitScope_Output, 0, &disable,
                             UInt32(MemoryLayout<UInt32>.size))

        var devID = deviceID
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice,
                             kAudioUnitScope_Global, 0, &devID,
                             UInt32(MemoryLayout<AudioDeviceID>.size))

        var map = channelMap
        if !map.isEmpty {
            AudioUnitSetProperty(unit, kAudioOutputUnitProperty_ChannelMap,
                                 kAudioUnitScope_Output, 1, &map,
                                 UInt32(map.count * MemoryLayout<Int32>.size))
        }

        var asbd = AudioStreamBasicDescription(
            mSampleRate: sampleRate,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
            mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4,
            mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 32, mReserved: 0
        )
        AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat,
                             kAudioUnitScope_Output, 1, &asbd,
                             UInt32(MemoryLayout<AudioStreamBasicDescription>.size))

        var callback = AURenderCallbackStruct(
            inputProc: inputRenderCallback,
            inputProcRefCon: Unmanaged.passUnretained(state).toOpaque()
        )
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_SetInputCallback,
                             kAudioUnitScope_Global, 0, &callback,
                             UInt32(MemoryLayout<AURenderCallbackStruct>.size))

        return unit
    }

    // MARK: - AUX 输入 AUHAL

    /// 为 AUX 路建一个 AUHAL 输入单元（不改动该设备的采样率，用其当前速率，漂移由环缓冲兜底）。
    private func makeAuxAU(tag: Int,
                           deviceID: AudioDeviceID,
                           sampleRate: Double,
                           channelMap: [Int32]) throws -> AuxContext {
        var desc = AudioComponentDescription(
            componentType: kAudioUnitType_Output,
            componentSubType: kAudioUnitSubType_HALOutput,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0, componentFlagsMask: 0
        )
        guard let comp = AudioComponentFindNext(nil, &desc) else { throw NSError(domain: "AU", code: -1) }
        var au: AudioUnit?
        AudioComponentInstanceNew(comp, &au)
        guard let unit = au else { throw NSError(domain: "AU", code: -1) }

        var enable: UInt32 = 1
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                             kAudioUnitScope_Input, 1, &enable,
                             UInt32(MemoryLayout<UInt32>.size))
        var disable: UInt32 = 0
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                             kAudioUnitScope_Output, 0, &disable,
                             UInt32(MemoryLayout<UInt32>.size))

        var devID = deviceID
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice,
                             kAudioUnitScope_Global, 0, &devID,
                             UInt32(MemoryLayout<AudioDeviceID>.size))

        var map = channelMap
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_ChannelMap,
                             kAudioUnitScope_Output, 1, &map,
                             UInt32(map.count * MemoryLayout<Int32>.size))

        let deviceRate = Self.getDeviceSampleRate(deviceID: deviceID) ?? sampleRate
        var asbd = AudioStreamBasicDescription(
            mSampleRate: deviceRate,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
            mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4,
            mChannelsPerFrame: 2, mBitsPerChannel: 32, mReserved: 0
        )
        AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat,
                             kAudioUnitScope_Output, 1, &asbd,
                             UInt32(MemoryLayout<AudioStreamBasicDescription>.size))

        let ctx = AuxContext(state: state, tag: tag)
        ctx.unit = unit
        var callback = AURenderCallbackStruct(
            inputProc: auxRenderCallback,
            inputProcRefCon: Unmanaged.passUnretained(ctx).toOpaque()
        )
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_SetInputCallback,
                             kAudioUnitScope_Global, 0, &callback,
                             UInt32(MemoryLayout<AURenderCallbackStruct>.size))

        return ctx
    }

    // MARK: - 输出 AUHAL

    private func makeOutputAU(deviceID: AudioDeviceID,
                              sampleRate: Double,
                              channels: Int,
                              channelMap: [Int32]?,
                              callback: AURenderCallback) throws -> AudioUnit {
        var desc = AudioComponentDescription(
            componentType: kAudioUnitType_Output,
            componentSubType: kAudioUnitSubType_HALOutput,
            componentManufacturer: kAudioUnitManufacturer_Apple,
            componentFlags: 0, componentFlagsMask: 0
        )
        guard let comp = AudioComponentFindNext(nil, &desc) else { throw NSError(domain: "AU", code: -1) }
        var au: AudioUnit?
        AudioComponentInstanceNew(comp, &au)
        guard let unit = au else { throw NSError(domain: "AU", code: -1) }

        var disable: UInt32 = 0
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                             kAudioUnitScope_Input, 1, &disable,
                             UInt32(MemoryLayout<UInt32>.size))
        var enable: UInt32 = 1
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO,
                             kAudioUnitScope_Output, 0, &enable,
                             UInt32(MemoryLayout<UInt32>.size))

        var devID = deviceID
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice,
                             kAudioUnitScope_Global, 0, &devID,
                             UInt32(MemoryLayout<AudioDeviceID>.size))

        var asbd = AudioStreamBasicDescription(
            mSampleRate: sampleRate,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
            mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4,
            mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 32, mReserved: 0
        )
        AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat,
                             kAudioUnitScope_Input, 0, &asbd,
                             UInt32(MemoryLayout<AudioStreamBasicDescription>.size))

        // 直通映射
        var identity = (0..<channels).map { Int32($0) }
        AudioUnitSetProperty(unit, kAudioOutputUnitProperty_ChannelMap,
                             kAudioUnitScope_Input, 0, &identity,
                             UInt32(identity.count * MemoryLayout<Int32>.size))

        var callbackStruct = AURenderCallbackStruct(
            inputProc: callback,
            inputProcRefCon: Unmanaged.passUnretained(state).toOpaque()
        )
        AudioUnitSetProperty(unit, kAudioUnitProperty_SetRenderCallback,
                             kAudioUnitScope_Input, 0, &callbackStruct,
                             UInt32(MemoryLayout<AURenderCallbackStruct>.size))

        return unit
    }

    // MARK: - 预设

    func savePreset(named name: String) -> RackPreset {
        var pluginPresets: [PluginPreset] = []
        for plugin in pluginChain {
            var fullStateData: Data?
            let auUnit = plugin.avAudioUnit.auAudioUnit
            if let state = auUnit.fullState {
                fullStateData = try? PropertyListSerialization.data(
                    fromPropertyList: state, format: .binary, options: 0)
            }
            let desc = plugin.componentDesc
            pluginPresets.append(PluginPreset(
                name: plugin.name,
                componentType: desc.componentType,
                componentSubType: desc.componentSubType,
                componentManufacturer: desc.componentManufacturer,
                bypassed: plugin.bypassed,
                fullState: fullStateData
            ))
        }

        // 打包 Send A
        var sendPresetA: PluginPreset?
        if let unitA = state.sendPluginA {
            var fullStateData: Data?
            let auUnit = unitA.auAudioUnit
            if let st = auUnit.fullState {
                fullStateData = try? PropertyListSerialization.data(
                    fromPropertyList: st, format: .binary, options: 0)
            }
            let desc = unitA.auAudioUnit.componentDescription
            sendPresetA = PluginPreset(
                name: sendPluginNameA ?? "Send A",
                componentType: desc.componentType,
                componentSubType: desc.componentSubType,
                componentManufacturer: desc.componentManufacturer,
                bypassed: sendBypassedA,
                fullState: fullStateData
            )
        }

        // 打包 Send B
        var sendPresetB: PluginPreset?
        if let unitB = state.sendPluginB {
            var fullStateData: Data?
            let auUnit = unitB.auAudioUnit
            if let st = auUnit.fullState {
                fullStateData = try? PropertyListSerialization.data(
                    fromPropertyList: st, format: .binary, options: 0)
            }
            let desc = unitB.auAudioUnit.componentDescription
            sendPresetB = PluginPreset(
                name: sendPluginNameB ?? "Send B",
                componentType: desc.componentType,
                componentSubType: desc.componentSubType,
                componentManufacturer: desc.componentManufacturer,
                bypassed: sendBypassedB,
                fullState: fullStateData
            )
        }

        // 打包插入效果器
        var insertPreset: PluginPreset?
        if let unitI = state.insertPlugin {
            var fullStateData: Data?
            let auUnit = unitI.auAudioUnit
            if let st = auUnit.fullState {
                fullStateData = try? PropertyListSerialization.data(
                    fromPropertyList: st, format: .binary, options: 0)
            }
            let desc = auUnit.componentDescription
            insertPreset = PluginPreset(
                name: insertPluginName ?? "插入效果器",
                componentType: desc.componentType,
                componentSubType: desc.componentSubType,
                componentManufacturer: desc.componentManufacturer,
                bypassed: insertBypassed,
                fullState: fullStateData
            )
        }

        return RackPreset(
            name: name,
            inputDeviceName: selectedInputDevice?.name,
            outputDeviceName: selectedOutputDevice?.name,
            inputChannelMap: selectedInputChannel?.channelMap,
            outputChannelStart: selectedOutputChannel?.startChannel,
            sampleRate: selectedSampleRate,
            bufferSize: selectedBufferSize,
            masterGainDB: masterGainDB,
            plugins: pluginPresets,
            sendPluginA: sendPresetA,
            sendPluginB: sendPresetB,
            sendVolumeA: sendVolumeA,
            sendVolumeB: sendVolumeB,
            sendMasterVolume: sendMasterVolume,
            aux1Name: aux1Name.isEmpty ? nil : aux1Name,
            aux1DeviceName: aux1Device?.name,
            aux1ChannelMap: aux1Channel?.channelMap,
            aux1GainDB: aux1GainDB,
            aux2Name: aux2Name.isEmpty ? nil : aux2Name,
            aux2DeviceName: aux2Device?.name,
            aux2ChannelMap: aux2Channel?.channelMap,
            aux2GainDB: aux2GainDB,
            insertPlugin: insertPreset,
            monitorDeviceName: monitorDevice?.name,
            monitorChannelStart: monitorChannel?.startChannel
        )
    }

    func loadPreset(_ preset: RackPreset) async throws {
        let wasRunning = isRunning
        if wasRunning { stop() }

        pluginChain.removeAll()
        state.plugins.removeAll()

        masterGainDB = preset.masterGainDB
        selectedSampleRate = preset.sampleRate
        selectedBufferSize = preset.bufferSize

        if let name = preset.inputDeviceName,
           let dev = devices.first(where: { $0.name == name && $0.inputChannelCount > 0 }) {
            selectedInputDevice = dev
            updateInputChannels(for: dev)
            updateDeviceFormats(for: dev)
        }
        if let name = preset.outputDeviceName,
           let dev = devices.first(where: { $0.name == name && $0.outputChannelCount >= 2 }) {
            selectedOutputDevice = dev
            updateOutputChannels(for: dev)
        }
        if let map = preset.inputChannelMap {
            selectedInputChannel = inputChannelOptions.first(where: { $0.channelMap == map })
        }
        if let start = preset.outputChannelStart {
            selectedOutputChannel = outputChannelOptions.first(where: { $0.startChannel == start })
        }

        for pluginPreset in preset.plugins {
            let desc = AudioComponentDescription(
                componentType: pluginPreset.componentType,
                componentSubType: pluginPreset.componentSubType,
                componentManufacturer: pluginPreset.componentManufacturer,
                componentFlags: 0, componentFlagsMask: 0
            )
            let avUnit: AVAudioUnit? = await withCheckedContinuation { continuation in
                AVAudioUnit.instantiate(with: desc, options: []) { unit, _ in
                    continuation.resume(returning: unit)
                }
            }
            guard let avUnit = avUnit else { continue }

            if let data = pluginPreset.fullState,
               let st = try? PropertyListSerialization.propertyList(
                   from: data, options: [], format: nil) as? [String: Any] {
                avUnit.auAudioUnit.fullState = st
            }

            let plugin = LoadedPlugin(name: pluginPreset.name,
                                      componentDesc: desc,
                                      avAudioUnit: avUnit)
            plugin.bypassed = pluginPreset.bypassed
            pluginChain.append(plugin)
            state.plugins.append(plugin)
        }

        // 恢复 Send 音量
        if let va = preset.sendVolumeA { sendVolumeA = va }
        if let vb = preset.sendVolumeB { sendVolumeB = vb }
        if let vm = preset.sendMasterVolume { sendMasterVolume = vm }

        // 恢复 AUX 输入
        aux1Name = preset.aux1Name ?? ""
        if let name = preset.aux1DeviceName,
           let dev = devices.first(where: { $0.name == name && $0.inputChannelCount > 0 }) {
            aux1Device = dev
            if let map = preset.aux1ChannelMap {
                aux1Channel = Self.auxChannelOptions(for: dev).first(where: { $0.channelMap == map })
            }
        } else {
            aux1Device = nil
            aux1Channel = nil
        }
        if let g1 = preset.aux1GainDB { aux1GainDB = g1 }
        if let name = preset.aux2DeviceName,
           let dev = devices.first(where: { $0.name == name && $0.inputChannelCount > 0 }) {
            aux2Device = dev
            if let map = preset.aux2ChannelMap {
                aux2Channel = Self.auxChannelOptions(for: dev).first(where: { $0.channelMap == map })
            }
        } else {
            aux2Device = nil
            aux2Channel = nil
        }
        if let g2 = preset.aux2GainDB { aux2GainDB = g2 }
        aux2Name = preset.aux2Name ?? ""

        // 恢复 Send A 插件
        if let pA = preset.sendPluginA {
            let descA = AudioComponentDescription(
                componentType: pA.componentType,
                componentSubType: pA.componentSubType,
                componentManufacturer: pA.componentManufacturer,
                componentFlags: 0, componentFlagsMask: 0
            )
            let unitA: AVAudioUnitEffect? = await withCheckedContinuation { cont in
                AVAudioUnit.instantiate(with: descA, options: []) { unit, _ in
                    cont.resume(returning: unit as? AVAudioUnitEffect)
                }
            }
            if let unitA = unitA {
                if let data = pA.fullState,
                   let st = try? PropertyListSerialization.propertyList(
                       from: data, options: [], format: nil) as? [String: Any] {
                    unitA.auAudioUnit.fullState = st
                }
                state.sendPluginA = unitA
                sendPluginNameA = pA.name
                sendBypassedA = pA.bypassed
            }
        }

        // 恢复 Send B 插件
        if let pB = preset.sendPluginB {
            let descB = AudioComponentDescription(
                componentType: pB.componentType,
                componentSubType: pB.componentSubType,
                componentManufacturer: pB.componentManufacturer,
                componentFlags: 0, componentFlagsMask: 0
            )
            let unitB: AVAudioUnitEffect? = await withCheckedContinuation { cont in
                AVAudioUnit.instantiate(with: descB, options: []) { unit, _ in
                    cont.resume(returning: unit as? AVAudioUnitEffect)
                }
            }
            if let unitB = unitB {
                if let data = pB.fullState,
                   let st = try? PropertyListSerialization.propertyList(
                       from: data, options: [], format: nil) as? [String: Any] {
                    unitB.auAudioUnit.fullState = st
                }
                state.sendPluginB = unitB
                sendPluginNameB = pB.name
                sendBypassedB = pB.bypassed
            }
        }

        // 恢复插入效果器
        if let pI = preset.insertPlugin {
            let descI = AudioComponentDescription(
                componentType: pI.componentType,
                componentSubType: pI.componentSubType,
                componentManufacturer: pI.componentManufacturer,
                componentFlags: 0, componentFlagsMask: 0
            )
            let unitI: AVAudioUnitEffect? = await withCheckedContinuation { cont in
                AVAudioUnit.instantiate(with: descI, options: []) { unit, _ in
                    cont.resume(returning: unit as? AVAudioUnitEffect)
                }
            }
            if let unitI = unitI {
                if let data = pI.fullState,
                   let st = try? PropertyListSerialization.propertyList(
                       from: data, options: [], format: nil) as? [String: Any] {
                    unitI.auAudioUnit.fullState = st
                }
                state.insertPlugin = unitI
                insertPluginName = pI.name
                insertBypassed = pI.bypassed
                unitI.auAudioUnit.shouldBypassEffect = pI.bypassed
            }
        } else {
            state.insertPlugin = nil
            insertPluginName = nil
            insertBypassed = false
        }

        // 恢复第二路输出
        if let name = preset.monitorDeviceName,
           let dev = devices.first(where: { $0.name == name && $0.outputChannelCount >= 2 }) {
            monitorDevice = dev
            if let start = preset.monitorChannelStart {
                monitorChannel = Self.monitorChannelOptions(for: dev).first(where: { $0.startChannel == start })
            } else {
                monitorChannel = Self.monitorChannelOptions(for: dev).first
            }
        } else {
            monitorDevice = nil
            monitorChannel = nil
        }

        if wasRunning {
            try start()
        }
    }

    func autoSave() {
        let preset = savePreset(named: "autosave")
        if let data = try? JSONEncoder().encode(preset) {
            try? data.write(to: Self.autoSaveURL)
        }
    }

    func restoreAutoSaveIfExists() async {
        guard FileManager.default.fileExists(atPath: Self.autoSaveURL.path) else { return }
        if let data = try? Data(contentsOf: Self.autoSaveURL),
           let preset = try? JSONDecoder().decode(RackPreset.self, from: data) {
            try? await loadPreset(preset)
        }
    }

    // MARK: - 设备查询

    static func getDevices() -> [AudioDevice] {
        var devices: [AudioDevice] = []
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject),
                                             &addr, 0, nil, &size) == noErr else { return [] }
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        var ids = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject),
                                         &addr, 0, nil, &size, &ids) == noErr else { return [] }
        for id in ids {
            var name: CFString = "" as CFString
            var nameAddr = AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyDeviceNameCFString,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain
            )
            var nameSize = UInt32(MemoryLayout<CFString>.size)
            guard AudioObjectGetPropertyData(id, &nameAddr, 0, nil, &nameSize, &name) == noErr else { continue }
            let inCh = channelCount(deviceID: id, scope: kAudioDevicePropertyScopeInput)
            let outCh = channelCount(deviceID: id, scope: kAudioDevicePropertyScopeOutput)
            devices.append(AudioDevice(id: id, name: name as String,
                                       inputChannelCount: inCh, outputChannelCount: outCh))
        }
        return devices
    }

    static func getDeviceSampleRate(deviceID: AudioDeviceID) -> Double? {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyNominalSampleRate,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var rate: Double = 0
        var size = UInt32(MemoryLayout<Double>.size)
        guard AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &size, &rate) == noErr else { return nil }
        return rate
    }

    static func setDeviceSampleRate(deviceID: AudioDeviceID, rate: Double) {
        var sampleRate = AudioValueRange(mMinimum: rate, mMaximum: rate)
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyNominalSampleRate,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        AudioObjectSetPropertyData(deviceID, &addr, 0, nil,
                                   UInt32(MemoryLayout<AudioValueRange>.size), &sampleRate)
    }

    static func getDeviceBufferSize(deviceID: AudioDeviceID) -> Int? {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyBufferFrameSize,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var bufSize: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &size, &bufSize) == noErr else { return nil }
        return Int(bufSize)
    }

    static func setDeviceBufferSize(deviceID: AudioDeviceID, size: Int) {
        var bufSize = UInt32(size)
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyBufferFrameSize,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        AudioObjectSetPropertyData(deviceID, &addr, 0, nil,
                                   UInt32(MemoryLayout<UInt32>.size), &bufSize)
    }

    static func getAvailableSampleRates(deviceID: AudioDeviceID) -> [Double] {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyAvailableNominalSampleRates,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &addr, 0, nil, &size) == noErr, size > 0 else {
            return [44100, 48000, 88200, 96000]
        }
        let count = Int(size) / MemoryLayout<AudioValueRange>.size
        var ranges = [AudioValueRange](repeating: AudioValueRange(), count: count)
        var ioSize = size
        guard AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &ioSize, &ranges) == noErr else {
            return [44100, 48000, 88200, 96000]
        }
        var result: [Double] = []
        for r in ranges {
            if r.mMinimum == r.mMaximum {
                result.append(r.mMinimum)
            } else {
                for c in [44100.0, 48000.0, 88200.0, 96000.0, 176400.0, 192000.0] {
                    if c >= r.mMinimum && c <= r.mMaximum && !result.contains(c) {
                        result.append(c)
                    }
                }
            }
        }
        return result.sorted()
    }

    static func getAvailableBufferSizes(deviceID: AudioDeviceID) -> [Int] {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyBufferFrameSizeRange,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var range = AudioValueRange()
        var size = UInt32(MemoryLayout<AudioValueRange>.size)
        guard AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &size, &range) == noErr else {
            return [32, 64, 128, 256, 512, 1024, 2048]
        }
        var result: [Int] = []
        for v in [32, 64, 128, 256, 512, 1024, 2048] {
            if v >= Int(range.mMinimum) && v <= Int(range.mMaximum) {
                result.append(v)
            }
        }
        if result.isEmpty { result = [32, 64, 128, 256, 512, 1024, 2048] }
        return result.sorted()
    }

    private static func channelCount(deviceID: AudioDeviceID, scope: AudioObjectPropertyScope) -> Int {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: scope,
            mElement: kAudioObjectPropertyElementMain
        )
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(deviceID, &addr, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let buf = UnsafeMutableRawPointer.allocate(byteCount: Int(size),
                                                   alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { buf.deallocate() }
        guard AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &size, buf) == noErr else { return 0 }
        let abl = UnsafeMutableAudioBufferListPointer(buf.assumingMemoryBound(to: AudioBufferList.self))
        var total = 0
        for b in abl { total += Int(b.mNumberChannels) }
        return total
    }
}

// MARK: - C 回调

/// AUX 输入回调：把所选立体声通道对推入对应的环缓冲
private let auxRenderCallback: AURenderCallback = {
    (inRefCon, ioActionFlags, inTimeStamp, inBusNumber, inNumberFrames, _) -> OSStatus in

    let ctx = Unmanaged<AuxContext>.fromOpaque(inRefCon).takeUnretainedValue()
    guard let unit = ctx.unit else { return noErr }
    let state = ctx.state

    let frames = Int(inNumberFrames)
    let bytesPerChannel = frames * MemoryLayout<Float>.size

    let dataPtrs: [UnsafeMutableRawPointer] = (0..<2).map { _ in
        UnsafeMutableRawPointer.allocate(byteCount: bytesPerChannel,
                                         alignment: MemoryLayout<Float>.alignment)
    }
    defer { dataPtrs.forEach { $0.deallocate() } }

    let bufferListSize = MemoryLayout<AudioBufferList>.size + MemoryLayout<AudioBuffer>.size
    let rawABL = UnsafeMutableRawPointer.allocate(byteCount: bufferListSize,
                                                   alignment: MemoryLayout<AudioBufferList>.alignment)
    defer { rawABL.deallocate() }

    let abl = rawABL.assumingMemoryBound(to: AudioBufferList.self)
    let ablPtr = UnsafeMutableAudioBufferListPointer(abl)
    ablPtr.count = 2
    for i in 0..<2 {
        ablPtr[i] = AudioBuffer(mNumberChannels: 1,
                                mDataByteSize: UInt32(bytesPerChannel),
                                mData: dataPtrs[i])
    }

    let status = AudioUnitRender(unit, ioActionFlags, inTimeStamp,
                                 inBusNumber, inNumberFrames, abl)
    guard status == noErr else { return status }

    let l = dataPtrs[0].assumingMemoryBound(to: Float.self)
    let r = dataPtrs[1].assumingMemoryBound(to: Float.self)

    // 交错存入环缓冲
    var interleaved = [Float](repeating: 0, count: frames * 2)
    var sum: Float = 0
    for i in 0..<frames {
        interleaved[i * 2] = l[i]
        interleaved[i * 2 + 1] = r[i]
        sum += l[i] * l[i] + r[i] * r[i]
    }

    switch ctx.tag {
    case 1:
        state.auxRing1.append(interleaved)
        state.auxRMS1 = sqrt(sum / Float(max(frames * 2, 1)))
    default:
        state.auxRing2.append(interleaved)
        state.auxRMS2 = sqrt(sum / Float(max(frames * 2, 1)))
    }
    return noErr
}

private let inputRenderCallback: AURenderCallback = {
    (inRefCon, ioActionFlags, inTimeStamp, inBusNumber, inNumberFrames, _) -> OSStatus in

    let state = Unmanaged<AudioEngineState>.fromOpaque(inRefCon).takeUnretainedValue()
    guard let inputUnit = state.inputAU else { return noErr }

    let channels = state.channels
    let frames = Int(inNumberFrames)
    let bytesPerChannel = frames * MemoryLayout<Float>.size

    let dataPtrs: [UnsafeMutableRawPointer] = (0..<channels).map { _ in
        UnsafeMutableRawPointer.allocate(byteCount: bytesPerChannel,
                                         alignment: MemoryLayout<Float>.alignment)
    }
    defer { dataPtrs.forEach { $0.deallocate() } }

    let bufferListSize = MemoryLayout<AudioBufferList>.size
        + (channels - 1) * MemoryLayout<AudioBuffer>.size
    let rawABL = UnsafeMutableRawPointer.allocate(byteCount: bufferListSize,
                                                   alignment: MemoryLayout<AudioBufferList>.alignment)
    defer { rawABL.deallocate() }

    let abl = rawABL.assumingMemoryBound(to: AudioBufferList.self)
    let ablPtr = UnsafeMutableAudioBufferListPointer(abl)
    ablPtr.count = channels
    for i in 0..<channels {
        ablPtr[i] = AudioBuffer(mNumberChannels: 1,
                                mDataByteSize: UInt32(bytesPerChannel),
                                mData: dataPtrs[i])
    }

    let status = AudioUnitRender(inputUnit, ioActionFlags, inTimeStamp,
                                 inBusNumber, inNumberFrames, abl)
    guard status == noErr else { return status }

    let isMonoToStereo = state.inputChannelMap.count == 2
        && state.inputChannelMap[0] == state.inputChannelMap[1]
    let attenuation: Float = isMonoToStereo ? 0.70794578 : 1.0

    let scratch = state.inputScratch
    for ch in 0..<channels {
        let src = dataPtrs[ch].assumingMemoryBound(to: Float.self)
        let dstOffset = ch * frames
        for i in 0..<frames {
            scratch[dstOffset + i] = src[i] * attenuation
        }
    }

    // 闭麦：直接把输入声音静音（输出、Send、录音里都没有麦克风）
    if state.inputMuted {
        for i in 0..<(frames * channels) {
            scratch[i] = 0
        }
    }

    var sum: Float = 0
    for i in 0..<(frames * channels) { sum += scratch[i] * scratch[i] }
    state.inputRMS = sqrt(sum / Float(max(frames * channels, 1)))

    state.ring.append(scratch, count: frames * channels)

    return noErr
}

/// 第二路输出回调：从监听环缓冲取最终混音，写到所选输出通道对
private let monitorRenderCallback: AURenderCallback = {
    (inRefCon, _, _, _, inNumberFrames, ioData) -> OSStatus in

    let state = Unmanaged<AudioEngineState>.fromOpaque(inRefCon).takeUnretainedValue()
    guard let ioData = ioData else { return noErr }

    let frames = Int(inNumberFrames)
    let abl = UnsafeMutableAudioBufferListPointer(ioData)

    for buffer in abl {
        if let data = buffer.mData {
            memset(data, 0, Int(buffer.mDataByteSize))
        }
    }

    guard state.monitorAU != nil, !state.outputMuted else { return noErr }

    let startCh = Int(state.monitorChannelOption?.startChannel ?? 0)
    guard startCh + 1 < abl.count,
          let dataL = abl[startCh].mData?.assumingMemoryBound(to: Float.self),
          let dataR = abl[startCh + 1].mData?.assumingMemoryBound(to: Float.self) else {
        return noErr
    }

    let want = frames * 2
    let ring = state.monitorRing

    // 积压过多：丢掉最旧的，延迟不累积
    if ring.count > want * 4 {
        let drop = min(ring.count - want, 65536)
        _ = ring.readInto(state.monitorTrash, count: drop)
    }

    let n = ring.readInto(state.monitorTmp, count: want)

    for i in 0..<frames {
        dataL[i] = i * 2 < n ? state.monitorTmp[i * 2] : 0
        dataR[i] = i * 2 + 1 < n ? state.monitorTmp[i * 2 + 1] : 0
    }
    return noErr
}

/// 把 AUX 环缓冲混入主输出（非交错 L/R），返回施加增益后的 RMS 电平
/// scratch/trash 为调用方预分配缓冲（实时线程禁止分配内存）
private func mixAuxRing(_ ring: AudioRingBuffer,
                        into l: UnsafeMutablePointer<Float>,
                        and r: UnsafeMutablePointer<Float>,
                        frames: Int,
                        gain: Float,
                        scratch: UnsafeMutablePointer<Float>,
                        trash: UnsafeMutablePointer<Float>) -> Float {
    let want = frames * 2
    let available = ring.count

    // 积压过多（采样率不一致等）：丢掉最旧的，延迟不累积
    if available > want * 4 {
        let drop = min(available - want, 65536)
        _ = ring.readInto(trash, count: drop)
    }

    let n = ring.readInto(scratch, count: want)

    var sum: Float = 0
    for i in 0..<frames {
        let gl: Float = i * 2 < n ? scratch[i * 2] * gain : 0
        let gr: Float = i * 2 + 1 < n ? scratch[i * 2 + 1] * gain : 0
        l[i] += gl
        r[i] += gr
        sum += gl * gl + gr * gr
    }
    return sqrt(sum / Float(max(frames * 2, 1)))
}

private let outputRenderCallback: AURenderCallback = {
    (inRefCon, _, _, _, inNumberFrames, ioData) -> OSStatus in

    let state = Unmanaged<AudioEngineState>.fromOpaque(inRefCon).takeUnretainedValue()
    guard let ioData = ioData else { return noErr }
    guard let engine = state.engine else { return noErr }

    let frames = Int(inNumberFrames)
    let abl = UnsafeMutableAudioBufferListPointer(ioData)
    let outChannelCount = abl.count

    let tempL = state.renderTempL
    let tempR = state.renderTempR

    let tempABLSize = MemoryLayout<AudioBufferList>.size + MemoryLayout<AudioBuffer>.size
    let tempRaw = UnsafeMutableRawPointer.allocate(byteCount: tempABLSize, alignment: 8)
    defer { tempRaw.deallocate() }
    let tempABL = tempRaw.assumingMemoryBound(to: AudioBufferList.self)
    let tempABLPtr = UnsafeMutableAudioBufferListPointer(tempABL)
    tempABLPtr.count = 2
    tempABLPtr[0] = AudioBuffer(mNumberChannels: 1, mDataByteSize: UInt32(frames * 4), mData: tempL)
    tempABLPtr[1] = AudioBuffer(mNumberChannels: 1, mDataByteSize: UInt32(frames * 4), mData: tempR)

    var renderError: OSStatus = noErr
    let status = engine.manualRenderingBlock(inNumberFrames,
                                             tempABLPtr.unsafeMutablePointer,
                                             &renderError)

    // 引擎渲染结果已包含：麦克风主链 + Send + AUX（AUX 由 auxSourceNode 混入，
    // 再经过插入效果器与总推子）。这里把它同时送到 主输出 / 第二路输出 / 录音。

    // 第二路输出：把最终混音推入监听环缓冲（预分配缓冲，实时线程零分配）
    if status == .success, state.monitorAU != nil {
        let scratch = state.monitorScratch
        for i in 0..<frames {
            scratch[i * 2] = tempL[i]
            scratch[i * 2 + 1] = tempR[i]
        }
        state.monitorRing.append(scratch, count: frames * 2)
    }

    for buffer in abl {
        if let data = buffer.mData {
            memset(data, 0, Int(buffer.mDataByteSize))
        }
    }

    if status == .success && !state.outputMuted {
        let startCh = Int(state.outputChannelOption?.startChannel ?? 0)
        if startCh + 1 < outChannelCount {
            if let dataL = abl[startCh].mData?.assumingMemoryBound(to: Float.self),
               let dataR = abl[startCh + 1].mData?.assumingMemoryBound(to: Float.self) {
                for i in 0..<frames {
                    dataL[i] = tempL[i]
                    dataR[i] = tempR[i]
                }
            }
        }
    }

    // 录音不受静音影响
    if status == .success {
        if let recorder = AudioDeviceManager.recorderRef {
            var interleaved = [Float](repeating: 0, count: frames * 2)
            for i in 0..<frames {
                interleaved[i * 2] = tempL[i]
                interleaved[i * 2 + 1] = tempR[i]
            }
            interleaved.withUnsafeBufferPointer { ptr in
                if let base = ptr.baseAddress {
                    recorder.appendFromAudioThread(base, count: interleaved.count)
                }
            }
        }
    }

    var sum: Float = 0
    for buffer in abl {
        if let data = buffer.mData?.assumingMemoryBound(to: Float.self) {
            for i in 0..<frames { sum += data[i] * data[i] }
        }
    }
    state.outputRMS = sqrt(sum / Float(max(frames * outChannelCount, 1)))

    return noErr
}
