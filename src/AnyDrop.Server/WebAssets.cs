using Microsoft.Extensions.Logging;

namespace AnyDrop.Server;

/// <summary>
/// 内嵌前端资源：由 scripts/build-web.ps1 生成 wwwroot.g.props，把 dist 里的文件以
/// LogicalName = "web/&lt;url 路径&gt;" 登记为 EmbeddedResource，这里在启动时读进内存字典。
/// 不依赖反射与动态程序集加载，AOT 安全。
/// </summary>
public sealed class WebAssetStore
{
    private const string WebPrefix = "web/";
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _files.Count;
    public bool HasUi => _files.ContainsKey("index.html");

    public WebAssetStore(ILogger<WebAssetStore> logger)
    {
        var assembly = typeof(WebAssetStore).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(WebPrefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            _files[name[WebPrefix.Length..]] = buffer.ToArray();
        }
        logger.LogInformation("内嵌前端资源 {Count} 个（index.html 存在：{HasUi}）", _files.Count, HasUi);
    }

    public bool TryGet(string path, out byte[] content, out string contentType)
    {
        var key = path.TrimStart('/');
        if (_files.TryGetValue(key, out var found))
        {
            content = found;
            contentType = ContentTypeFor(key);
            return true;
        }
        content = [];
        contentType = "application/octet-stream";
        return false;
    }

    public static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".map" => "application/json; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };
}

/// <summary>Pages/*.html 作为内嵌资源加载，避免依赖运行目录下的文件。</summary>
public static class PageTemplates
{
    public const string DownloadPath = "page/download.html";
    public const string UiNotBuiltPath = "page/ui-not-built.html";

    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Load(string logicalName)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(logicalName, out var cached)) return cached;
            var assembly = typeof(PageTemplates).Assembly;
            using var stream = assembly.GetManifestResourceStream(logicalName)
                ?? throw new InvalidOperationException($"缺少内嵌页面资源：{logicalName}");
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            Cache[logicalName] = text;
            return text;
        }
    }

    public static string Render(string logicalName, string content) =>
        Load(logicalName).Replace("{{CONTENT}}", content, StringComparison.Ordinal);
}
