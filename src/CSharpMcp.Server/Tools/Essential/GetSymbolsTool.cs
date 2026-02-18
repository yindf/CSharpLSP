using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
public class GetSymbolsTool
{
    [McpServerTool, Description("Get all symbols (classes, methods, properties, etc.) declared in a C# file")]
    public static async Task<string> GetSymbols(
        [Description("Path to the C# file (absolute, relative, or filename only for fuzzy matching)")] string filePath,
        IWorkspaceManager workspaceManager,
        ILogger<GetSymbolsTool> logger,
        CancellationToken cancellationToken,
        [Description("Whether to include method/property implementation in output")] bool includeBody = true,
        [Description("Maximum number of lines to include for implementation code")] int maxBodyLines = 100,
        [Description("Minimum accessibility level: Public, Internal, Protected, Private")] string minVisibility = "Private",
        [Description("Filter by symbol kinds: e.g., NamedType, Method, Property, Field (null = all)")] string[]? symbolKinds = null,
        [Description("Exclude local variables and parameters from output")] bool excludeLocal = true)
    {
        try
        {
            var workspaceError = WorkspaceErrorHelper.CheckWorkspaceLoaded(workspaceManager, "Get Symbols");
            if (workspaceError != null)
            {
                return workspaceError;
            }

            logger.LogDebug("Getting symbols for: {FilePath}", filePath);

            var document = await workspaceManager.GetDocumentAsync(filePath, cancellationToken);
            if (document == null)
            {
                logger.LogWarning("Document not found: {FilePath}", filePath);
                return GetErrorHelpResponse(
                    $"Document not found: `{filePath}`\n\nMake sure the file path is correct and the workspace is loaded.");
            }

            var symbols = (await document.GetDeclaredSymbolsAsync(cancellationToken)).ToImmutableList();
            symbols = ApplyFilters(symbols, minVisibility, symbolKinds, excludeLocal);

            logger.LogDebug("Found {Count} symbols in {FilePath}", symbols.Count, filePath);

            return await BuildSymbolsMarkdownAsync(filePath, symbols, includeBody, maxBodyLines, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetSymbolsTool");
            return GetErrorHelpResponse($"Failed to get symbols: {ex.Message}");
        }
    }

    private static string GetErrorHelpResponse(string message)
    {
        return MarkdownHelper.BuildErrorResponse(
            "Get Symbols",
            message,
            "GetSymbols(\n    filePath: \"path/to/File.cs\"\n)",
            "- `GetSymbols(filePath: \"C:/MyProject/MyClass.cs\")`\n- `GetSymbols(filePath: \"./Utils.cs\", minVisibility: \"Public\")`"
        );
    }

    private static async Task<string> BuildSymbolsMarkdownAsync(
        string filePath,
        IReadOnlyList<ISymbol> symbols,
        bool includeBody,
        int maxBodyLines,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var fileName = System.IO.Path.GetFileName(filePath);

        sb.AppendLine($"## Symbols: {fileName}");
        sb.AppendLine($"**Total: {symbols.Count} symbol{(symbols.Count != 1 ? "s" : "")}**");
        sb.AppendLine();

        foreach (var symbol in symbols.Where(s => !s.IsImplicitlyDeclared))
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

    private static ImmutableList<ISymbol> ApplyFilters(
        ImmutableList<ISymbol> symbols,
        string minVisibility,
        string[]? symbolKinds,
        bool excludeLocal)
    {
        var filtered = symbols.Where(s => !s.IsImplicitlyDeclared);

        if (Enum.TryParse<Accessibility>(minVisibility, ignoreCase: true, out var minAccess) &&
            minAccess != Accessibility.Private)
        {
            filtered = FilterByAccessibility(filtered, minAccess);
        }

        if (symbolKinds is { Length: > 0 })
        {
            var kinds = new HashSet<SymbolKind>(
                symbolKinds.Select(ParseSymbolKind));
            filtered = filtered.Where(s => kinds.Contains(s.Kind));
        }

        if (excludeLocal)
        {
            filtered = filtered.Where(s => s.Kind != SymbolKind.Local && s.Kind != SymbolKind.Parameter);
        }

        return filtered.ToImmutableList();
    }

    private static IEnumerable<ISymbol> FilterByAccessibility(IEnumerable<ISymbol> symbols, Accessibility minLevel)
    {
        var accessibilityOrder = new[] { Accessibility.Private, Accessibility.Protected, Accessibility.Internal, Accessibility.Public };
        var minIndex = Array.IndexOf(accessibilityOrder, minLevel);

        return symbols.Where(s =>
        {
            var accessibility = s.DeclaredAccessibility;
            var index = Array.IndexOf(accessibilityOrder, accessibility);
            return index >= minIndex || accessibility == Accessibility.ProtectedAndInternal;
        });
    }

    private static SymbolKind ParseSymbolKind(string kind)
    {
        return Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out var result)
            ? result
            : SymbolKind.Namespace;
    }
}
