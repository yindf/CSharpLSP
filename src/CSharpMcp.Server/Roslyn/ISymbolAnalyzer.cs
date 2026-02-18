using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using CSharpMcp.Server.Models;
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

    /// <summary>
    /// 获取符号的定义位置
    /// </summary>
    Task<SymbolLocation?> GetSymbolLocationAsync(
        ISymbol symbol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 转换为 SymbolInfo 模型
    /// </summary>
    Task<Models.SymbolInfo> ToSymbolInfoAsync(
        ISymbol symbol,
        DetailLevel detailLevel = DetailLevel.Summary,
        int? bodyMaxLines = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取符号的文档注释
    /// </summary>
    Task<string?> GetDocumentationCommentAsync(
        ISymbol symbol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 提取符号的源代码
    /// </summary>
    Task<string?> ExtractSourceCodeAsync(
        ISymbol symbol,
        bool includeBody,
        int? maxLines,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 提取完整的方法声明（包括 attributes、注释、签名、body）
    /// </summary>
    Task<FullMethodDeclaration?> ExtractFullMethodDeclarationAsync(
        ISymbol symbol,
        int? maxLines,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 完整的方法声明信息
/// </summary>
public record FullMethodDeclaration(
    string FullMethodText,           // 完整的方法文本（包括所有内容）
    IReadOnlyList<string> Attributes,  // 属性列表
    string? Documentation,            // 文档注释
    string Signature,                 // 方法签名
    string Body                       // 方法 body
);
