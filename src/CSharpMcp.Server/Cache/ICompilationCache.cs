using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace CSharpMcp.Server.Cache;

/// <summary>
/// 缓存统计信息
/// </summary>
public record CacheStatistics(
    int HitCount,
    int MissCount,
    int TotalItems,
    double HitRate
);

/// <summary>
/// 编译缓存接口
/// </summary>
public interface ICompilationCache
{
    /// <summary>
    /// 获取或添加编译
    /// </summary>
    Task<Compilation?> GetOrAddAsync(
        string key,
        Func<Task<Compilation?>> factory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 使指定键无效
    /// </summary>
    void Invalidate(string key);

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    void Clear();

    /// <summary>
    /// 获取缓存统计
    /// </summary>
    CacheStatistics GetStatistics();
}

/// <summary>
/// 符号缓存接口
/// </summary>
public interface ISymbolCache
{
    /// <summary>
    /// 获取或添加符号
    /// </summary>
    Task<T?> GetOrAddAsync<T>(
        string key,
        Func<Task<T>> factory,
        CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// 使指定键无效
    /// </summary>
    void Invalidate(string key);

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    void Clear();

    /// <summary>
    /// 获取缓存统计
    /// </summary>
    CacheStatistics GetStatistics();
}
