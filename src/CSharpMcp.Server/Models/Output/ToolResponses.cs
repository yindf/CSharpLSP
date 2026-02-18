using System.Collections.Generic;
using System.Linq;
using System.Text;
using DiagnosticSeverity = CSharpMcp.Server.Models.Tools.DiagnosticSeverity;
using WorkspaceKind = CSharpMcp.Server.Roslyn.WorkspaceKind;

namespace CSharpMcp.Server.Models.Output;

/// <summary>
/// 工具响应基类（仅保留基类定义）
/// 所有工具现在直接返回 string（Markdown），不再使用响应类型
/// </summary>
public abstract record ToolResponse
{
    /// <summary>
    /// 转换为 Markdown 格式
    /// </summary>
    public abstract string ToMarkdown();
}

/// <summary>
/// 错误响应
/// </summary>
public record ErrorResponse(string Message) : ToolResponse
{
    public override string ToMarkdown() => $"**Error**: {Message}";
}

/// <summary>
/// load_workspace 输出
/// </summary>
public record LoadWorkspaceResponse(
    string Path,
    WorkspaceKind Kind,
    int ProjectCount,
    int DocumentCount
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Workspace Loaded");
        sb.AppendLine();
        sb.AppendLine($"**Path**: `{Path}`");
        sb.AppendLine($"**Type**: {Kind}");
        sb.AppendLine($"**Projects**: {ProjectCount}");
        sb.AppendLine($"**Documents**: {DocumentCount}");
        sb.AppendLine();
        sb.AppendLine("You can now use other C# analysis tools:");
        sb.AppendLine("- `SearchSymbols` - Search for symbols by name");
        sb.AppendLine("- `GetSymbols` - Get symbols from a file");
        sb.AppendLine("- `GoToDefinition` - Navigate to symbol definition");
        sb.AppendLine("- `FindReferences` - Find all references to a symbol");
        sb.AppendLine("- `GetDiagnostics` - Check for compilation errors");
        sb.AppendLine();
        return sb.ToString();
    }
}

/// <summary>
/// get_diagnostics 输出
/// </summary>
public record DiagnosticItem(
    string Id,
    string Message,
    DiagnosticSeverity Severity,
    string FilePath,
    int StartLine,
    int EndLine,
    int StartColumn,
    int EndColumn,
    string? Category
);

public record DiagnosticsSummary(
    int TotalErrors,
    int TotalWarnings,
    int TotalInfo,
    int TotalHidden,
    int FilesWithDiagnostics
);

public record GetDiagnosticsResponse(
    DiagnosticsSummary Summary,
    IReadOnlyList<DiagnosticItem> Diagnostics
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Diagnostics Report");
        sb.AppendLine();

        // Summary
        sb.AppendLine("**Summary**:");
        sb.AppendLine($"- Errors: {Summary.TotalErrors}");
        sb.AppendLine($"- Warnings: {Summary.TotalWarnings}");
        sb.AppendLine($"- Info: {Summary.TotalInfo}");
        sb.AppendLine($"- Files affected: {Summary.FilesWithDiagnostics}");
        sb.AppendLine();

        // Group by file
        var grouped = Diagnostics.GroupBy(d => d.FilePath);

        foreach (var group in grouped)
        {
            var fileName = System.IO.Path.GetFileName(group.Key);
            sb.AppendLine($"### {fileName}");
            sb.AppendLine();

            foreach (var diag in group)
            {
                var severityLabel = diag.Severity switch
                {
                    DiagnosticSeverity.Error => "[ERROR]",
                    DiagnosticSeverity.Warning => "[WARNING]",
                    DiagnosticSeverity.Info => "[INFO]",
                    DiagnosticSeverity.Hidden => "[HIDDEN]",
                    _ => "[?]"
                };

                sb.AppendLine($"- {severityLabel} **{diag.Id}** (Line {diag.StartLine}): {diag.Message}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
