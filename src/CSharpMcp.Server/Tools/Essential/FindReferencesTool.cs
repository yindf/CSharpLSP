using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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
            var (symbol, document) = await parameters.ResolveSymbolFromLocationAsync(workspaceManager, cancellationToken: cancellationToken);
            if (symbol == null)
            {
                var errorDetails = BuildErrorDetails(parameters, workspaceManager, cancellationToken);
                logger.LogWarning("Symbol not found: {Details}", errorDetails);
                throw new FileNotFoundException(errorDetails);
            }

            // Get the solution
            var solution = document.Project.Solution;

            // Find references
            var referencedSymbols = (await SymbolFinder.FindReferencesAsync(
                symbol,
                solution,
                cancellationToken)).ToImmutableList();

            logger.LogDebug("Found {Count} references for {SymbolName}", referencedSymbols.Count, symbol.Name);

            // Build Markdown directly
            return await BuildReferencesMarkdownAsync(symbol, referencedSymbols, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing FindReferencesTool");
            throw;
        }
    }

    private static async Task<string> BuildReferencesMarkdownAsync(
        ISymbol symbol,
        IReadOnlyList<ReferencedSymbol> referencedSymbols,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var displayName = symbol.GetDisplayName();

        sb.AppendLine($"## References: `{displayName}`");
        sb.AppendLine();
        sb.AppendLine($"**Found {referencedSymbols.Count} reference location{(referencedSymbols.Count != 1 ? "s" : "")}**");
        sb.AppendLine();

        // Group by file
        var groupedByFile = referencedSymbols
            .SelectMany(rs => rs.Locations.Select(loc => new
            {
                ReferenceLocation = loc,
                Definition = rs.Definition
            }))
            .GroupBy(r => r.ReferenceLocation.Document.FilePath);

        foreach (var fileGroup in groupedByFile.OrderBy(g => g.Key))
        {
            var fileName = System.IO.Path.GetFileName(fileGroup.Key);
            sb.AppendLine($"### {fileName}");
            sb.AppendLine();

            foreach (var refLoc in fileGroup.OrderBy(r => GetLineNumber(r.ReferenceLocation)))
            {
                var location = refLoc.ReferenceLocation.Location;
                var lineSpan = location.GetLineSpan();
                var startLine = lineSpan.StartLinePosition.Line + 1;
                var endLine = lineSpan.EndLinePosition.Line + 1;

                // Extract line text
                var lineText = await ExtractLineTextAsync(refLoc.ReferenceLocation.Document, startLine, cancellationToken);

                var lineRange = endLine > startLine ? $"L{startLine}-{endLine}" : $"L{startLine}";
                sb.AppendLine($"- {lineRange}: {lineText?.Trim() ?? ""}");
            }
            sb.AppendLine();
        }

        // Summary
        sb.AppendLine("**Summary**:");
        long totalRefs = 0;
        foreach (var rs in referencedSymbols)
        {
            totalRefs += rs.Locations.Count();
        }
        var filesAffected = groupedByFile.Count();

        sb.AppendLine($"- **Total References**: {totalRefs}");
        sb.AppendLine($"- **Files Affected**: {filesAffected}");

        return sb.ToString();
    }

    private static int GetLineNumber(ReferenceLocation location)
    {
        return location.Location.GetLineSpan().StartLinePosition.Line + 1;
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

    private static string BuildErrorDetails(
        FindReferencesParams parameters,
        IWorkspaceManager workspaceManager,
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
