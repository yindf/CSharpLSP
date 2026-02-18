using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using CSharpMcp.Server.Models;

namespace CSharpMcp.Server.Models.Tools;

/// <summary>
/// 通用文件路径定位参数
/// </summary>
public record FileLocationParams
{
    /// <summary>
    /// 文件路径 (支持绝对路径、相对路径、仅文件名模糊匹配)
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// 行号 (1-based, 用于模糊匹配)
    /// </summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// 符号名称 (用于验证和模糊匹配)
    /// </summary>
    public string? SymbolName { get; init; }
}

/// <summary>
/// get_symbols 工具参数
/// </summary>
public record GetSymbolsParams : FileLocationParams
{
    /// <summary>
    /// 输出详细级别
    /// </summary>
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Summary;

    /// <summary>
    /// 是否包含方法体
    /// </summary>
    public bool IncludeBody { get; init; } = true;

    /// <summary>
    /// 方法体最大行数
    /// </summary>
    public int BodyMaxLines { get; init; } = 100;

}

/// <summary>
/// go_to_definition 工具参数
/// </summary>
public record GoToDefinitionParams : FileLocationParams
{
    /// <summary>
    /// 输出详细级别
    /// </summary>
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Standard;

    /// <summary>
    /// 是否包含方法体
    /// </summary>
    public bool IncludeBody { get; init; } = true;

    /// <summary>
    /// 方法体最大行数
    /// </summary>
    public int BodyMaxLines { get; init; } = 100;
}

/// <summary>
/// find_references 工具参数
/// </summary>
public record FindReferencesParams : FileLocationParams
{
    /// <summary>
    /// 是否包含上下文代码
    /// </summary>
    public bool IncludeContext { get; init; } = true;

    /// <summary>
    /// 上下文代码行数
    /// </summary>
    public int ContextLines { get; init; } = 3;
}

/// <summary>
/// search_symbols 工具参数
/// </summary>
public record SearchSymbolsParams
{
    /// <summary>
    /// 搜索查询 (支持通配符如 My.*, *.Controller)
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// 输出详细级别
    /// </summary>
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Summary;

    /// <summary>
    /// 最大结果数量
    /// </summary>
    public int MaxResults { get; init; } = 100;
}

/// <summary>
/// resolve_symbol 工具参数
/// </summary>
public record ResolveSymbolParams : FileLocationParams
{
    /// <summary>
    /// 输出详细级别
    /// </summary>
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Standard;

    /// <summary>
    /// 是否包含方法体
    /// </summary>
    public bool IncludeBody { get; init; } = true;

    /// <summary>
    /// 方法体最大行数
    /// </summary>
    public int BodyMaxLines { get; init; } = 50;
}

/// <summary>
/// get_inheritance_hierarchy 工具参数
/// </summary>
public record GetInheritanceHierarchyParams : FileLocationParams
{
    /// <summary>
    /// 是否包含派生类
    /// </summary>
    public bool IncludeDerived { get; init; } = true;

    /// <summary>
    /// 派生类最大深度
    /// </summary>
    public int MaxDerivedDepth { get; init; } = 3;
}

/// <summary>
/// get_call_graph 工具参数
/// </summary>
public record GetCallGraphParams : FileLocationParams
{
    /// <summary>
    /// 最大深度
    /// </summary>
    public int MaxDepth { get; init; } = 2;

    /// <summary>
    /// 是否包含外部调用
    /// </summary>
    public bool IncludeExternalCalls { get; init; } = true;
}

/// <summary>
/// get_type_members 工具参数
/// </summary>
public record GetTypeMembersParams : FileLocationParams
{
    /// <summary>
    /// 是否包含继承的成员
    /// </summary>
    public bool IncludeInherited { get; init; } = false;

}

/// <summary>
/// get_symbol_complete 工具参数 - 整合多个信息源获取完整符号信息
/// </summary>
public record GetSymbolCompleteParams : FileLocationParams
{
    /// <summary>
    /// 要获取的信息部分 (按需返回信息)
    /// </summary>
    public SymbolCompleteSections Sections { get; init; } = SymbolCompleteSections.All;

