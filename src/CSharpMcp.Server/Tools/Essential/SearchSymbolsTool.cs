using System;
using System.Collections.Generic;
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
/// search_symbols 工具 - 搜索跨工作区的符号
/// </summary>
[McpServerToolType]
public class SearchSymbolsTool
{
    /// <summary>
    /// Search for symbols across the entire workspace by name pattern
    /// </summary>
    [McpServerTool]
    public static async Task<string> SearchSymbols(
        SearchSymbolsParams parameters,
        IWorkspaceManager workspaceManager,
        ILogger<SearchSymbolsTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validate parameters
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            if (string.IsNullOrWhiteSpace(parameters.Query))
            {
                throw new ArgumentException("Search query cannot be empty.", nameof(parameters));
            }

            // Use extension method to ensure default value
            var maxResults = parameters.GetMaxResults();

            logger.LogInformation("=== SearchSymbolsTool START === Query: {Query}, maxResults: {MaxResults}", parameters.Query, maxResults);

            var skippedCount = 0;
            var errorCount = 0;
            var results = new List<ISymbol>();
            foreach (var symbol in (await workspaceManager.SearchSymbolsAsync(parameters.Query)).Take(maxResults))
            {
                try
                {
                    // Check if symbol has source location
                    var locations = symbol.Locations.Where(l => l.IsInSource).ToList();
                    if (locations.Count == 0)
                    {
                        skippedCount++;
                        continue;
                    }

                    // Get the first source location
                    var location = locations[0];
                    var filePath = location.SourceTree?.FilePath ?? "";

                    if (string.IsNullOrEmpty(filePath))
                    {
                        skippedCount++;
                        continue;
                    }

                    results.Add(symbol);

                    if (results.Count >= maxResults)
                        break;
                }
                catch (Exception ex)
                {
                    errorCount++;
                    logger.LogInformation(ex, "Error processing symbol during search");
                }
            }

            logger.LogInformation("Found {Count} symbols matching: {Query}, skipped: {Skipped}, errors: {Errors}",
                results.Count, parameters.Query, skippedCount, errorCount);

            // If no results found, provide helpful guidance
            if (results.Count == 0)
            {
                return GetNoResultsHelpResponse(parameters.Query, parameters.Query, errorCount > 0);
            }

            // Build Markdown directly
            return BuildSearchResultsMarkdown(parameters.Query, results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "=== SearchSymbolsTool ERROR ===: {Message}", ex.Message);
            logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
            throw;
        }
    }

    private static string BuildSearchResultsMarkdown(string query, List<ISymbol> results)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## Search Results: \"{query}\"");
        sb.AppendLine();
        sb.AppendLine($"**Found {results.Count} result{(results.Count != 1 ? "s" : "")}**");
        sb.AppendLine();

        // Group by kind
        var groupedByKind = results.GroupBy(r => r.Kind);

        foreach (var kindGroup in groupedByKind.OrderBy(g => g.Key))
        {
            // 对于 NamedType，使用第一个符号的实际类型名称作为分组标题
            string groupTitle;
            if (kindGroup.Key == SymbolKind.NamedType && kindGroup.Any())
            {
                var firstSymbol = kindGroup.First();
                var displayKind = firstSymbol.GetDisplayKind();
                groupTitle = SymbolExtensions.PluralizeKind(displayKind);
            }
            else
            {
                var kind = kindGroup.Key.ToString().ToLower();
                groupTitle = SymbolExtensions.PluralizeKind(kind);
            }
            sb.AppendLine($"### {groupTitle}");
            sb.AppendLine();

            foreach (var symbol in kindGroup.OrderBy(s => s.GetDisplayName()))
            {
                var displayName = symbol.GetDisplayName();
                var (startLine, endLine) = symbol.GetLineRange();
                var filePath = symbol.GetFilePath();
                var signature = symbol.GetSignature();
                var accessibility = symbol.GetAccessibilityString();

                var fileName = System.IO.Path.GetFileName(filePath);
                var containingType = symbol.GetContainingTypeName();

                sb.Append($"- **{displayName}**");
                if (!string.IsNullOrEmpty(containingType))
                {
                    sb.Append($" (in {containingType})");
                }
                sb.AppendLine($" `{accessibility} {symbol.GetDisplayKind()}`");

                if (startLine > 0)
                {
                    sb.AppendLine($"  - `{fileName}:{startLine}`");
                }

                if (!string.IsNullOrEmpty(signature))
                {
                    sb.AppendLine($"  - `{signature}`");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generate helpful error response when parameters are invalid
    /// </summary>
    private static string GetErrorHelpResponse(string message)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Invalid Input");
        sb.AppendLine();
        sb.AppendLine($"**Error**: {message}");
        sb.AppendLine();
        sb.AppendLine("**Usage**:");
        sb.AppendLine("```");
        sb.AppendLine("SearchSymbols(query: \"SymbolName\")");
        sb.AppendLine("SearchSymbols(query: \"MyClass*\", maxResults: 50)");
        sb.AppendLine("```");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Generate helpful response when no workspace is loaded
    /// </summary>
    private static string GetNoWorkspaceHelpResponse()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## No Workspace Loaded");
        sb.AppendLine();
        sb.AppendLine("**Action Required**: Call `LoadWorkspace` first to load a C# solution or project.");
        sb.AppendLine();
        sb.AppendLine("**Usage**:");
        sb.AppendLine("```");
        sb.AppendLine("LoadWorkspace(path: \"path/to/MySolution.sln\")");
        sb.AppendLine("LoadWorkspace(path: \"path/to/MyProject.csproj\")");
        sb.AppendLine("LoadWorkspace(path: \".\")  // Auto-detect in current directory");
        sb.AppendLine("```");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Generate helpful response when no results are found
    /// </summary>
    private static string GetNoResultsHelpResponse(string originalQuery, string searchTerm, bool hadErrors)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## No Results Found");
        sb.AppendLine();
        sb.AppendLine($"**Query**: \"{originalQuery}\"");
        sb.AppendLine();
        sb.AppendLine("**Try**:");
        sb.AppendLine("1. Shorter search term");
        sb.AppendLine("2. Different part of the name");
        sb.AppendLine("3. Wildcards: `*MyTerm*`, `MyClass*`, `*Manager`");
        sb.AppendLine("4. `GetDiagnostics` to check for workspace errors");
        sb.AppendLine();

        return sb.ToString();
    }
}
