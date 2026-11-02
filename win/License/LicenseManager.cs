using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XSDAudioRack.License;

/// <summary>
/// MAC 地址绑定授权：
///   机器码 = 本机所有物理网卡 MAC 的哈希（16 字符）
///   激活码 = 到期日 + 对(机器码|到期日)的 ECDSA P-256 签名
/// App 内只嵌公钥，签名无法伪造；到期后自动失效，并检测系统时钟回拨。
/// </summary>
public static class LicenseManager
{
    /// <summary>ECDSA P-256 公钥（SPKI，DER，Base64）— 与算号机私钥配对（历史密钥，手动发的码仍有效）。</summary>
    public const string PublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEiJr7Ao/IXjUJWN2ENQ4ok9hcZMvjyK9TIV28vvJ+L/Gk5/hLk30zMRMOFwzeRtPg3HqezNoaDgjwtQLZIXcneA==";

    /// <summary>
    /// 机架专用公钥（2026-10-29 起，线上 life 终身订单由服务器用配对私钥自动签发）。
    /// 与直播助手的主密钥隔离：机架的码激活不了直播助手，反之亦然（直播助手只认主密钥）。
    /// 校验时先试新密钥再试旧密钥，历史激活码不受影响。
    /// </summary>
    public const string RackPublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEHQ3Lke+C7EJJolCpt7CXld4XNkVLgpPW8VE/N5LsUMBR+dT1oTeEPxBtWjavc4j6L1QL6+mfoIKtq8uVXPzO0g==";

    /// <summary>信任的公钥列表（按新→旧排序）。</summary>
    public static readonly string[] TrustedPublicKeys = { RackPublicKeySpkiBase64, PublicKeySpkiBase64 };

    private const string Base32Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"; // 无 0/1/I/O，避免抄错
    private const string HmacLabel = "XSDLiveSinger.v1";
    private static readonly DateTime ExpiryEpoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ================= 机器码 =================

    /// <summary>本机机器码（XXXX-XXXX-XXXX-XXXX）。取所有启用物理网卡的 MAC。</summary>
    public static string ComputeMachineCode()
    {
        var macs = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                        && n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.GetPhysicalAddress()?.GetAddressBytes())
            .Where(b => b is { Length: 6 } && b.Any(x => x != 0))
            .Select(b => Convert.ToHexString(b!))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (macs.Count == 0)
            throw new InvalidOperationException("没有找到可用的网卡，无法生成机器码。");

