import SwiftUI
import UniformTypeIdentifiers
import KeyboardShortcuts

struct ContentView: View {
    @StateObject private var rack = AudioDeviceManager()
    @State private var showPluginPicker = false

    // Send A / B 的插件选择弹窗
    @State private var showSendAPicker = false
    @State private var showSendBPicker = false

    // 插入效果器的插件选择弹窗
    @State private var showInsertPicker = false

    @AppStorage("showLeftPanel") private var showLeftPanel = true
    @AppStorage("showRightPanel") private var showRightPanel = true

    private let leftWidth: CGFloat = 280
    private let centerWidth: CGFloat = 450
    private let rightWidth: CGFloat = 60
    private let contentHeight: CGFloat = 580
    private let toolbarHeight: CGFloat = 60
    private var totalHeight: CGFloat { contentHeight + toolbarHeight }
    private var totalWidth: CGFloat {
        var w = centerWidth
        if showRightPanel { w += rightWidth + 1 }
        if showLeftPanel { w += leftWidth + 1 }
        return w
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar.frame(height: toolbarHeight)
            Divider()

            HStack(spacing: 0) {
                // 第 1 栏：插件链
                pluginPanel
                    .frame(width: centerWidth, height: contentHeight)
                    .background(Color(nsColor: .textBackgroundColor))

                // 第 2 栏：电平 + 主音量
                if showRightPanel {
                    Divider()
                    rightPanel
                        .frame(width: rightWidth, height: contentHeight)
                        .background(Color(nsColor: .windowBackgroundColor))
                }

                // 第 3 栏：设备 + 二维码
                if showLeftPanel {
                    Divider()
                    devicePanel
                        .frame(width: leftWidth, height: contentHeight)
                        .background(Color(nsColor: .windowBackgroundColor))
                }
            }
            .frame(height: contentHeight)
        }
        .frame(width: totalWidth, height: totalHeight)
        .task {
            rack.refreshAvailableEffects()
            await rack.restoreAutoSaveIfExists()
        }
        .sheet(isPresented: $rack.licenseNeedsAttention) {
            LicenseActivationView(message: rack.licenseMessage) {
                rack.checkLicenseNow()
            }
            .interactiveDismissDisabled(true)
        }
        .sheet(isPresented: $showPluginPicker) {
            PluginPickerView(
                effects: rack.availableEffects,
                onSelect: { info in
                    rack.addPlugin(info)
                    showPluginPicker = false
                },
                onCancel: { showPluginPicker = false }
            )
        }
        .sheet(isPresented: $showSendAPicker) {
            PluginPickerView(
                effects: rack.availableEffects,
                onSelect: { info in
                    rack.setSendPluginA(info)
                    showSendAPicker = false
                },
                onCancel: { showSendAPicker = false }
            )
        }
        .sheet(isPresented: $showSendBPicker) {
            PluginPickerView(
                effects: rack.availableEffects,
                onSelect: { info in
                    rack.setSendPluginB(info)
                    showSendBPicker = false
                },
                onCancel: { showSendBPicker = false }
            )
        }
        .sheet(isPresented: $showInsertPicker) {
            PluginPickerView(
                effects: rack.availableEffects,
                onSelect: { info in
                    rack.setInsertPlugin(info)
                    showInsertPicker = false
                },
                onCancel: { showInsertPicker = false }
            )
        }
    }

    // MARK: - 顶部工具栏

    private var toolbar: some View {
        HStack(spacing: 12) {
            Text("XSD Audio Rack")
                .font(.title3).fontWeight(.semibold)

            Spacer()

            Button {
                if rack.isRunning { rack.stop() }
                else { try? rack.start() }
            } label: {
                Image(systemName: rack.isRunning ? "stop.fill" : "play.fill")
                    .font(.system(size: 14))
                    .frame(width: 20, height: 20)
            }
            .buttonStyle(.borderedProminent)
            .tint(rack.isRunning ? .red : .accentColor)
            .controlSize(.large)
            .keyboardShortcut(.space, modifiers: [])

            Button { rack.refreshDevices() } label: {
                Image(systemName: "arrow.clockwise")
                    .font(.system(size: 14))
                    .frame(width: 20, height: 20)
            }
            .buttonStyle(.bordered)
            .controlSize(.large)

            HStack(spacing: 6) {
                Circle()
                    .fill(rack.isRunning ? .green : .gray)
                    .frame(width: 8, height: 8)
                Text(rack.isRunning ? "Running" : "Stopped")
                    .font(.caption).foregroundStyle(.secondary)
            }

            Button {
                showLeftPanel.toggle()
            } label: {
                Image(systemName: "sidebar.right").font(.system(size: 15))
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 20)
    }

    // MARK: - 中栏：插件链 + Send

    private var pluginPanel: some View {
        VStack(alignment: .leading, spacing: 0) {

            // ───── 标题栏 ─────
            HStack {
                Text("PLUGIN CHAIN")
                    .font(.caption).fontWeight(.semibold).foregroundStyle(.secondary)

                Spacer()

                Button {
                    rack.refreshAvailableEffects()
                    showPluginPicker = true
                } label: {
                    Label("Add Plugin", systemImage: "plus")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)

                Spacer()

                Text("\(rack.pluginChain.count) 个插件")
                    .font(.caption).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 20).padding(.top, 20).padding(.bottom, 12)

            // ───── 主插件链列表 ─────
            ScrollView {
                VStack(spacing: 6) {
                    if rack.pluginChain.isEmpty {
                        Text("还没有插件").font(.callout).foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity).padding(.vertical, 40)
                    }
                    ForEach(Array(rack.pluginChain.enumerated()), id: \.element.id) { index, plugin in
                        HStack(spacing: 10) {
                            Text("\(index + 1)")
                                .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                                .frame(width: 20)

                            VStack(spacing: 1) {
                                Button {
                                    rack.movePluginUp(id: plugin.id)
                                } label: { Image(systemName: "chevron.up").font(.system(size: 9)) }
                                .buttonStyle(.borderless)
                                .disabled(rack.pluginChain.first?.id == plugin.id)

                                Button {
                                    rack.movePluginDown(id: plugin.id)
                                } label: { Image(systemName: "chevron.down").font(.system(size: 9)) }
                                .buttonStyle(.borderless)
                                .disabled(rack.pluginChain.last?.id == plugin.id)
                            }

                            Text(plugin.name).fontWeight(.medium).lineLimit(1)
                            Spacer()
                            Button("Open") { rack.openPluginUI(id: plugin.id) }
                                .controlSize(.small)
                            Toggle("", isOn: Binding(
                                get: { !plugin.bypassed },
                                set: { _ in rack.toggleBypass(id: plugin.id) }
                            ))
                            .toggleStyle(.switch).controlSize(.mini).labelsHidden()
                            Button {
                                rack.removePlugin(id: plugin.id)
                            } label: {
                                Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                            }
                            .buttonStyle(.borderless)
                        }
                        .padding(.horizontal, 12).padding(.vertical, 8)
                        .background(RoundedRectangle(cornerRadius: 8)
                            .fill(Color(nsColor: .controlBackgroundColor)))
                    }
                }
                .padding(.horizontal, 20).padding(.bottom, 12)
            }

            Divider()

            // ───── Send 区域 ─────
            VStack(alignment: .leading, spacing: 10) {

                // Send Volume 推子
                SendVolumeRow(
                    volumeDB: $rack.sendMasterVolume,
                    label: "Send Volume"
                )
                .padding(.horizontal, 20)
                .padding(.top, 12)

                // Send A
                SendStrip(
                    pluginName: rack.sendPluginNameA,
                    isEnabled: !rack.sendBypassedA,
                    volumeDB: $rack.sendVolumeA,
                    onSelect: { rack.refreshAvailableEffects(); showSendAPicker = true },
                    onOpen: { rack.openSendPluginUI(id: "A") },
                    onToggle: { rack.toggleSendBypassA() },
                    onClear: { rack.setSendPluginA(nil) }
                )
                .padding(.horizontal, 20)

                // Send B
                SendStrip(
                    pluginName: rack.sendPluginNameB,
                    isEnabled: !rack.sendBypassedB,
                    volumeDB: $rack.sendVolumeB,
                    onSelect: { rack.refreshAvailableEffects(); showSendBPicker = true },
                    onOpen: { rack.openSendPluginUI(id: "B") },
                    onToggle: { rack.toggleSendBypassB() },
                    onClear: { rack.setSendPluginB(nil) }
                )
                .padding(.horizontal, 20)
                .padding(.bottom, 12)
            }
            .background(Color.orange.opacity(0.06))

            Divider()

            // ───── AUX 输入（音乐/音效软件进声）─────
            VStack(alignment: .leading, spacing: 10) {
                Text("AUX 输入（音乐/音效软件进声）")
                    .font(.caption).fontWeight(.semibold).foregroundStyle(.secondary)
                    .padding(.top, 10)

                AuxChannelRow(index: 1,
                              device: $rack.aux1Device,
                              channel: $rack.aux1Channel,
                              gainDB: $rack.aux1GainDB,
                              customName: $rack.aux1Name,
                              level: rack.aux1Level,
                              devices: rack.devices) { rack.autoSave() }

                AuxChannelRow(index: 2,
                              device: $rack.aux2Device,
                              channel: $rack.aux2Channel,
                              gainDB: $rack.aux2GainDB,
                              customName: $rack.aux2Name,
                              level: rack.aux2Level,
                              devices: rack.devices) { rack.autoSave() }
            }
            .padding(.horizontal, 20)
            .padding(.bottom, 12)
            .background(Color.green.opacity(0.05))

            Divider()

            // ───── 第二路输出 + 插入效果器 ─────
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 10) {
                    // 第二路输出通道选择
                    OutputRoutePicker(device: $rack.monitorDevice,
                                      channel: $rack.monitorChannel,
                                      devices: rack.devices) { rack.autoSave() }
                        .frame(width: 180)

                    // 插入效果器（作用于所有声音之后、总推子之前）
                    InsertStrip(pluginName: rack.insertPluginName,
                                isEnabled: !rack.insertBypassed,
                                onSelect: { rack.refreshAvailableEffects(); showInsertPicker = true },
                                onOpen: { rack.openInsertPluginUI() },
                                onToggle: { rack.toggleInsertBypass() },
                                onClear: { rack.setInsertPlugin(nil) })
                }
            }
            .padding(.horizontal, 20)
            .padding(.top, 10)
            .padding(.bottom, 12)
            .background(Color.blue.opacity(0.05))
            .onChange(of: rack.monitorDevice) { _, newDev in
                if let dev = newDev, rack.monitorChannel == nil {
                    rack.monitorChannel = AudioDeviceManager.monitorChannelOptions(for: dev).first
                }
                rack.reloadEngineIfNeeded()
                rack.autoSave()
            }
            .onChange(of: rack.monitorChannel) { _, _ in
                rack.reloadEngineIfNeeded(); rack.autoSave()
            }

            Divider()

            // ───── 底部按钮行 ─────
            HStack(spacing: 8) {
                // 左：录音 + 打开文件夹
                Button {
                    if rack.isRecording { rack.stopRecording() }
                    else { rack.startRecording() }
                } label: {
                    Image(systemName: rack.isRecording ? "stop.fill" : "record.circle")
                        .frame(width: 20, height: 20)
                }
                .buttonStyle(.borderedProminent)
                .tint(rack.isRecording ? .red : .accentColor)
                .controlSize(.large)
                .help(rack.isRecording ? "停止录音" : "开始录音")

                Button {
                    rack.openRecordingsFolder()
                } label: {
                    Image(systemName: "folder")
                        .frame(width: 20, height: 20)
                }
                .buttonStyle(.bordered)
                .controlSize(.large)
                .help("打开录音文件夹")

                // 右：加载 / 保存预设
                Button {
                    loadPreset()
                } label: {
                    Image(systemName: "square.and.arrow.down.on.square")
                        .frame(width: 20, height: 20)
                }
                .buttonStyle(.bordered)
                .controlSize(.large)
                .help("加载预设")

                Button {
                    savePreset()
                } label: {
                    Image(systemName: "square.and.arrow.down")
                        .frame(width: 20, height: 20)
                }
                .buttonStyle(.bordered)
                .controlSize(.large)
                .help("保存预设")

                Spacer()

                // 新声代两行文字
                VStack(alignment: .trailing, spacing: 4) {
                    Text("新声代音频服务")
                        .font(.body)
                        .fontWeight(.semibold)
                        .foregroundStyle(Color(red: 0.90, green: 0.50, blue: 0.10))
                    Text("混音制作、主播调试、音频硬件")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 20)
            .padding(.top, 12)
            .padding(.bottom, 16)
        }
    }

    // MARK: - 右栏

    private var rightPanel: some View {
        VStack(spacing: 12) {
            Spacer(minLength: 0)

            HStack(spacing: 8) {
                VerticalLevelMeter(title: "IN",
                                   value: rack.inputLevel,
                                   peak: rack.inputPeak)
                VerticalLevelMeter(title: "OUT",
                                   value: rack.outputLevel,
                                   peak: rack.outputPeak)
            }
            .frame(height: 180)

            Divider()

            VerticalVolumeSlider(value: $rack.masterGainDB, range: -60...12)
                .onChange(of: rack.masterGainDB) { _, _ in rack.autoSave() }
                .frame(maxHeight: .infinity)

            if rack.inputMuted {
                Button {
                    rack.setInputMuted(false)
                } label: {
                    Image(systemName: "mic.slash.fill")
                        .font(.system(size: 14))
                        .frame(width: 20, height: 20)
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .controlSize(.large)
                .help("取消闭麦")
            } else {
                Button {
                    rack.setInputMuted(true)
                } label: {
                    Image(systemName: "mic.fill")
                        .font(.system(size: 14))
                        .frame(width: 20, height: 20)
                }
                .buttonStyle(.bordered)
                .controlSize(.large)
                .help("闭麦（静音麦克风输入）")
            }
        }
        .padding(.vertical, 20)
        .padding(.horizontal, 6)
    }

    // MARK: - 第三栏

    private var devicePanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                panelSection(title: "INPUT") {
                    VStack(alignment: .leading, spacing: 10) {
                        labeledPicker(label: "Device", selection: $rack.selectedInputDevice) {
                            Text("None").tag(nil as AudioDevice?)
                            ForEach(rack.devices.filter { $0.inputChannelCount > 0 }) { d in
                                Text("\(d.name) (\(d.inputChannelCount)ch)").tag(d as AudioDevice?)
                            }
                        }
                        .onChange(of: rack.selectedInputDevice) { _, newDev in
                            rack.updateInputChannels(for: newDev)
                            rack.updateDeviceFormats(for: newDev)
                            rack.reloadEngineIfNeeded()
                            rack.autoSave()
                        }
                        labeledPicker(label: "Channel", selection: $rack.selectedInputChannel) {
                            Text("None").tag(nil as InputChannelOption?)
                            ForEach(rack.inputChannelOptions) { opt in
                                Text(opt.name).tag(opt as InputChannelOption?)
                            }
                        }
                        .onChange(of: rack.selectedInputChannel) { _, _ in
                            rack.reloadEngineIfNeeded(); rack.autoSave()
                        }
                    }
                }

                panelSection(title: "OUTPUT") {
                    VStack(alignment: .leading, spacing: 10) {
                        labeledPicker(label: "Device", selection: $rack.selectedOutputDevice) {
                            Text("Default").tag(nil as AudioDevice?)
                            ForEach(rack.devices.filter { $0.outputChannelCount >= 2 }) { d in
                                Text("\(d.name) (\(d.outputChannelCount)ch)").tag(d as AudioDevice?)
                            }
                        }
                        .onChange(of: rack.selectedOutputDevice) { _, newDev in
                            rack.updateOutputChannels(for: newDev)
                            rack.reloadEngineIfNeeded()
                            rack.autoSave()
                        }
                        labeledPicker(label: "Channel", selection: $rack.selectedOutputChannel) {
                            Text("Default").tag(nil as OutputChannelOption?)
                            ForEach(rack.outputChannelOptions) { opt in
                                Text(opt.name).tag(opt as OutputChannelOption?)
                            }
                        }
                        .onChange(of: rack.selectedOutputChannel) { _, _ in
                            rack.reloadEngineIfNeeded(); rack.autoSave()
                        }
                    }
                }

                panelSection(title: "FORMAT") {
                    VStack(alignment: .leading, spacing: 10) {
                        labeledPicker(label: "Sample Rate", selection: $rack.selectedSampleRate) {
                            ForEach(rack.availableSampleRates, id: \.self) { r in
                                Text(formatSampleRate(r)).tag(r)
                            }
                        }
                        .onChange(of: rack.selectedSampleRate) { _, _ in
                            rack.reloadEngineIfNeeded(); rack.autoSave()
                        }
                        labeledPicker(label: "Buffer", selection: $rack.selectedBufferSize) {
                            ForEach(rack.availableBufferSizes, id: \.self) { s in
                                Text("\(s)").tag(s)
                            }
                        }
                        .onChange(of: rack.selectedBufferSize) { _, _ in
                            rack.reloadEngineIfNeeded(); rack.autoSave()
                        }
                    }
                }

                panelSection(title: "SHORTCUT") {
                    KeyboardShortcuts.Recorder("闭麦:", name: .toggleOutputMute)
                        .font(.caption)
                }

                AdBannerView(qrImageName: "QRCode")
            }
            .padding(20)
        }
    }

    // MARK: - 辅助

    private func panelSection<Content: View>(title: String,
                                              @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.caption).fontWeight(.semibold).foregroundStyle(.secondary)
            content()
        }
    }

    private func labeledPicker<T: Hashable, Content: View>(
        label: String, selection: Binding<T>, @ViewBuilder content: () -> Content
    ) -> some View {
        HStack(spacing: 8) {
            Text(label)
                .font(.caption2)
                .foregroundStyle(.secondary)
                .frame(width: 70, alignment: .leading)
            Picker("", selection: selection) { content() }
                .labelsHidden()
                .frame(maxWidth: .infinity)
        }
    }

    private func formatSampleRate(_ rate: Double) -> String {
        rate >= 1000 ? String(format: "%.1f kHz", rate / 1000) : String(format: "%.0f Hz", rate)
    }

    private func savePreset() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "XSDRackPreset.json"
        if panel.runModal() == .OK, let url = panel.url {
            let preset = rack.savePreset(named: url.deletingPathExtension().lastPathComponent)
            do {
                let encoder = JSONEncoder()
                encoder.outputFormatting = .prettyPrinted
                let data = try encoder.encode(preset)
                try data.write(to: url)
                print("✅ 预设已保存: \(url.path)")
            } catch {
                print("⚠️ 保存失败: \(error)")
            }
        }
    }

    private func loadPreset() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        if panel.runModal() == .OK, let url = panel.url {
            let didStartAccessing = url.startAccessingSecurityScopedResource()
            defer { if didStartAccessing { url.stopAccessingSecurityScopedResource() } }
            do {
                let data = try Data(contentsOf: url)
                let preset = try JSONDecoder().decode(RackPreset.self, from: data)
                Task {
                    do { try await rack.loadPreset(preset) }
                    catch { print("⚠️ 加载预设失败: \(error)") }
                }
            } catch {
                print("⚠️ 读取预设失败: \(error)")
            }
        }
    }
}

