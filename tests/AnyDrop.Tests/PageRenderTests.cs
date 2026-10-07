using AnyDrop.Server;
using Xunit;

namespace AnyDrop.Tests;

/// <summary>
/// 服务端直出页面的展示格式。
/// **时区口径与前端不同**：这里固定 UTC 并在页面上标注（收件人时区未知）；
/// 管理面板的 `formatTime` 用浏览器本地时区（给自己看）。格式相同、时区刻意不同。
/// </summary>
public sealed class PageRenderTests
{
    [Theory]
    [InlineData("2026-11-05T14:31:15Z", "2026-11-05 14:31:15")]
    [InlineData("2026-01-02T03:04:05Z", "2026-01-02 03:04:05")]
    // 带偏移的 ISO 也先归一成 UTC，免得同一时刻在不同机器上显示成两个样子
    [InlineData("2026-11-05T22:31:15+08:00", "2026-11-05 14:31:15")]
    public void HumanTime把ISO格式化成给人看的UTC(string iso, string expected) =>
        Assert.Equal(expected, PageEndpoints.HumanTime(iso));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HumanTime对空值返回空串(string? iso) => Assert.Equal("", PageEndpoints.HumanTime(iso));

    [Fact]
    public void HumanTime对解析不了的串原样返回() =>
        Assert.Equal("not-a-time", PageEndpoints.HumanTime("not-a-time"));
}
