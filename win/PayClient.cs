using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XSDAudioRack;

/// <summary>
/// 扫码付款客户端：对接线上发码服务器（与直播助手同一套）。
/// 下单（life 终身套餐）→ 返回支付宝二维码 → 轮询订单 → 已支付则拿到激活码自动激活。
/// </summary>
public static class PayClient
{
    public const string BaseUrl = "https://license.xsdmusic.cn";
    /// <summary>官方网站（激活页展示用）。</summary>
    public const string OfficialSite = "https://xsdmusic.cn";
    public const string ContactWeChat = "277878676";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public sealed record CreateOrderResponse(
        string OrderId, string? QrCode, decimal Amount, int AmountCents,
        string? ContactWeChat, bool Mock);

    public sealed record OrderStatusResponse(
        string Status, string OrderId, string? ActivationCode,
        DateTime? ExpiryUtc, double? RemainingDays);

    /// <summary>创建 ¥9.9 终身买断订单（life 套餐，product=rack）。</summary>
    public static async Task<CreateOrderResponse> CreateLifeOrderAsync(
        string machineCode, CancellationToken ct = default)
    {
        using var resp = await Http.PostAsJsonAsync($"{BaseUrl}/api/order",
            new { machineCode, plan = "life", product = "rack" }, JsonOpts, ct);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? "创建订单失败，请稍后重试。");
        return new CreateOrderResponse(
            body.GetProperty("orderId").GetString()!,
            body.TryGetProperty("qrCode", out var qr) ? qr.GetString() : null,
            body.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.String
                ? decimal.Parse(a.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : 0m,
            body.TryGetProperty("amountCents", out var c) ? c.GetInt32() : 0,
            body.TryGetProperty("contactWeChat", out var w) ? w.GetString() : null,
            body.TryGetProperty("mock", out var m) && m.ValueKind == JsonValueKind.True);
    }

    /// <summary>轮询订单状态；已支付时返回激活码。</summary>
    public static async Task<OrderStatusResponse> GetOrderAsync(string orderId, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"{BaseUrl}/api/order/{Uri.EscapeDataString(orderId)}", ct);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(TryError(body) ?? "查询订单失败。");
        return new OrderStatusResponse(
            body.GetProperty("status").GetString() ?? "pending",
            body.GetProperty("orderId").GetString() ?? orderId,
            body.TryGetProperty("activationCode", out var code) ? code.GetString() : null,
            body.TryGetProperty("expiryUtc", out var exp) && exp.ValueKind == JsonValueKind.String
                ? DateTime.Parse(exp.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind) : null,
            body.TryGetProperty("remainingDays", out var rd) ? rd.GetDouble() : null);
    }

    private static string? TryError(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("error", out var e)
        && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
}
