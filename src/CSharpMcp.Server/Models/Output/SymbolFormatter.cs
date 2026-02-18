using CSharpMcp.Server.Models;
using Microsoft.CodeAnalysis;
using System.Text;
using System.Linq;

namespace CSharpMcp.Server.Models.Output;

/// <summary>
/// 符号格式化选项
/// </summary>
public enum SymbolDetailLevel
{
    /// <summary>简化输出：仅名称、行号范围、签名、注释</summary>
    Simplified,
    /// <summary>详细输出：包含body、完整注释等</summary>
    Detailed
}

/// <summary>
/// 统一的符号格式化器
/// </summary>
public static class SymbolFormatter
{
    /// <summary>
    /// 获取符号的显示名称（处理 .ctor 等特殊情况）
    /// </summary>
    public static string GetDisplayName(SymbolInfo symbol)
    {
        if (symbol.Name == ".ctor" && !string.IsNullOrEmpty(symbol.ContainingType))
        {
            return symbol.ContainingType.Split('.').Last();
        }
        return symbol.Name;
    }

    /// <summary>
    /// 获取符号的行号范围字符串
    /// </summary>
    public static string GetLineRange(SymbolInfo symbol)
    {
        return $"{symbol.Location.StartLine}-{symbol.Location.EndLine}";
    }

    /// <summary>
    /// 获取符号的签名字符串
    /// </summary>
    public static string GetSignatureString(SymbolInfo symbol)
    {
        if (symbol.Signature == null)
            return "";

        var displayName = GetDisplayName(symbol);
        var returnType = !string.IsNullOrEmpty(symbol.Signature.ReturnType) ? $"{symbol.Signature.ReturnType} " : "";
        var paramsStr = symbol.Signature.Parameters.Count > 0
            ? $"({string.Join(", ", symbol.Signature.Parameters)})"
            : "()";

        return symbol.Kind == SymbolKind.Property
            ? $"{displayName}{paramsStr}"
            : $"{returnType}{displayName}{paramsStr}";
    }

    /// <summary>
    /// 获取符号的完整声明字符串（带修饰符）
    /// </summary>
    public static string GetFullDeclaration(SymbolInfo symbol)
    {
        var sb = new StringBuilder();

        // Accessibility
        var accessibility = symbol.Accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.Private => "private",
            Accessibility.ProtectedInternal => "protected internal",
            Accessibility.PrivateProtected => "private protected",
            _ => ""
        };

