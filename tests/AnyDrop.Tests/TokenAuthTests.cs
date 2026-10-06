using AnyDrop.Server;
using Xunit;

namespace AnyDrop.Tests;

public sealed class TokenAuthTests
{
    [Fact]
    public async Task 新建的上传密钥可直接认证()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync();

        Assert.StartsWith("ad_", key);
        Assert.Equal(key[..9], token.KeyPrefix);
        Assert.DoesNotContain(key, token.KeyHash, StringComparison.OrdinalIgnoreCase);
        Assert.Null(token.RevokedAt);
    }

    [Fact]
    public async Task maxFileBytes不能超过服务端上限()
    {
        using var app = new TestApp(("server:maxUploadBytes", "2048"));
        var tokens = app.GetService<TokenService>();
        var result = await tokens.CreateAsync(
            new CreateTokenRequest("big", null, null, 999_999),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2048, result.Value!.Token.MaxFileBytes);
    }

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("ok", 99999, null, null)]
    [InlineData("ok", null, 0L, null)]
    [InlineData("ok", null, null, -1L)]
    public async Task 非法入参被拒绝(string name, int? ttlDays, long? quotaBytes, long? maxFileBytes)
    {
        using var app = new TestApp();
        var tokens = app.GetService<TokenService>();
        var request = new CreateTokenRequest(name, ttlDays, quotaBytes, maxFileBytes);

        var result = await tokens.CreateAsync(request, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(400, result.Failure!.Value.Status);
        Assert.Equal(ErrorCodes.BadRequest, result.Failure.Value.Code);
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
}
