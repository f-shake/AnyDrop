using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnyDrop.Server;

public sealed record GcReport(int ExpiredBlobs, int StaleParts, int OrphanFiles);

/// <summary>过期清理与孤儿回收。不参与业务写入。</summary>
public sealed class GcService(
    AppConfig config,
    SqliteIndex index,
    BlobStore store,
    ILogger<GcService> logger) : BackgroundService
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "首轮 GC 失败");
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, config.Retention.GcIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "GC 轮次失败，等待下一轮");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停止服务时 PeriodicTimer 会以取消异常结束等待，属于正常关闭
        }
    }

    public async Task<GcReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = Time.NowIso();
        var expired = await index.ListExpiredAsync(now, 500, cancellationToken);
        var deleted = 0;
        var touchedTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var blob in expired)
        {
            // 条件删除：pin 与回收并发时，删不到行就绝不能删文件（管理员刚点过「保留」）。
            if (!await index.DeleteExpiredBlobAsync(blob.Id, cancellationToken)) continue;
            store.Delete(blob.CipherPath);
            await index.InsertAuditAsync("blob.expire", blob.TokenId, blob.Id, blob.Namespace, null, blob.Size, null, cancellationToken);
            touchedTokens.Add(blob.TokenId);
            deleted++;
        }
        foreach (var tokenId in touchedTokens)
            await index.RecomputeTokenUsageAsync(tokenId, cancellationToken);

        var parts = store.CleanStaleParts(Grace);

        // 孤儿回收分两步：先按宽限期挑候选，再向数据库复核「此刻是否仍被引用」。
        // 中间可能刚有一次上传完成了 rename 但还没写库，单靠快照会误删。
        var known = await index.ListAllBlobsAsync(cancellationToken);
        var knownPaths = known.Select(b => b.CipherPath).ToHashSet(StringComparer.Ordinal);
        var candidates = store.FindOrphanCandidates(knownPaths, Grace);
        var stillReferenced = await index.FilterExistingPathsAsync(candidates, cancellationToken);
        candidates.RemoveAll(stillReferenced.Contains);
        var orphans = store.DeleteFiles(candidates);

        var danglingKeys = await index.PurgeDanglingIdempotencyAsync(cancellationToken);

        if (deleted > 0 || parts > 0 || orphans > 0 || danglingKeys > 0)
            logger.LogInformation("GC 完成：过期 {Expired}，残留分片 {Parts}，孤儿文件 {Orphans}，失效幂等键 {Keys}",
                deleted, parts, orphans, danglingKeys);

        return new GcReport(deleted, parts, orphans);
    }
}
