using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models;
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
    [McpServerTool]
    public static async Task<string> BatchGetSymbols(
        BatchGetSymbolsParams parameters,
        IWorkspaceManager workspaceManager,
        ILogger<BatchGetSymbolsTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            logger.LogDebug("Batch getting {Count} symbols", parameters.Symbols.Count);

            // Use a semaphore to limit concurrency
            var semaphore = new SemaphoreSlim(5);
            var tasks = new List<Task<SymbolBatchResult>>();

            foreach (var symbolParams in parameters.Symbols)
            {
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var (symbol, _) = await symbolParams.ResolveSymbolFromLocationAsync(workspaceManager, cancellationToken: cancellationToken);
                        if (symbol == null)
                        {
                            return new SymbolBatchResult(
                                GetSymbolName(symbolParams),
                                null,
                                "Symbol not found"
                            );
                        }

                        // Build symbol info directly
                        var info = await BuildSymbolInfoAsync(
                            symbol,
                            parameters.IncludeBody ? parameters.GetBodyMaxLines() : null,
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

            logger.LogDebug("Batch query completed: {Success} succeeded, {Errors} failed",
                successCount, errorCount);

            // Build Markdown directly
            return BuildBatchResultsMarkdown(results, successCount, errorCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing BatchGetSymbolsTool");
            throw;
        }
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
        int? bodyMaxLines,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var displayName = symbol.GetDisplayName();
        var (startLine, endLine) = symbol.GetLineRange();
        var filePath = symbol.GetFilePath();
        var kind = symbol.GetDisplayKind();

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
