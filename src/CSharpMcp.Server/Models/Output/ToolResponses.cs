using System.Collections.Generic;
using System.Linq;
using System.Text;
using CSharpMcp.Server.Models;
using CSharpMcp.Server.Models.Tools;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server.Models.Output;

/// <summary>
/// 工具响应基类
/// </summary>
public abstract record ToolResponse
{
    /// <summary>
    /// 转换为 Markdown 格式
    /// </summary>
    public abstract string ToMarkdown();

    /// <summary>
    /// 获取符号的显示名称（处理 .ctor 等特殊情况）
    /// </summary>
    protected static string GetDisplayName(SymbolInfo symbol)
    {
        if (symbol.Name == ".ctor" && !string.IsNullOrEmpty(symbol.ContainingType))
        {
            return symbol.ContainingType.Split('.').Last();
        }
        return symbol.Name;
    }

    /// <summary>
    /// 获取符号的显示名称（简单字符串版本）
    /// </summary>
    protected static string GetDisplayName(string name, string? containingType)
    {
        if (name == ".ctor" && !string.IsNullOrEmpty(containingType))
        {
            return containingType.Split('.').Last();
        }
        return name;
    }

    /// <summary>
    /// 获取相对路径（如果可能）
    /// </summary>
    protected static string GetRelativePath(string filePath)
    {
        try
        {
            var currentDir = System.IO.Directory.GetCurrentDirectory();
            var relativePath = System.IO.Path.GetRelativePath(currentDir, filePath);
            return string.IsNullOrEmpty(relativePath) ? filePath : relativePath.Replace('\\', '/');
        }
        catch
        {
            return filePath.Replace('\\', '/');
        }
    }
}

/// <summary>
/// 错误响应
/// </summary>
public record ErrorResponse(string Message) : ToolResponse
{
    public override string ToMarkdown() => $"**Error**: {Message}";
}

