using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace CSharpMcp.Server.Cache;

/// <summary>
/// 内存编译缓存实现
/// </summary>
internal sealed class CompilationCache : ICompilationCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<Compilation?>>> _cache = new();
    private readonly ConcurrentDictionary<string, DateTime> _accessTimes = new();
    private long _hitCount;
    private long _missCount;

    public Task<Compilation?> GetOrAddAsync(
        string key,
        Func<Task<Compilation?>> factory,
        CancellationToken cancellationToken = default)
    {
        var lazy = _cache.GetOrAdd(key, k => new Lazy<Task<Compilation?>>(() =>
        {
            _accessTimes.TryAdd(k, DateTime.UtcNow);
            return factory();
        }));

        if (lazy.IsValueCreated)
        {
            Interlocked.Increment(ref _hitCount);
        }
        else
        {
            // Check if value was created by another thread
            try
            {
                _ = lazy.Value;
                Interlocked.Increment(ref _hitCount);
            }
            catch
            {
                Interlocked.Increment(ref _missCount);
                throw;
            }
        }

        return lazy.Value;
    }

    public void Invalidate(string key)
    {
        _cache.TryRemove(key, out _);
        _accessTimes.TryRemove(key, out _);
    }

    public void Clear()
    {
        _cache.Clear();
        _accessTimes.Clear();
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _missCount, 0);
    }

    public CacheStatistics GetStatistics()
    {
        var hitCount = (long)Interlocked.CompareExchange(ref _hitCount, 0, 0);
        var missCount = (long)Interlocked.CompareExchange(ref _missCount, 0, 0);
        var total = hitCount + missCount;
        var hitRate = total > 0 ? (double)hitCount / total : 0;

        return new CacheStatistics(
            (int)hitCount,
            (int)missCount,
            _cache.Count,
            hitRate
        );
    }
}

/// <summary>
/// 内存符号缓存实现
/// </summary>
internal sealed class SymbolCache : ISymbolCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _cache = new();
    private readonly ConcurrentDictionary<string, DateTime> _accessTimes = new();
    private long _hitCount;
    private long _missCount;

    public async Task<T?> GetOrAddAsync<T>(
        string key,
        Func<Task<T>> factory,
        CancellationToken cancellationToken = default) where T : class
    {
        var lazy = _cache.GetOrAdd(key, k => new Lazy<Task<object?>>(async () =>
        {
            _accessTimes.TryAdd(k, DateTime.UtcNow);
            return await factory();
        }));

        try
        {
            var value = await lazy.Value;
            if (value != null)
            {
                Interlocked.Increment(ref _hitCount);
                return (T)value;
            }

            Interlocked.Increment(ref _missCount);
            return null;
        }
        catch
        {
            Interlocked.Increment(ref _missCount);
            throw;
        }
    }

    public void Invalidate(string key)
    {
        _cache.TryRemove(key, out _);
        _accessTimes.TryRemove(key, out _);
    }

    public void Clear()
    {
        _cache.Clear();
        _accessTimes.Clear();
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _missCount, 0);
    }

    public CacheStatistics GetStatistics()
    {
        var hitCount = (long)Interlocked.CompareExchange(ref _hitCount, 0, 0);
        var missCount = (long)Interlocked.CompareExchange(ref _missCount, 0, 0);
        var total = hitCount + missCount;
        var hitRate = total > 0 ? (double)hitCount / total : 0;

        return new CacheStatistics(
            (int)hitCount,
            (int)missCount,
            _cache.Count,
            hitRate
        );
    }
}

/// <summary>
/// 缓存工厂
/// </summary>
internal static class CacheFactory
{
    public static ICompilationCache CreateCompilationCache() => new CompilationCache();
    public static ISymbolCache CreateSymbolCache() => new SymbolCache();
}
