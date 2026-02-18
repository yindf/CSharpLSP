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
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server.Tools.Essential;

[McpServerToolType]
public class SearchSymbolsTool
{
    [McpServerTool, Description("Search for symbols across the entire workspace by name with wildcard support")]
    public static async Task<string> SearchSymbols(
        [Description("Search query with optional wildcards (e.g., 'MyClass.*', '*.Controller', 'MyMethod')")] string query,
        IWorkspaceManager workspaceManager,
        ILogger<SearchSymbolsTool> logger,
        CancellationToken cancellationToken,
        [Description("Maximum number of results to return")] int maxResults = 100,
        [Description("Sort order: relevance (type>field, exact>wildcard), name, or kind")] string sortBy = "relevance")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return GetErrorHelpResponse("Search query cannot be empty. Please provide a search term.");
            }

            var workspaceError = WorkspaceErrorHelper.CheckWorkspaceLoaded(workspaceManager, "Search Symbols");
            if (workspaceError != null)
            {
                return workspaceError;
            }

            logger.LogInformation("=== SearchSymbolsTool START === Query: {Query}, maxResults: {MaxResults}", query, maxResults);

            var skippedCount = 0;
            var errorCount = 0;
            var results = new List<ISymbol>();

            var symbols = await workspaceManager.SearchSymbolsAsync(query,
                SymbolFilter.TypeAndMember, cancellationToken);
            if (!symbols.Any())
            {
                symbols = await workspaceManager.SearchSymbolsWithPatternAsync(query,
                    SymbolFilter.TypeAndMember, cancellationToken);
            }

            foreach (var symbol in symbols.Take(maxResults))
            {
                try
                {
                    var locations = symbol.Locations.Where(l => l.IsInSource).ToList();
                    if (locations.Count == 0)
                    {
                        skippedCount++;
                        continue;
                    }

                    var filePath = locations[0].SourceTree?.FilePath ?? "";
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
                results.Count, query, skippedCount, errorCount);

            if (results.Count == 0)
            {
                return GetNoResultsHelpResponse(query, errorCount > 0);
            }

            results = SortResults(results, query, sortBy).ToList();

            return BuildSearchResultsMarkdown(query, results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "=== SearchSymbolsTool ERROR ===: {Message}", ex.Message);
            logger.LogError(ex, "Stack trace: {StackTrace}", ex.StackTrace);
            return GetErrorHelpResponse($"Failed to search symbols: {ex.Message}");
        }
    }

    private static string BuildSearchResultsMarkdown(string query, List<ISymbol> results)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## Search Results: \"{query}\"");
        sb.AppendLine();
        sb.AppendLine($"**Found {results.Count} result{(results.Count != 1 ? "s" : "")}**");
        sb.AppendLine();

        var groupedByKind = results.GroupBy(r => r.Kind);

        foreach (var kindGroup in groupedByKind.OrderBy(g => g.Key))
        {
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

    private static List<ISymbol> SortResults(List<ISymbol> results, string query, string sortBy)
    {
        var sortByLower = sortBy.ToLowerInvariant();

        if (sortByLower == "name")
        {
            return results.OrderBy(s => s.GetDisplayName()).ToList();
        }

        if (sortByLower == "kind")
        {
            return results
                .OrderBy(s => s.Kind.ToString())
                .ThenBy(s => s.GetDisplayName())
                .ToList();
        }

        return SortByRelevance(results, query).ToList();
    }

    private static IEnumerable<ISymbol> SortByRelevance(List<ISymbol> symbols, string query)
    {
        bool hasWildcard = query.Contains('*');
        bool isExactMatch = !hasWildcard;

        return symbols.OrderByDescending(s =>
        {
            int score = 0;

            if (s is INamedTypeSymbol) score += 1000;
            else if (s is IMethodSymbol) score += 500;
            else if (s is IPropertySymbol) score += 200;

            if (isExactMatch && string.Equals(s.Name, query, StringComparison.OrdinalIgnoreCase))
                score += 500;

            if (s.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                score += 200;

            return score;
        }).ThenBy(s => s.Name);
    }

    private static string GetErrorHelpResponse(string message)
    {
        return MarkdownHelper.BuildErrorResponse(
            "Search Symbols",
            message,
            "SearchSymbols(query: \"SymbolName\")\nSearchSymbols(query: \"MyClass*\", maxResults: 50)",
            "- `SearchSymbols(query: \"MyClass\")`\n- `SearchSymbols(query: \"*.Controller\", maxResults: 50)`"
        );
    }

    private static string GetNoResultsHelpResponse(string originalQuery, bool hadErrors)
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
