using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 继承分析器接口
/// </summary>
public interface IInheritanceAnalyzer
{
    /// <summary>
    /// 获取类型的继承层次结构
    /// </summary>
    Task<InheritanceTree> GetInheritanceTreeAsync(
        INamedTypeSymbol type,
        Solution solution,
        bool includeDerived,
        int maxDerivedDepth,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查找所有派生类型
    /// </summary>
    Task<IReadOnlyList<INamedTypeSymbol>> FindDerivedTypesAsync(
        INamedTypeSymbol type,
        Solution solution,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取继承链（基类链）
    /// </summary>
    IReadOnlyList<INamedTypeSymbol> GetBaseTypeChain(INamedTypeSymbol type);
}

/// <summary>
/// 继承树
/// </summary>
public record InheritanceTree(
    IReadOnlyList<Models.SymbolInfo> BaseTypes,
    IReadOnlyList<Models.SymbolInfo> Interfaces,
    IReadOnlyList<Models.SymbolInfo> DerivedTypes,
    int Depth
);
