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

namespace CSharpMcp.Server.Tools.HighValue;

[McpServerToolType]
public class GetTypeMembersTool
{
    [McpServerTool, Description("Get all members (methods, properties, fields, events) of a type")]
    public static async Task<string> GetTypeMembers(
        [Description("Path to the file containing the type")] string filePath,
        IWorkspaceManager workspaceManager,
        ILogger<GetTypeMembersTool> logger,
        CancellationToken cancellationToken,
        [Description("1-based line number near the type declaration")] int lineNumber = 0,
        [Description("The name of the type to get members for")] string? symbolName = null,
        [Description("Whether to include inherited members")] bool includeInherited = false)
    {
        try
        {
            var workspaceError = WorkspaceErrorHelper.CheckWorkspaceLoaded(workspaceManager, "Get Type Members");
            if (workspaceError != null)
            {
                return workspaceError;
            }

            logger.LogInformation("Getting type members: {FilePath}:{LineNumber} - {SymbolName}",
                filePath, lineNumber, symbolName);

            var symbol = await SymbolResolver.ResolveSymbolAsync(filePath, lineNumber, symbolName ?? "", workspaceManager, SymbolFilter.Type, cancellationToken);
            if (symbol == null)
            {
                logger.LogWarning("Type not found: {SymbolName}", symbolName ?? "at specified location");
                return GetNoSymbolFoundHelpResponse(filePath, lineNumber, symbolName);
            }

            if (symbol is not INamedTypeSymbol type)
            {
                logger.LogWarning("Symbol is not a type: {SymbolName}", symbol.Name);
                return GetNotATypeHelpResponse(symbol.Name, symbol.Kind.ToString(), filePath, lineNumber);
            }

            var members = GetMembers(type, includeInherited);

            logger.LogInformation("Retrieved {Count} members for: {TypeName}", members.Count, type.Name);

            return BuildMembersMarkdown(type, members);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetTypeMembersTool");
            return GetErrorHelpResponse($"Failed to get type members: {ex.Message}");
        }
    }

    private static string GetErrorHelpResponse(string message)
    {
        return MarkdownHelper.BuildErrorResponse(
            "Get Type Members",
            message,
            "GetTypeMembers(\n    filePath: \"path/to/File.cs\",\n    lineNumber: 7,  // Line where class is declared\n    symbolName: \"MyClass\"\n)",
            "- `GetTypeMembers(filePath: \"C:/MyProject/Models.cs\", lineNumber: 15, symbolName: \"User\")`\n- `GetTypeMembers(filePath: \"./Controllers.cs\", lineNumber: 42, symbolName: \"BaseController\", includeInherited: true)`"
        );
    }

    private static List<ISymbol> GetMembers(
        INamedTypeSymbol type,
        bool includeInherited)
    {
        var allMembers = includeInherited
            ? type.AllInterfaces
                .Concat(new[] { type })
                .SelectMany(t => t.GetMembers())
                .Distinct(SymbolEqualityComparer.Default)
            : type.GetMembers();

        return allMembers
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m.Locations.FirstOrDefault()?.IsInMetadata != true)
            .ToList();
    }

    private static string BuildMembersMarkdown(
        INamedTypeSymbol type,
        List<ISymbol> members)
    {
        var sb = new StringBuilder();
        var typeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        sb.AppendLine($"## Type Members: `{typeName}`");
        sb.AppendLine();
        sb.AppendLine($"**Total: {members.Count} member{(members.Count != 1 ? "s" : "")}**");
        sb.AppendLine();

        var groupedByKind = members.GroupBy(m => m.GetDisplayKind());

        foreach (var kindGroup in groupedByKind.OrderBy(g => g.Key))
        {
            var title = SymbolExtensions.PluralizeKind(kindGroup.Key);
            sb.AppendLine($"### {title}");
            sb.AppendLine();

            foreach (var member in kindGroup.OrderBy(m => m.GetDisplayName()))
            {
                if (member.IsImplicitlyDeclared)
                    continue;

                var displayName = member.GetDisplayName();
                var (startLine, endLine) = member.GetLineRange();
                var displayKind = member.GetDisplayKind();
                var accessibility = member.GetAccessibilityString();
                var signature = member.GetSignature();

                sb.Append($"- **{displayName}**");
                sb.Append($" ({accessibility} {displayKind})");
                if (startLine > 0)
                {
                    sb.Append($" L{startLine}-{endLine}");
                }

                if (!string.IsNullOrEmpty(signature))
                {
                    sb.AppendLine($" - `{signature}`");
                }
                else
                {
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GetNoSymbolFoundHelpResponse(string filePath, int? lineNumber, string? symbolName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## No Symbol Found");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(symbolName))
        {
            sb.AppendLine($"**Symbol Name**: {symbolName}");
        }
        if (lineNumber.HasValue)
        {
            sb.AppendLine($"**Line Number**: {lineNumber.Value}");
        }
        sb.AppendLine($"**File**: {filePath}");
        sb.AppendLine();
        sb.AppendLine("**Tips**:");
        sb.AppendLine("- Line numbers should point to a class, struct, interface, or enum declaration");
        sb.AppendLine("- Use `GetSymbols` first to find valid line numbers for types");
        sb.AppendLine("- Or provide a valid `symbolName` parameter");
        sb.AppendLine();
        sb.AppendLine("**Usage**:");
        sb.AppendLine("```");
        sb.AppendLine("GetTypeMembers(");
        sb.AppendLine("    filePath: \"path/to/File.cs\",");
        sb.AppendLine("    lineNumber: 7  // Line where class is declared");
        sb.AppendLine(")");
        sb.AppendLine("```");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string GetNotATypeHelpResponse(string symbolName, string symbolKind, string filePath, int? lineNumber)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Not a Type");
        sb.AppendLine();
        sb.AppendLine($"**Symbol**: `{symbolName}`");
        sb.AppendLine($"**Kind**: {symbolKind}");
        sb.AppendLine();
        sb.AppendLine("The symbol at the specified location is not a class, struct, interface, or enum.");
        sb.AppendLine();
        sb.AppendLine("**Tips**:");
        sb.AppendLine("- Use `GetSymbols` first to find valid type declarations");
        sb.AppendLine("- Ensure the line number points to a type declaration (not a method, property, etc.)");
        sb.AppendLine();
        return sb.ToString();
    }
}
