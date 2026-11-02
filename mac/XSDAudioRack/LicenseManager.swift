import Foundation
import CryptoKit

/// MAC 地址绑定授权（与 Windows 版 XSDLiveSinger 完全兼容：
/// 同一套机器码算法 + 同一对 ECDSA 密钥，算号机通用）。
enum LicenseManager {

    /// ECDSA P-256 公钥（与算号机私钥配对，原始 65 字节非压缩点）—— 历史密钥，手动发的码仍有效
    private static let publicKeyHex =
        "04889afb028fc85e350958dd84350e2893d85c64cbe3c8af53215dbcbef27e2f" +
        "f1a4e7f84b937d3331130e170cde46d3e0dc7a9eccda1a0e08f0b502d921772778"

    /// 机架专用公钥（2026-10-29 起，线上 life 终身订单由服务器用配对私钥自动签发）。
    /// 与直播助手主密钥隔离：机架的码激活不了直播助手，反之亦然。
    private static let rackPublicKeyHex =
        "041d0dcb91ef82ec4249a250a9b7b09795de1736454b8293d6f1513f3792ec50c051" +
        "f9d4f5a137843f106d5a36af7388fa2f540bebe99fa082adabcb955cfcced2"

    /// 信任的公钥列表（按新→旧排序）
    private static var trustedPublicKeyPoints: [Data] {
        [rackPublicKeyHex, publicKeyHex].compactMap { hex -> Data? in
            var data = Data(capacity: hex.count / 2)
            var index = hex.startIndex
            while index < hex.endIndex {
                let next = hex.index(index, offsetBy: 2)
                guard let byte = UInt8(hex[index..<next], radix: 16) else { return nil }
                data.append(byte)
                index = next
            }
            return data
        }
    }

    private static let base32Alphabet = Array("23456789ABCDEFGHJKLMNPQRSTUVWXYZ") // 无 0/1/I/O
    private static let hmacLabel = "XSDLiveSinger.v1"
    private static let expiryEpoch = Date(timeIntervalSince1970: 1704067200) // 2024-01-01 UTC

    enum LicenseError: Equatable {
        case none
        case malformed        // 格式无法识别
        case machineMismatch  // 与这台电脑不匹配
        case badSignature     // 伪造或被改
        case expired          // 已到期
    }

    struct CheckResult {
        var error: LicenseError
        var expiry: Date
        var isValid: Bool { error == .none }
    }

    // MARK: - 机器码

