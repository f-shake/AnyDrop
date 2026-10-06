using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AnyDrop.Server;

/// <summary>密文文件布局与原子写入。只和文件系统打交道，不加密、不碰元数据。</summary>
public sealed class BlobStore
{
    private readonly string _dataDir;
    private readonly string _blobsDir;
    private readonly string _tempDir;
    private readonly ILogger<BlobStore> _logger;

    /// <summary>进行中的临时文件：清理分片时必须跳过，避免删掉正在写入的上传。</summary>
    private readonly ConcurrentDictionary<string, byte> _activeParts = new(StringComparer.Ordinal);

    public BlobStore(AppConfig config, ILogger<BlobStore> logger)
    {
        _dataDir = config.DataDir;
        _blobsDir = config.BlobsDir;
        _tempDir = config.TempDir;
        _logger = logger;
        Directory.CreateDirectory(_blobsDir);
        Directory.CreateDirectory(_tempDir);
    }

    public string DataDir => _dataDir;

    public static string RelativePath(string id) => $"blobs/{id[..2]}/{id}";

    public string Resolve(string relativePath) =>
        Path.Combine(_dataDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>创建临时文件（写入过程中不落最终路径，避免读到半截文件）。</summary>
    public (string TempPath, FileStream Stream) CreateTemp()
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".part");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.Asynchronous);
        _activeParts[path] = 0;
        return (path, stream);
    }

    /// <summary>
    /// 把临时文件原子地移动到最终路径，返回相对路径。
    /// **不覆盖**已存在的文件：id 撞车时抛 IOException，由调用方换 id 重试，绝不破坏已有密文。
    /// </summary>
    public string Commit(string tempPath, string id)
    {
        var relative = RelativePath(id);
        var target = Resolve(relative);
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try
        {
            File.Move(tempPath, target, overwrite: false);
        }
        finally
        {
            _activeParts.TryRemove(tempPath, out _);
        }
        return relative;
    }

    /// <summary>
    /// 把已提交的密文退回临时路径，供调用方换 id 重试（元数据写入失败时用）。
    /// 退不回去就删除该文件，返回 false。始终不碰任何其它密文。
    /// </summary>
    public bool Rollback(string relativePath, string tempPath)
    {
        try
        {
            File.Move(Resolve(relativePath), tempPath, overwrite: false);
            _activeParts[tempPath] = 0;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回退已提交的密文失败，改为删除：{Path}", relativePath);
            Delete(relativePath);
            return false;
        }
    }

    public FileStream OpenRead(string relativePath) =>
        new(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public bool Delete(string relativePath)
    {
        try
        {
            var path = Resolve(relativePath);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "删除密文文件失败，留待下一轮 GC：{Path}", relativePath);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "删除密文文件被拒绝，留待下一轮 GC：{Path}", relativePath);
            return false;
        }
    }

    public void TryDeleteTemp(string tempPath)
    {
        _activeParts.TryRemove(tempPath, out _);
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理临时文件失败：{Path}", tempPath);
        }
    }

    /// <summary>返回 (剩余字节, 已用百分比)；无法获取时返回 (long.MaxValue, 0) 表示不做水位限制。</summary>
    public (long FreeBytes, int UsedPercent) DiskStatus()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_dataDir));
            if (string.IsNullOrEmpty(root)) return (long.MaxValue, 0);
            var drive = new DriveInfo(root);
            if (drive.TotalSize <= 0) return (long.MaxValue, 0);
            var usedPercent = (int)((drive.TotalSize - drive.TotalFreeSpace) * 100 / drive.TotalSize);
            return (drive.TotalFreeSpace, usedPercent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取磁盘容量失败，本轮不启用水位限制");
            return (long.MaxValue, 0);
        }
    }

    public bool IsDiskLow(long minFreeBytes, int maxUsedPercent)
    {
        var (free, used) = DiskStatus();
        return free < minFreeBytes || used > maxUsedPercent;
    }

    /// <summary>清理过期的 .part 残留（默认 1 小时前的）。进行中的上传会被跳过。</summary>
    public int CleanStaleParts(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_tempDir, "*.part"))
        {
            if (_activeParts.ContainsKey(file)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
                // 正在写入，下一轮再说
            }
        }
        return removed;
    }

    /// <summary>
    /// 列出「不在已知集合里、且超过宽限期」的孤儿候选（只列不删）。
    /// 删除前调用方必须再向数据库复核一次「此刻是否仍被引用」，否则会误删刚落盘的新文件。
    /// </summary>
    public List<string> FindOrphanCandidates(IReadOnlyCollection<string> knownRelativePaths, TimeSpan olderThan)
    {
        var known = knownRelativePaths as HashSet<string>
            ?? new HashSet<string>(knownRelativePaths, StringComparer.Ordinal);
        var cutoff = DateTime.UtcNow - olderThan;
        var candidates = new List<string>();
        if (!Directory.Exists(_blobsDir)) return candidates;
        foreach (var file in Directory.EnumerateFiles(_blobsDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_dataDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (known.Contains(relative)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
            }
            catch (IOException)
            {
                continue;
            }
            candidates.Add(relative);
        }
        return candidates;
    }

    public int DeleteFiles(IEnumerable<string> relativePaths)
    {
        var removed = 0;
        foreach (var relative in relativePaths)
        {
            if (Delete(relative))
            {
                removed++;
                _logger.LogInformation("回收孤儿密文文件：{Path}", relative);
            }
        }
        return removed;
    }
}
