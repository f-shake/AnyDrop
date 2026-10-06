using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AnyDrop.Server;

/// <summary>
/// 取真实客户端 IP。仅在直连方是回环地址（本机 nginx）时才信任转发头。
/// 取值优先级：X-Real-IP（nginx 无条件覆盖，客户端伪造不了）→ X-Forwarded-For 的**最右**一项
/// （nginx 用 $proxy_add_x_forwarded_for 追加，最右才是它写进去的；最左是客户端可控的）。
/// </summary>
public sealed class IpAccessor(AppConfig config)
{
    public string Get(HttpContext context)
    {
        if (config.Security.TrustProxy && IsLoopback(context.Connection.RemoteIpAddress))
        {
            if (context.Request.Headers.TryGetValue("X-Real-IP", out var realIp))
            {
                var value = realIp.ToString().Trim();
                if (value.Length > 0 && IPAddress.TryParse(value, out var parsed)) return parsed.ToString();
            }

            if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
            {
                // 只认最后一项：nginx 用 $proxy_add_x_forwarded_for 追加，最后一项才是它写进去的。
                // 最后一项不合法就回落到直连地址（回环），绝不去左边捞客户端可控的值。
                var parts = forwarded.ToString().Split(',');
                var candidate = parts[^1].Trim();
                if (candidate.Length > 0 && IPAddress.TryParse(candidate, out var parsed)) return parsed.ToString();
            }
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);
}

public static class HttpJson
{
    /// <summary>读取 JSON 请求体；失败返回 null（端点据此回 400）。</summary>
    public static async ValueTask<T?> TryReadAsync<T>(
        HttpRequest request,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0) return default;
        try
        {
            return await JsonSerializer.DeserializeAsync(request.Body, typeInfo, cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo, int statusCode = 200) =>
        Results.Json(value, typeInfo, statusCode: statusCode);
}