// MARK: - Send 空白横条（点加载 / 显示插件名 + 控制 + 音量旋钮）

struct SendStrip: View {
    let pluginName: String?
    let isEnabled: Bool
    @Binding var volumeDB: Double
    let onSelect: () -> Void
    let onOpen: () -> Void
    let onToggle: () -> Void
    let onClear: () -> Void

    @State private var showVolume = false

    var body: some View {
        HStack(spacing: 10) {
            // ───── 右：音量旋钮 ─────
            SendVolumeKnob(volumeDB: $volumeDB, showValue: $showVolume)
                .frame(width: 40, height: 28)
            // ───── 左：插件名/加载 ─────
            if let name = pluginName {
                HStack(spacing: 6) {
                    Text(name)
                        .fontWeight(.medium)
                        .lineLimit(1)
                    Spacer()
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 6)
                    .fill(Color(nsColor: .controlBackgroundColor)))

                Button("Open", action: onOpen)
                    .controlSize(.small)
            } else {
                Button(action: onSelect) {
                    HStack {
                        Image(systemName: "plus")
                        Text("点击加载插件")
                        Spacer()
                    }
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 6)
                        .fill(Color(nsColor: .controlBackgroundColor).opacity(0.5)))
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .strokeBorder(style: StrokeStyle(lineWidth: 1, dash: [4]))
                            .foregroundStyle(.tertiary)
                    )
                }
                .buttonStyle(.plain)
            }

            // ───── 中：关闭开关 ─────
            Toggle("", isOn: Binding(
                get: { isEnabled },
                set: { _ in onToggle() }
            ))
            .toggleStyle(.switch)
            .controlSize(.mini)
            .labelsHidden()
            .help(isEnabled ? "关闭 Send" : "开启 Send")

            // ───── 删除 ─────
            if pluginName != nil {
                Button(action: onClear) {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.borderless)
            }
        }
    }
}

