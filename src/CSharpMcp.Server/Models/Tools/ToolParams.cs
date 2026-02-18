using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.CodeAnalysis;

namespace CSharpMcp.Server.Models.Tools;

/// <summary>
/// 通用文件路径定位参数
/// </summary>
public record FileLocationParams
{
    /// <summary>
    /// 符号名称 (用于验证和模糊匹配)
    /// </summary>
    [Description("The name of the symbol to locate (e.g., 'MyMethod', 'MyClass')")]
    public string SymbolName { get; init; }

    /// <summary>
    /// 文件路径 (支持绝对路径、相对路径、仅文件名模糊匹配)
    /// </summary>
    [Description("Path to the file containing the symbol. Can be absolute, relative, or filename only for fuzzy matching")]
    public string FilePath { get; init; }

    /// <summary>
    /// 行号 (1-based, 用于模糊匹配)
    /// </summary>
    [Description("1-based line number near the symbol declaration (used for fuzzy matching)")]
    public int LineNumber { get; init; } = 0;
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
    [Description("Path to .sln file, .csproj file, or directory containing them")]
    public required string Path { get; init; }
}
