# XSD Audio Rack — 直播机架（双平台）

Windows（WPF / .NET 8）+ macOS（SwiftUI 原生）的直播音频机架应用。

```
win/    Windows 版（WPF/.NET 8）
mac/    macOS 版（SwiftUI + Xcode）：XSDAudioRack.xcodeproj + 源码 + entitlements
```

## 构建

- **Windows**：进入 `win/` 运行 `打包单文件exe.bat`（单文件自包含发布），或直接 `dotnet build`。
  ⚠️ 脚本全部用 `%~dp0` 相对路径，目录迁移无需修改；但 `obj/bin` 里的旧缓存若报路径错误，删掉 `obj` 重新 build。
- **macOS**：打开 `mac/XSDAudioRack.xcodeproj`，scheme = XSDAudioRack。entitlements 已带麦克风/音频输入/AU 宿主临时例外。

## 授权机制（两平台同构）

采用机器码绑定的激活码机制（ECDSA P-256 签名验证，公钥内置，私钥不在本仓库）：

- 3 天试用：`%APPDATA%\XSDAudioRack\trial.json`（Win）/ 同名 UserDefaults 或文件（Mac，见各自 LicenseManager）
- 激活存储：Win `%APPDATA%\XSDAudioRack\license.json`；Mac 侧见 `mac/XSDAudioRack/LicenseManager.swift`
- 防破解：时钟回拨检测（.state，回拨超 2 天清授权）、启动/60 秒轮询复验

## 关键代码对照

| 功能 | Windows | macOS |
|---|---|---|
| 授权 | `win/License/LicenseManager.cs` | `mac/XSDAudioRack/LicenseManager.swift` |
| 音频引擎 | `win/Audio/AudioEngine.cs` + `Wdm2VstBridge.cs`（WDM→VST 桥） | `mac/XSDAudioRack/AudioDeviceManager.swift` |
| 主界面 | `win/MainWindow.xaml(.cs)` | `mac/XSDAudioRack/ContentView.swift` |
| 购买/激活 UI | `win/ActivationWindow.xaml` | `mac/XSDAudioRack/PurchaseView.swift` |
| VST3 宿主 | `win/Vst3/`（自研 interop） | （见 AudioDeviceManager） |

## 许可证

本项目基于 [GPL-3.0](LICENSE) 协议开源。
