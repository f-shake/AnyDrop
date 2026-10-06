using AnyDrop.Server;
using Xunit;

namespace AnyDrop.Tests;

public sealed class TokenAuthTests
{
    [Theory]
    [InlineData(TokenPresets.AiWrite, true, false, false)]
    [InlineData(TokenPresets.MeRead, false, true, false)]
    [InlineData(TokenPresets.NasPull, false, true, true)]
    public async Task 预设对应的能力位正确(string preset, bool upload, bool read, bool delete)
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync(preset);

        Assert.Equal(upload, token.CanUpload);
        Assert.Equal(read, token.CanRead);
        Assert.Equal(delete, token.CanDelete);
        Assert.StartsWith("ad_", key);
        Assert.Equal(key[..9], token.KeyPrefix);
        Assert.DoesNotContain(key, token.KeyHash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 显式能力位覆盖预设()
    {
        using var app = new TestApp();
        var tokens = app.GetService<TokenService>();
        var result = await tokens.CreateAsync(
            new CreateTokenRequest("custom", TokenPresets.AiWrite, "ns1", false, true, true, null, null, null),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.False(result.Value!.Token.CanUpload);
        Assert.True(result.Value.Token.CanRead);
        Assert.True(result.Value.Token.CanDelete);
    }

    [Fact]
    public async Task maxFileBytes不能超过服务端上限()
    {
        using var app = new TestApp(("server:maxUploadBytes", "2048"));
        var tokens = app.GetService<TokenService>();
        var result = await tokens.CreateAsync(
            new CreateTokenRequest("big", null, null, null, null, null, null, null, 999_999),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2048, result.Value!.Token.MaxFileBytes);
    }

    [Theory]
    [InlineData("", TokenPresets.AiWrite, "default", "name")]
    [InlineData("ok", "unknown-preset", "default", "preset")]
    [InlineData("ok", null, "Bad-NS", "namespace")]
    [InlineData("ok", null, "with space", "namespace")]
    [InlineData("ok", null, ".leading-dot", "namespace")]
    [InlineData("ok", null, "default", "quota")]
    [InlineData("ok", null, "default", "maxfile")]
    [InlineData("ok", null, "default", "ttl")]
    public async Task 非法入参被拒绝(string name, string? preset, string ns, string kind)
    {
        using var app = new TestApp();
        var tokens = app.GetService<TokenService>();
        var request = new CreateTokenRequest(
            name,
            preset,
            ns,
            null, null, null,
            kind == "ttl" ? 99999 : null,
            kind == "quota" ? 0 : null,
            kind == "maxfile" ? -1 : null);

        var result = await tokens.CreateAsync(request, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(400, result.Failure!.Value.Status);
        Assert.Equal(ErrorCodes.BadRequest, result.Failure.Value.Code);
    }

    [Fact]
    public async Task 至少要有一项能力()
    {
        using var app = new TestApp();
        var tokens = app.GetService<TokenService>();
        var result = await tokens.CreateAsync(
            new CreateTokenRequest("none", null, null, false, false, false, null, null, null),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(400, result.Failure!.Value.Status);
    }

    [Fact]
    public async Task 认证接受裸key与Bearer前缀()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync();
        var tokens = app.GetService<TokenService>();

        Assert.True((await tokens.AuthenticateAsync(key, CancellationToken.None)).Ok);
        Assert.True((await tokens.AuthenticateAsync($"Bearer {key}", CancellationToken.None)).Ok);
        Assert.True((await tokens.AuthenticateAsync($"bearer {key}", CancellationToken.None)).Ok);
        Assert.Equal(token.Id, (await tokens.AuthenticateAsync(key, CancellationToken.None)).Value!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer ")]
    [InlineData("not-a-key")]
    [InlineData("Bearer ad_tooshort")]
    [InlineData("Bearer ad_000000000000000000000000000000000000000")]
    public async Task 非法密钥返回401(string? authorization)
    {
        using var app = new TestApp();
        await app.CreateTokenAsync();
        var tokens = app.GetService<TokenService>();

        var result = await tokens.AuthenticateAsync(authorization, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(401, result.Failure!.Value.Status);
        Assert.Equal(ErrorCodes.InvalidToken, result.Failure.Value.Code);
    }

    [Fact]
    public async Task 被撤销的密钥返回401()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync();
        await app.GetService<SqliteIndex>().RevokeTokenAsync(token.Id, Time.NowIso());

        var result = await app.GetService<TokenService>().AuthenticateAsync(key, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidToken, result.Failure!.Value.Code);
    }

    [Fact]
    public async Task 过期的密钥返回token_expired()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync(ttlDays: 1);
        await app.ExpireTokenAsync(token.Id);

        var result = await app.GetService<TokenService>().AuthenticateAsync(key, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(401, result.Failure!.Value.Status);
        Assert.Equal(ErrorCodes.TokenExpired, result.Failure.Value.Code);
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("ns-1", true)]
    [InlineData("a.b_c-d", true)]
    [InlineData("A", false)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("_x", false)]
    [InlineData("0123456789012345678901234567890123", false)]
    public void 命名空间校验规则(string ns, bool expected) =>
        Assert.Equal(expected, TokenService.IsValidNamespace(ns));
}
