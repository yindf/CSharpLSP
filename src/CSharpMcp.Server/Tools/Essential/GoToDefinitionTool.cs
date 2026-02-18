using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models.Output;
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
        ISymbolAnalyzer symbolAnalyzer,
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

            // Try by position first
            if (parameters.LineNumber.HasValue)
            {
                var result = await TryFindByPositionAsync(parameters, workspaceManager, symbolAnalyzer, logger, cancellationToken);
                if (result != null)
                {
                    return result.ToMarkdown();
                }
            }

            // Try by name
            if (!string.IsNullOrEmpty(parameters.SymbolName))
            {
                var result = await TryFindByNameAsync(parameters, workspaceManager, symbolAnalyzer, logger, cancellationToken);
                if (result != null)
                {
                    return result.ToMarkdown();
                }
            }

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

    private static async Task<GoToDefinitionResponse?> TryFindByPositionAsync(
        GoToDefinitionParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        ILogger<GoToDefinitionTool> logger,
        CancellationToken cancellationToken)
    {
        var document = await workspaceManager.GetDocumentAsync(parameters.FilePath, cancellationToken);
        if (document == null)
        {
            return null;
        }

        var lineNumber = parameters.LineNumber!.Value;
        var symbol = await symbolAnalyzer.ResolveSymbolAtPositionAsync(
            document,
            lineNumber,
            1,
            cancellationToken);

        if (symbol != null)
        {
            return await CreateResponseAsync(symbol, parameters, symbolAnalyzer, logger, cancellationToken);
        }

        return null;
    }

    private static async Task<GoToDefinitionResponse?> TryFindByNameAsync(
        GoToDefinitionParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        ILogger<GoToDefinitionTool> logger,
        CancellationToken cancellationToken)
    {
        var document = await workspaceManager.GetDocumentAsync(parameters.FilePath, cancellationToken);
        if (document == null)
        {
            return null;
        }

        var symbols = await symbolAnalyzer.FindSymbolsByNameAsync(
            document,
            parameters.SymbolName!,
            parameters.LineNumber,
            cancellationToken);

        if (symbols.Count > 0)
        {
            return await CreateResponseAsync(symbols[0], parameters, symbolAnalyzer, logger, cancellationToken);
        }

        return null;
    }

    private static async Task<GoToDefinitionResponse> CreateResponseAsync(
        Microsoft.CodeAnalysis.ISymbol symbol,
        GoToDefinitionParams parameters,
        ISymbolAnalyzer symbolAnalyzer,
        ILogger<GoToDefinitionTool> logger,
        CancellationToken cancellationToken)
    {
        var info = await symbolAnalyzer.ToSymbolInfoAsync(
            symbol,
            parameters.DetailLevel,
            parameters.IncludeBody ? parameters.GetBodyMaxLines() : null,
            cancellationToken);

        // Calculate total lines in the full method span
        var totalLines = info.Location.EndLine - info.Location.StartLine + 1;
        // Calculate actual lines returned (may be truncated)
        var sourceLines = info.SourceCode?.Split('\n').Length ?? 0;
        var isTruncated = parameters.IncludeBody && parameters.GetBodyMaxLines() < totalLines;

        logger.LogDebug("Found definition: {SymbolName} at {FilePath}:{LineNumber}",
            symbol.Name, info.Location.FilePath, info.Location.StartLine);

        // Pass totalLines (full span) not sourceLines (truncated count)
        return new GoToDefinitionResponse(info, isTruncated, totalLines);
    }

    private static string BuildErrorDetails(
        GoToDefinitionParams parameters,
        IWorkspaceManager workspaceManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var details = new System.Text.StringBuilder();
        details.AppendLine($"## Symbol Not Found");
        details.AppendLine();
        details.AppendLine($"**File**: `{parameters.FilePath}`");
        details.AppendLine($"**Line Number**: {parameters.LineNumber?.ToString() ?? "Not specified"}");
        details.AppendLine($"**Symbol Name**: `{parameters.SymbolName ?? "Not specified"}`");
        details.AppendLine();

        // 尝试读取文件内容显示该行
        try
        {
            var document = workspaceManager.CurrentSolution?.Projects
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
