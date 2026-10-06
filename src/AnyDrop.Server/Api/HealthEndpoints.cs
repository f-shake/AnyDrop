namespace AnyDrop.Server;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", async (BlobStore store, SqliteIndex index, AppConfig config) =>
        {
            var (free, _) = store.DiskStatus();
            var count = await index.CountBlobsAsync();
            var response = new HealthResponse(
                "ok",
                free == long.MaxValue ? -1 : free,
                count,
                (long)(DateTimeOffset.UtcNow - Program.StartedAt).TotalSeconds);
            return HttpJson.Json(response, AppJsonContext.Default.HealthResponse);
        });
    }
}
