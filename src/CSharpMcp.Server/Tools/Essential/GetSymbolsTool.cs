using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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

            logger.LogDebug("Found {Count} symbols in {FilePath}", symbols.Count, parameters.FilePath);

            // Build Markdown directly
            return await BuildSymbolsMarkdownAsync(parameters.FilePath, symbols, parameters, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetSymbolsTool");
            throw;
        }
    }

    private static async Task<string> BuildSymbolsMarkdownAsync(
        string filePath,
        IReadOnlyList<ISymbol> symbols,
        GetSymbolsParams parameters,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var fileName = System.IO.Path.GetFileName(filePath);

        sb.AppendLine($"## Symbols: {fileName}");
        sb.AppendLine($"**Total: {symbols.Count} symbol{(symbols.Count != 1 ? "s" : "")}**");
        sb.AppendLine();

        foreach (var symbol in symbols)
        {
            var displayName = symbol.GetDisplayName();
            var (startLine, endLine) = symbol.GetLineRange();
            var kind = symbol.GetDisplayKind();

            sb.Append($"- **{displayName}** ({kind}) L{startLine}-{endLine}");

            var signature = symbol.GetSignature();
            if (!string.IsNullOrEmpty(signature))
                sb.Append($" - `{signature}`");

            var summary = symbol.GetSummaryComment();
            if (!string.IsNullOrEmpty(summary))
                sb.Append($" // {summary}");

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
