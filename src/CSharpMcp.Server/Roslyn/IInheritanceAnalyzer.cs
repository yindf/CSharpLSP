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
}

/// <summary>
/// 继承树
/// </summary>
public record InheritanceTree(
    IReadOnlyList<INamedTypeSymbol> BaseTypes,
    IReadOnlyList<INamedTypeSymbol> Interfaces,
    IReadOnlyList<INamedTypeSymbol> DerivedTypes,
    int Depth
);
