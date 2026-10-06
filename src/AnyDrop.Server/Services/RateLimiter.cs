using System.Collections.Concurrent;

namespace AnyDrop.Server;

/// <summary>进程内的滑动窗口限流与登录失败锁定。单实例部署，重启即失效。</summary>
public sealed class RateLimiter(AppConfig config)
{
    private sealed class Window
    {
        public readonly object Gate = new();
        public readonly Queue<long> Ticks = new();
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.Ordinal);

    private sealed class Failure
    {
        public int Count;
        public long LockedUntilTicks;
    }

    /// <summary>key 通常形如 "upload:{tokenId}" 或 "ip:{ip}"。</summary>
    public bool IsAllowed(string key, int? perMinute = null)
    {
        var limit = perMinute ?? config.Security.RateLimitPerMinute;
        if (limit <= 0) return true;
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var window = _windows.GetOrAdd(key, _ => new Window());
        lock (window.Gate)
        {
            var cutoff = now - TimeSpan.TicksPerMinute;
            while (window.Ticks.Count > 0 && window.Ticks.Peek() < cutoff) window.Ticks.Dequeue();
            if (window.Ticks.Count >= limit) return false;
            window.Ticks.Enqueue(now);
            return true;
        }
    }

    public void RecordFailure(string key)
    {
        var failure = _failures.GetOrAdd(key, _ => new Failure());
        lock (failure)
        {
            failure.Count++;
            if (failure.Count >= config.Security.MaxLoginFailures)
            {
                failure.LockedUntilTicks = DateTimeOffset.UtcNow.AddMinutes(config.Security.LockoutMinutes).UtcTicks;
                failure.Count = 0;
            }
        }
    }

    public bool IsLockedOut(string key, out int minutesLeft)
    {
        minutesLeft = 0;
        if (!_failures.TryGetValue(key, out var failure)) return false;
        lock (failure)
        {
            var now = DateTimeOffset.UtcNow.UtcTicks;
            if (failure.LockedUntilTicks <= now) return false;
            minutesLeft = (int)Math.Ceiling((failure.LockedUntilTicks - now) / (double)TimeSpan.TicksPerMinute);
            return true;
        }
    }

    public void ResetFailures(string key) => _failures.TryRemove(key, out _);
}
