using System;
using System.Collections.Generic;
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
/// resolve_symbol 工具 - 获取符号的完整信息
/// </summary>
[McpServerToolType]
public class ResolveSymbolTool
{
    /// <summary>
    /// Get comprehensive symbol information including documentation, comments, and context
    /// </summary>
    [McpServerTool]
    public static async Task<string> ResolveSymbol(
        ResolveSymbolParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        ILogger<ResolveSymbolTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            logger.LogDebug("Resolving symbol: {FilePath}:{LineNumber} - {SymbolName}",
                parameters.FilePath, parameters.LineNumber, parameters.SymbolName);

            // Resolve the symbol
            var (symbol, document) = await ResolveSymbolAsync(parameters, workspaceManager, symbolAnalyzer, cancellationToken);
            if (symbol == null)
            {
                var errorDetails = BuildErrorDetails(parameters, workspaceManager, cancellationToken);
                logger.LogWarning("Symbol not found: {Details}", errorDetails);
                throw new FileNotFoundException(errorDetails);
            }

            // Get symbol info
            var info = await symbolAnalyzer.ToSymbolInfoAsync(
                symbol,
                parameters.DetailLevel,
                parameters.IncludeBody ? parameters.GetBodyMaxLines() : null,
                cancellationToken);

            // Get definition source
            string? definition = null;
            if (parameters.IncludeBody)
            {
                definition = await symbolAnalyzer.ExtractSourceCodeAsync(
                    symbol,
                    true,
                    parameters.GetBodyMaxLines(),
                    cancellationToken);
            }

            // Get references (limited)
            List<Models.SymbolReference>? references = null;
            try
            {
                var solution = document.Project.Solution;
                var referencedSymbols = await symbolAnalyzer.FindReferencesAsync(
                    symbol,
                    solution,
                    cancellationToken);

                references = new List<Models.SymbolReference>();
                foreach (var refSym in referencedSymbols.Take(20))
                {
                    foreach (var loc in refSym.Locations.Take(3))
                    {
                        var location = new Models.SymbolLocation(
                            loc.Document.FilePath,
                            loc.Location.GetLineSpan().StartLinePosition.Line + 1,
                            loc.Location.GetLineSpan().EndLinePosition.Line + 1,
                            loc.Location.GetLineSpan().StartLinePosition.Character + 1,
                            loc.Location.GetLineSpan().EndLinePosition.Character + 1
                        );

                        // Extract line text
                        var lineText = await ExtractLineTextAsync(loc.Document, location.StartLine, cancellationToken);

                        references.Add(new Models.SymbolReference(
                            location,
                            refSym.Definition?.Name ?? "Unknown",
                            null,
                            lineText
                        ));
                    }
                }
            }
            catch
            {
                // Ignore reference errors
            }

            logger.LogDebug("Resolved symbol: {SymbolName}", symbol.Name);

            return new ResolveSymbolResponse(info, definition, references).ToMarkdown();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing ResolveSymbolTool");
            throw;
        }
    }

    private static async Task<string?> ExtractLineTextAsync(
        Document document,
        int lineNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceText = await document.GetTextAsync(cancellationToken);
            var lines = sourceText.Lines;

            if (lineNumber < 1 || lineNumber > lines.Count)
                return null;

            var lineIndex = lineNumber - 1;
            return lines[lineIndex].ToString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(Microsoft.CodeAnalysis.ISymbol? symbol, Document document)> ResolveSymbolAsync(
        ResolveSymbolParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        CancellationToken cancellationToken)
    {
        var document = await workspaceManager.GetDocumentAsync(parameters.FilePath, cancellationToken);
        if (document == null)
        {
            return (null, null!);
        }

        Microsoft.CodeAnalysis.ISymbol? symbol = null;

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

    private static string BuildErrorDetails(
        ResolveSymbolParams parameters,
        IWorkspaceManager workspaceManager,
        CancellationToken cancellationToken)
    {
        var details = new System.Text.StringBuilder();
        details.AppendLine("## Symbol Not Found");
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
                        details.AppendLine("**Line Content**:");
                        details.AppendLine("```csharp");
                        details.AppendLine(line.ToString().Trim());
                        details.AppendLine("```");
                        details.AppendLine();
                    }
                }
            }
        }
        catch
        {
            details.AppendLine("**Line Content**: Unable to read file content");
            details.AppendLine();
        }

        details.AppendLine("**Possible Reasons**:");
        details.AppendLine("1. The symbol is defined in an external library (not in this workspace)");
        details.AppendLine("2. The symbol is a built-in C# type or keyword");
        details.AppendLine("3. The file path or line number is incorrect");
        details.AppendLine("4. The workspace needs to be reloaded (try LoadWorkspace again)");

        return details.ToString();
    }
}
