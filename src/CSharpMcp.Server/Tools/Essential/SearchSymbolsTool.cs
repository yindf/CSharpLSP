using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models;
using CSharpMcp.Server.Models.Output;
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
        ISymbolAnalyzer symbolAnalyzer,
        ILogger<SearchSymbolsTool> logger,
        CancellationToken cancellationToken)
    {
        // Validate parameters
        if (parameters == null)
        {
            return GetErrorHelpResponse("No parameters provided. You must provide a search query.");
        }

        if (string.IsNullOrWhiteSpace(parameters.Query))
        {
            return GetErrorHelpResponse("Search query is empty. Provide a symbol name to search for.");
        }

        // Use extension method to ensure default value
        var maxResults = parameters.GetMaxResults();

        logger.LogDebug("Searching symbols: {Query}, maxResults: {MaxResults}", parameters.Query, maxResults);

        // Get solution to search across ALL projects
        var solution = workspaceManager.GetCurrentSolution();
        if (solution == null)
        {
            return GetNoWorkspaceHelpResponse();
        }

        // Parse query for wildcards
        var searchTerm = parameters.Query.Replace("*", "").Replace(".", "").Trim();

        if (string.IsNullOrEmpty(searchTerm) || searchTerm.Length < 2)
        {
            return GetErrorHelpResponse($"Search term '{parameters.Query}' is too short. Use at least 2 characters for search.");
        }

        // Collect symbols from ALL projects in the solution
        var allSymbols = new List<ISymbol>();
        var processedProjects = new HashSet<string>();
        int projectsSearched = 0;
        int projectsFailed = 0;
        int projectsSkipped = 0;

        logger.LogInformation("Searching across {ProjectCount} projects in solution", solution.Projects.Count());

        var projectsList = solution.Projects.ToList();
        logger.LogInformation("Project list has {Count} projects", projectsList.Count);

        for (int i = 0; i < projectsList.Count; i++)
        {
            var project = projectsList[i];
            try
            {
                logger.LogInformation("Processing project {Index}/{Total}: {ProjectName}", i + 1, projectsList.Count, project.Name);

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation != null)
                {
                    var projectSymbols = compilation.GetSymbolsWithName(
                        n => n.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                             n.Equals(searchTerm, StringComparison.OrdinalIgnoreCase),
                        SymbolFilter.All);

                    int symbolCount = 0;
                    foreach (var symbol in projectSymbols)
                    {
                        // Avoid duplicates by using symbol name and kind
                        var key = $"{symbol.Kind}:{symbol.Name}";
                        if (!processedProjects.Contains(key))
                        {
                            allSymbols.Add(symbol);
                            processedProjects.Add(key);
                            symbolCount++;
                        }
                    }

                    projectsSearched++;
                    logger.LogInformation("Project {ProjectName}: found {Count} matching symbols", project.Name, symbolCount);
                }
                else
                {
                    projectsFailed++;
                    logger.LogInformation("Project {ProjectName}: compilation is null", project.Name);
                }
            }
            catch (Exception ex)
            {
                projectsFailed++;
                logger.LogError(ex, "Failed to get compilation for project: {ProjectName}", project.Name);
            }

            logger.LogInformation("After project {Index}: symbols={Symbols}, searched={Searched}, failed={Failed}, maxResults={Max}",
                i + 1, allSymbols.Count, projectsSearched, projectsFailed, maxResults);

            // Stop if we have enough results
            if (allSymbols.Count >= maxResults * 2)
            {
                logger.LogInformation("Stopping early: found {Count} symbols which exceeds threshold", allSymbols.Count);
                break;
            }
        }

        logger.LogInformation("Searched {ProjectsSearched} projects, {ProjectsFailed} failed, {ProjectsSkipped} skipped, found {TotalSymbols} unique symbols",
            projectsSearched, projectsFailed, projectsSkipped, allSymbols.Count);

        var results = new List<Models.SymbolInfo>();
        int skippedCount = 0;
        int errorCount = 0;
        string? lastError = null;

        foreach (var symbol in allSymbols)
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
                var lineSpan = location.GetLineSpan();
                var filePath = location.SourceTree?.FilePath ?? "";

                if (string.IsNullOrEmpty(filePath))
                {
                    skippedCount++;
                    continue;
                }

                var symbolLocation = new Models.SymbolLocation(
                    filePath,
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.EndLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    lineSpan.EndLinePosition.Character + 1
                );

                // Extract line text
                string? lineText = null;
                try
                {
                    var document = await workspaceManager.GetDocumentAsync(filePath, cancellationToken);
                    if (document != null)
                    {
                        var sourceText = await document.GetTextAsync(cancellationToken);
                        if (sourceText != null && lineSpan.StartLinePosition.Line < sourceText.Lines.Count)
                        {
                            lineText = sourceText.Lines[lineSpan.StartLinePosition.Line].ToString();
                        }
                    }
                }
                catch
                {
                    // Ignore errors extracting line text
                }

                // Create symbol info using SymbolFormatter (includes signature and documentation)
                var info = SymbolFormatter.CreateFrom(symbol, symbolLocation, lineText);

                results.Add(info);

                if (results.Count >= maxResults)
                    break;
            }
            catch (Exception ex)
            {
                errorCount++;
                lastError = ex.Message;
                logger.LogDebug(ex, "Error processing symbol during search");
            }
        }

        logger.LogDebug("Found {Count} symbols matching: {Query}, skipped: {Skipped}, errors: {Errors}",
            results.Count, parameters.Query, skippedCount, errorCount);

        // If no results found, provide helpful guidance
        if (results.Count == 0)
        {
            return GetNoResultsHelpResponse(parameters.Query, searchTerm, errorCount > 0);
        }

        return new SearchSymbolsResponse(parameters.Query, results).ToMarkdown();
    }

    /// <summary>
    /// Generate helpful error response when parameters are invalid
    /// </summary>
    private static string GetErrorHelpResponse(string message)
    {
        var sb = new System.Text.StringBuilder();
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
        var sb = new System.Text.StringBuilder();
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
        var sb = new System.Text.StringBuilder();
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
