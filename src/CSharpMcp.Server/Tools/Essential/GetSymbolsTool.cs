using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models.Output;
using CSharpMcp.Server.Models.Tools;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server.Tools.Essential;

/// <summary>
/// get_symbols 工具 - 获取文档中的所有符号
/// </summary>
[McpServerToolType]
public class GetSymbolsTool
{
    /// <summary>
    /// Get all symbols in a C# document with optional filtering
    /// </summary>
    /// <param name="parameters">Tool parameters including file path and filters</param>
    /// <param name="workspaceManager">Workspace manager service</param>
    /// <param name="symbolAnalyzer">Symbol analyzer service</param>
    /// <param name="logger">Logger instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of symbols found in the document</returns>
    [McpServerTool]
    public static async Task<string> GetSymbols(
        GetSymbolsParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        ILogger<GetSymbolsTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            logger.LogDebug("Getting symbols for: {FilePath}", parameters.FilePath);

            var document = await workspaceManager.GetDocumentAsync(parameters.FilePath, cancellationToken);
            if (document == null)
            {
                logger.LogWarning("Document not found: {FilePath}", parameters.FilePath);
                throw new FileNotFoundException($"Document not found: {parameters.FilePath}");
            }

            var symbols = await symbolAnalyzer.GetDocumentSymbolsAsync(document, cancellationToken);

            // Apply filters
            if (parameters.FilterKinds != null && parameters.FilterKinds.Count > 0)
            {
                var filterSet = new HashSet<Models.SymbolKind>(parameters.FilterKinds);
                symbols = symbols.Where(s => filterSet.Contains(MapSymbolKind(s.Kind))).ToList();
            }

            // Convert to SymbolInfo
            var symbolInfos = new List<Models.SymbolInfo>();
            foreach (var symbol in symbols)
            {
                var info = await symbolAnalyzer.ToSymbolInfoAsync(
                    symbol,
                    parameters.DetailLevel,
                    parameters.IncludeBody ? parameters.BodyMaxLines : null,
                    cancellationToken);

                symbolInfos.Add(info);
            }

            logger.LogDebug("Found {Count} symbols in {FilePath}", symbolInfos.Count, parameters.FilePath);

            return new GetSymbolsResponse(
                parameters.FilePath,
                symbolInfos,
                symbols.Count).ToMarkdown();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetSymbolsTool");
            throw;
        }
    }

    private static Models.SymbolKind MapSymbolKind(Microsoft.CodeAnalysis.SymbolKind kind)
    {
        return kind switch
        {
            Microsoft.CodeAnalysis.SymbolKind.NamedType => Models.SymbolKind.Class,
            Microsoft.CodeAnalysis.SymbolKind.Method => Models.SymbolKind.Method,
            Microsoft.CodeAnalysis.SymbolKind.Property => Models.SymbolKind.Property,
            Microsoft.CodeAnalysis.SymbolKind.Field => Models.SymbolKind.Field,
            Microsoft.CodeAnalysis.SymbolKind.Event => Models.SymbolKind.Event,
            _ => Models.SymbolKind.Method
        };
    }
}
