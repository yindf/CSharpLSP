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
/// find_references 工具 - 查找符号的所有引用
/// </summary>
[McpServerToolType]
public class FindReferencesTool
{
    /// <summary>
    /// Find all references to a symbol across the workspace
    /// </summary>
    [McpServerTool]
    public static async Task<string> FindReferences(
        FindReferencesParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        ILogger<FindReferencesTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            logger.LogDebug("Finding references: {FilePath}:{LineNumber} - {SymbolName}",
                parameters.FilePath, parameters.LineNumber, parameters.SymbolName);

            // First, resolve the symbol
            var (symbol, document) = await ResolveSymbolAsync(parameters, workspaceManager, symbolAnalyzer, cancellationToken);
            if (symbol == null)
            {
                var errorDetails = BuildErrorDetails(parameters, workspaceManager, cancellationToken);
                logger.LogWarning("Symbol not found: {Details}", errorDetails);
                throw new FileNotFoundException(errorDetails);
            }

            // Get the solution
            var solution = document.Project.Solution;

            // Find references
            var referencedSymbols = await symbolAnalyzer.FindReferencesAsync(
                symbol,
                solution,
                cancellationToken);

            // Convert to reference info
            var references = new List<Models.SymbolReference>();
            var files = new HashSet<string>();
            var sameFileCount = 0;
            var targetFilePath = parameters.FilePath;

            foreach (var refSym in referencedSymbols)
            {
                foreach (var loc in refSym.Locations)
                {
                    var location = new Models.SymbolLocation(
                        loc.Document.FilePath,
                        loc.Location.GetLineSpan().StartLinePosition.Line + 1,
                        loc.Location.GetLineSpan().EndLinePosition.Line + 1,
                        loc.Location.GetLineSpan().StartLinePosition.Character + 1,
                        loc.Location.GetLineSpan().EndLinePosition.Character + 1
                    );

                    string? contextCode = null;
                    string? lineText = null;

                    if (parameters.IncludeContext)
                    {
                        contextCode = await ExtractContextCodeAsync(loc.Document, location, parameters.GetContextLines(), cancellationToken);
                    }

                    // Always extract line text
                    lineText = await ExtractLineTextAsync(loc.Document, location.StartLine, cancellationToken);

                    references.Add(new Models.SymbolReference(
                        location,
                        refSym.Definition?.Name ?? "Unknown",
                        contextCode,
                        lineText
                    ));

                    files.Add(location.FilePath);
                    if (string.Equals(location.FilePath, targetFilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        sameFileCount++;
                    }
                }
            }

            var summary = new ReferenceSummary(
                references.Count,
                sameFileCount,
                references.Count - sameFileCount,
                files.ToList()
            );

            var symbolInfo = await symbolAnalyzer.ToSymbolInfoAsync(symbol, cancellationToken: cancellationToken);

            logger.LogDebug("Found {Count} references for {SymbolName}", references.Count, symbol.Name);

            return new FindReferencesResponse(symbolInfo, references, summary).ToMarkdown();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing FindReferencesTool");
            throw;
        }
    }

    private static async Task<(ISymbol? symbol, Document document)> ResolveSymbolAsync(
        FindReferencesParams parameters,
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

    private static async Task<string?> ExtractContextCodeAsync(
        Document document,
        Models.SymbolLocation location,
        int contextLines,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceText = await document.GetTextAsync(cancellationToken);
            var lines = sourceText.Lines;

            var startLine = Math.Max(0, location.StartLine - contextLines - 1);
            var endLine = Math.Min(lines.Count - 1, location.EndLine + contextLines - 1);

            if (startLine >= endLine)
                return null;

            var text = sourceText.GetSubText(
                Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                    lines[startLine].Start,
                    lines[endLine].End
                )
            ).ToString();

            return text;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildErrorDetails(
        FindReferencesParams parameters,
        IWorkspaceManager workspaceManager,
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