/// <summary>
/// get_symbols 输出
/// </summary>
public record GetSymbolsResponse(
    string FilePath,
    IReadOnlyList<SymbolInfo> Symbols,
    int TotalCount
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Symbols: {System.IO.Path.GetFileName(FilePath)}");
        sb.AppendLine($"**Total: {TotalCount} symbol{(TotalCount != 1 ? "s" : "")}**");
        sb.AppendLine();

        // 使用统一的格式化器输出符号（简化格式）
        foreach (var symbol in Symbols)
        {
            sb.AppendLine(SymbolFormatter.FormatSymbolSimplified(symbol));
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// go_to_definition 输出
/// </summary>
public record GoToDefinitionResponse(
    SymbolInfo Symbol,
    bool IsTruncated,
    int TotalLines
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        var displayName = GetDisplayName(Symbol);
        sb.AppendLine($"### Definition: `{displayName}`");

        // Calculate actual lines shown
        var linesShown = Symbol.SourceCode?.Split('\n').Length ?? 0;

        if (IsTruncated)
        {
            sb.AppendLine($"(showing {linesShown} of {TotalLines} total lines)");
        }

        sb.AppendLine();

        // 使用统一的详细格式化器
        var relativePath = GetRelativePath(Symbol.Location.FilePath);
        sb.Append(SymbolFormatter.FormatSymbolDetailed(Symbol, relativePath, true, null, TotalLines));

        return sb.ToString();
    }
}

/// <summary>
/// find_references 输出
/// </summary>
public record ReferenceSummary(
    int TotalReferences,
    int ReferencesInSameFile,
    int ReferencesInOtherFiles,
    IReadOnlyList<string> Files
);

public record FindReferencesResponse(
    SymbolInfo Symbol,
    IReadOnlyList<SymbolReference> References,
    ReferenceSummary Summary
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        var displayName = GetDisplayName(Symbol);
        sb.AppendLine($"## References: `{displayName}`");
        sb.AppendLine();
        sb.AppendLine($"**Found {References.Count} reference{(References.Count != 1 ? "s" : "")}**");
        sb.AppendLine();

        // Group by file
        var groupedByFile = References.GroupBy(r => r.Location.FilePath);

        foreach (var fileGroup in groupedByFile.OrderBy(g => g.Key))
        {
            var fileName = System.IO.Path.GetFileName(fileGroup.Key);
            sb.AppendLine($"### {fileName}");
            sb.AppendLine();

            foreach (var @ref in fileGroup.OrderBy(r => r.Location.StartLine))
            {
                var lineText = @ref.LineText?.Trim() ?? "";
                var lineRange = @ref.Location.EndLine > @ref.Location.StartLine
                    ? $"L{@ref.Location.StartLine}-{@ref.Location.EndLine}"
                    : $"L{@ref.Location.StartLine}";
                sb.AppendLine($"- {lineRange}: {lineText}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("**Summary**:");
        sb.AppendLine($"- Total references: {Summary.TotalReferences}");
        sb.AppendLine($"- In same file: {Summary.ReferencesInSameFile}");
        sb.AppendLine($"- In other files: {Summary.ReferencesInOtherFiles}");
        sb.AppendLine($"- Files affected: {Summary.Files.Count}");

        return sb.ToString();
    }

    private static string GetNamespaceFromPath(string filePath)
    {
        // Extract namespace/assembly from file path
        // For src/CSharpMcp.Server/Roslyn/WorkspaceManager.cs -> CSharpMcp.Server.Roslyn
        var parts = filePath.Replace('\\', '/').Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "src")
            {
                // Find the next non-empty part as the project/assembly
                var j = i + 1;
                if (j < parts.Length && !string.IsNullOrEmpty(parts[j]))
                {
                    return parts[j];
                }
            }
        }
        return "Unknown";
    }
}

/// <summary>
/// search_symbols 输出
/// </summary>
public record SearchSymbolsResponse(
    string Query,
    IReadOnlyList<SymbolInfo> Symbols
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Search Results: \"{Query}\"");
        sb.AppendLine();
        sb.AppendLine($"**Found {Symbols.Count} symbol{(Symbols.Count != 1 ? "s" : "")}**");
        sb.AppendLine();

        // Group by file
        var grouped = Symbols.GroupBy(s => s.Location.FilePath);

        foreach (var group in grouped.OrderBy(g => g.Key))
        {
            var relativePath = GetRelativePath(group.Key);
            sb.AppendLine($"### {relativePath}");
            sb.AppendLine();

            foreach (var symbol in group.OrderBy(s => s.Location.StartLine))
            {
                // 使用统一的格式化器输出简化版本
                sb.AppendLine(SymbolFormatter.FormatSymbolSimplified(symbol));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// resolve_symbol 输出
/// </summary>
public record ResolveSymbolResponse(
    SymbolInfo Symbol,
    string? Definition,
    IReadOnlyList<SymbolReference>? References
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        var displayName = GetDisplayName(Symbol);

        // 使用统一的详细格式化器输出符号详情
        var relativePath = GetRelativePath(Symbol.Location.FilePath);
        var totalLines = Symbol.Location.EndLine - Symbol.Location.StartLine + 1;
        sb.Append(SymbolFormatter.FormatSymbolDetailed(Symbol, relativePath, !string.IsNullOrEmpty(Definition), null, totalLines));

        // 添加 Definition（如果单独提供）
        if (!string.IsNullOrEmpty(Definition))
        {
            sb.AppendLine("**Definition**:");
            sb.AppendLine("```csharp");
            sb.AppendLine(Definition);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // 添加 References
        if (References != null && References.Count > 0)
        {
            sb.AppendLine($"**References** ({References.Count} found):");
            // 按文件分组
            var groupedByFile = References.GroupBy(r => r.Location.FilePath);
            foreach (var fileGroup in groupedByFile.Take(5))
            {
                var refPath = GetRelativePath(fileGroup.Key);
                sb.AppendLine($"  - {refPath}:");
                foreach (var @ref in fileGroup.Take(3))
                {
                    var lineInfo = @ref.LineText != null ? $" - {@ref.LineText.Trim()}" : "";
                    sb.AppendLine($"    L{@ref.Location.StartLine}-{@ref.Location.EndLine}{lineInfo}");
                }
                if (fileGroup.Count() > 3)
                {
                    sb.AppendLine($"    ... and {fileGroup.Count() - 3} more in this file");
                }
            }
            if (groupedByFile.Count() > 5)
            {
                var remainingRefs = groupedByFile.Skip(5).Sum(g => g.Count());
                sb.AppendLine($"  - ... and {remainingRefs} more in other files");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// get_inheritance_hierarchy 输出
/// </summary>
public record InheritanceHierarchyData(
    string TypeName,
    SymbolKind Kind,
    IReadOnlyList<SymbolInfo> BaseTypes,
    IReadOnlyList<SymbolInfo> Interfaces,
    IReadOnlyList<SymbolInfo> DerivedTypes,
    int Depth
);

public record InheritanceHierarchyResponse(
    string TypeName,
    InheritanceHierarchyData Hierarchy
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Inheritance Hierarchy: `{TypeName}`");
        sb.AppendLine();

        // Base types
        if (Hierarchy.BaseTypes.Count > 0)
        {
            sb.AppendLine("**Base Types**:");
            foreach (var baseType in Hierarchy.BaseTypes)
            {
                if (!string.IsNullOrEmpty(baseType.Location.FilePath))
                {
                    var relativePath = GetRelativePath(baseType.Location.FilePath);
                    sb.AppendLine($"- {baseType.Name} - {relativePath}:L{baseType.Location.StartLine}-{baseType.Location.EndLine}");
                }
                else
                {
                    sb.AppendLine($"- {baseType.Name} (system type)");
                }
            }
            sb.AppendLine();
        }

        // Interfaces
        if (Hierarchy.Interfaces.Count > 0)
        {
            sb.AppendLine("**Implemented Interfaces**:");
            foreach (var iface in Hierarchy.Interfaces)
            {
                if (!string.IsNullOrEmpty(iface.Location.FilePath))
                {
                    var relativePath = GetRelativePath(iface.Location.FilePath);
                    sb.AppendLine($"- {iface.Name} - {relativePath}:L{iface.Location.StartLine}-{iface.Location.EndLine}");
                }
                else
                {
                    sb.AppendLine($"- {iface.Name} (system type)");
                }
            }
            sb.AppendLine();
        }

        // Derived types
        if (Hierarchy.DerivedTypes.Count > 0)
        {
            sb.AppendLine($"**Derived Types** ({Hierarchy.DerivedTypes.Count}, depth: {Hierarchy.Depth}):");
            foreach (var derived in Hierarchy.DerivedTypes)
            {
                var relativePath = GetRelativePath(derived.Location.FilePath);
                var displayName = GetDisplayName(derived);
                sb.AppendLine($"- **{displayName}** ({derived.Kind}) - {relativePath}:L{derived.Location.StartLine}-{derived.Location.EndLine}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// get_call_graph 输出
/// </summary>
public record CallLocationItem(
    string ContainingSymbol,
    Models.SymbolLocation Location
);

public record CallRelationshipItem(
    SymbolInfo Symbol,
    IReadOnlyList<CallLocationItem> CallLocations
);

public record CallStatisticsItem(
    int TotalCallers,
    int TotalCallees,
    int CyclomaticComplexity
);

public record CallGraphResponse(
    string MethodName,
    IReadOnlyList<CallRelationshipItem> Callers,
    IReadOnlyList<CallRelationshipItem> Callees,
    CallStatisticsItem Statistics
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Call Graph: `{MethodName}`");
        sb.AppendLine();

        // Statistics
        sb.AppendLine("**Statistics**:");
        sb.AppendLine($"- Total callers: {Statistics.TotalCallers}");
        sb.AppendLine($"- Total callees: {Statistics.TotalCallees}");
        sb.AppendLine($"- Cyclomatic complexity: {Statistics.CyclomaticComplexity}");
        sb.AppendLine();

        // Callers - 按文件分组
        if (Callers.Count > 0)
        {
            sb.AppendLine($"**Called By** ({Callers.Count}):");
            var groupedByFile = Callers.GroupBy(c => c.Symbol.Location.FilePath);

            foreach (var fileGroup in groupedByFile)
            {
                var fileName = System.IO.Path.GetFileName(fileGroup.Key);
                sb.AppendLine($"  - {fileName}:");
                foreach (var caller in fileGroup)
                {
                    var displayName = GetDisplayName(caller.Symbol);
                    var lineRange = $"L{caller.Symbol.Location.StartLine}-{caller.Symbol.Location.EndLine}";
                    sb.AppendLine($"    - **{displayName}** ({lineRange})");
                }
            }
            sb.AppendLine();
        }

        // Callees - 按文件分组
        if (Callees.Count > 0)
        {
            sb.AppendLine($"**Calls** ({Callees.Count}):");
            var groupedByFile = Callees.GroupBy(c => c.Symbol.Location.FilePath);

            foreach (var fileGroup in groupedByFile)
            {
                var fileName = System.IO.Path.GetFileName(fileGroup.Key);
                sb.AppendLine($"  - {fileName}:");
                foreach (var callee in fileGroup)
                {
                    var displayName = GetDisplayName(callee.Symbol);
                    var lineRange = $"L{callee.Symbol.Location.StartLine}-{callee.Symbol.Location.EndLine}";
                    sb.AppendLine($"    - **{displayName}** ({lineRange})");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// get_type_members 输出
/// </summary>
public record MemberInfoItem(
    string Name,
    SymbolKind Kind,
    Accessibility Accessibility,
    bool IsStatic,
    bool IsVirtual,
    bool IsOverride,
    bool IsAbstract,
    Models.SymbolLocation Location,
    string? ContainingType,
    string? ReturnType,
    IReadOnlyList<string> Parameters
);

public record MethodInfoItem(
    MemberInfoItem Base,
    string? ReturnType,
    IReadOnlyList<string> Parameters
);

public record EventInfoItem(
    MemberInfoItem Base,
    string? EventType
);

public record TypeMembersData(
    string TypeName,
    IReadOnlyList<MemberInfoItem> Members,
    int TotalCount
);

public record GetTypeMembersResponse(
    string TypeName,
    TypeMembersData MembersData
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Type Members: `{TypeName}`");
        sb.AppendLine();
        sb.AppendLine($"**Total: {MembersData.TotalCount} member{(MembersData.TotalCount != 1 ? "s" : "")}**");
        sb.AppendLine();

        // Group by file first, then by kind
        var groupedByFile = MembersData.Members.GroupBy(m => m.Location.FilePath);

        foreach (var fileGroup in groupedByFile)
        {
            var fileName = System.IO.Path.GetFileName(fileGroup.Key);
            sb.AppendLine($"### {fileName}");
            sb.AppendLine();

            // Within each file, group by kind
            var groupedByKind = fileGroup.GroupBy(m => m.Kind);

            foreach (var kindGroup in groupedByKind.OrderBy(g => g.Key))
            {
                sb.AppendLine($"#### {kindGroup.Key}");
                sb.AppendLine();

                foreach (var member in kindGroup)
                {
                    var modifiers = new List<string>();
                    if (member.IsStatic) modifiers.Add("static");
                    if (member.IsVirtual) modifiers.Add("virtual");
                    if (member.IsOverride) modifiers.Add("override");
                    if (member.IsAbstract) modifiers.Add("abstract");

                    var modifierStr = modifiers.Count > 0 ? string.Join(" ", modifiers) + " " : "";
                    var displayName = GetDisplayName(member.Name, member.ContainingType);

                    // Build signature string
                    var signature = "";
                    if (!string.IsNullOrEmpty(member.ReturnType) || (member.Parameters != null && member.Parameters.Count > 0))
                    {
                        var returnType = !string.IsNullOrEmpty(member.ReturnType) ? $"{member.ReturnType} " : "";
                        var paramsStr = member.Parameters != null && member.Parameters.Count > 0
                            ? $"({string.Join(", ", member.Parameters)})"
                            : "()";
                        signature = $" `{returnType}{displayName}{paramsStr}`";
                    }


                    sb.AppendLine($"- **{displayName}** ({member.Accessibility} {modifierStr}{member.Kind}) - L{member.Location.StartLine}-{member.Location.EndLine}{signature}");


                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// get_symbol_complete 输出
/// </summary>
public record SymbolCompleteData(
    SymbolInfo BasicInfo,
    string? Documentation,
    string? SourceCode,
    IReadOnlyList<SymbolReference> References,
    InheritanceHierarchyData? Inheritance,
    CallGraphResponse? CallGraph,
    bool IsSourceTruncated,
    int TotalSourceLines
);

public record GetSymbolCompleteResponse(
    string SymbolName,
    SymbolCompleteData Data,
    bool HasMore
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        var displayName = GetDisplayName(Data.BasicInfo);
        sb.AppendLine($"## Symbol: `{displayName}`");
        sb.AppendLine();

        // Basic info
        sb.AppendLine($"**Type**: {Data.BasicInfo.Kind}");

        // Use relative path
        var relativePath = GetRelativePath(Data.BasicInfo.Location.FilePath);
        sb.AppendLine($"**Location**: {relativePath}:L{Data.BasicInfo.Location.StartLine}-{Data.BasicInfo.Location.EndLine}");
        sb.AppendLine();

        // Signature
        if (Data.BasicInfo.Signature != null)
        {
            sb.AppendLine("**Signature**:");
            sb.AppendLine("```csharp");
            var returnType = !string.IsNullOrEmpty(Data.BasicInfo.Signature.ReturnType) ? $"{Data.BasicInfo.Signature.ReturnType} " : "";
            var paramsStr = Data.BasicInfo.Signature.Parameters.Count > 0
                ? string.Join(", ", Data.BasicInfo.Signature.Parameters)
                : "";
            sb.AppendLine($"{returnType}{displayName}({paramsStr});");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Documentation
        if (!string.IsNullOrEmpty(Data.Documentation))
        {
            sb.AppendLine("**Documentation**:");
            sb.AppendLine(Data.Documentation);
            sb.AppendLine();
        }

        // Source code (always include for methods)
        if (!string.IsNullOrEmpty(Data.SourceCode))
        {
            sb.AppendLine("**Source Code**:");
            sb.AppendLine("```csharp");
            sb.AppendLine(Data.SourceCode);
            sb.AppendLine("```");

            if (Data.IsSourceTruncated)
            {
                var linesShown = Data.SourceCode.Split('\n').Length;
                var remaining = Data.TotalSourceLines - linesShown;
                sb.AppendLine($"*... {remaining} more lines hidden*");
            }
            sb.AppendLine();
        }

        // References - 按文件分组
        if (Data.References.Count > 0)
        {
            sb.AppendLine($"**References** ({Data.References.Count}):");
            var groupedByFile = Data.References.GroupBy(r => r.Location.FilePath);

            foreach (var fileGroup in groupedByFile.Take(10))
            {
                var refPath = GetRelativePath(fileGroup.Key);
                sb.AppendLine($"  - {refPath}:");
                foreach (var @ref in fileGroup.Take(3))
                {
                    var lineRange = @ref.Location.EndLine > @ref.Location.StartLine
                        ? $"L{@ref.Location.StartLine}-{@ref.Location.EndLine}"
                        : $"L{@ref.Location.StartLine}";
                    var lineInfo = @ref.LineText != null ? $" - {@ref.LineText.Trim()}" : "";
                    sb.AppendLine($"    - {lineRange}{lineInfo}");
                }
                if (fileGroup.Count() > 3)
                {
                    sb.AppendLine($"    - ... and {fileGroup.Count() - 3} more in this file");
                }
            }
            if (groupedByFile.Count() > 10)
            {
                var remainingRefs = groupedByFile.Skip(10).Sum(g => g.Count());
                sb.AppendLine($"  - ... and {remainingRefs} more in other files");
            }
            sb.AppendLine();
        }

        // Inheritance
        if (Data.Inheritance != null)
        {
            sb.AppendLine("**Inheritance**:");
            if (Data.Inheritance.BaseTypes.Count > 0)
            {
                sb.AppendLine("- Base Types: " + string.Join(", ", Data.Inheritance.BaseTypes));
            }
            if (Data.Inheritance.Interfaces.Count > 0)
            {
                sb.AppendLine("- Interfaces: " + string.Join(", ", Data.Inheritance.Interfaces));
            }
            if (Data.Inheritance.DerivedTypes.Count > 0)
            {
                sb.AppendLine($"- Derived Types: {Data.Inheritance.DerivedTypes.Count}");
            }
            sb.AppendLine();
        }

        // Call graph
        if (Data.CallGraph != null)
        {
            sb.AppendLine("**Call Graph**:");
            sb.AppendLine($"- Callers: {Data.CallGraph.Callers.Count}");
            sb.AppendLine($"- Callees: {Data.CallGraph.Callees.Count}");
            sb.AppendLine($"- Complexity: {Data.CallGraph.Statistics.CyclomaticComplexity}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// batch_get_symbols 输出
/// </summary>
public record BatchSymbolResult(
    string? SymbolName,
    SymbolInfo? Symbol,
    string? Error
);

public record BatchGetSymbolsResponse(
    int TotalCount,
    int SuccessCount,
    int ErrorCount,
    IReadOnlyList<BatchSymbolResult> Results
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Batch Symbol Query Results");
        sb.AppendLine();
        sb.AppendLine($"**Total**: {TotalCount} | **Success**: {SuccessCount} | **Errors**: {ErrorCount}");
        sb.AppendLine();

        foreach (var result in Results)
        {
            if (result.Error != null)
            {
                sb.AppendLine($"### [ERROR] {result.SymbolName ?? "Unknown"}");
                sb.AppendLine($"Error: {result.Error}");
                sb.AppendLine();
            }
            else if (result.Symbol != null)
            {
                var fileName = System.IO.Path.GetFileName(result.Symbol.Location.FilePath);
                var displayName = GetDisplayName(result.Symbol);
                sb.AppendLine($"### {displayName}");
                sb.AppendLine($"- Type: {result.Symbol.Kind}");
                sb.AppendLine($"- Location: {fileName}:{result.Symbol.Location.StartLine}");
                if (result.Symbol.Signature != null)
                {
                    var returnType = !string.IsNullOrEmpty(result.Symbol.Signature.ReturnType) ? $"{result.Symbol.Signature.ReturnType} " : "";
                    var paramsStr = result.Symbol.Signature.Parameters.Count > 0
                        ? string.Join(", ", result.Symbol.Signature.Parameters)
                        : "";
                    sb.AppendLine($"- Signature: {returnType}{displayName}({paramsStr})");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// load_workspace 输出
/// </summary>
public record LoadWorkspaceResponse(
    string Path,
    WorkspaceKind Kind,
    int ProjectCount,
    int DocumentCount
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Workspace Loaded");
        sb.AppendLine();
        sb.AppendLine($"**Path**: `{Path}`");
        sb.AppendLine($"**Type**: {Kind}");
        sb.AppendLine($"**Projects**: {ProjectCount}");
        sb.AppendLine($"**Documents**: {DocumentCount}");
        sb.AppendLine();
        sb.AppendLine("You can now use other C# analysis tools:");
        sb.AppendLine("- `SearchSymbols` - Search for symbols by name");
        sb.AppendLine("- `GetSymbols` - Get symbols from a file");
        sb.AppendLine("- `GoToDefinition` - Navigate to symbol definition");
        sb.AppendLine("- `FindReferences` - Find all references to a symbol");
        sb.AppendLine("- `GetDiagnostics` - Check for compilation errors");
        sb.AppendLine();
        return sb.ToString();
    }
}

/// <summary>
/// get_diagnostics 输出
/// </summary>
public record DiagnosticItem(
    string Id,
    string Message,
    DiagnosticSeverity Severity,
    string FilePath,
    int StartLine,
    int EndLine,
    int StartColumn,
    int EndColumn,
    string? Category
);

public record DiagnosticsSummary(
    int TotalErrors,
    int TotalWarnings,
    int TotalInfo,
    int TotalHidden,
    int FilesWithDiagnostics
);

public record GetDiagnosticsResponse(
    DiagnosticsSummary Summary,
    IReadOnlyList<DiagnosticItem> Diagnostics
) : ToolResponse
{
    public override string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Diagnostics Report");
        sb.AppendLine();

        // Summary
        sb.AppendLine("**Summary**:");
        sb.AppendLine($"- Errors: {Summary.TotalErrors}");
        sb.AppendLine($"- Warnings: {Summary.TotalWarnings}");
        sb.AppendLine($"- Info: {Summary.TotalInfo}");
        sb.AppendLine($"- Files affected: {Summary.FilesWithDiagnostics}");
        sb.AppendLine();

        // Group by file
        var grouped = Diagnostics.GroupBy(d => d.FilePath);

        foreach (var group in grouped)
        {
            var fileName = System.IO.Path.GetFileName(group.Key);
            sb.AppendLine($"### {fileName}");
            sb.AppendLine();

            foreach (var diag in group)
            {
                var severityLabel = diag.Severity switch
                {
                    DiagnosticSeverity.Error => "[ERROR]",
                    DiagnosticSeverity.Warning => "[WARNING]",
                    DiagnosticSeverity.Info => "[INFO]",
                    DiagnosticSeverity.Hidden => "[HIDDEN]",
                    _ => "[?]"
                };

                sb.AppendLine($"- {severityLabel} **{diag.Id}** (Line {diag.StartLine}): {diag.Message}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