// MARK: - Send 音量旋钮（旋转 Slider）

struct SendVolumeKnob: View {
    @Binding var volumeDB: Double
    @Binding var showValue: Bool
    @State private var dragStartVolume: Double? = nil

    private let minDB: Double = -60
    private let maxDB: Double = 12

    private var displayText: String {
        String(format: "%.0f", volumeDB)
    }

    var body: some View {
        ZStack {
            Circle()
                .fill(Color(nsColor: .controlBackgroundColor))
            Circle()
                .stroke(Color(nsColor: .separatorColor), lineWidth: 1)

            Text(displayText)
                .font(.system(size: 9, weight: .medium).monospacedDigit())
                .foregroundStyle(Color(red: 0.90, green: 0.50, blue: 0.10))
        }
        .frame(width: 26, height: 26)
        .contentShape(Circle())
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { drag in
                    if dragStartVolume == nil {
                        dragStartVolume = volumeDB
                    }
                    let start = dragStartVolume ?? volumeDB
                    // 每 200pt 走完整个量程（-60 到 +12 共 72 dB）
                    let delta = -drag.translation.height / 400.0 * (maxDB - minDB)
                    let nv = max(minDB, min(maxDB, start + delta))
                    volumeDB = nv
                    showValue = true
                }
                .onEnded { _ in
                    dragStartVolume = nil
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
                        showValue = false
                    }
                }
        )
        .simultaneousGesture(
            TapGesture(count: 2).onEnded {
                volumeDB = -6
                showValue = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
                    showValue = false
                }
            }
        )
    }
}

