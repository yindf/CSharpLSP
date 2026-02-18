using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models.Output;
using CSharpMcp.Server.Models.Tools;
using CSharpMcp.Server.Roslyn;
using System.Text;

namespace CSharpMcp.Server.Tools.HighValue;

/// <summary>
/// get_type_members 工具 - 获取类型的成员
/// </summary>
[McpServerToolType]
public class GetTypeMembersTool
{
    /// <summary>
    /// Get all members of a type
    /// </summary>
    [McpServerTool]
    public static async Task<string> GetTypeMembers(
        GetTypeMembersParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        ILogger<GetTypeMembersTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            logger.LogDebug("Getting type members: {FilePath}:{LineNumber} - {SymbolName}",
                parameters.FilePath, parameters.LineNumber, parameters.SymbolName);

            // Resolve the type symbol
            var (symbol, document) = await ResolveSymbolAsync(parameters, workspaceManager, symbolAnalyzer, cancellationToken);
            if (symbol == null)
            {
                logger.LogWarning("Type not found: {SymbolName}", parameters.SymbolName ?? "at specified location");
                return GetNoSymbolFoundHelpResponse(parameters.FilePath, parameters.LineNumber, parameters.SymbolName);
            }

            if (symbol is not INamedTypeSymbol type)
            {
                logger.LogWarning("Symbol is not a type: {SymbolName}", symbol.Name);
                return GetNotATypeHelpResponse(symbol.Name, symbol.Kind.ToString(), parameters.FilePath, parameters.LineNumber);
            }

            // Get all members
            var members = await GetMembersAsync(type, parameters.IncludeInherited, parameters.FilterKinds, cancellationToken);

            // Convert to response format
            var memberItems = members.Select(m => new MemberInfoItem(
                m.Name,
                m.Kind,
                m.Accessibility,
                m.IsStatic,
                m.IsVirtual,
                m.IsOverride,
                m.IsAbstract,
                m.Location,
                m.ContainingType,
                m.Signature?.ReturnType,
                m.Signature?.Parameters.ToList() ?? new List<string>()
            )).ToList();

            var membersData = new TypeMembersData(
                TypeName: type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Members: memberItems,
                TotalCount: members.Count
            );

            logger.LogDebug("Retrieved {Count} members for: {TypeName}", members.Count, type.Name);

            return new GetTypeMembersResponse(type.Name, membersData).ToMarkdown();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetTypeMembersTool");
            throw;
        }
    }

    private static async Task<List<Models.SymbolInfo>> GetMembersAsync(
        INamedTypeSymbol type,
        bool includeInherited,
        IReadOnlyList<Models.SymbolKind>? filterKinds,
        CancellationToken cancellationToken)
    {
        var members = new List<Models.SymbolInfo>();

        // Get all members
        var allMembers = includeInherited
            ? type.AllInterfaces
                .Concat(new[] { type })
                .SelectMany(t => t.GetMembers())
                .Distinct(SymbolEqualityComparer.Default)
            : type.GetMembers();

        foreach (var member in allMembers)
        {
            // Skip implicitly declared members
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            // Get the symbol kind
            var symbolKind = member.Kind switch
            {
                SymbolKind.Method => Models.SymbolKind.Method,
                SymbolKind.Property => Models.SymbolKind.Property,
                SymbolKind.Field => Models.SymbolKind.Field,
                SymbolKind.Event => Models.SymbolKind.Event,
                SymbolKind.NamedType => member.ToDisplayString().EndsWith("Attribute)") ? Models.SymbolKind.Attribute : Models.SymbolKind.Class,
                _ => Models.SymbolKind.Unknown
            };

            // Apply filter if specified
            if (filterKinds != null && filterKinds.Count > 0 && !filterKinds.Contains(symbolKind))
            {
                continue;
            }

            // Get location
            var location = member.Locations.FirstOrDefault();
            var symbolLocation = new Models.SymbolLocation(
                location?.SourceTree?.FilePath ?? "",
                location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
                location?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
                location?.GetLineSpan().StartLinePosition.Character + 1 ?? 0,
                location?.GetLineSpan().EndLinePosition.Character + 1 ?? 0
            );

            // Skip metadata members
            if (location?.IsInMetadata == true)
            {
                continue;
            }

            var accessibility = member.DeclaredAccessibility switch
            {
                Microsoft.CodeAnalysis.Accessibility.Public => Models.Accessibility.Public,
                Microsoft.CodeAnalysis.Accessibility.Internal => Models.Accessibility.Internal,
                Microsoft.CodeAnalysis.Accessibility.Protected => Models.Accessibility.Protected,
                Microsoft.CodeAnalysis.Accessibility.Private => Models.Accessibility.Private,
                Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Models.Accessibility.ProtectedInternal,
                Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Models.Accessibility.PrivateProtected,
                _ => Models.Accessibility.NotApplicable
            };

            // Build signature based on member type
            Models.SymbolSignature? signature = null;
            bool isAsync = false;

            if (member is IMethodSymbol method)
            {
                var methodParams = method.Parameters
                    .Select(param => param.Type?.ToDisplayString() ?? "object")
                    .ToList();

                var typeParameters = method.TypeParameters
                    .Select(tp => tp.Name)
                    .ToList();

                signature = new Models.SymbolSignature(
                    method.Name,
                    method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    method.ReturnType?.ToDisplayString() ?? "void",
                    methodParams,
                    typeParameters
                );
                isAsync = method.IsAsync;
            }
            else if (member is IPropertySymbol property)
            {
                var propertyParams = property.Parameters
                    .Select(param => param.Type?.ToDisplayString() ?? "object")
                    .ToList();

                // Properties don't have TypeParameters
                var typeParameters = Array.Empty<string>();

                signature = new Models.SymbolSignature(
                    property.Name,
                    property.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    property.Type?.ToDisplayString() ?? "object",
                    propertyParams,
                    typeParameters
                );
            }

            var symbolInfo = new Models.SymbolInfo
            {
                Name = member.Name,
                Kind = symbolKind,
                Accessibility = accessibility,
                Namespace = member.ContainingNamespace?.ToDisplayString() ?? "",
                ContainingType = member.ContainingType?.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "",
                IsStatic = member.IsStatic,
                IsVirtual = member.IsVirtual,
                IsOverride = member.IsOverride,
                IsAbstract = member.IsAbstract,
                IsAsync = isAsync,
                Location = symbolLocation,
                Signature = signature
            };

            members.Add(symbolInfo);
        }

        return members;
    }

    private static async Task<(ISymbol? symbol, Document document)> ResolveSymbolAsync(
        GetTypeMembersParams parameters,
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

            // If we found a symbol but it's not a type, try to get the containing type
            if (symbol is not INamedTypeSymbol && symbol != null)
            {
                symbol = symbol.ContainingType;
            }
        }

        // Try by name
        if (symbol == null && !string.IsNullOrEmpty(parameters.SymbolName))
        {
            var symbols = await symbolAnalyzer.FindSymbolsByNameAsync(
                document,
                parameters.SymbolName,
                parameters.LineNumber,
                cancellationToken);

            // Prefer named type symbols
            symbol = symbols.OfType<INamedTypeSymbol>().FirstOrDefault()
                     ?? symbols.FirstOrDefault();
        }

        return (symbol, document);
    }

    /// <summary>
    /// Generate helpful error response when no symbol is found
    /// </summary>
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

    /// <summary>
    /// Generate helpful error response when symbol is not a type
    /// </summary>
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
