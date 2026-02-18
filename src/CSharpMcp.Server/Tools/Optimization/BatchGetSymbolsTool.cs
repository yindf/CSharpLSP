using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models.Tools;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server.Tools.Optimization;

/// <summary>
/// batch_get_symbols 工具 - 批量获取符号信息
/// 使用并行处理提高性能，减少往返次数
/// </summary>
[McpServerToolType]
public class BatchGetSymbolsTool
{
    /// <summary>
    /// Batch get symbol information for multiple symbols in parallel
    /// </summary>
    [McpServerTool, Description("Query multiple symbols in parallel for improved performance")]
    public static async Task<string> BatchGetSymbols(
        [Description("List of symbol locations to query in batch")] IReadOnlyList<FileLocationParams> symbols,
        IWorkspaceManager workspaceManager,
        ILogger<BatchGetSymbolsTool> logger,
        CancellationToken cancellationToken,
        [Description("Whether to include implementation code")] bool includeBody = true,
        [Description("Maximum lines per symbol implementation")] int maxBodyLines = 50)
    {
        try
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            // Check workspace state
            var workspaceError = WorkspaceErrorHelper.CheckWorkspaceLoaded(workspaceManager, "Batch Get Symbols");
            if (workspaceError != null)
            {
                return workspaceError;
            }

            logger.LogInformation("Batch getting {Count} symbols", symbols.Count);

            // Use a semaphore to limit concurrency
            var semaphore = new SemaphoreSlim(5);
            var tasks = new List<Task<SymbolBatchResult>>();

            foreach (var symbolParams in symbols)
            {
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var symbol = await SymbolResolver.ResolveSymbolAsync(
                            symbolParams.FilePath,
                            symbolParams.LineNumber,
                            symbolParams.SymbolName ?? "",
                            workspaceManager,
                            SymbolFilter.TypeAndMember,
                            cancellationToken);

                        if (symbol == null)
                        {
                            return new SymbolBatchResult(
                                GetSymbolName(symbolParams),
                                null,
                                "Symbol not found"
                            );
                        }

                        // Check for fuzzy match warning
                        string? fuzzyMatchWarning = null;
                        if (!string.IsNullOrEmpty(symbolParams.FilePath))
                        {
                            var actualFilePath = symbol.GetFilePath();
                            var requestedFileName = System.IO.Path.GetFileName(symbolParams.FilePath);
                            var actualFileName = System.IO.Path.GetFileName(actualFilePath);

                            // Warn if file names don't match (indicating fuzzy matching occurred)
                            if (!string.Equals(requestedFileName, actualFileName, StringComparison.OrdinalIgnoreCase))
                            {
                                fuzzyMatchWarning = $"⚠️ Fuzzy match: Requested '{requestedFileName}' but found '{actualFileName}' in '{actualFilePath}'";
                                logger.LogWarning("Fuzzy match detected: Requested '{Requested}' but found '{Actual}' in '{Path}'",
                                    requestedFileName, actualFileName, actualFilePath);
                            }
                        }

                        // Build symbol info directly
                        var info = await BuildSymbolInfoAsync(
                            symbol,
                            fuzzyMatchWarning,
                            includeBody ? maxBodyLines : null,
                            cancellationToken);

                        return new SymbolBatchResult(
                            GetSymbolName(symbolParams),
                            info,
                            null
                        );
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error processing symbol");
                        return new SymbolBatchResult(
                            GetSymbolName(symbolParams),
                            null,
                            ex.Message
                        );
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);

                tasks.Add(task);
            }

            var results = new List<SymbolBatchResult>();
            foreach (var task in tasks)
            {
                var result = await task;
                results.Add(result);
            }

            var successCount = results.Count(r => r.Error == null);
            var errorCount = results.Count(r => r.Error != null);

            logger.LogInformation("Batch query completed: {Success} succeeded, {Errors} failed",
                successCount, errorCount);

