using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server.Tools.HighValue;

[McpServerToolType]
public class GetInheritanceHierarchyTool
{
    [McpServerTool, Description("Get the complete inheritance hierarchy for a type including base types, interfaces, and derived types")]
    public static async Task<string> GetInheritanceHierarchy(
        [Description("The name of the type to analyze")] string symbolName,
        IWorkspaceManager workspaceManager,
        IInheritanceAnalyzer inheritanceAnalyzer,
        ILogger<GetInheritanceHierarchyTool> logger,
        CancellationToken cancellationToken,
        [Description("Path to the file containing the type")] string filePath = "",
        [Description("1-based line number near the type declaration")] int lineNumber = 0,
        [Description("Whether to include derived types in the hierarchy")] bool includeDerivedTypes = true,
        [Description("Maximum depth for derived type search (0 = unlimited, default 3)")] int maxDepth = 3)
    {
        try
        {
            var workspaceError = WorkspaceErrorHelper.CheckWorkspaceLoaded(workspaceManager, "Get Inheritance Hierarchy");
            if (workspaceError != null)
            {
                return workspaceError;
            }

            logger.LogInformation("Getting inheritance hierarchy: {FilePath}:{LineNumber} - {SymbolName}",
                filePath, lineNumber, symbolName);

            var symbol = await SymbolResolver.ResolveSymbolAsync(filePath, lineNumber, symbolName ?? "", workspaceManager, SymbolFilter.Type, cancellationToken);
            if (symbol == null)
            {
                logger.LogWarning("Type not found: {SymbolName}", symbolName ?? "at specified location");
                return MarkdownHelper.BuildSymbolNotFoundResponse(
                    filePath,
                    lineNumber,
                    symbolName,
                    "- Line numbers should point to a class, struct, interface, or enum declaration\n- Use `GetSymbols` first to find valid line numbers for types\n- Or provide a valid `symbolName` parameter");
            }

            if (symbol is not INamedTypeSymbol type)
            {
                logger.LogWarning("Symbol is not a type: {SymbolName}", symbol.Name);
                return MarkdownHelper.BuildNotATypeResponse(symbol.Name, symbol.Kind.ToString());
            }

            var solution = workspaceManager.GetCurrentSolution();
            var depth = maxDepth > 0 ? maxDepth : 3;

            var tree = await inheritanceAnalyzer.GetInheritanceTreeAsync(
                type,
                solution,
                includeDerivedTypes,
                depth,
                cancellationToken);

            logger.LogInformation("Retrieved inheritance hierarchy for: {TypeName}", type.Name);

            return BuildHierarchyMarkdown(type, tree, includeDerivedTypes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetInheritanceHierarchyTool");
            return GetErrorHelpResponse($"Failed to get inheritance hierarchy: {ex.Message}");
        }
    }

    private static string GetErrorHelpResponse(string message)
    {
        return MarkdownHelper.BuildErrorResponse(
            "Get Inheritance Hierarchy",
            message,
            "GetInheritanceHierarchy(\n    filePath: \"path/to/File.cs\",\n    lineNumber: 7,  // Line where class is declared\n    symbolName: \"MyClass\"\n)",
            "- `GetInheritanceHierarchy(filePath: \"C:/MyProject/Models.cs\", lineNumber: 15, symbolName: \"User\")`\n- `GetInheritanceHierarchy(filePath: \"./Controllers.cs\", lineNumber: 42, symbolName: \"BaseController\", includeDerivedTypes: true)`"
        );
    }

    private static string BuildHierarchyMarkdown(
        INamedTypeSymbol type,
        InheritanceTree tree,
        bool includeDerivedTypes)
    {
        var sb = new StringBuilder();
        var typeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        sb.AppendLine($"## Inheritance Hierarchy: `{typeName}`");
        sb.AppendLine();

        var kind = type.GetNamedTypeKindDisplay();
        sb.AppendLine($"**Kind**: {kind}");
        sb.AppendLine();

        if (tree.BaseTypes.Count > 0)
        {
            sb.AppendLine($"### Base Types ({tree.BaseTypes.Count})");
            sb.AppendLine();
            foreach (var baseType in tree.BaseTypes)
            {
                sb.AppendLine($"- **{baseType.ToDisplayString()}**");
                MarkdownHelper.AppendLocationIfExists(sb, baseType);
            }
            sb.AppendLine();
        }

        if (tree.Interfaces.Count > 0)
        {
            sb.AppendLine($"### Interfaces ({tree.Interfaces.Count})");
            sb.AppendLine();
            foreach (var iface in tree.Interfaces)
            {
                sb.AppendLine($"- **{iface.ToDisplayString()}**");
                MarkdownHelper.AppendLocationIfExists(sb, iface);
            }
            sb.AppendLine();
        }

        if (includeDerivedTypes && tree.DerivedTypes.Count > 0)
        {
            sb.AppendLine($"### Derived Types ({tree.DerivedTypes.Count}, depth: {tree.Depth})");
            sb.AppendLine();
            foreach (var derived in tree.DerivedTypes)
            {
                sb.AppendLine($"- **{derived.ToDisplayString()}**");
                MarkdownHelper.AppendLocationIfExists(sb, derived);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

}
