using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace AnyDrop.Server;

/// <summary>
/// Token 为 null 表示管理端上传：没有密钥身份，因此不占配额、不记 token 用量，
/// 命名空间取 <see cref="TokenService.DefaultNamespace"/>，落库时用 <see cref="AdminUploadIdentity"/> 记账。
/// </summary>
public sealed record UploadRequest(
    TokenRecord? Token,
    string? FileName,
    string? ContentType,
    string? IdempotencyKey,
    long ContentLength,
    Stream Body,
    string Ip);

public sealed record DownloadHandle(BlobRecord Blob, BlobFile File);

/// <summary>业务编排：鉴权已在上层完成，这里负责配额、TTL、编排加解密与元数据写入。</summary>
public sealed class BlobService(
    AppConfig config,
    SqliteIndex index,
    BlobStore store,
    KeyRing keyRing,
    ILogger<BlobService> logger)
{
    private readonly SemaphoreSlim _uploadSlots =
        new(config.Server.MaxConcurrentUploads, config.Server.MaxConcurrentUploads);

    public long EffectiveMaxUpload(TokenRecord? token) =>
        token is null ? config.Server.MaxUploadBytes : Math.Min(token.MaxFileBytes, config.Server.MaxUploadBytes);

    public async Task<ServiceResult<UploadResponse>> UploadAsync(UploadRequest request, CancellationToken cancellationToken)
    {
        var token = request.Token;
        if (token is not null && !token.CanUpload)
            return ServiceResult<UploadResponse>.Fail(403, ErrorCodes.ScopeDenied, "该密钥无权上传");

        // 记账身份与命名空间：真实密钥，或管理端上传的哨兵身份 + 默认命名空间。
        // 哨兵让管理端上传的文件与「建 token 时未指定命名空间」的密钥同处 default，现有读密钥立刻能取走。
        var ownerId = token?.Id ?? AdminUploadIdentity.TokenId;
        var ns = token?.Namespace ?? TokenService.DefaultNamespace;
        if (request.ContentLength < 0)
            return ServiceResult<UploadResponse>.Fail(411, ErrorCodes.LengthRequired, "上传必须带 Content-Length");

        var maxBytes = EffectiveMaxUpload(token);
        if (request.ContentLength > maxBytes)
            return ServiceResult<UploadResponse>.Fail(413, ErrorCodes.FileTooLarge,
                $"文件超过上限（最大 {maxBytes / (1024 * 1024)} MiB）");
        if (store.IsDiskLow(config.Storage.MinFreeBytes, config.Storage.MaxUsedPercent))
            return ServiceResult<UploadResponse>.Fail(507, ErrorCodes.DiskLow, "服务器空间不足，暂时拒绝写入");
        if (!await AcquireUploadSlotAsync(cancellationToken))
            return ServiceResult<UploadResponse>.Fail(429, ErrorCodes.RateLimited, "并发上传过多，请稍后重试");

        var reserved = false;
        var stored = false;
        try
        {
            // 原子预占配额：并发的多个请求不会都读到同一个 used_bytes 快照而一起通过。
            // 管理端上传不属于任何密钥，而配额是密钥的属性：对不存在的 token 行预占必然 0 行受影响，
            // 所以这里必须整段跳过，否则超管会被自己的配额挡在 507 外面。
            if (token is not null)
            {
                if (!await index.TryReserveQuotaAsync(token.Id, request.ContentLength, cancellationToken))
                    return ServiceResult<UploadResponse>.Fail(507, ErrorCodes.QuotaExceeded, "该密钥的存储配额已用尽");
                reserved = true;
            }

            // 幂等键按密钥记账，管理端上传不参与（哨兵身份没有可复用的重试语义）。
            if (token is not null && !string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                var existingId = await index.GetIdempotentBlobIdAsync(token.Id, request.IdempotencyKey, cancellationToken);
                if (existingId is not null)
                {
                    var existing = await index.GetBlobAsync(existingId, cancellationToken);
                    if (existing is null || Time.IsExpired(existing.ExpiresAt, DateTimeOffset.UtcNow))
                    {
                        // 目标 blob 已被删除或过期回收：清掉这行并按新上传处理，
                        // 否则「重试同一个幂等键」会永远返回 409。
                        await index.DeleteIdempotencyAsync(token.Id, request.IdempotencyKey, cancellationToken);
                    }
                    else
                    {
                        var incomingHash = await HashBodyAsync(request.Body, maxBytes, cancellationToken);
                        if (SecretHasher.FixedTimeEqualsHex(existing.Sha256, incomingHash))
                        {
                            await index.TouchTokenAsync(token.Id, Time.NowIso(), cancellationToken);
                            return ServiceResult<UploadResponse>.Success(ToUploadResponse(existing));
                        }
                        return ServiceResult<UploadResponse>.Fail(409, ErrorCodes.IdempotencyConflict,
                            "该幂等键已用于不同内容");
                    }
                }
            }

            var (tempPath, stream) = store.CreateTemp();
            BlobEncryptResult encrypted;
            try
            {
                await using (stream)
                {
                    encrypted = await BlobFormat.EncryptStreamAsync(
                        request.Body, stream, keyRing, config.Crypto.ChunkSizeBytes, maxBytes, cancellationToken);
                    // 先真正落盘（fsync）再 rename，避免断电后留下「rename 成功、内容为空」的密文
                    stream.Flush(flushToDisk: true);
                }
            }
            catch
            {
                store.TryDeleteTemp(tempPath);
                throw;
            }

            if (encrypted.Size != request.ContentLength)
            {
                store.TryDeleteTemp(tempPath);
                return ServiceResult<UploadResponse>.Fail(400, ErrorCodes.IncompleteBody,
                    "请求体长度与 Content-Length 不一致");
            }

            // 幂等键按密钥记账：没有密钥身份时既不查也不写，避免落下永远查不回来的行。
            // 这个判断必须和上面的重放查询保持一致。
            var idempotencyKey = token is null || string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? null
                : request.IdempotencyKey;
            BlobRecord? blob = null;
            for (var attempt = 0; attempt < 3 && blob is null; attempt++)
            {
                var id = Ids.NewBlobId();
                string relativePath;
                try
                {
                    relativePath = store.Commit(tempPath, id);
                }
                catch (IOException)
                {
                    // 目标路径已存在（id 撞车）：换一个 id 重试，绝不覆盖已有密文
                    continue;
                }

                var candidate = new BlobRecord
                {
                    Id = id,
                    Name = request.FileName,
                    Size = encrypted.Size,
                    Sha256 = encrypted.Sha256,
                    ContentType = request.ContentType,
                    CipherPath = relativePath,
                    KeyVersion = encrypted.KeyVersion,
                    FileNonce = encrypted.FileNonce,
                    ChunkSize = encrypted.ChunkSize,
                    WrappedDek = encrypted.WrappedDek,
                    DekNonce = encrypted.DekNonce,
                    TokenId = ownerId,
                    Namespace = ns,
                    CreatedAt = Time.NowIso(),
                    ExpiresAt = Time.AddDaysIso(config.Retention.DefaultTtlDays),
                };

                try
                {
                    // blob 行与幂等键在同一事务里写入
                    await index.InsertBlobAsync(candidate, idempotencyKey, cancellationToken);
                    blob = candidate;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "写入元数据失败，退回密文以便换 id 重试：{Path}", relativePath);
                    // 密文已经 rename 到最终路径，temp 已被消耗：必须先把文件退回去，
                    // 下一轮才可能用新 id 重新提交；退不回去就删除并放弃。
                    if (!store.Rollback(relativePath, tempPath) || attempt == 2) throw;
                }
            }

            if (blob is null)
            {
                store.TryDeleteTemp(tempPath);
                return ServiceResult<UploadResponse>.Fail(500, ErrorCodes.InternalError, "服务内部错误");
            }

            stored = true;
            await index.InsertAuditAsync("blob.upload", ownerId, blob.Id, ns, request.Ip, blob.Size,
                request.FileName, cancellationToken);
            if (token is not null) await index.TouchTokenAsync(token.Id, Time.NowIso(), cancellationToken);

            return ServiceResult<UploadResponse>.Success(ToUploadResponse(blob));
        }
        finally
        {
            // 预占了但没落盘（重放、报错、提前返回）就还回去；成功路径由预占量充当实际用量。
            // reserved 只可能在「有密钥身份」时置位（管理端上传不预占配额），非空判断只是让编译器满意。
            if (reserved && !stored && token is not null)
            {
                try
                {
                    await index.AddTokenUsageAsync(token.Id, -request.ContentLength, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "回滚配额预占失败：{TokenId}", token.Id);
                }
            }
            _uploadSlots.Release();
        }
    }

    private async Task<bool> AcquireUploadSlotAsync(CancellationToken cancellationToken)
    {
        if (_uploadSlots.Wait(0)) return true;
        return await _uploadSlots.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    private static async Task<string> HashBodyAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            while (true)
            {
                var read = await body.ReadAsync(buffer, cancellationToken);
                if (read <= 0) break;
                total += read;
                if (total > maxBytes) throw new PayloadTooLargeException(maxBytes);
                hasher.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>只做元数据与权限判断，不打开密文文件。</summary>
    public async Task<ServiceResult<BlobRecord>> GetFileInfoAsync(
        string blobId, TokenRecord? token, bool isAdmin, CancellationToken cancellationToken)
    {
        if (!Ids.IsBlobId(blobId))
            return ServiceResult<BlobRecord>.Fail(404, ErrorCodes.NotFound, "文件不存在或已过期");
        var blob = await index.GetBlobAsync(blobId, cancellationToken);
        if (blob is null || Time.IsExpired(blob.ExpiresAt, DateTimeOffset.UtcNow))
            return ServiceResult<BlobRecord>.Fail(404, ErrorCodes.NotFound, "文件不存在或已过期");

        if (!isAdmin)
        {
            if (token is null || !token.CanRead)
                return ServiceResult<BlobRecord>.Fail(403, ErrorCodes.ScopeDenied, "该密钥无权下载文件");
            if (!string.Equals(token.Namespace, blob.Namespace, StringComparison.Ordinal))
                return ServiceResult<BlobRecord>.Fail(403, ErrorCodes.ScopeDenied, "该密钥无权访问该命名空间的文件");
        }
        return ServiceResult<BlobRecord>.Success(blob);
    }

    public async Task<ServiceResult<DownloadHandle>> OpenDownloadAsync(string blobId, TokenRecord? token, bool isAdmin, CancellationToken cancellationToken)
    {
        var info = await GetFileInfoAsync(blobId, token, isAdmin, cancellationToken);
        if (!info.Ok) return ServiceResult<DownloadHandle>.Fail(info.Failure!.Value);
        var blob = info.Value!;

        Stream stream;
        try
        {
            stream = store.OpenRead(blob.CipherPath);
        }
        catch (FileNotFoundException)
        {
            return ServiceResult<DownloadHandle>.Fail(404, ErrorCodes.NotFound, "文件不存在或已过期");
        }

        try
        {
            var file = BlobFile.Open(stream, keyRing);
            var cipherLength = stream.Length;
            // 数据库是权威：文件头反推出的尺寸/分块/密钥版本与密文总长必须与元数据完全一致，
            // 否则截断或格式漂移会一路走到响应中途才炸（那时状态码与 Content-Length 已经发出去了）。
            var expectedLength = BlobFormat.TotalFileSize(blob.Size, blob.ChunkSize);
            if (file.Size != blob.Size || file.ChunkSize != blob.ChunkSize ||
                file.KeyVersion != blob.KeyVersion || cipherLength != expectedLength)
            {
                logger.LogError(
                    "密文与元数据不一致：{BlobId} db(size={DbSize},chunk={DbChunk},kv={DbKv}) file(size={FileSize},chunk={FileChunk},kv={FileKv},len={Len})",
                    blobId, blob.Size, blob.ChunkSize, blob.KeyVersion, file.Size, file.ChunkSize, file.KeyVersion, cipherLength);
                file.Dispose();
                return ServiceResult<DownloadHandle>.Fail(500, ErrorCodes.IntegrityError, "文件数据校验失败");
            }
            return ServiceResult<DownloadHandle>.Success(new DownloadHandle(blob, file));
        }
        catch (Exception ex)
        {
            stream.Dispose();
            logger.LogError(ex, "打开密文文件失败：{BlobId}", blobId);
            return ServiceResult<DownloadHandle>.Fail(500, ErrorCodes.IntegrityError, "文件数据校验失败");
        }
    }

    public async Task RecordDownloadAsync(BlobRecord blob, string ip, CancellationToken cancellationToken)
    {
        try
        {
            await index.IncrementDownloadCountAsync(blob.Id, cancellationToken);
            await index.InsertAuditAsync("blob.download", blob.TokenId, blob.Id, blob.Namespace, ip, blob.Size, null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "记录下载审计失败：{BlobId}", blob.Id);
        }
    }

    public async Task<ServiceResult<bool>> DeleteAsync(
        string blobId, TokenRecord? token, bool isAdmin, string ip, CancellationToken cancellationToken)
    {
        if (!Ids.IsBlobId(blobId))
            return ServiceResult<bool>.Fail(404, ErrorCodes.NotFound, "文件不存在或已过期");
        var blob = await index.GetBlobAsync(blobId, cancellationToken);
        if (blob is null)
            return ServiceResult<bool>.Fail(404, ErrorCodes.NotFound, "文件不存在或已过期");

        if (!isAdmin)
        {
            if (token is null || !token.CanDelete)
                return ServiceResult<bool>.Fail(403, ErrorCodes.ScopeDenied, "该密钥无权删除文件");
            if (!string.Equals(token.Namespace, blob.Namespace, StringComparison.Ordinal))
                return ServiceResult<bool>.Fail(403, ErrorCodes.ScopeDenied, "该密钥无权访问该命名空间的文件");
        }

        await index.DeleteBlobAsync(blobId, cancellationToken);
        store.Delete(blob.CipherPath);
        await index.AddTokenUsageAsync(blob.TokenId, -blob.Size, cancellationToken);
        await index.InsertAuditAsync("blob.delete", token?.Id, blob.Id, blob.Namespace, ip, blob.Size, null, cancellationToken);
        return ServiceResult<bool>.Success(true);
    }

    // ---------------- 超管操作 ----------------

    public async Task<FileListResponse> ListFilesAsync(string? query, int page, int size, CancellationToken cancellationToken)
    {
        // 夹住页码：page 直接来自查询串，(page-1)*size 溢出会让 OFFSET 变成负数而静默返回第一页。
        page = Math.Clamp(page, 1, 1_000_000);
        size = Math.Clamp(size, 1, 200);
        var (total, items) = await index.ListBlobsAsync(query, page, size, cancellationToken);
        return new FileListResponse(total, page, size, items.Select(b => b.ToDto()).ToList());
    }

    public async Task<ServiceResult<FileInfoDto>> SetExpiryAsync(string blobId, int days, CancellationToken cancellationToken)
    {
        if (days is < 1 or > 3650)
            return ServiceResult<FileInfoDto>.Fail(400, ErrorCodes.BadRequest, "days 必须在 1 到 3650 之间");
        var blob = await index.GetBlobAsync(blobId, cancellationToken);
        if (blob is null)
            return ServiceResult<FileInfoDto>.Fail(404, ErrorCodes.NotFound, "文件不存在或已过期");
        var expiresAt = Time.AddDaysIso(days);
        await index.UpdateExpiryAsync(blobId, expiresAt, cancellationToken);
        blob.ExpiresAt = expiresAt;
        return ServiceResult<FileInfoDto>.Success(blob.ToDto());
    }

    public async Task<ServiceResult<FileInfoDto>> SetPinnedAsync(string blobId, bool pinned, CancellationToken cancellationToken)
    {
        var blob = await index.GetBlobAsync(blobId, cancellationToken);
        if (blob is null)
            return ServiceResult<FileInfoDto>.Fail(404, ErrorCodes.NotFound, "文件不存在或已过期");
        await index.SetPinnedAsync(blobId, pinned, cancellationToken);
        blob.Pinned = pinned;
        return ServiceResult<FileInfoDto>.Success(blob.ToDto());
    }

    public UploadResponse ToUploadResponse(BlobRecord blob) => new(
        blob.Id,
        config.PublicUrl($"f/{blob.Id}"),
        blob.Sha256,
        blob.Size,
        blob.ExpiresAt);
}
