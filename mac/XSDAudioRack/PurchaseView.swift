import SwiftUI
import CoreImage.CIFilterBuiltins

// MARK: - 支付宝扫码购买（发码服务器自动激活，¥9.9 终身）

struct PurchaseView: View {
    let machineCode: String
    let onActivated: () -> Void

    @Environment(\.dismiss) private var dismiss

    @State private var order: LicenseManager.PurchaseOrder?
    @State private var qrImage: NSImage?
    @State private var statusText = "正在创建订单…"
    @State private var errorMessage: String?
    @State private var pollTimer: Timer?
    @State private var isPolling = false

    var body: some View {
        VStack(spacing: 12) {
            HStack {
                Text("支付宝扫码 · 终身使用")
                    .font(.title3).fontWeight(.bold)
                Spacer()
            }
            .padding(.horizontal, 24)
            .padding(.top, 22)

            Group {
                if let img = qrImage {
                    Image(nsImage: img)
                        .interpolation(.none)
                        .resizable()
                        .frame(width: 176, height: 176)
                        .padding(6)
                        .background(Color.white)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                } else {
                    RoundedRectangle(cornerRadius: 8)
                        .fill(Color(nsColor: .controlBackgroundColor))
                        .frame(width: 176, height: 176)
                        .overlay(ProgressView().controlSize(.large))
                }
            }

            if order != nil {
                Text("¥9.9 元 · 一台电脑永久授权")
                    .font(.callout).fontWeight(.semibold)
            }

            Text(statusText)
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            if let err = errorMessage {
                Text(err)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 24)

                Button("重新生成订单") { createOrder() }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
            }

            if order?.mock == true {
                Button("（测试）模拟支付成功") { mockPay() }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
                    .help("商户号配置好并关闭模拟模式后，此按钮自动消失")
            }

            Text("支付遇到问题？微信联系 新声代音乐工作室（277878676）")
                .font(.caption2)
                .foregroundStyle(.tertiary)

            if let url = URL(string: LicenseManager.officialSite) {
                Link("官方网站：xsdmusic.cn", destination: url)
                    .font(.caption2)
            }

            Spacer(minLength: 6)

            Button("取消") { dismiss() }
                .buttonStyle(.bordered)
                .controlSize(.regular)
                .padding(.bottom, 18)
        }
        .frame(width: 380, height: 520)
        .onAppear { createOrder() }
        .onDisappear { pollTimer?.invalidate() }
    }

    // MARK: - 逻辑

    @MainActor
    private func createOrder() {
        errorMessage = nil
        pollTimer?.invalidate()
        qrImage = nil
        order = nil
        statusText = "正在创建订单…"

        Task {
            do {
                let o = try await LicenseManager.createPurchaseOrder(machineCode: machineCode)
                order = o
                if o.qrCode.isEmpty {
                    statusText = "等待支付结果…"
                } else {
                    statusText = "请用支付宝扫码支付，付款成功后自动激活"
                    qrImage = Self.makeQR(from: o.qrCode)
                }
                startPolling()
            } catch {
                statusText = "创建订单失败"
                errorMessage = error.localizedDescription
            }
        }
    }

    @MainActor
    private func startPolling() {
        pollTimer?.invalidate()
        let timer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { _ in
            Task { await pollOnce() }
        }
        pollTimer = timer
        Task { await pollOnce() }
    }

    @MainActor
    private func pollOnce() async {
        guard let o = order, !isPolling else { return }
        isPolling = true
        defer { isPolling = false }
        do {
            let st = try await LicenseManager.queryPurchaseOrder(orderId: o.orderId, machineCode: machineCode)
            if st.status == "expired" {
                pollTimer?.invalidate()
                statusText = "二维码已过期，正在重新获取…"
                errorMessage = nil
                createOrder()
                return
            }
            guard st.status == "paid", let code = st.activationCode else { return }
            pollTimer?.invalidate()
            statusText = "支付成功，正在激活…"
            try activate(code: code)
        } catch {
            // 轮询网络抖动静默重试；激活失败（验签不过）才提示
            if let e = error as? NSError, e.domain == "Purchase" {
                pollTimer?.invalidate()
                statusText = "激活失败"
                errorMessage = e.localizedDescription
            }
        }
    }

    @MainActor
    private func activate(code: String) throws {
        let result = LicenseManager.validateActivationCode(code)
        guard result.error == .none else {
            throw LicenseManager.purchaseError("服务器返回的激活码无效，请联系 新声代音乐工作室（277878676）")
        }
        LicenseManager.saveLocalLicense(code)
        onActivated()
        dismiss()
    }

    @MainActor
    private func mockPay() {
        guard let o = order else { return }
        Task {
            try? await LicenseManager.mockPayOrder(orderId: o.orderId)
            await pollOnce()
        }
    }

    // MARK: - 二维码

    static func makeQR(from string: String) -> NSImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(string.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }
        let scaled = output.transformed(by: CGAffineTransform(scaleX: 8, y: 8))
        guard let cg = CIContext().createCGImage(scaled, from: scaled.extent) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: 176, height: 176))
    }
}