// MARK: - Send Volume 推子

struct SendVolumeRow: View {
    @Binding var volumeDB: Double
    let label: String

    var body: some View {
        HStack(spacing: 8) {
            Text(label)
                .font(.caption)
                .foregroundStyle(.secondary)

            Slider(value: $volumeDB, in: -60...12, step: 0.1)

            Text(String(format: "%+.1f dB", volumeDB))
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 60, alignment: .trailing)
        }
    }
}

// MARK: - 大号电平表

struct BigLevelMeter: View {
    let title: String
    let value: Double
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(title).font(.caption).foregroundStyle(.secondary)
                Spacer()
                Text(String(format: "%.1f dB", value))
                    .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
            }
            GeometryReader { geo in
                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 4).fill(Color(nsColor: .controlBackgroundColor))
                    RoundedRectangle(cornerRadius: 4)
                        .fill(LinearGradient(colors: [.green, .yellow, .red],
                                             startPoint: .leading, endPoint: .trailing))
                        .frame(width: geo.size.width * CGFloat(max(0, min(1, (value + 60) / 60))))
                }
            }
            .frame(height: 12)
        }
    }
}

// MARK: - 竖电平

struct VerticalLevelMeter: View {
    let title: String
    let value: Double
    let peak: Double

    private func ratio(_ db: Double) -> CGFloat {
        CGFloat(max(0, min(1, (db + 60) / 60)))
    }

    var body: some View {
        VStack(spacing: 6) {
            Text(String(format: "%.1f", peak))
                .font(.system(size: 7).monospacedDigit())
                .foregroundStyle(.secondary)

            GeometryReader { geo in
                ZStack(alignment: .bottom) {
                    RoundedRectangle(cornerRadius: 6)
                        .fill(Color(nsColor: .controlBackgroundColor))

                    // 实时电平
                    RoundedRectangle(cornerRadius: 6)
                        .fill(LinearGradient(
                            colors: [.green, .green, .yellow, .red],
                            startPoint: .bottom,
                            endPoint: .top
                        ))
                        .frame(height: geo.size.height * ratio(value))

                    // 峰值线
                    Rectangle()
                        .fill(Color.primary)
                        .frame(height: 2)
                        .offset(y: -geo.size.height * ratio(peak))
                        .frame(maxHeight: .infinity, alignment: .bottom)
                }
                .overlay(
                    RoundedRectangle(cornerRadius: 6)
                        .stroke(Color(nsColor: .separatorColor), lineWidth: 0.5)
                )
            }
            .frame(width: 18)

            Text(title)
                .font(.system(size: 9))
                .foregroundStyle(.secondary)
        }
    }
}