        var data = Encoding.UTF8.GetBytes(HmacLabel + "|" + string.Join(",", macs));
        var hash = SHA256.HashData(data);
        var encoded = ToBase32(hash, charCount: 16);
        return FormatGroups(encoded, 4);
    }

    // ================= 激活码生成（算号机用） =================

    /// <summary>生成激活码。expiryUtc 为授权到期时间。</summary>
    public static string GenerateActivationCode(string machineCode, DateTime expiryUtc, byte[] pkcs8PrivateKey)
    {
        machineCode = NormalizeCode(machineCode);
        if (machineCode.Length != 16) throw new ArgumentException("机器码必须为 16 个字符。");

        int days = (int)(expiryUtc.Date - ExpiryEpoch).TotalDays;
        if (days < 0) throw new ArgumentException("到期时间早于授权纪元。");
        var payload = Encoding.UTF8.GetBytes($"{machineCode}|{days}");
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        var sig = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var blob = new byte[2 + sig.Length];
        blob[0] = (byte)(days & 0xFF);
        blob[1] = (byte)(days >> 8);
        Array.Copy(sig, 0, blob, 2, sig.Length);
        return FormatGroups(ToBase32(blob, charCount: -1), 5);
    }

    // ================= 激活码校验 =================

    public enum LicenseError
    {
        None,
        /// <summary>激活码格式无法识别。</summary>
        Malformed,
        /// <summary>激活码与这台电脑不匹配。</summary>
        MachineMismatch,
        /// <summary>签名无效（伪造或被改）。</summary>
        BadSignature,
        /// <summary>授权已到期。</summary>
        Expired,
    }

    public static (LicenseError Error, DateTime ExpiryUtc) ValidateActivationCode(
        string activationCode, string machineCode, DateTime nowUtc, string[]? publicKeys = null)
    {
        var encoded = NormalizeCode(activationCode);
        byte[] blob;
        try
        {
            blob = FromBase32(encoded);
        }
        catch
        {
            return (LicenseError.Malformed, default);
        }
        if (blob.Length < 2 + 64)
            return (LicenseError.Malformed, default);

        int days = blob[0] | (blob[1] << 8);
        var expiry = ExpiryEpoch.AddDays(days);
        machineCode = NormalizeCode(machineCode);

        var payload = Encoding.UTF8.GetBytes($"{machineCode}|{days}");
        var keys = publicKeys ?? TrustedPublicKeys;
        var sig = blob[2..];

        // 依次尝试信任的公钥（新机架密钥 → 旧算号机密钥），任一验签通过即有效
        foreach (var spki in keys)
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
            if (key.VerifyData(payload, sig, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                goto Verified;
        }

        {
            // 签名对不上：要么是伪造，要么是别的机器的码 —— 再用正确的机器码验证一次来区分提示
            var payload2 = Encoding.UTF8.GetBytes($"{NormalizeCode(ComputeMachineCode())}|{days}");
            foreach (var spki in keys)
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
                if (machineCode != NormalizeCode(ComputeMachineCode())
                    && key.VerifyData(payload2, sig, HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return (LicenseError.MachineMismatch, expiry);
            }
            return (LicenseError.BadSignature, expiry);
        }

    Verified:
        if (nowUtc >= expiry)
            return (LicenseError.Expired, expiry);
        return (LicenseError.None, expiry);
    }

    // ================= 本机授权存取 =================

    private static string LicenseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XSDAudioRack");
    private static string LicenseFilePath => Path.Combine(LicenseDir, "license.json");
    private static string ClockStatePath => Path.Combine(LicenseDir, ".state");
    private static string TrialFilePath => Path.Combine(LicenseDir, "trial.json");

    /// <summary>
    /// 三天试用：首次调用时记录开始时间（trial.json），此后每次校验剩余时间。
    /// 返回 (试用是否有效, 试用截止时间)。已激活用户不必调用。
    /// </summary>
    public static (bool Valid, DateTime EndUtc) CheckTrial(DateTime nowUtc)
    {
        try
        {
            DateTime startUtc;
            if (File.Exists(TrialFilePath))
            {
                var ticks = JsonSerializer.Deserialize<long>(File.ReadAllText(TrialFilePath));
                startUtc = new DateTime(ticks, DateTimeKind.Utc);
            }
            else
            {
                startUtc = nowUtc;
                Directory.CreateDirectory(LicenseDir);
                File.WriteAllText(TrialFilePath, JsonSerializer.Serialize(nowUtc.Ticks));
                UpdateClockState(nowUtc);
            }
            var end = startUtc.AddDays(3);
            return (nowUtc < end, end);
        }
        catch
        {
            return (false, default);
        }
    }

    private sealed class LicenseFile
    {
        public string? MachineCode { get; set; }
        public string? ActivationCode { get; set; }
    }

    /// <summary>
    /// 检查本机授权。返回 (是否有效, 提示信息, 到期时间)。
    /// 激活码与本机机器码一起持久化，每次启动重新验签，防止改文件。
    /// </summary>
    public static (bool Valid, string Message, DateTime ExpiryUtc) CheckLocalLicense(DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(LicenseFilePath))
                return (false, "未激活", default);
            var lic = JsonSerializer.Deserialize<LicenseFile>(File.ReadAllText(LicenseFilePath));
            if (lic?.ActivationCode == null)
                return (false, "授权文件损坏，请重新激活", default);

            var machine = ComputeMachineCode();
            if (lic.MachineCode != FormatGroups(machine, 4) && lic.MachineCode != machine)
                return (false, "授权文件与这台电脑不匹配", default);

            var (err, expiry) = ValidateActivationCode(lic.ActivationCode, machine, nowUtc);
            return err switch
            {
                LicenseError.None => (true, $"已授权，到期：{expiry:yyyy-MM-dd}", expiry),
                LicenseError.Expired => (false, $"授权已于 {expiry:yyyy-MM-dd} 到期，请续期", expiry),
                LicenseError.MachineMismatch => (false, "激活码与这台电脑不匹配", default),
                _ => (false, "激活码无效", default),
            };
        }
        catch (Exception ex)
        {
            return (false, "读取授权失败：" + ex.Message, default);
        }
    }

    /// <summary>保存激活信息（激活成功后调用）。</summary>
    public static void SaveLocalLicense(string activationCode)
    {
        Directory.CreateDirectory(LicenseDir);
        var lic = new LicenseFile
        {
            MachineCode = FormatGroups(NormalizeCode(ComputeMachineCode()), 4),
            ActivationCode = activationCode.Trim(),
        };
        File.WriteAllText(LicenseFilePath, JsonSerializer.Serialize(lic));
        UpdateClockState(DateTime.UtcNow);
    }

    /// <summary>
    /// 时钟回拨检测：正常使用中记录"见过的最大时间"，如果当前时间比它早超过 2 天，
    /// 说明系统时钟被回调过（想绕过期），返回 true。
    /// </summary>
    public static bool IsClockRolledBack(DateTime nowUtc)
    {
        try
        {
            if (File.Exists(ClockStatePath))
            {
                var ticks = JsonSerializer.Deserialize<long>(File.ReadAllText(ClockStatePath));
                if (ticks is > 0 && nowUtc.AddDays(2) < new DateTime(ticks, DateTimeKind.Utc))
                    return true;
            }
            UpdateClockState(nowUtc);
        }
        catch { /* 检测失败不阻断正常使用 */ }
        return false;
    }

    private static void UpdateClockState(DateTime nowUtc)
    {
        long ticks = nowUtc.Ticks;
        if (File.Exists(ClockStatePath))
        {
            try
            {
                var old = JsonSerializer.Deserialize<long>(File.ReadAllText(ClockStatePath));
                if (old > 0 && old > ticks) ticks = old; // 只前进不后退
            }
            catch { }
        }
        Directory.CreateDirectory(LicenseDir);
        File.WriteAllText(ClockStatePath, JsonSerializer.Serialize(ticks));
    }

    /// <summary>删除本机授权（激活失败重置用）。</summary>
    public static void ClearLocalLicense()
    {
        try { if (File.Exists(LicenseFilePath)) File.Delete(LicenseFilePath); } catch { }
    }

    // ================= 工具 =================

    /// <summary>去掉分隔符、转大写、纠正易混淆字符（O→0 不需要，字母表里没有）。</summary>
    public static string NormalizeCode(string code) =>
        new(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string FormatGroups(string code, int groupSize) =>
        string.Join("-", Enumerable.Range(0, (code.Length + groupSize - 1) / groupSize)
            .Select(i => code.Substring(i * groupSize, Math.Min(groupSize, code.Length - i * groupSize))));

    private static string ToBase32(byte[] data, int charCount)
    {
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(value >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(value << (5 - bits)) & 31]);
        var s = sb.ToString();
        return charCount < 0 ? s : s[..charCount];
    }

    private static byte[] FromBase32(string s)
    {
        var list = new List<byte>(s.Length * 5 / 8);
        int bits = 0, value = 0;
        foreach (var c in s)
        {
            int idx = Base32Alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"非法字符 '{c}'");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                list.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return list.ToArray();
    }
}
