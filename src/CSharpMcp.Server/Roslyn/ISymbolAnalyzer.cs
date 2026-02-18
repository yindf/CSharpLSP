using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynReferencedSymbol = Microsoft.CodeAnalysis.FindSymbols.ReferencedSymbol;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 符号分析服务接口
/// </summary>
public interface ISymbolAnalyzer
{
    /// <summary>
    /// 获取文档中的所有符号
    /// </summary>
    Task<IReadOnlyList<ISymbol>> GetDocumentSymbolsAsync(
        Document document,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在位置处解析符号
    /// </summary>
    Task<ISymbol?> ResolveSymbolAtPositionAsync(
        Document document,
        int lineNumber,
        int column,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按名称查找符号（支持模糊匹配）
    /// </summary>
    Task<IReadOnlyList<ISymbol>> FindSymbolsByNameAsync(
        Document document,
        string symbolName,
        int? approximateLineNumber = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查找符号的所有引用
    /// </summary>
    Task<IReadOnlyList<RoslynReferencedSymbol>> FindReferencesAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken = default);
}