// MARK: - 竖音量推子

struct VerticalVolumeSlider: View {
    @Binding var value: Double
    let range: ClosedRange<Double>

    private let visualBottom: Double = -60
    private let visualSplit: Double = -30
    private let visualTop: Double = 12
    private let splitRatio: CGFloat = 0.3
    private let scrollStepDB: Double = 1.0

    private func ratio(for db: Double) -> CGFloat {
        if db <= visualSplit {
            let t = (db - visualBottom) / (visualSplit - visualBottom)
            return CGFloat(max(0, min(1, t))) * splitRatio
        } else {
            let t = (db - visualSplit) / (visualTop - visualSplit)
            return splitRatio + CGFloat(max(0, min(1, t))) * (1 - splitRatio)
        }
    }

    private func db(for ratio: CGFloat) -> Double {
        if ratio <= splitRatio {
            let t = ratio / splitRatio
            return visualBottom + Double(t) * (visualSplit - visualBottom)
        } else {
            let t = (ratio - splitRatio) / (1 - splitRatio)
            return visualSplit + Double(t) * (visualTop - visualSplit)
        }
    }

    private var tickValues: [Double] {
        stride(from: 6.0, through: -60.0, by: -3.0).map { $0 }
    }

    var body: some View {
        VStack(spacing: 6) {
            Text(String(format: "%+.1f", value))
                .font(.system(size: 10).monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(height: 16)

            GeometryReader { geo in
                ZStack(alignment: .bottom) {
                    ForEach(tickValues, id: \.self) { db in
                        let isZero = abs(db) < 0.01
                        let isMajor = db.truncatingRemainder(dividingBy: 15) == 0
                        let y = geo.size.height * (1 - ratio(for: db))
                        HStack(spacing: 0) {
                            Rectangle()
                                .fill(isZero ? Color.primary : Color(nsColor: .separatorColor))
                                .frame(width: isZero ? 14 : (isMajor ? 8 : 5),
                                       height: isZero ? 1.5 : 1)
                            Spacer(minLength: 0).frame(width: 8)
                            Rectangle()
                                .fill(isZero ? Color.primary : Color(nsColor: .separatorColor))
                                .frame(width: isZero ? 14 : (isMajor ? 8 : 5),
                                       height: isZero ? 1.5 : 1)
                        }
                        .position(x: geo.size.width / 2, y: y)
                    }

                    RoundedRectangle(cornerRadius: 3)
                        .fill(Color(nsColor: .controlBackgroundColor))
                        .frame(width: 6).frame(maxHeight: .infinity)
                        .overlay(
                            RoundedRectangle(cornerRadius: 3)
                                .stroke(Color(nsColor: .separatorColor), lineWidth: 0.5)
                                .frame(width: 6)
                        )

                    let r = ratio(for: value)
                    RoundedRectangle(cornerRadius: 3)
                        .fill(Color.accentColor)
                        .frame(width: 6, height: geo.size.height * r)

                    RoundedRectangle(cornerRadius: 4)
                        .fill(Color.white)
                        .frame(width: 18, height: 12)
                        .overlay(
                            RoundedRectangle(cornerRadius: 4)
                                .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                        )
                        .shadow(radius: 1.5)
                        .position(x: geo.size.width / 2, y: geo.size.height * (1 - r))

                    ScrollWheelCatcher { deltaY in
                        let step = deltaY > 0 ? scrollStepDB : -scrollStepDB
                        let nv = max(range.lowerBound, min(range.upperBound, value + step))
                        value = nv
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .contentShape(Rectangle())
                .simultaneousGesture(
                    DragGesture(minimumDistance: 0)
                        .onChanged { drag in
                            let y = drag.location.y
                            let cr = 1 - (y / geo.size.height)
                            let clamped = max(0, min(1, cr))
                            value = db(for: clamped)
                        }
                )
                .simultaneousGesture(
                    TapGesture(count: 2).onEnded { value = 0.0 }
                )
            }
        }
        .frame(width: 40)
    }
}

// MARK: - 滚轮捕获

struct ScrollWheelCatcher: NSViewRepresentable {
    let onScroll: (CGFloat) -> Void

    func makeNSView(context: Context) -> NSView {
        let v = ScrollView()
        v.onScroll = onScroll
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {
        if let v = nsView as? ScrollView { v.onScroll = onScroll }
    }

    private class ScrollView: NSView {
        var onScroll: ((CGFloat) -> Void)?
        override func hitTest(_ point: NSPoint) -> NSView? {
            if let e = NSApp.currentEvent, e.type == .scrollWheel { return self }
            return nil
        }
        override func scrollWheel(with event: NSEvent) {
            onScroll?(event.scrollingDeltaY)
        }
        override var acceptsFirstResponder: Bool { true }
    }
}

// MARK: - 二维码

struct AdBannerView: View {
    let qrImageName: String?
    var body: some View {
        VStack(spacing: 8) {
            if let n = qrImageName, let img = NSImage(named: n) {
                Image(nsImage: img)
                    .resizable().interpolation(.none)
                    .aspectRatio(contentMode: .fit)
                    .frame(width: 120, height: 120)
                    .padding(6)
                    .background(Color.white)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
            } else {
                RoundedRectangle(cornerRadius: 8)
                    .fill(Color(nsColor: .controlBackgroundColor))
                    .frame(width: 120, height: 120)
                    .overlay(Image(systemName: "qrcode")
                        .font(.system(size: 40)).foregroundStyle(.tertiary))
            }
            Text("扫一扫加微信")
                .font(.caption2).foregroundStyle(.tertiary)
        }
        .frame(maxWidth: .infinity)
    }
}

// MARK: - 插件选择器

struct PluginPickerView: View {
    let effects: [AUEffectInfo]
    let onSelect: (AUEffectInfo) -> Void
    let onCancel: () -> Void
    @State private var keyword = ""

    var filtered: [AUEffectInfo] {
        keyword.isEmpty ? effects : effects.filter { $0.name.localizedCaseInsensitiveContains(keyword) }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack { Text("选择 AU 插件").font(.headline); Spacer() }.padding(16)
            Divider()
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("搜索…", text: $keyword).textFieldStyle(.plain)
            }
            .padding(10).background(Color(nsColor: .controlBackgroundColor))
            .cornerRadius(8).padding(.horizontal, 16).padding(.vertical, 12)

            ScrollView {
                LazyVStack(spacing: 2) {
                    ForEach(filtered) { info in
                        Button { onSelect(info) } label: {
                            HStack {
                                Text(info.name).lineLimit(1)
                                if info.isIntelOnly {
                                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                                }
                                Spacer()
                            }
                            .contentShape(Rectangle()).padding(.horizontal, 10).padding(.vertical, 8)
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(.horizontal, 8)
            }

            Divider()
            HStack {
                Text("\(filtered.count) 个插件").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Cancel", action: onCancel).keyboardShortcut(.escape)
            }
            .padding(16)
        }
        .frame(width: 520, height: 600)
    }
}


// MARK: - AUX 输入通道行

struct AuxChannelRow: View {
    let index: Int
    @Binding var device: AudioDevice?
    @Binding var channel: InputChannelOption?
    @Binding var gainDB: Double
    @Binding var customName: String
    let level: Double
    let devices: [AudioDevice]
    var onCommit: () -> Void

    @State private var isEditingName = false
    @State private var nameDraft = ""

    private let rowHeight: CGFloat = 30

    private var displayName: String {
        customName.isEmpty ? "AUX\(index)" : customName
    }

    var body: some View {
        HStack(spacing: 10) {
            // ───── 可改名胶囊标签（白底 + 灰实线边）─────
            Group {
                if isEditingName {
                    TextField("名称", text: $nameDraft)
                        .textFieldStyle(.roundedBorder)
                        .font(.caption)
                        .frame(width: 56, height: rowHeight)
                        .onSubmit {
                            customName = String(nameDraft.prefix(6))
                            isEditingName = false
                            onCommit()
                        }
                } else {
                    Text(displayName)
                        .font(.caption)
                        .fontWeight(.semibold)
                        .lineLimit(1)
                        .padding(.horizontal, 8)
                        .frame(minWidth: 44)
                        .frame(height: rowHeight)
                        .background(
                            RoundedRectangle(cornerRadius: 5)
                                .fill(Color(nsColor: .textBackgroundColor))
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 5)
                                .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                        )
                        .contentShape(Rectangle())
                        .onTapGesture {
                            nameDraft = customName.isEmpty ? "AUX\(index)" : customName
                            isEditingName = true
                        }
                        .help("点击改名（如：伴奏 / 音效）")
                }
            }

            // ───── 选择输入通道（白底 + 灰虚线框）─────
            Menu {
                Button("关闭（不使用）") {
                    device = nil
                    channel = nil
                    onCommit()
                }
                ForEach(devices.filter { $0.inputChannelCount > 0 }) { dev in
                    Section(dev.name) {
                        ForEach(AudioDeviceManager.auxChannelOptions(for: dev)) { opt in
                            Button(opt.name) {
                                device = dev
                                channel = opt
                                onCommit()
                            }
                        }
                    }
                }
            } label: {
                Group {
                    if let dev = device, let ch = channel {
                        // 已选中：白底 + 灰虚线
                        Text("\(dev.name) · \(ch.name)")
                            .lineLimit(1)
                            .fontWeight(.medium)
                            .padding(.horizontal, 10)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .frame(height: rowHeight)
                            .background(
                                RoundedRectangle(cornerRadius: 6)
                                    .fill(Color(nsColor: .textBackgroundColor))
                            )
                            .overlay(
                                RoundedRectangle(cornerRadius: 6)
                                    .strokeBorder(
                                        Color(nsColor: .separatorColor),
                                        style: StrokeStyle(lineWidth: 1, dash: [4])
                                    )
                            )
                    } else {
                        // 未选中：白底 + 灰虚线（和上面完全一致）
                        HStack {
                            Image(systemName: "plus")
                            Text("点击选择通道")
                            Spacer()
                        }
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 10)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .frame(height: rowHeight)
                        .background(
                            RoundedRectangle(cornerRadius: 6)
                                .fill(Color(nsColor: .textBackgroundColor))
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 6)
                                .strokeBorder(
                                    Color(nsColor: .separatorColor),
                                    style: StrokeStyle(lineWidth: 1, dash: [4])
                                )
                        )
                    }
                }
                .font(.caption)
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize(horizontal: false, vertical: true)
            .frame(width: 180)
            .frame(height: rowHeight)

            // ───── 电平推子（保持不变，只统一高度）─────
            MeteredFader(volumeDB: $gainDB, levelDB: level)
                .frame(maxWidth: .infinity)
                .frame(height: rowHeight)
                .onChange(of: gainDB) { _, _ in onCommit() }
        }
    }
}

// MARK: - 第二路输出通道选择行（样式与 AUX 通道选择一致）

struct OutputRoutePicker: View {
    @Binding var device: AudioDevice?
    @Binding var channel: OutputChannelOption?
    let devices: [AudioDevice]
    var onCommit: () -> Void

    private let rowHeight: CGFloat = 30

    var body: some View {
        Menu {
            Button("关闭（不使用）") {
                device = nil
                channel = nil
                onCommit()
            }
            ForEach(devices.filter { $0.outputChannelCount >= 2 }) { dev in
                Section(dev.name) {
                    ForEach(AudioDeviceManager.monitorChannelOptions(for: dev)) { opt in
                        Button(opt.name) {
                            device = dev
                            channel = opt
                            onCommit()
                        }
                    }
                }
            }
        } label: {
            Group {
                if let dev = device, let ch = channel {
                    Text("第二路 · \(dev.name) · \(ch.name)")
                        .lineLimit(1)
                        .fontWeight(.medium)
                        .padding(.horizontal, 10)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .frame(height: rowHeight)
                        .background(
                            RoundedRectangle(cornerRadius: 6)
                                .fill(Color(nsColor: .textBackgroundColor))
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: 6)
                                .strokeBorder(
                                    Color(nsColor: .separatorColor),
                                    style: StrokeStyle(lineWidth: 1, dash: [4])
                                )
                        )
                } else {
                    HStack {
                        Image(systemName: "speaker.wave.2")
                        Text("第二路输出")
                        Spacer()
                    }
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 10)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .frame(height: rowHeight)
                    .background(
                        RoundedRectangle(cornerRadius: 6)
                            .fill(Color(nsColor: .textBackgroundColor))
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .strokeBorder(
                                Color(nsColor: .separatorColor),
                                style: StrokeStyle(lineWidth: 1, dash: [4])
                            )
                    )
                }
            }
            .font(.caption)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize(horizontal: false, vertical: true)
        .frame(height: rowHeight)
        .help("选择第二路输出的声卡通道（整路混音同时送到这里）")
    }
}

// MARK: - 插入效果器横条（所有声音之后、总推子之前）

struct InsertStrip: View {
    let pluginName: String?
    let isEnabled: Bool
    let onSelect: () -> Void
    let onOpen: () -> Void
    let onToggle: () -> Void
    let onClear: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            Text("插入")
                .font(.caption)
                .fontWeight(.semibold)
                .foregroundStyle(.secondary)

            if let name = pluginName {
                Text(name)
                    .fontWeight(.medium)
                    .lineLimit(1)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(
                        RoundedRectangle(cornerRadius: 6)
                            .fill(Color(nsColor: .textBackgroundColor))
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .strokeBorder(
                                Color(nsColor: .separatorColor),
                                style: StrokeStyle(lineWidth: 1, dash: [4])
                            )
                    )

                Button("Open", action: onOpen)
                    .controlSize(.small)

                Toggle("", isOn: Binding(
                    get: { isEnabled },
                    set: { _ in onToggle() }
                ))
                .toggleStyle(.switch)
                .controlSize(.mini)
                .labelsHidden()
                .help(isEnabled ? "旁通插入效果器" : "启用插入效果器")

                Button(action: onClear) {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.borderless)
            } else {
                Button(action: onSelect) {
                    HStack {
                        Image(systemName: "plus")
                        Text("点击加载效果器")
                        Spacer()
                    }
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(
                        RoundedRectangle(cornerRadius: 6)
                            .fill(Color(nsColor: .textBackgroundColor).opacity(0.5))
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .strokeBorder(
                                Color(nsColor: .separatorColor),
                                style: StrokeStyle(lineWidth: 1, dash: [4])
                            )
                    )
                }
                .buttonStyle(.plain)
            }
        }
    }
}
// MARK: - 电平推子（横版：白底虚线框 + 实时电平 + 刻度 + 推拉键 + 数值）

