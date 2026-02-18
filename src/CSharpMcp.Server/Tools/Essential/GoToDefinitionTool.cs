using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models.Tools;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server.Tools.Essential;

/// <summary>
/// go_to_definition 工具 - 跳转到符号定义
/// </summary>
[McpServerToolType]
public class GoToDefinitionTool
{
    /// <summary>
    /// Navigate to the definition of a symbol
    /// </summary>
    [McpServerTool]
    public static async Task<string> GoToDefinition(
        GoToDefinitionParams parameters,
        IWorkspaceManager workspaceManager,
        ILogger<GoToDefinitionTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            logger.LogDebug("Going to definition: {FilePath}:{LineNumber} - {SymbolName}",
                parameters.FilePath, parameters.LineNumber, parameters.SymbolName);

            parameters.ResolveSymbolFromLocationAsync(workspaceManager, SymbolExtensions.SymbolKindPreference.None)

            // 构建详细的错误信息
            var errorDetails = BuildErrorDetails(parameters, workspaceManager, logger, cancellationToken);
            logger.LogWarning("Symbol not found: {Details}", errorDetails);

            throw new FileNotFoundException(errorDetails);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GoToDefinitionTool");
            throw;
        }
    }

    private static async Task<string> CreateResponseAsync(
        ISymbol symbol,
        GoToDefinitionParams parameters,
        ILogger<GoToDefinitionTool> logger,
        CancellationToken cancellationToken)
    {
        var displayName = symbol.GetDisplayName();
        var (startLine, endLine) = symbol.GetLineRange();
        var filePath = symbol.GetFilePath();
        var relativePath = GetRelativePath(filePath);
        var containingType = symbol.GetContainingTypeName();

        // Calculate total lines
        var totalLines = endLine - startLine + 1;
        var bodyMaxLines = parameters.GetBodyMaxLines();
        var isTruncated = parameters.IncludeBody && bodyMaxLines < totalLines;

        logger.LogDebug("Found definition: {SymbolName} at {FilePath}:{LineNumber}",
            symbol.Name, filePath, startLine);

        // Build Markdown
        var sb = new StringBuilder();
        sb.AppendLine($"### Definition: `{displayName}`");

        if (isTruncated)
        {
            sb.AppendLine($"(showing up to {bodyMaxLines} of {totalLines} total lines)");
        }

        sb.AppendLine();
        sb.AppendLine("**Location**:");
        sb.AppendLine($"- File: {relativePath}:{startLine}-{endLine}");

        if (!string.IsNullOrEmpty(containingType))
            sb.AppendLine($"- Containing Type: {containingType}");

        var ns = symbol.GetNamespace();
        if (!string.IsNullOrEmpty(ns))
            sb.AppendLine($"- Namespace: {ns}");

        sb.AppendLine();

        // Show full method/implementation
        if (parameters.IncludeBody)
        {
            var implementation = await symbol.GetFullImplementationAsync(bodyMaxLines, cancellationToken);
            if (!string.IsNullOrEmpty(implementation))
            {
                sb.AppendLine("**Full Method**:");
                sb.AppendLine("```csharp");
                sb.AppendLine(implementation);
                sb.AppendLine("```");

                if (isTruncated)
                {
                    sb.AppendLine($"*... {totalLines - bodyMaxLines} more lines hidden*");
                }
            }
        }

        // Show documentation
        var summary = symbol.GetSummaryComment();
        if (!string.IsNullOrEmpty(summary))
        {
            sb.AppendLine("**Documentation**:");
            sb.AppendLine(summary);
        }

        return sb.ToString();
    }

    private static string GetRelativePath(string filePath)
    {
        try
        {
            var currentDir = System.IO.Directory.GetCurrentDirectory();
            var relativePath = System.IO.Path.GetRelativePath(currentDir, filePath);
            return string.IsNullOrEmpty(relativePath) ? filePath : relativePath.Replace('\\', '/');
        }
        catch
        {
            return filePath.Replace('\\', '/');
        }
    }

    private static string BuildErrorDetails(
        GoToDefinitionParams parameters,
        IWorkspaceManager workspaceManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var details = new StringBuilder();
        details.AppendLine($"## Symbol Not Found");
        details.AppendLine();
        details.AppendLine($"**File**: `{parameters.FilePath}`");
        details.AppendLine($"**Line Number**: {parameters.LineNumber?.ToString() ?? "Not specified"}");
        details.AppendLine($"**Symbol Name**: `{parameters.SymbolName ?? "Not specified"}`");
        details.AppendLine();

        // 尝试读取文件内容显示该行
        try
        {
            var document = workspaceManager.GetCurrentSolution()?.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.FilePath == parameters.FilePath);

            if (document != null && parameters.LineNumber.HasValue)
            {
                var sourceText = document.GetTextAsync(cancellationToken).GetAwaiter().GetResult();
                if (sourceText != null)
                {
                    var line = sourceText.Lines.FirstOrDefault(l => l.LineNumber == parameters.LineNumber.Value - 1);
                    if (line.LineNumber >= 0)
                    {
                        details.AppendLine($"**Line Content**:");
                        details.AppendLine($"```csharp");
                        details.AppendLine(line.ToString().Trim());
                        details.AppendLine($"```");
                        details.AppendLine();
                    }
                }
            }
        }
        catch
        {
            details.AppendLine($"**Line Content**: Unable to read file content");
            details.AppendLine();
        }

        details.AppendLine($"**Possible Reasons**:");
        details.AppendLine($"1. The symbol is defined in an external library (not in this workspace)");
        details.AppendLine($"2. The symbol is a built-in C# type or keyword");
        details.AppendLine($"3. The file path or line number is incorrect");
        details.AppendLine($"4. The workspace needs to be reloaded (try LoadWorkspace again)");

        return details.ToString();
    }
}