        // Modifiers
        var modifiers = new List<string>();
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsAsync) modifiers.Add("async");

        var allModifiers = new List<string>();
        if (!string.IsNullOrEmpty(accessibility)) allModifiers.Add(accessibility);
        allModifiers.AddRange(modifiers);

        if (allModifiers.Count > 0)
            sb.Append(string.Join(" ", allModifiers)).Append(" ");

        // Signature
        sb.Append(GetSignatureString(symbol));

        // Type suffix for properties/fields
        if ((symbol.Kind == SymbolKind.Property || symbol.Kind == SymbolKind.Field) &&
            symbol.Signature != null &&
            !string.IsNullOrEmpty(symbol.Signature.ReturnType))
        {
            sb.Append(" : ").Append(symbol.Signature.ReturnType);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化单个符号（简化版，用于列表）
    /// </summary>
    public static string FormatSymbolSimplified(SymbolInfo symbol, string? filePathContext = null)
    {
        var sb = new StringBuilder();
        var displayName = GetDisplayName(symbol);
        var lineRange = GetLineRange(symbol);

        sb.Append($"- **{displayName}** ({symbol.Kind}) L{lineRange}");

        // 签名
        if (symbol.Signature != null)
        {
            sb.Append($" - `{GetSignatureString(symbol)}`");
        }

        // 单行注释
        if (!string.IsNullOrEmpty(symbol.Documentation))
        {
            var singleLineComment = symbol.Documentation
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Trim();
            while (singleLineComment.Contains("  "))
            {
                singleLineComment = singleLineComment.Replace("  ", " ");
            }
            sb.Append($" // {singleLineComment}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化单个符号（详细版，用于单个符号展示）
    /// </summary>
    public static string FormatSymbolDetailed(SymbolInfo symbol, string? relativePath = null, bool includeBody = true, int? bodyMaxLines = null, int totalLines = 0)
    {
        var sb = new StringBuilder();
        var displayName = GetDisplayName(symbol);

        // Header
        sb.AppendLine($"### {displayName}");
        sb.AppendLine();

        // Location
        sb.AppendLine("**Location**:");
        if (!string.IsNullOrEmpty(relativePath))
        {
            sb.AppendLine($"- File: {relativePath}:{symbol.Location.StartLine}-{symbol.Location.EndLine}");
        }
        else
        {
            sb.AppendLine($"- Lines: {symbol.Location.StartLine}-{symbol.Location.EndLine}");
        }

        // Containing info
        if (!string.IsNullOrEmpty(symbol.ContainingType))
        {
            sb.AppendLine($"- Containing Type: {symbol.ContainingType}");
        }
        else if (!string.IsNullOrEmpty(symbol.Namespace))
        {
            sb.AppendLine($"- Namespace: {symbol.Namespace}");
        }
        sb.AppendLine();

        // Declaration
        sb.AppendLine("**Declaration**:");
        sb.AppendLine("```csharp");
        sb.AppendLine(GetFullDeclaration(symbol) + ";");
        sb.AppendLine("```");
        sb.AppendLine();

        // Documentation
        if (!string.IsNullOrEmpty(symbol.Documentation))
        {
            sb.AppendLine("**Documentation**:");
            sb.AppendLine(symbol.Documentation);
            sb.AppendLine();
        }

        // Source body (for methods/properties)
        if (includeBody && !string.IsNullOrEmpty(symbol.SourceCode))
        {
            sb.AppendLine("**Implementation**:");
            sb.AppendLine("```csharp");
            sb.AppendLine(symbol.SourceCode);
            sb.AppendLine("```");

            if (bodyMaxLines.HasValue && totalLines > bodyMaxLines.Value)
            {
                var linesShown = symbol.SourceCode.Split('\n').Length;
                var remaining = totalLines - linesShown;
                sb.AppendLine($"*... {remaining} more lines hidden*");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化类型符号（带成员列表）
    /// </summary>
    public static string FormatTypeWithMembers(SymbolInfo typeSymbol, IReadOnlyList<SymbolInfo> members, int totalMemberCount)
    {
        var sb = new StringBuilder();
        var displayName = GetDisplayName(typeSymbol);

        sb.AppendLine($"### Type: {displayName}");
        sb.AppendLine();

        // Type info
        sb.AppendLine($"**Kind**: {typeSymbol.Kind}");
        sb.AppendLine($"**Lines**: {GetLineRange(typeSymbol)}");

        if (!string.IsNullOrEmpty(typeSymbol.BaseType))
        {
            sb.AppendLine($"**Base**: {typeSymbol.BaseType}");
        }

        if (typeSymbol.Interfaces.Count > 0)
        {
            sb.AppendLine($"**Interfaces**: {string.Join(", ", typeSymbol.Interfaces)}");
        }
        sb.AppendLine();

        // Members
        sb.AppendLine($"**Members** ({members.Count} of {totalMemberCount}):");
        sb.AppendLine();

        // Group by kind
        var groupedByKind = members.GroupBy(m => m.Kind);

        foreach (var kindGroup in groupedByKind.OrderBy(g => g.Key))
        {
            sb.AppendLine($"#### {kindGroup.Key}");
            sb.AppendLine();

            foreach (var member in kindGroup.OrderBy(m => m.Location.StartLine))
            {
                sb.AppendLine(FormatSymbolSimplified(member));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 按文件分组格式化多个符号
    /// </summary>
    public static string FormatSymbolsGroupedByFile(
        IReadOnlyList<SymbolInfo> symbols,
        SymbolDetailLevel detailLevel = SymbolDetailLevel.Simplified,
        bool includeBody = true,
        int? bodyMaxLines = null)
    {
        var sb = new StringBuilder();

        if (symbols.Count == 0)
        {
            sb.AppendLine("No symbols found.");
            return sb.ToString();
        }

        // Group by file
        var groupedByFile = symbols.GroupBy(s => s.Location.FilePath);

        foreach (var fileGroup in groupedByFile.OrderBy(g => g.Key))
        {
            var fileName = System.IO.Path.GetFileName(fileGroup.Key);
            sb.AppendLine($"## {fileName}");
            sb.AppendLine();

            foreach (var symbol in fileGroup.OrderBy(s => s.Location.StartLine))
            {
                if (detailLevel == SymbolDetailLevel.Detailed)
                {
                    var totalLines = symbol.Location.EndLine - symbol.Location.StartLine + 1;
                    sb.AppendLine(FormatSymbolDetailed(symbol, null, includeBody, bodyMaxLines, totalLines));
                }
                else
                {
                    sb.AppendLine(FormatSymbolSimplified(symbol));
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 从 Roslyn ISymbol 创建 SymbolInfo，包含签名和文档信息
    /// </summary>
    public static SymbolInfo CreateFrom(ISymbol symbol, SymbolLocation location, string? lineText = null)
    {
        // Build base info
        bool isStatic = false, isVirtual = false, isOverride = false, isAbstract = false, isAsync = false;

        // Set modifiers based on symbol type
        switch (symbol)
        {
            case IMethodSymbol method:
                isStatic = method.IsStatic;
                isVirtual = method.IsVirtual;
                isOverride = method.IsOverride;
                isAbstract = method.IsAbstract;
                isAsync = method.IsAsync;
                break;
            case IPropertySymbol property:
                isStatic = property.IsStatic;
                isVirtual = property.IsVirtual;
                isOverride = property.IsOverride;
                isAbstract = property.IsAbstract;
                break;
            case IFieldSymbol field:
                isStatic = field.IsStatic;
                break;
            case IEventSymbol eventSymbol:
                isStatic = eventSymbol.IsStatic;
                break;
        }

        return new SymbolInfo
        {
            Name = symbol.Name,
            Kind = MapSymbolKind(symbol.Kind),
            Location = location,
            ContainingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "",
            Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? "",
            Accessibility = MapAccessibility(symbol.DeclaredAccessibility),
            Signature = BuildSignature(symbol),
            Documentation = GetDocumentation(symbol),
            LineText = lineText,
            IsStatic = isStatic,
            IsVirtual = isVirtual,
            IsOverride = isOverride,
            IsAbstract = isAbstract,
            IsAsync = isAsync
        };
    }

    /// <summary>
    /// 构建符号签名信息
    /// </summary>
    private static SymbolSignature? BuildSignature(ISymbol symbol)
    {
        try
        {
            var displayName = symbol.Name;
            var parameters = Array.Empty<string>();
            var typeParameters = Array.Empty<string>();
            string? returnType = null;

            switch (symbol)
            {
                case IMethodSymbol method:
                    parameters = method.Parameters
                        .Select(p => p.Type?.ToDisplayString() ?? "object")
                        .ToArray();
                    typeParameters = method.TypeParameters
                        .Select(tp => tp.Name)
                        .ToArray();
                    returnType = method.ReturnType?.ToDisplayString() ?? "void";
                    break;

                case IPropertySymbol property:
                    parameters = property.Parameters
                        .Select(p => p.Type?.ToDisplayString() ?? "object")
                        .ToArray();
                    returnType = property.Type?.ToDisplayString() ?? "object";
                    break;

                case IFieldSymbol field:
                    returnType = field.Type?.ToDisplayString() ?? "object";
                    break;

                case IEventSymbol eventSymbol:
                    returnType = eventSymbol.Type?.ToDisplayString() ?? "object";
                    break;
            }

            return new SymbolSignature(
                displayName,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                returnType ?? "",
                parameters.ToList(),
                typeParameters.ToList()
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取符号的文档注释
    /// </summary>
    private static string? GetDocumentation(ISymbol symbol)
    {
        try
        {
            return symbol.GetDocumentationCommentXml();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 映射符号类型
    /// </summary>
    private static Models.SymbolKind MapSymbolKind(Microsoft.CodeAnalysis.SymbolKind kind)
    {
        return kind switch
        {
            Microsoft.CodeAnalysis.SymbolKind.NamedType => Models.SymbolKind.Class,
            Microsoft.CodeAnalysis.SymbolKind.Method => Models.SymbolKind.Method,
            Microsoft.CodeAnalysis.SymbolKind.Property => Models.SymbolKind.Property,
            Microsoft.CodeAnalysis.SymbolKind.Field => Models.SymbolKind.Field,
            Microsoft.CodeAnalysis.SymbolKind.Event => Models.SymbolKind.Event,
            _ => Models.SymbolKind.Unknown
        };
    }

    /// <summary>
    /// 映射可访问性
    /// </summary>
    private static Models.Accessibility MapAccessibility(Microsoft.CodeAnalysis.Accessibility accessibility)
    {
        return accessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => Models.Accessibility.Public,
            Microsoft.CodeAnalysis.Accessibility.Internal => Models.Accessibility.Internal,
            Microsoft.CodeAnalysis.Accessibility.Protected => Models.Accessibility.Protected,
            Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Models.Accessibility.ProtectedInternal,
            Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Models.Accessibility.PrivateProtected,
            Microsoft.CodeAnalysis.Accessibility.Private => Models.Accessibility.Private,
            _ => Models.Accessibility.NotApplicable
        };
    }
}