struct MeteredFader: View {
    @Binding var volumeDB: Double
    let levelDB: Double

    private let minDB: Double = -60
    private let maxDB: Double = 12
    private let scrollStepDB: Double = 0.5      // 滚轮每格 0.5dB
    private let knobW: CGFloat = 12
    private let dragSensitivity: Double = 0.3   // ← 越小越不敏感（0.3 = 全宽约 22dB 行程）

    @State private var dragStartValue: Double? = nil

    private func ratio(_ db: Double) -> CGFloat {
        CGFloat(max(0, min(1, (db - minDB) / (maxDB - minDB))))
    }

    private func db(for r: CGFloat) -> Double {
        minDB + Double(max(0, min(1, r))) * (maxDB - minDB)
    }

    /// 量化到 0.5 dB，避免抖
    private func quantize(_ v: Double) -> Double {
        (v * 2).rounded() / 2
    }

    private var tickValues: [Double] { [12, 0, -15, -30, -45, -60] }

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width
            let h = geo.size.height

            ZStack(alignment: .leading) {
                // ───── 轨道：白底 + 灰色虚线边框 ─────
                RoundedRectangle(cornerRadius: 6)
                    .fill(Color(nsColor: .textBackgroundColor))
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .strokeBorder(
                                Color(nsColor: .separatorColor),
                                style: StrokeStyle(lineWidth: 1, dash: [4])
                            )
                    )