    /// <summary>
    /// 输出详细级别
    /// </summary>
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Standard;

    /// <summary>
    /// 方法体最大行数
    /// </summary>
    public int BodyMaxLines { get; init; } = 100;

    /// <summary>
    /// 是否包含引用
    /// </summary>
    public bool IncludeReferences { get; init; } = true;

    /// <summary>
    /// 引用最大数量
    /// </summary>
    public int MaxReferences { get; init; } = 50;

    /// <summary>
    /// 是否包含继承信息
    /// </summary>
    public bool IncludeInheritance { get; init; } = false;

    /// <summary>
    /// 是否包含调用图
    /// </summary>
    public bool IncludeCallGraph { get; init; } = false;

    /// <summary>
    /// 调用图最大深度
    /// </summary>
    public int CallGraphMaxDepth { get; init; } = 1;
}

/// <summary>
/// 符号完整信息部分
/// </summary>
[Flags]
public enum SymbolCompleteSections
{
    /// <summary>
    /// 基本信息 (名称、类型、位置)
    /// </summary>
    Basic = 1,

    /// <summary>
    /// 签名信息 (参数、返回值)
    /// </summary>
    Signature = 2,

    /// <summary>
    /// 文档注释
    /// </summary>
    Documentation = 4,

    /// <summary>
    /// 源代码
    /// </summary>
    SourceCode = 8,

    /// <summary>
    /// 引用位置
    /// </summary>
    References = 16,

    /// <summary>
    /// 继承层次
    /// </summary>
    Inheritance = 32,

    /// <summary>
    /// 调用图
    /// </summary>
    CallGraph = 64,

    /// <summary>
    /// 所有信息
    /// </summary>
    All = Basic | Signature | Documentation | SourceCode | References | Inheritance | CallGraph
}

/// <summary>
/// batch_get_symbols 工具参数 - 批量获取符号信息
/// </summary>
public record BatchGetSymbolsParams
{
    /// <summary>
    /// 符号位置列表
    /// </summary>
    public required IReadOnlyList<FileLocationParams> Symbols { get; init; }

    /// <summary>
    /// 输出详细级别
    /// </summary>
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Summary;

    /// <summary>
    /// 是否包含方法体
    /// </summary>
    public bool IncludeBody { get; init; } = true;

    /// <summary>
    /// 方法体最大行数
    /// </summary>
    public int BodyMaxLines { get; init; } = 50;

    /// <summary>
    /// 最大并发数
    /// </summary>
    public int MaxConcurrency { get; init; } = 5;
}

/// <summary>
/// get_diagnostics 工具参数 - 获取编译诊断信息
/// </summary>
public record GetDiagnosticsParams
{
    /// <summary>
    /// 文件路径 (可选，不指定则获取整个工作区的诊断)
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// 是否包含警告
    /// </summary>
    public bool IncludeWarnings { get; init; } = true;

    /// <summary>
    /// 是否包含信息
    /// </summary>
    public bool IncludeInfo { get; init; } = false;

    /// <summary>
    /// 是否包含隐藏诊断
    /// </summary>
    public bool IncludeHidden { get; init; } = false;

    /// <summary>
    /// 严重性过滤
    /// </summary>
    public IReadOnlyList<DiagnosticSeverity>? SeverityFilter { get; init; }
}

/// <summary>
/// 诊断严重性
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>
    /// 错误
    /// </summary>
    Error,

    /// <summary>
    /// 警告
    /// </summary>
    Warning,

    /// <summary>
    /// 信息
    /// </summary>
    Info,

    /// <summary>
    /// 隐藏
    /// </summary>
    Hidden
}

/// <summary>
/// load_workspace 工具参数 - 加载 C# 解决方案或项目
/// </summary>
public record LoadWorkspaceParams
{
    /// <summary>
    /// 工作区路径 (支持 .sln 文件、.csproj 文件或包含它们的目录)
    /// </summary>
    public required string Path { get; init; }
}