            // Build Markdown directly
            return BuildBatchResultsMarkdown(results, successCount, errorCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing BatchGetSymbolsTool");
            return GetErrorHelpResponse($"Failed to batch get symbols: {ex.Message}\n\nStack Trace:\n```\n{ex.StackTrace}\n```\n\nCommon issues:\n- Workspace is not loaded (call LoadWorkspace first)\n- Invalid symbol parameters provided\n- Symbols are from external libraries");
        }
    }

    private static string GetErrorHelpResponse(string message)
    {
        return MarkdownHelper.BuildErrorResponse(
            "Batch Get Symbols",
            message,
            "BatchGetSymbols(\n    symbols: [\n        { filePath: \"path/to/File1.cs\", lineNumber: 15, symbolName: \"MyClass\" },\n        { filePath: \"path/to/File2.cs\", lineNumber: 42, symbolName: \"MyMethod\" }\n    ]\n)",
            "- `BatchGetSymbols(symbols: [{ filePath: \"C:/MyProject/Models.cs\", lineNumber: 15, symbolName: \"User\" }])`"
        );
    }

    private static string BuildBatchResultsMarkdown(
        List<SymbolBatchResult> results,
        int successCount,
        int errorCount)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## Batch Symbol Query Results");
        sb.AppendLine();
        sb.AppendLine($"**Total**: {results.Count} | **Success**: {successCount} | **Errors**: {errorCount}");
        sb.AppendLine();

        foreach (var result in results)
        {
            if (result.Error != null)
            {
                sb.AppendLine($"### ❌ `{result.Name}`");
                sb.AppendLine();
                sb.AppendLine($"**Error**: {result.Error}");
            }
            else if (result.Info != null)
            {
                sb.AppendLine($"### ✅ `{result.Name}`");
                sb.AppendLine();
                sb.Append(result.Info);
            }
            else
            {
                sb.AppendLine($"### ❓ `{result.Name}`");
                sb.AppendLine();
                sb.AppendLine("**No information available**");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static async Task<string> BuildSymbolInfoAsync(
        ISymbol symbol,
        string? fuzzyMatchWarning,
        int? bodyMaxLines,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var displayName = symbol.GetDisplayName();
        var (startLine, endLine) = symbol.GetLineRange();
        var filePath = symbol.GetFilePath();
        var kind = symbol.GetDisplayKind();

        // Add fuzzy match warning if present
        if (!string.IsNullOrEmpty(fuzzyMatchWarning))
        {
            sb.AppendLine(fuzzyMatchWarning);
            sb.AppendLine();
        }

        sb.AppendLine($"**Type**: {char.ToUpper(kind[0]) + kind.Substring(1)}");

        if (startLine > 0)
        {
            var fileName = System.IO.Path.GetFileName(filePath);
            sb.AppendLine($"**Location**: `{fileName}:{startLine}`");
        }

        var signature = symbol.GetSignature();
        if (!string.IsNullOrEmpty(signature))
        {
            sb.AppendLine($"**Signature**: `{signature}`");
        }

        var summary = symbol.GetSummaryComment();
        if (!string.IsNullOrEmpty(summary))
        {
            sb.AppendLine($"**Documentation**: {summary}");
        }

        if (bodyMaxLines.HasValue && bodyMaxLines.Value > 0)
        {
            var implementation = await symbol.GetFullImplementationAsync(bodyMaxLines, cancellationToken);
            if (!string.IsNullOrEmpty(implementation))
            {
                sb.AppendLine();
                sb.AppendLine("**Implementation**:");
                sb.AppendLine("```csharp");
                sb.AppendLine(implementation);
                sb.AppendLine("```");
            }
        }

        return sb.ToString();
    }

    private static string GetSymbolName(FileLocationParams p) =>
        p.SymbolName ?? $"{System.IO.Path.GetFileName(p.FilePath)}:{p.LineNumber}";

    /// <summary>
    /// Result of a single symbol query in batch operation
    /// </summary>
    private record SymbolBatchResult(
        string Name,
        string? Info,
        string? Error
    );
}