                // ───── 实时电平填充 ─────
                RoundedRectangle(cornerRadius: 3)
                    .fill(LinearGradient(
                        colors: [.green, .green, .yellow, .red],
                        startPoint: .leading,
                        endPoint: .trailing
                    ))
                    .frame(width: max(0, (w - 4) * ratio(levelDB)))
                    .padding(.vertical, 4)
                    .padding(.leading, 2)

                // ───── 刻度（0dB 强调）─────
                ForEach(tickValues, id: \.self) { tick in
                    let isZero = abs(tick) < 0.01
                    Rectangle()
                        .fill(isZero ? Color.primary : Color(nsColor: .separatorColor))
                        .frame(width: isZero ? 1.5 : 1,
                               height: isZero ? 8 : 4)
                        .offset(x: w * ratio(tick) - (isZero ? 0.75 : 0.5))
                }

                // ───── 白色推拉键 ─────
                RoundedRectangle(cornerRadius: 4)
                    .fill(Color.white)
                    .frame(width: knobW, height: h - 6)
                    .overlay(
                        RoundedRectangle(cornerRadius: 4)
                            .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                    )
                    .shadow(radius: 1.5)
                    .offset(x: knobOffset(width: w))
                    .frame(maxHeight: .infinity, alignment: .center)

                // ───── 数值显示（跟随键帽，居中在键帽上方）─────
                Text(String(format: "%+.1f", volumeDB))
                    .font(.system(size: 8, weight: .medium).monospacedDigit())
                    .foregroundStyle(.secondary)
                    .padding(.horizontal, 3)
                    .background(
                        RoundedRectangle(cornerRadius: 2)
                            .fill(Color(nsColor: .textBackgroundColor).opacity(0.9))
                    )
                    .fixedSize()
                    .offset(x: knobOffset(width: w) + knobW / 2 - 18)
                    .frame(maxHeight: .infinity, alignment: .top)
                    .offset(y: 1)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { drag in
                        if dragStartValue == nil {
                            dragStartValue = volumeDB
                        }
                        // 用「相对位移 × 灵敏度」计算，越拖越不敏感
                        let start = dragStartValue ?? volumeDB
                        let delta = Double(drag.translation.width) * dragSensitivity
                        let nv = max(minDB, min(maxDB, quantize(start + delta)))
                        volumeDB = nv
                    }
                    .onEnded { _ in
                        dragStartValue = nil
                    }
            )
            .simultaneousGesture(
                TapGesture(count: 2).onEnded { volumeDB = 0.0 }
            )
            .overlay(
                ScrollWheelCatcher { deltaY in
                    let step = deltaY > 0 ? scrollStepDB : -scrollStepDB
                    volumeDB = max(minDB, min(maxDB, quantize(volumeDB + step)))
                }
            )
        }
    }

    /// 键帽 x 偏移
    private func knobOffset(width w: CGFloat) -> CGFloat {
        let usable = max(1, w - knobW)
        let x = usable * ratio(volumeDB)
        return max(2, min(w - knobW - 2, x))
    }
}
// MARK: - 激活窗口

