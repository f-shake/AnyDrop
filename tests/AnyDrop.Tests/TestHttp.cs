using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AnyDrop.Server;
using Xunit;

namespace AnyDrop.Tests;

internal static class TestHttp
{
    public static HttpRequestMessage Upload(
        string? key,
        byte[] body,
        string url = "/v1/blobs",
        string? fileName = "a.txt",
        string? idempotencyKey = null,
        string contentType = "application/octet-stream",
        bool sendContentLength = true)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (!sendContentLength) content.Headers.ContentLength = null;
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (fileName is not null) request.Headers.TryAddWithoutValidation("X-Filename", fileName);
        if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (!sendContentLength) request.Headers.TransferEncodingChunked = true;
        return request;
    }

    public static async Task<UploadResponse> UploadOkAsync(
        HttpClient client, string key, byte[] body, string? fileName = "a.txt", string? idempotencyKey = null,
        string url = "/v1/blobs", string contentType = "application/octet-stream")
    {
        var response = await client.SendAsync(
            Upload(key, body, url: url, fileName: fileName, idempotencyKey: idempotencyKey, contentType: contentType));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<UploadResponse>();
        Assert.NotNull(parsed);
        return parsed!;
    }

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "";
    }

    public static async Task<string> ErrorMessageAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
    }

    public static HttpRequestMessage WithBearer(this HttpRequestMessage request, string key)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