    /// 本机机器码（XXXX-XXXX-XXXX-XXXX）。取所有启用的物理网卡 MAC（与 Windows 端一致）。
    static func computeMachineCode() -> String {
        var macs: [String] = []
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0 else { return "" }
        defer { freeifaddrs(ifaddr) }

        var ptr = ifaddr
        while let current = ptr {
            defer { ptr = ptr?.pointee.ifa_next }
            let ifa = current.pointee
            guard let addr = ifa.ifa_addr else { continue }
            guard addr.pointee.sa_family == UInt8(AF_LINK) else { continue }
            // 只取启用的物理接口（en0/en1…），排除回环和虚拟接口
            let flags = Int32(ifa.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else { continue }

            let name = String(cString: ifa.ifa_name)
            guard name.hasPrefix("en") else { continue }

            let dl = addr.withMemoryRebound(to: sockaddr_dl.self, capacity: 1) { $0.pointee }
            let nlen = Int(dl.sdl_nlen)
            let alen = Int(dl.sdl_alen)
            guard alen == 6, nlen + alen <= Int(dl.sdl_len) else { continue }
            let bytes = withUnsafeBytes(of: dl) { raw -> [UInt8] in
                Array(raw.dropFirst(nlen + 8).prefix(alen)) // 8 字节头部 + sdl_data
            }
            guard bytes.contains(where: { $0 != 0 }) else { continue }
            macs.append(bytes.map { String(format: "%02X", $0) }.joined())
        }

        macs.sort()
        guard !macs.isEmpty else { return "" }

        let data = Data((hmacLabel + "|" + macs.joined(separator: ",")).utf8)
        let hash = SHA256.hash(data: data)
        let encoded = toBase32(Array(hash.prefix(10))) // 10 字节 → 16 字符
        return formatGroups(encoded, 4)
    }

    // MARK: - 激活码校验

    /// 校验激活码。机器码传 nil = 用本机机器码。
    static func validateActivationCode(_ activationCode: String,
                                       machineCode: String? = nil,
                                       now: Date = Date()) -> CheckResult {
        let machine = normalizeCode(machineCode ?? computeMachineCode())
        let encoded = normalizeCode(activationCode)
        guard let blob = fromBase32(encoded), blob.count >= 66 else {
            return CheckResult(error: .malformed, expiry: .distantPast)
        }

        let days = Int(blob[0]) | (Int(blob[1]) << 8)
        let expiry = expiryEpoch.addingTimeInterval(Double(days) * 86400)
        let payload = Data("\(machine)|\(days)".utf8)

        guard let sig = try? P256.Signing.ECDSASignature(rawRepresentation: blob[2...]) else {
            return CheckResult(error: .malformed, expiry: .distantPast)
        }
        // 依次尝试信任的公钥（新机架密钥 → 旧算号机密钥），任一验签通过即有效
        var verified = false
        for point in trustedPublicKeyPoints {
            if let pubKey = try? P256.Signing.PublicKey(x963Representation: point),
               pubKey.isValidSignature(sig, for: payload) {
                verified = true
                break
            }
        }
        guard verified else {
            return CheckResult(error: .badSignature, expiry: expiry)
        }
        if now >= expiry {
            return CheckResult(error: .expired, expiry: expiry)
        }
        return CheckResult(error: .none, expiry: expiry)
    }

    // MARK: - 本机授权存取

    private static var licenseDir: URL {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("XSDAudioRack", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }
    private static var licenseFileURL: URL { licenseDir.appendingPathComponent("license.json") }
    private static var trialFileURL: URL { licenseDir.appendingPathComponent("trial.json") }
    private static var clockStateURL: URL { licenseDir.appendingPathComponent(".state") }

    // MARK: - 三天试用

    /// 试用天数
    static let trialDays = 3

    /// 试用状态。started = 是否曾点过试用（即使已到期）；active = 试用期内；expiry = 试用到期时间。
    static func trialStatus(now: Date = Date()) -> (started: Bool, active: Bool, daysLeft: Int, expiry: Date) {
        guard let data = try? Data(contentsOf: trialFileURL),
              let dict = try? JSONDecoder().decode([String: Double].self, from: data),
              let start = dict["Start"], start > 0 else {
            return (false, false, 0, .distantPast)
        }
        let expiry = Date(timeIntervalSince1970: start).addingTimeInterval(Double(trialDays) * 86400)
        if now >= expiry { return (true, false, 0, expiry) }
        let daysLeft = max(1, Int(ceil(expiry.timeIntervalSince(now) / 86400)))
        return (true, true, daysLeft, expiry)
    }

    /// 开始试用（每台机器仅一次）。成功返回 true；已用过或写入失败返回 false。
    @discardableResult
    static func startTrial(now: Date = Date()) -> Bool {
        let status = trialStatus(now: now)
        guard !status.started else { return false }
        let dict = ["Start": now.timeIntervalSince1970]
        // 写入失败必须返回 false，否则本次能用、下次启动试用却"没开始过"
        guard let data = try? JSONEncoder().encode(dict),
              (try? data.write(to: trialFileURL)) != nil else {
            return false
        }
        updateClockState(now)
        return true
    }


    /// 检查本机授权（授权无效时自动回退到试用期内）。返回 (是否有效, 提示信息, 到期时间)。
    static func checkLocalLicense(now: Date = Date()) -> (valid: Bool, message: String, expiry: Date) {
        if isClockRolledBack(now: now) {
            clearLocalLicense()
            return (false, "检测到系统时钟被回调，请校正时间后重新激活", .distantPast)
        }

        let contact = "新声代音乐工作室（微信 277878676）"

        // ── 第 1 步：检查激活码授权 ──
        // 授权文件有问题（机器码不符/激活码无效）时只记录，不直接拦截——
        // 否则试用期内也会被旧授权文件挡在激活窗，点试用也进不去。
        var licenseProblem: String? = nil
        if let data = try? Data(contentsOf: licenseFileURL),
           let dict = try? JSONDecoder().decode([String: String].self, from: data),
           let code = dict["ActivationCode"] {

            let machine = computeMachineCode()
            if let saved = dict["MachineCode"], normalizeCode(saved) != normalizeCode(machine) {
                licenseProblem = "授权文件与这台电脑不匹配"
            } else {
                let result = validateActivationCode(code, machineCode: machine, now: now)
                let df = DateFormatter()
                df.dateFormat = "yyyy-MM-dd"
                switch result.error {
                case .none:
                    return (true, "已授权，到期：\(df.string(from: result.expiry))", result.expiry)
                case .expired:
                    // 授权到期 → 再看是否还在试用期内
                    let trial = trialStatus(now: now)
                    if trial.active {
                        return (true, "试用期剩余 \(trial.daysLeft) 天，到期后请激活", trial.expiry)
                    }
                    return (false, "授权已于 \(df.string(from: result.expiry)) 到期，请联系 \(contact) 续期", result.expiry)
                case .machineMismatch:
                    licenseProblem = "激活码与这台电脑不匹配"
                case .badSignature, .malformed:
                    licenseProblem = "激活码无效"
                }
            }
        }

        // ── 第 2 步：授权不可用 → 检查试用（试用有效就放行）──
        let trial = trialStatus(now: now)
        if trial.active {
            return (true, "试用期剩余 \(trial.daysLeft) 天，到期后请激活", trial.expiry)
        }
        let hint = trial.started
            ? "试用期已结束，请联系 \(contact) 获取激活码"
            : "本软件需激活后使用（授权按年计费），也可先免费试用 3 天"
        if let problem = licenseProblem {
            return (false, "\(problem)。\(hint)", .distantPast)
        }
        return (false, hint, .distantPast)
    }

    /// 保存激活信息（激活成功后调用）。
    static func saveLocalLicense(_ activationCode: String) {
        let dict = ["MachineCode": computeMachineCode(), "ActivationCode": activationCode.trimmingCharacters(in: .whitespacesAndNewlines)]
        if let data = try? JSONEncoder().encode(dict) {
            try? data.write(to: licenseFileURL)
        }
        updateClockState(Date())
    }

    /// 时钟回拨检测：当前时间比记录的最大时间早超过 2 天 → 时钟被回调。
    static func isClockRolledBack(now: Date) -> Bool {
        guard let data = try? Data(contentsOf: clockStateURL),
              let ticks = try? JSONDecoder().decode(Double.self, from: data),
              ticks > 0 else {
            updateClockState(now)
            return false
        }
        let seen = Date(timeIntervalSince1970: ticks)
        let rolled = now.addingTimeInterval(2 * 86400) < seen
        updateClockState(now)
        return rolled
    }

    private static func updateClockState(_ now: Date) {
        var latest = now.timeIntervalSince1970
        if let data = try? Data(contentsOf: clockStateURL),
           let old = try? JSONDecoder().decode(Double.self, from: data), old > latest {
            latest = old // 只前进不后退
        }
        if let data = try? JSONEncoder().encode(latest) {
            try? data.write(to: clockStateURL)
        }
    }

    static func clearLocalLicense() {
        try? FileManager.default.removeItem(at: licenseFileURL)
    }

    // MARK: - 在线购买（发码服务器自动签发）

    /// 线上发码服务器（与直播助手同一套；life 终身套餐用机架专用密钥签发激活码）
    static let licenseServerURL = URL(string: "https://license.xsdmusic.cn")

    /// 官方网站（购买页展示）
    static let officialSite = "https://xsdmusic.cn"

    struct PurchaseOrder: Codable {
        var orderId: String
        var qrCode: String       // 支付宝收款二维码内容（客户端渲染成 QR 图）
        var amountCents: Int
        var amount: String       // 服务器格式化好的金额字符串，如 "9.90"
        var mock: Bool
        var contactWeChat: String?
    }

    struct PurchaseStatus: Codable {
        var status: String              // pending / paid
        var orderId: String
        var activationCode: String?
        var expiryUtc: String?
        var remainingDays: Double?
    }

    static func purchaseError(_ message: String) -> NSError {
        NSError(domain: "Purchase", code: -1,
                userInfo: [NSLocalizedDescriptionKey: message])
    }

    /// 创建 ¥9.9 终身买断订单（life 套餐，product=rack）
    static func createPurchaseOrder(machineCode: String) async throws -> PurchaseOrder {
        guard let base = licenseServerURL else {
            throw purchaseError("未配置发码服务器地址")
        }
        var req = URLRequest(url: base.appendingPathComponent("api/order"))
        req.httpMethod = "POST"
        req.timeoutInterval = 15
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let payload: [String: String] = [
            "machineCode": normalizeCode(machineCode),
            "plan": "life",
            "product": "rack",
        ]
        req.httpBody = try JSONSerialization.data(withJSONObject: payload)
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse, http.statusCode == 200 else {
            throw purchaseError(purchaseErrorMessage(data))
        }
        return try JSONDecoder().decode(PurchaseOrder.self, from: data)
    }

    static func queryPurchaseOrder(orderId: String, machineCode: String) async throws -> PurchaseStatus {
        guard let base = licenseServerURL else {
            throw purchaseError("未配置发码服务器地址")
        }
        var req = URLRequest(url: base.appendingPathComponent("api/order/\(orderId)"))
        req.timeoutInterval = 15
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse, http.statusCode == 200 else {
            throw purchaseError(purchaseErrorMessage(data))
        }
        return try JSONDecoder().decode(PurchaseStatus.self, from: data)
    }

    static func mockPayOrder(orderId: String) async throws {
        guard let base = licenseServerURL else {
            throw purchaseError("未配置发码服务器地址")
        }
        var req = URLRequest(url: base.appendingPathComponent("api/dev/pay/\(orderId)"))
        req.httpMethod = "POST"
        req.timeoutInterval = 15
        _ = try await URLSession.shared.data(for: req)
    }

    private static func purchaseErrorMessage(_ data: Data) -> String {
        if let dict = try? JSONDecoder().decode([String: String].self, from: data),
           let msg = dict["error"], !msg.isEmpty {
            return msg
        }
        return "无法连接发码服务器，请稍后重试，或微信联系 新声代音乐工作室（277878676）手动激活"
    }

    // MARK: - 工具

    static func normalizeCode(_ code: String) -> String {
        String(code.uppercased().filter { $0.isLetter || $0.isNumber })
    }

    static func formatGroups(_ code: String, _ groupSize: Int) -> String {
        stride(from: 0, to: code.count, by: groupSize).map { i in
            String(code.dropFirst(i).prefix(groupSize))
        }.joined(separator: "-")
    }

    private static func toBase32(_ bytes: [UInt8]) -> String {
        var out = ""
        var bits = 0
        var value = 0
        for b in bytes {
            value = (value << 8) | Int(b)
            bits += 8
            while bits >= 5 {
                out.append(base32Alphabet[(value >> (bits - 5)) & 31])
                bits -= 5
            }
        }
        if bits > 0 {
            out.append(base32Alphabet[(value << (5 - bits)) & 31])
        }
        return out
    }

    private static func fromBase32(_ s: String) -> Data? {
        var bytes: [UInt8] = []
        var bits = 0
        var value = 0
        for ch in s {
            guard let idx = base32Alphabet.firstIndex(of: ch) else { return nil }
            value = (value << 5) | idx
            bits += 5
            if bits >= 8 {
                bytes.append(UInt8((value >> (bits - 8)) & 0xFF))
                bits -= 8
            }
        }
        return Data(bytes)
    }
}