struct LicenseActivationView: View {
    let message: String
    let onActivated: () -> Void

    @State private var machineCode = ""
    @State private var activationInput = ""
    @State private var resultText = ""
    @State private var resultOK = false
    @State private var trialStarted = false
    @State private var trialActive = false
    @State private var trialDaysLeft = 0
    @State private var showPurchase = false

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 6) {
                Text("软件激活")
                    .font(.title2).fontWeight(.bold)
                Text(message)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                Text("开发商：新声代音乐工作室（微信 277878676）")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }
            .padding(.top, 24)
            .padding(.horizontal, 30)

            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("本机机器码")
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Button {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(machineCode, forType: .string)
                        resultText = "机器码已复制，请发给 新声代音乐工作室（微信 277878676）获取激活码"
                        resultOK = false
                    } label: {
                        Label("复制", systemImage: "doc.on.doc")
                            .font(.caption)
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
                }
                Text(machineCode)
                    .font(.system(size: 18, weight: .semibold, design: .monospaced))
                    .foregroundStyle(Color(red: 0.90, green: 0.50, blue: 0.10))
                    .frame(maxWidth: .infinity)
                    .padding(10)
                    .background(RoundedRectangle(cornerRadius: 8)
                        .fill(Color(nsColor: .controlBackgroundColor)))
            }
            .padding(.horizontal, 30)
            .padding(.top, 18)

            VStack(alignment: .leading, spacing: 6) {
                Text("激活码").font(.caption).foregroundStyle(.secondary)
                TextField("粘贴激活码", text: $activationInput)
                    .textFieldStyle(.roundedBorder)
                    .font(.body)
            }
            .padding(.horizontal, 30)
            .padding(.top, 14)

            Button {
                activate()
            } label: {
                Text("激  活")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .padding(.horizontal, 30)
            .padding(.top, 14)

            // ───── 微信扫码购买（自动激活）─────
            Button {
                showPurchase = true
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: "qrcode")
                    Text("微信扫码购买 · 自动激活")
                        .frame(maxWidth: .infinity)
                }
            }
            .buttonStyle(.bordered)
            .controlSize(.large)
            .padding(.horizontal, 30)
            .padding(.top, 10)

            // ───── 免费试用 3 天 ─────
            Button {
                startTrial()
            } label: {
                Text(trialButtonTitle)
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .controlSize(.large)
            .disabled(trialStarted && !trialActive)
            .padding(.horizontal, 30)
            .padding(.top, 10)

            Text(resultText)
                .font(.caption)
                .foregroundStyle(resultOK ? Color.green : Color.secondary)
                .multilineTextAlignment(.center)
                .frame(minHeight: 16)
                .padding(.horizontal, 30)
                .padding(.top, 8)

            Spacer(minLength: 14)

            Button("退出应用程序") {
                NSApp.terminate(nil)
                // 兜底：万一 terminate 被系统拦下，确保程序能退出
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { exit(0) }
            }
            .buttonStyle(.bordered)
            .controlSize(.regular)
            .foregroundStyle(.secondary)
            .padding(.bottom, 20)
        }
        .frame(width: 460, height: 600)
        .sheet(isPresented: $showPurchase) {
            PurchaseView(machineCode: machineCode, onActivated: onActivated)
        }
        .onAppear {
            machineCode = LicenseManager.formatGroups(LicenseManager.computeMachineCode(), 4)
            let trial = LicenseManager.trialStatus()
            trialStarted = trial.started
            trialActive = trial.active
            trialDaysLeft = trial.daysLeft
        }
    }

    private var trialButtonTitle: String {
        if trialActive { return "继续试用（剩余 \(trialDaysLeft) 天）" }
        if trialStarted { return "试用期已结束" }
        return "免费试用 3 天"
    }

    /// 点击试用：未用过 → 开始 3 天试用；试用中 → 继续使用
    private func startTrial() {
        if LicenseManager.trialStatus().active {
            resultText = "试用期有效，继续使用"
            resultOK = true
            onActivated()
            return
        }
        if LicenseManager.startTrial() {
            resultText = "试用已开始，可免费使用 3 天"
            resultOK = true
            trialStarted = true
            trialActive = true
            trialDaysLeft = LicenseManager.trialDays
            onActivated()
        } else {
            trialStarted = true
            trialActive = false
            resultText = "本机试用期已用过，请激活"
            resultOK = false
        }
    }

    private func activate() {
        let code = activationInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !code.isEmpty else {
            resultText = "请先粘贴激活码"
            resultOK = false
            return
        }

        let result = LicenseManager.validateActivationCode(code)
        let df = DateFormatter()
        df.dateFormat = "yyyy-MM-dd"
        switch result.error {
        case .none:
            LicenseManager.saveLocalLicense(code)
            resultText = "激活成功！授权到 \(df.string(from: result.expiry))"
            resultOK = true
            onActivated()
        case .expired:
            resultText = "这个激活码已于 \(df.string(from: result.expiry)) 到期，请联系新声代音乐工作室（微信 277878676）续期"
            resultOK = false
        case .badSignature:
            resultText = "激活码无效（内容被篡改或输错）"
            resultOK = false
        case .malformed:
            resultText = "激活码格式不对，请完整复制"
            resultOK = false
        case .machineMismatch:
            resultText = "这个激活码是别的电脑的，与本机不匹配"
            resultOK = false
        }
    }
}

#Preview {
    ContentView()
}
