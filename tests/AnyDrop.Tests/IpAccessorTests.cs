using System.Net;
using AnyDrop.Server;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AnyDrop.Tests;

/// <summary>
/// 真实客户端 IP 的取值规则：直连方不是回环时一律不信任转发头；
/// 是回环时优先 X-Real-IP（nginx 会覆盖，客户端伪造不了），否则取 X-Forwarded-For 的最右一项。
/// </summary>
public sealed class IpAccessorTests
{
    private static IpAccessor Accessor(bool trustProxy = true) =>
        new(new AppConfig { Security = new SecurityOptions { TrustProxy = trustProxy } });

    private static HttpContext Context(string remoteIp, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        return context;
    }

    [Fact]
    public void 回环直连时优先XRealIP而不是可伪造的XFF()
    {
        var context = Context("127.0.0.1",
            ("X-Real-IP", "203.0.113.9"),
            ("X-Forwarded-For", "1.2.3.4, 203.0.113.9"));

        Assert.Equal("203.0.113.9", Accessor().Get(context));
    }

    [Fact]
    public void 没有XRealIP时取XFF最右一项()
    {
        var context = Context("127.0.0.1", ("X-Forwarded-For", "1.2.3.4, 203.0.113.9"));

        Assert.Equal("203.0.113.9", Accessor().Get(context));
    }

    [Fact]
    public void XFF最右项非法时回落到直连地址而不是往左找()
    {
        // 只认 nginx 追加的最后一项；它不合法说明链路不合预期，
        // 此时绝不能去左边捞客户端可控的值。
        var context = Context("127.0.0.1", ("X-Forwarded-For", "1.2.3.4, not-an-ip"));

        Assert.Equal("127.0.0.1", Accessor().Get(context));
    }

    [Fact]
    public void 非回环直连不信任任何转发头()
    {
        var context = Context("198.51.100.7",
            ("X-Real-IP", "203.0.113.9"),
            ("X-Forwarded-For", "1.2.3.4"));

        Assert.Equal("198.51.100.7", Accessor().Get(context));
    }

    [Fact]
    public void 关闭信任代理时忽略转发头()
    {
        var context = Context("127.0.0.1", ("X-Real-IP", "203.0.113.9"));

        Assert.Equal("127.0.0.1", Accessor(trustProxy: false).Get(context));
    }

    [Fact]
    public void 没有任何来源信息时返回unknown()
    {
        var context = new DefaultHttpContext();

        Assert.Equal("unknown", Accessor().Get(context));
    }
}
