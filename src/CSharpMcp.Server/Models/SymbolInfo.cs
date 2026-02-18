using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace CSharpMcp.Server.Models;

/// <summary>
/// 可访问性
/// </summary>
public enum Accessibility
{
    Public,
    Internal,
    Protected,
    ProtectedInternal,
    PrivateProtected,
    Private,
    NotApplicable
}

/// <summary>
/// 符号签名信息
/// </summary>
public record SymbolSignature(
    string Name,
    string DisplayName,
    string? ReturnType,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string> TypeParameters
);

/// <summary>
/// 符号引用信息
/// </summary>
public record SymbolReference(
    SymbolLocation Location,
    string ContainingSymbol,
    string? ContextCode,
    string? LineText
);

/// <summary>
/// 核心符号信息模型
/// </summary>
public record SymbolInfo
{


    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public required SymbolLocation Location { get; init; }
    public required string ContainingType { get; init; }
    public required string Namespace { get; init; }

    // 可选详细信息
    public SymbolSignature? Signature { get; init; }
    public string? Documentation { get; init; }
    public string? SourceCode { get; init; }
    public string? LineText { get; init; }

    /// <summary>
    /// 完整的方法声明（包括 attributes、签名、注释）
    /// 用于详细输出时显示完整方法
    /// </summary>
    public string? FullDeclaration { get; init; }

    /// <summary>
    /// 方法上的属性列表
    /// </summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    // 关系信息
    public IReadOnlyList<SymbolReference> References { get; init; } = [];
    public IReadOnlyList<SymbolInfo> RelatedSymbols { get; init; } = [];

    // 类型继承信息
    public string? BaseType { get; init; }
    public IReadOnlyList<string> Interfaces { get; init; } = [];

    // 元数据
    public bool IsStatic { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsAsync { get; init; }
    public Accessibility Accessibility { get; init; } = Accessibility.NotApplicable;

    /// <summary>
    /// Test field added for verification - should be found by csharp-ls search
    /// </summary>
    public string TestFieldForSearch { get; init; } = string.Empty;
}
