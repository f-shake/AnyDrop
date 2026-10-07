using Microsoft.Data.Sqlite;

namespace AnyDrop.Server;

/// <summary>元数据存储。只负责 SQL；不碰文件内容，不做加密。</summary>
public sealed class SqliteIndex
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public SqliteIndex(AppConfig config)
    {
        _dbPath = config.DbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            DefaultTimeout = 30,
            Pooling = true,
        }.ToString();
    }

    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS tokens (
          id TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          key_hash TEXT NOT NULL,
          key_prefix TEXT NOT NULL,
          max_file_bytes INTEGER NOT NULL,
          quota_bytes INTEGER NOT NULL,
          used_bytes INTEGER NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL,
          expires_at TEXT NULL,
          revoked_at TEXT NULL,
          last_used_at TEXT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_tokens_hash ON tokens(key_hash)",
        """
        CREATE TABLE IF NOT EXISTS blobs (
          id TEXT PRIMARY KEY,
          name TEXT NULL,
          size INTEGER NOT NULL,
          sha256 TEXT NOT NULL,
          content_type TEXT NULL,
          cipher_path TEXT NOT NULL,
          key_version INTEGER NOT NULL,
          file_nonce BLOB NOT NULL,
          chunk_size INTEGER NOT NULL,
          wrapped_dek BLOB NOT NULL,
          dek_nonce BLOB NOT NULL,
          token_id TEXT NULL,
          created_at TEXT NOT NULL,
          expires_at TEXT NOT NULL,
          download_count INTEGER NOT NULL DEFAULT 0,
          pinned INTEGER NOT NULL DEFAULT 0
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_blobs_expires ON blobs(expires_at)",
        "CREATE INDEX IF NOT EXISTS idx_blobs_token ON blobs(token_id)",
        """
        CREATE TABLE IF NOT EXISTS idempotency (
          token_id TEXT NOT NULL,
          key TEXT NOT NULL,
          blob_id TEXT NOT NULL,
          created_at TEXT NOT NULL,
          PRIMARY KEY (token_id, key)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS audit_log (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          ts TEXT NOT NULL,
          action TEXT NOT NULL,
          token_id TEXT NULL,
          blob_id TEXT NULL,
          ip TEXT NULL,
          bytes INTEGER NULL,
          detail TEXT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_audit_ts ON audit_log(ts)",
        """
        CREATE TABLE IF NOT EXISTS admin (
          id INTEGER PRIMARY KEY CHECK (id = 1),
          password_hash TEXT NOT NULL,
          salt TEXT NOT NULL,
          iterations INTEGER NOT NULL,
          created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL
        )
        """,
    ];

    /// <summary>
    /// 结构版本。AnyDrop 不提供自动迁移：读到不匹配的旧库一律拒绝启动。
    /// 否则 CREATE TABLE IF NOT EXISTS 会把旧列原样留在表里，问题要拖到写入时才以一个
    /// 费解的约束错误暴露出来。
    /// </summary>
    public const int SchemaVersion = 2;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecAsync(connection, "PRAGMA journal_mode=WAL", cancellationToken);
        await ExecAsync(connection, "PRAGMA busy_timeout=10000", cancellationToken);
        await EnsureSchemaVersionAsync(connection, cancellationToken);
        foreach (var ddl in Ddl) await ExecAsync(connection, ddl, cancellationToken);
        await ExecAsync(connection, $"PRAGMA user_version = {SchemaVersion}", cancellationToken);
    }

    private async Task EnsureSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version", cancellationToken) ?? 0);
        if (version == SchemaVersion) return;
        if (version == 0)
        {
            // user_version 为 0 有两种可能：全新的空库，或 v1 时代建的库（那时不写版本号）。
            // 用「是否已经有 AnyDrop 自己的表」把两者分开，空的才放行。
            var tables = Convert.ToInt32(await ScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' " +
                "AND name IN ('tokens','blobs','idempotency','audit_log','admin')",
                cancellationToken) ?? 0);
            if (tables == 0) return;
        }
        // user_version 为 0 有两种可能：v1 时代建的旧库（那时不写版本号），
        // 或上次初始化在写完 DDL、写版本号之前就中断了。两者都只能删库重建，
        // 所以文案要把两种可能都说出来，别硬说成 v1。
        var found = version == 0 ? "v1 旧库（也可能是上次初始化没跑完的库）" : $"v{version}";
        throw new InvalidOperationException(
            $"数据库结构版本不匹配：{_dbPath} 是 {found}，当前程序需要 v{SchemaVersion}。" +
            "AnyDrop 不做自动迁移，请停止服务后删除该 data 目录（或把 storage:dataDir 指到新路径）再启动。");
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    // ---------------- tokens ----------------

    public async Task InsertTokenAsync(TokenRecord token, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            """
            INSERT INTO tokens (id, name, key_hash, key_prefix, max_file_bytes, quota_bytes, used_bytes,
                                created_at, expires_at, revoked_at, last_used_at)
            VALUES ($id, $name, $hash, $prefix, $max, $quota, $used, $created, $expires, NULL, NULL)
            """,
            ("$id", token.Id), ("$name", token.Name), ("$hash", token.KeyHash), ("$prefix", token.KeyPrefix),
            ("$max", token.MaxFileBytes), ("$quota", token.QuotaBytes),
            ("$used", token.UsedBytes), ("$created", token.CreatedAt), ("$expires", token.ExpiresAt));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TokenRecord?> FindTokenByHashAsync(string keyHash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "SELECT * FROM tokens WHERE key_hash = $hash LIMIT 1", ("$hash", keyHash));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadToken(reader) : null;
    }

    public async Task<TokenRecord?> GetTokenAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "SELECT * FROM tokens WHERE id = $id", ("$id", id));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadToken(reader) : null;
    }

    public async Task<List<TokenRecord>> ListTokensAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "SELECT * FROM tokens ORDER BY created_at DESC");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<TokenRecord>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadToken(reader));
        return items;
    }

    public async Task<bool> RevokeTokenAsync(string id, string revokedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "UPDATE tokens SET revoked_at = $at WHERE id = $id AND revoked_at IS NULL",
            ("$at", revokedAt), ("$id", id));
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task TouchTokenAsync(string id, string usedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "UPDATE tokens SET last_used_at = $at WHERE id = $id",
            ("$at", usedAt), ("$id", id));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddTokenUsageAsync(string id, long deltaBytes, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "UPDATE tokens SET used_bytes = MAX(0, used_bytes + $delta) WHERE id = $id",
            ("$delta", deltaBytes), ("$id", id));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 原子配额预占：只有「已用 + 本次 ≤ 配额」时才加。并发上传各自调用本方法，
    /// 由 SQLite 的单条 UPDATE 保证不会都通过，从而不会突破配额。
    /// </summary>
    public async Task<bool> TryReserveQuotaAsync(string tokenId, long bytes, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "UPDATE tokens SET used_bytes = used_bytes + $bytes WHERE id = $id AND used_bytes + $bytes <= quota_bytes",
            ("$bytes", bytes), ("$id", tokenId));
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task RecomputeTokenUsageAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "UPDATE tokens SET used_bytes = COALESCE((SELECT SUM(size) FROM blobs WHERE token_id = $id), 0) WHERE id = $id",
            ("$id", id));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TokenRecord ReadToken(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        KeyHash = r.GetString(r.GetOrdinal("key_hash")),
        KeyPrefix = r.GetString(r.GetOrdinal("key_prefix")),
        MaxFileBytes = r.GetInt64(r.GetOrdinal("max_file_bytes")),
        QuotaBytes = r.GetInt64(r.GetOrdinal("quota_bytes")),
        UsedBytes = r.GetInt64(r.GetOrdinal("used_bytes")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        ExpiresAt = Text(r, "expires_at"),
        RevokedAt = Text(r, "revoked_at"),
        LastUsedAt = Text(r, "last_used_at"),
    };

    // ---------------- blobs ----------------

    /// <summary>blob 行与幂等键在同一事务里写入，避免「文件已落库、幂等键没记上」的半截状态。</summary>
    public async Task InsertBlobAsync(BlobRecord blob, string? idempotencyKey = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using (var cmd = Command(connection,
            """
            INSERT INTO blobs (id, name, size, sha256, content_type, cipher_path, key_version, file_nonce,
                               chunk_size, wrapped_dek, dek_nonce, token_id, created_at, expires_at,
                               download_count, pinned)
            VALUES ($id, $name, $size, $sha, $ctype, $path, $kv, $fnonce, $chunk, $wdek, $dnonce, $token,
                    $created, $expires, $downloads, $pinned)
            """,
            ("$id", blob.Id), ("$name", blob.Name), ("$size", blob.Size), ("$sha", blob.Sha256),
            ("$ctype", blob.ContentType), ("$path", blob.CipherPath), ("$kv", blob.KeyVersion),
            ("$fnonce", blob.FileNonce), ("$chunk", blob.ChunkSize), ("$wdek", blob.WrappedDek),
            ("$dnonce", blob.DekNonce), ("$token", blob.TokenId),
            ("$created", blob.CreatedAt), ("$expires", blob.ExpiresAt),
            ("$downloads", blob.DownloadCount), ("$pinned", blob.Pinned ? 1 : 0)))
        {
            cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            await using var cmd = Command(connection,
                "INSERT OR IGNORE INTO idempotency (token_id, key, blob_id, created_at) VALUES ($t, $k, $b, $at)",
                ("$t", blob.TokenId), ("$k", idempotencyKey), ("$b", blob.Id), ("$at", Time.NowIso()));
            cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
    }

    public async Task<BlobRecord?> GetBlobAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "SELECT * FROM blobs WHERE id = $id", ("$id", id));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBlob(reader) : null;
    }

    public async Task<(int Total, List<BlobRecord> Items)> ListBlobsAsync(
        string? query, int page, int size, CancellationToken cancellationToken = default)
    {
        var filtered = !string.IsNullOrWhiteSpace(query);
        var where = filtered ? " WHERE name LIKE $q OR id LIKE $q" : "";
        var pattern = $"%{query}%";
        await using var connection = await OpenAsync(cancellationToken);
        int total;
        await using (var countCmd = filtered
            ? Command(connection, "SELECT COUNT(*) FROM blobs" + where, ("$q", pattern))
            : Command(connection, "SELECT COUNT(*) FROM blobs"))
        {
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);
        }
        await using var cmd = filtered
            ? Command(connection,
                "SELECT * FROM blobs" + where + " ORDER BY created_at DESC LIMIT $limit OFFSET $offset",
                ("$q", pattern), ("$limit", size), ("$offset", (long)(page - 1) * size))
            : Command(connection,
                "SELECT * FROM blobs ORDER BY created_at DESC LIMIT $limit OFFSET $offset",
                ("$limit", size), ("$offset", (long)(page - 1) * size));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<BlobRecord>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadBlob(reader));
        return (total, items);
    }

    public async Task<bool> DeleteBlobAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        int affected;
        await using (var cmd = Command(connection, "DELETE FROM blobs WHERE id = $id", ("$id", id)))
        {
            cmd.Transaction = transaction;
            affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        if (affected > 0)
        {
            await using var cmd = Command(connection, "DELETE FROM idempotency WHERE blob_id = $id", ("$id", id));
            cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
        return affected > 0;
    }

    /// <summary>GC 专用：只删仍未被 pin 的行。pin 与回收并发时，这里删不到行就不该删文件。</summary>
    public async Task<bool> DeleteExpiredBlobAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        int affected;
        await using (var cmd = Command(connection, "DELETE FROM blobs WHERE id = $id AND pinned = 0", ("$id", id)))
        {
            cmd.Transaction = transaction;
            affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        if (affected > 0)
        {
            await using var cmd = Command(connection, "DELETE FROM idempotency WHERE blob_id = $id", ("$id", id));
            cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
        return affected > 0;
    }

    public async Task IncrementDownloadCountAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "UPDATE blobs SET download_count = download_count + 1 WHERE id = $id", ("$id", id));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpdateExpiryAsync(string id, string expiresAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "UPDATE blobs SET expires_at = $at WHERE id = $id", ("$at", expiresAt), ("$id", id));
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> SetPinnedAsync(string id, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "UPDATE blobs SET pinned = $p WHERE id = $id",
            ("$p", pinned ? 1 : 0), ("$id", id));
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<long> CountBlobsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "SELECT COUNT(*) FROM blobs");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task<List<BlobRecord>> ListExpiredAsync(string nowIso, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "SELECT * FROM blobs WHERE pinned = 0 AND expires_at <= $now ORDER BY expires_at LIMIT $limit",
            ("$now", nowIso), ("$limit", limit));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<BlobRecord>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadBlob(reader));
        return items;
    }

    public async Task<List<BlobRecord>> ListAllBlobsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "SELECT * FROM blobs");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<BlobRecord>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadBlob(reader));
        return items;
    }

    private static BlobRecord ReadBlob(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = Text(r, "name"),
        Size = r.GetInt64(r.GetOrdinal("size")),
        Sha256 = r.GetString(r.GetOrdinal("sha256")),
        ContentType = Text(r, "content_type"),
        CipherPath = r.GetString(r.GetOrdinal("cipher_path")),
        KeyVersion = r.GetInt32(r.GetOrdinal("key_version")),
        FileNonce = Blob(r, "file_nonce"),
        ChunkSize = r.GetInt32(r.GetOrdinal("chunk_size")),
        WrappedDek = Blob(r, "wrapped_dek"),
        DekNonce = Blob(r, "dek_nonce"),
        TokenId = Text(r, "token_id"),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        ExpiresAt = r.GetString(r.GetOrdinal("expires_at")),
        DownloadCount = r.GetInt32(r.GetOrdinal("download_count")),
        Pinned = r.GetInt64(r.GetOrdinal("pinned")) != 0,
    };

    // ---------------- idempotency ----------------

    public async Task<string?> GetIdempotentBlobIdAsync(string tokenId, string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "SELECT blob_id FROM idempotency WHERE token_id = $t AND key = $k",
            ("$t", tokenId), ("$k", key));
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task InsertIdempotencyAsync(string tokenId, string key, string blobId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "INSERT OR IGNORE INTO idempotency (token_id, key, blob_id, created_at) VALUES ($t, $k, $b, $at)",
            ("$t", tokenId), ("$k", key), ("$b", blobId), ("$at", Time.NowIso()));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteIdempotencyAsync(string tokenId, string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "DELETE FROM idempotency WHERE token_id = $t AND key = $k", ("$t", tokenId), ("$k", key));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>清掉指向已不存在 blob 的幂等行（blob 被删/过期回收后，同一幂等键应能重新上传）。</summary>
    public async Task<int> PurgeDanglingIdempotencyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            "DELETE FROM idempotency WHERE blob_id NOT IN (SELECT id FROM blobs)");
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>给定一批密文相对路径，返回其中「此刻仍被数据库引用」的那些（孤儿回收的两阶段复核）。</summary>
    public async Task<HashSet<string>> FilterExistingPathsAsync(
        IReadOnlyCollection<string> relativePaths, CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (relativePaths.Count == 0) return result;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var path in relativePaths)
        {
            await using var cmd = Command(connection, "SELECT 1 FROM blobs WHERE cipher_path = $p LIMIT 1", ("$p", path));
            if (await cmd.ExecuteScalarAsync(cancellationToken) is not null) result.Add(path);
        }
        return result;
    }

    // ---------------- audit ----------------

    public async Task InsertAuditAsync(
        string action, string? tokenId, string? blobId, string? ip, long? bytes, string? detail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            """
            INSERT INTO audit_log (ts, action, token_id, blob_id, ip, bytes, detail)
            VALUES ($ts, $action, $token, $blob, $ip, $bytes, $detail)
            """,
            ("$ts", Time.NowIso()), ("$action", action), ("$token", tokenId), ("$blob", blobId),
            ("$ip", ip), ("$bytes", bytes), ("$detail", detail));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<AuditDto>> ListAuditAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "SELECT * FROM audit_log ORDER BY id DESC LIMIT $limit", ("$limit", limit));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var items = new List<AuditDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AuditDto(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("ts")),
                reader.GetString(reader.GetOrdinal("action")),
                Text(reader, "token_id"),
                Text(reader, "blob_id"),
                Text(reader, "ip"),
                reader.IsDBNull(reader.GetOrdinal("bytes")) ? null : reader.GetInt64(reader.GetOrdinal("bytes")),
                Text(reader, "detail")));
        }
        return items;
    }

    // ---------------- admin ----------------

    public async Task<(string Hash, string Salt, int Iterations)?> GetAdminAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection, "SELECT password_hash, salt, iterations FROM admin WHERE id = 1");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
    }

    public async Task SetAdminPasswordAsync(string hash, string salt, int iterations, CancellationToken cancellationToken = default)
    {
        var now = Time.NowIso();
        await using var connection = await OpenAsync(cancellationToken);
        await using var cmd = Command(connection,
            """
            INSERT INTO admin (id, password_hash, salt, iterations, created_at, updated_at)
            VALUES (1, $hash, $salt, $iter, $now, $now)
            ON CONFLICT(id) DO UPDATE SET password_hash = $hash, salt = $salt, iterations = $iter, updated_at = $now
            """,
            ("$hash", hash), ("$salt", salt), ("$iter", iterations), ("$now", now));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Text(SqliteDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static byte[] Blob(SqliteDataReader r, string column) => (byte[])r.GetValue(r.GetOrdinal(column));
}
