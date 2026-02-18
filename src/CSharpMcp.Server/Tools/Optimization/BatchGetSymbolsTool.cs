using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models.Output;
using CSharpMcp.Server.Models.Tools;
using CSharpMcp.Server.Roslyn;
using Microsoft.CodeAnalysis;

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
        ISymbolAnalyzer symbolAnalyzer,
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
            var semaphore = new System.Threading.SemaphoreSlim(parameters.GetMaxConcurrency());
            var tasks = new List<Task<BatchSymbolResult>>();

            foreach (var symbolParams in parameters.Symbols)
            {
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var (symbol, _) = await ResolveSymbolAsync(symbolParams, workspaceManager, symbolAnalyzer, cancellationToken);
                        if (symbol == null)
                        {
                            return new BatchSymbolResult(
                                GetSymbolName(symbolParams),
                                null,
                                "Symbol not found"
                            );
                        }

                        var info = await symbolAnalyzer.ToSymbolInfoAsync(
                            symbol,
                            parameters.DetailLevel,
                            parameters.IncludeBody ? parameters.GetBodyMaxLines() : null,
                            cancellationToken);

                        return new BatchSymbolResult(
                            GetSymbolName(symbolParams),
                            info,
                            null
                        );
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error processing symbol");
                        return new BatchSymbolResult(
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

            var results = new List<BatchSymbolResult>();
            foreach (var task in tasks)
            {
                var result = await task;
                results.Add(result);
            }

            var successCount = results.Count(r => r.Error == null);
            var errorCount = results.Count(r => r.Error != null);

            logger.LogDebug("Batch query completed: {Success} succeeded, {Errors} failed",
                successCount, errorCount);

            return new BatchGetSymbolsResponse(
                parameters.Symbols.Count,
                successCount,
                errorCount,
                results
            ).ToMarkdown();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing BatchGetSymbolsTool");
            throw;
        }
    }

    private static string GetSymbolName(FileLocationParams p) =>
        p.SymbolName ?? $"{System.IO.Path.GetFileName(p.FilePath)}:{p.LineNumber}";

    private static async Task<(ISymbol? symbol, Document document)> ResolveSymbolAsync(
        FileLocationParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        CancellationToken cancellationToken)
    {
        var document = await workspaceManager.GetDocumentAsync(parameters.FilePath, cancellationToken);
        if (document == null)
        {
            return (null, null!);
        }

        ISymbol? symbol = null;

        // Try by position
        if (parameters.LineNumber.HasValue)
        {
            symbol = await symbolAnalyzer.ResolveSymbolAtPositionAsync(
                document,
                parameters.LineNumber.Value,
                1,
                cancellationToken);
        }

        // Try by name
        if (symbol == null && !string.IsNullOrEmpty(parameters.SymbolName))
        {
            var symbols = await symbolAnalyzer.FindSymbolsByNameAsync(
                document,
                parameters.SymbolName,
                parameters.LineNumber,
                cancellationToken);

            symbol = symbols.FirstOrDefault();
        }

        return (symbol, document);
    }
}
