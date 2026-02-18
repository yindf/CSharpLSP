using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 调用图分析器接口
/// </summary>
public interface ICallGraphAnalyzer
{
    /// <summary>
    /// 获取方法的调用者
    /// </summary>
    Task<IReadOnlyList<IMethodSymbol>> GetCallersAsync(
        IMethodSymbol method,
        Solution solution,
        int maxDepth,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取方法调用的其他方法
    /// </summary>
    Task<IReadOnlyList<IMethodSymbol>> GetCalleesAsync(
        IMethodSymbol method,
        Document document,
        int maxDepth,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 计算方法的圈复杂度
    /// </summary>
    Task<int> CalculateCyclomaticComplexityAsync(
        IMethodSymbol method,
        Document document,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取完整调用图
    /// </summary>
    Task<CallGraphResult> GetCallGraphAsync(
        IMethodSymbol method,
        Solution solution,
        CallGraphDirection direction,
        int maxDepth,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 调用图方向
/// </summary>
public enum CallGraphDirection
{
    /// <summary>
    /// 调用者和被调用者
    /// </summary>
    Both,

    /// <summary>
    /// 仅调用者 (incoming calls)
    /// </summary>
    In,

    /// <summary>
    /// 仅被调用者 (outgoing calls)
    /// </summary>
    Out
}

/// <summary>
/// 调用图结果
/// </summary>
public record CallGraphResult(
    string MethodName,
    IReadOnlyList<CallRelationship> Callers,
    IReadOnlyList<CallRelationship> Callees,
    CallStatistics Statistics
);

/// <summary>
/// 调用关系
/// </summary>
public record CallRelationship(
    Models.SymbolInfo Symbol,
    IReadOnlyList<CallLocation> CallLocations
);

/// <summary>
/// 调用位置
/// </summary>
public record CallLocation(
    string ContainingSymbol,
    Models.SymbolLocation Location
);

/// <summary>
/// 调用统计
/// </summary>
public record CallStatistics(
    int TotalCallers,
    int TotalCallees,
    int CyclomaticComplexity
);
