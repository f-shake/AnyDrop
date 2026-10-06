namespace AnyDrop.Server;

public static class AppMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; form-action 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            await next();
        });

    public static IApplicationBuilder UseApiErrorHandling(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                var config = context.RequestServices.GetRequiredService<AppConfig>();
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.FileTooLarge,
                    $"文件超过上限（最大 {config.Server.MaxUploadBytes / (1024 * 1024)} MiB）");
            }
            catch (BadHttpRequestException)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.IncompleteBody, "请求体不完整，请重试");
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // 客户端断开：没有可回复的对象
            }
            catch (IOException) when (context.RequestAborted.IsCancellationRequested)
            {
                // 同上
            }
            catch (Exception ex)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AnyDrop.Errors");
                logger.LogError(ex, "未处理的异常：{Method} {Path}", context.Request.Method, context.Request.Path);
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, "服务内部错误");
            }
        });

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}");
    }
}
