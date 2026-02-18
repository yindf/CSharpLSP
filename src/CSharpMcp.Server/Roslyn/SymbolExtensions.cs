using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 统一的 ISymbol 扩展方法
/// 提供所有符号操作的扩展方法，替代中间模型层
/// </summary>
public static class SymbolExtensions
{
    // ========== 基础信息 ==========

    /// <summary>
    /// 获取符号的显示名称（处理构造函数等特殊情况）
    /// </summary>
    public static string GetDisplayName(this ISymbol symbol)
    {
        if (symbol.Name == ".ctor" && symbol.ContainingType != null)
        {
            return symbol.ContainingType.Name;
        }

        return symbol.Name;
    }

    /// <summary>
    /// 获取符号所在的文件路径
    /// </summary>
    public static string GetFilePath(this ISymbol symbol)
    {
        var locations = symbol.Locations;
        if (locations.Length > 0)
        {
            return locations[0].SourceTree?.FilePath ?? "";
        }

        return "";
    }


    /// <summary>
    /// 获取符号的完整行号范围（包含文档注释和属性）
    /// </summary>
    public static (int startLine, int endLine) GetLineRange(this ISymbol symbol)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var node = syntaxReference.GetSyntax();
            var tree = node.SyntaxTree;

            // 获取完整跨度（包含前导注释和属性）
            var fullSpan = GetFullSpanWithLeadingTrivia(node);
            var lineSpan = tree.GetLineSpan(fullSpan);

            return (lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1);
        }

        // 回退到 Locations（用于元数据引用等情况）
        if (symbol.Locations.Length > 0 && symbol.Locations[0].IsInSource)
        {
            var lineSpan = symbol.Locations[0].GetLineSpan();
            return (lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1);
        }

        return (0, 0);
    }

    /// <summary>
    /// 获取包含前导 XML 注释和属性的完整跨度
    /// </summary>
    private static TextSpan GetFullSpanWithLeadingTrivia(SyntaxNode node)
    {
        // 获取节点的起始位置
        var startPosition = node.SpanStart;

        // 向前查找 XML 文档注释和属性
        var leadingTrivia = node.GetLeadingTrivia();

        // 找到最远的相关前导内容（XML 注释或属性）
        var relevantTrivia = leadingTrivia.Where(t =>
            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
            t.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
            IsAttributeTrivia(t)
        ).ToList();

        if (relevantTrivia.Any())
        {
            // 找到第一个相关 trivia 的起始位置
            var firstTrivia = relevantTrivia.First();
            startPosition = firstTrivia.SpanStart;
        }

        // 结束位置使用 FullSpan 的结束（包含尾随内容如分号）
        var endPosition = node.FullSpan.End;

        return TextSpan.FromBounds(startPosition, endPosition);
    }

    /// <summary>
    /// 检查 trivia 是否包含属性（通过检查其中的结构化 trivia）
    /// </summary>
    private static bool IsAttributeTrivia(SyntaxTrivia trivia)
    {
        // 属性通常作为 StructuredTrivia 存在
        if (trivia.HasStructure)
        {
            var structure = trivia.GetStructure();
            return structure is AttributeListSyntax ||
                   structure.DescendantNodes().Any(n => n is AttributeListSyntax);
        }

        // 检查 trivia 文本是否看起来像属性（备用方案）
        var text = trivia.ToString().Trim();
        return text.StartsWith("[") && text.Contains("]");
    }

    // ========== 签名信息（使用模式匹配）==========

    /// <summary>
    /// 获取符号的签名字符串
    /// </summary>
    public static string GetSignature(this ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol m => FormatMethodSignature(m),
            IPropertySymbol p => FormatPropertySignature(p),
            IFieldSymbol f => $"{f.Type.ToDisplayString()} {f.Name}",
            IEventSymbol e => $"{e.Type.ToDisplayString()} {e.Name}",
            INamedTypeSymbol t => t.ToDisplayString(),
            _ => symbol.ToDisplayString()
        };
    }

    private static string? FormatMethodSignature(IMethodSymbol method)
    {
        // 跳过属性访问器
        if (method.AssociatedSymbol != null && method.MethodKind == MethodKind.PropertyGet)
            return null;

        var returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString();
        var parameters = string.Join(", ", method.Parameters.Select(p => p.ToDisplayString()));
        return $"{returnType} {method.Name}({parameters})";
    }

    private static string FormatPropertySignature(IPropertySymbol property)
    {
        var parameters = property.Parameters.Length > 0
            ? $"[{string.Join(", ", property.Parameters.ToArray().Select(p => p.ToDisplayString()))}]"
            : "";
        return $"{property.Type.ToDisplayString()} {property.Name}{parameters}";
    }

    // ========== 完整声明 ==========

    /// <summary>
    /// 获取符号的完整声明（包括修饰符）
    /// </summary>
    public static string GetFullDeclaration(this ISymbol symbol)
    {
        var parts = new List<string>();

        // 可访问性
        parts.Add(symbol.DeclaredAccessibility.ToString().ToLower());

        // 修饰符
        if (symbol.IsStatic) parts.Add("static");
        if (symbol.IsVirtual) parts.Add("virtual");
        if (symbol.IsOverride) parts.Add("override");
        if (symbol.IsAbstract) parts.Add("abstract");

        // 签名
        parts.Add(symbol.GetSignature());

        return string.Join(" ", parts);
    }

    // ========== 文档信息 ==========

    /// <summary>
    /// 获取符号的摘要文档注释
    /// </summary>
    public static string? GetSummaryComment(this ISymbol symbol)
    {
        var xmlComment = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrEmpty(xmlComment)) return null;

        var summaryStart = xmlComment.IndexOf("<summary>");
        if (summaryStart < 0) return null;

        summaryStart += "<summary>".Length;
        var summaryEnd = xmlComment.IndexOf("</summary>", summaryStart);
        if (summaryEnd < 0) return null;

        var summary = xmlComment.Substring(summaryStart, summaryEnd - summaryStart);
        summary = Regex.Replace(summary, "<[^>]+>", "");
        summary = HtmlDecode(summary);
        summary = Regex.Replace(summary, @"\s+", " ").Trim();

        return string.IsNullOrEmpty(summary) ? null : summary;
    }

    /// <summary>
    /// 获取符号的完整文档注释
    /// </summary>
    public static string? GetFullComment(this ISymbol symbol)
    {
        var xmlComment = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrEmpty(xmlComment)) return null;

        var summaryStart = xmlComment.IndexOf("<summary>");
        if (summaryStart < 0) return null;

        summaryStart += "<summary>".Length;
        var summaryEnd = xmlComment.IndexOf("</summary>", summaryStart);
        if (summaryEnd < 0) return null;

        var summary = xmlComment.Substring(summaryStart, summaryEnd - summaryStart);
        summary = Regex.Replace(summary, "<see[^>]*>([^<]+)</see>", "$1");
        summary = Regex.Replace(summary, "<paramref[^>]*>([^<]+)</paramref>", "$1");
        summary = HtmlDecode(summary);
        summary = Regex.Replace(summary, @"\s+", " ").Trim();

        return string.IsNullOrEmpty(summary) ? null : summary;
    }

    private static string HtmlDecode(string text) =>
        HttpUtility.HtmlDecode(text);

    // ========== 源码信息 ==========

    /// <summary>
    /// 获取符号的完整实现代码
    /// </summary>
    public static async Task<string?> GetFullImplementationAsync(
        this ISymbol symbol,
        int? maxLines = null,
        CancellationToken cancellationToken = default)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return null;

        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
        if (syntax == null) return null;

        var text = syntax.ToString();

        if (maxLines.HasValue && maxLines.Value > 0)
        {
            var lines = text.Split('\n');
            if (lines.Length > maxLines.Value)
            {
                text = string.Join('\n', lines.Take(maxLines.Value));
            }
        }

        return text;
    }

    // ========== 类型信息 ==========

    /// <summary>
    /// 获取包含类型的名称
    /// </summary>
    public static string? GetContainingTypeName(this ISymbol symbol) =>
        symbol.ContainingType?.ToDisplayString();

    /// <summary>
    /// 获取命名空间
    /// </summary>
    public static string? GetNamespace(this ISymbol symbol) =>
        symbol.ContainingNamespace?.ToString();


    // ========== 辅助方法 ==========

    /// <summary>
    /// 获取符号的相对路径
    /// </summary>
    public static string GetRelativePath(this ISymbol symbol)
    {
        var filePath = symbol.GetFilePath();
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

    /// <summary>
    /// 获取符号的可访问性字符串
    /// </summary>
    public static string GetAccessibilityString(this ISymbol symbol)
    {
        return symbol.DeclaredAccessibility.ToString().ToLower();
    }

    /// <summary>
    /// 获取符号的显示类型名称
    /// 对于 NamedType，返回具体的类型名称（class, struct, enum, interface, record 等）
    /// 对于其他类型，返回 Kind 的小写形式
    /// </summary>
    public static string GetDisplayKind(this ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol namedType)
        {
            return GetNamedTypeKindString(namedType);
        }

        return symbol.Kind.ToString();
    }

    /// <summary>
    /// 获取命名类型的具体类型名称
    /// </summary>
    private static string GetNamedTypeKindString(INamedTypeSymbol namedType)
    {
        // 检查是否是 record（record 是 class 或 struct 的修饰符）
        bool isRecord = namedType.IsRecord;

        switch (namedType.TypeKind)
        {
            case TypeKind.Class:
                return isRecord ? "record" : "class";

            case TypeKind.Struct:
                return isRecord ? "record struct" : "struct";

            case TypeKind.Interface:
                return "interface";

            case TypeKind.Enum:
                return "enum";

            case TypeKind.Delegate:
                return "delegate";

            case TypeKind.Unknown:
                return "type";

            default:
                // 对于其他情况，返回 TypeKind 的小写形式
                return namedType.TypeKind.ToString().ToLower();
        }
    }

    /// <summary>
    /// 获取命名类型的显示类型名称（公开方法，供 INamedTypeSymbol 直接使用）
    /// </summary>
    public static string GetNamedTypeKindDisplay(this INamedTypeSymbol namedType)
    {
        return GetNamedTypeKindString(namedType);
    }

    /// <summary>
    /// 获取类型名称的复数形式（PascalCase）
    /// </summary>
    public static string PluralizeKind(string kind)
    {
        // 特殊情况处理
        switch (kind)
        {
            case "class":
                return "Classes";
            case "record struct":
                return "Record Structs";
            default:
                // 默认规则：首字母大写 + s
                return $"{char.ToUpper(kind[0]) + kind.Substring(1)}s";
        }
    }


    public static async Task<string> GetCallGraphMarkdown(this IMethodSymbol method,
        Solution solution, int maxCallers, int maxCallees, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var methodName = method.Name;

        sb.AppendLine($"## Call Graph: `{methodName}`");
        sb.AppendLine();

        var callers = (await SymbolFinder.FindCallersAsync(method, solution, cancellationToken)).ToArray();
        var callees = (await method.GetCalleesAsync(solution, cancellationToken)).ToArray();

        var callerCount = Math.Min(callers.Length, maxCallers);
        sb.AppendLine($"- Callers: {callerCount} shown (of {callers.Length} total, maxCallers={maxCallers})");
        sb.AppendLine();

        // Callers
        foreach (var caller in callers.Take(maxCallers))
        {
            var signature = caller.CallingSymbol.GetSignature();
            var (start, end) = caller.CallingSymbol.GetLineRange();
            sb.AppendLine($"- **{signature}** L{start}-{end}");

            foreach (var location in caller.Locations)
            {
                sb.AppendLine($"  - `{location.GetLineContent()}` {location.ToFileNameWithLineNumber()}");
            }
        }

        if (callers.Length > maxCallers)
        {
            sb.AppendLine($"- ... {callers.Length - maxCallers} more callers (increase maxCallers to see more)");
        }

        sb.AppendLine();

        var calleeCount = Math.Min(callees.Length, maxCallees);
        sb.AppendLine($"- Callees: {calleeCount} shown (of {callees.Length} total, maxCallees={maxCallees})");
        sb.AppendLine();

        // Callees - unified format with callers
        foreach (var callee in callees.Take(maxCallees))
        {
            var signature = callee.Method.GetSignature();
            var (start, end) = callee.Method.GetLineRange();
            sb.AppendLine($"- **{signature}** L{start}-{end}");
            sb.AppendLine($"  - `{callee.SourceText.ToString().Trim()}` {callee.FileLinePositionSpan.ToFileNameWithLineNumber()}");
        }

        if (callees.Length > maxCallees)
        {
            sb.AppendLine($"- ... {callees.Length - maxCallees} more callees (increase maxCallees to see more)");
        }

        sb.AppendLine();

        return sb.ToString();
    }

    public static string ToFileNameWithLineNumber(this FileLinePositionSpan location)
    {
        return $"{Path.GetFileName(location.Path)}:{location.StartLinePosition.Line}-{location.EndLinePosition.Line}";
    }

    public static string ToFileNameWithLineNumber(this Location location)
    {
        var linespan = location.GetLineSpan();
        return $"{Path.GetFileName(linespan.Path)}:{linespan.StartLinePosition.Line}-{linespan.EndLinePosition.Line}";
    }

    public static string GetLineContent(this Location location)
    {
        if (!location.IsInSource) return null;

        var text = location.SourceTree.GetText();
        var lineSpan = location.GetLineSpan();
        return text.Lines[lineSpan.StartLinePosition.Line].ToString();
    }

    public static string ToLineNumber(this Location location)
    {
        var linespan = location.GetLineSpan();
        return $"{linespan.StartLinePosition.Line}-{linespan.EndLinePosition.Line}";
    }

    public record SymbolCalleeInfo( IMethodSymbol Method, SourceText SourceText, FileLinePositionSpan FileLinePositionSpan);
    public static FileLinePositionSpan GetFileLinePositionSpan(this SyntaxNode node)
    {
        if (node.SyntaxTree == null)
            throw new InvalidOperationException("Node is not associated with a syntax tree");

        return node.SyntaxTree.GetLineSpan(node.Span);
    }
    public static async Task<IEnumerable<SymbolCalleeInfo>> GetCalleesAsync(this
        IMethodSymbol methodSymbol,
        Solution solution,
        CancellationToken cancellationToken = default)
    {
        var callees = new Dictionary<IMethodSymbol, SymbolCalleeInfo>(SymbolEqualityComparer.Default);

        foreach (var location in methodSymbol.Locations)
        {
            if (!location.IsInSource)
                continue;

            var syntaxTree = location.SourceTree;
            var document = solution.GetDocument(syntaxTree);
            if (document == null)
                continue;

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var root = await syntaxTree.GetRootAsync(cancellationToken);

            // 找到包含该位置的语法节点
            var node = root.FindNode(location.SourceSpan);

            // 获取方法体：处理方法、属性、索引器、运算符、构造函数等
            SyntaxNode bodyNode = GetBodyNode(node);
            if (bodyNode == null)
                continue;

            // 收集所有调用
            CollectInvocations(bodyNode, semanticModel, callees, cancellationToken);
        }

        return callees.Values;
    }

    private static SyntaxNode GetBodyNode(SyntaxNode node)
    {
        // 处理各种可执行代码的载体
        return node switch
        {
            BaseMethodDeclarationSyntax method => (SyntaxNode)method.Body ?? method.ExpressionBody,
            AccessorDeclarationSyntax accessor => (SyntaxNode)accessor.Body ?? accessor.ExpressionBody,
            PropertyDeclarationSyntax property => property.ExpressionBody?.Expression,
            IndexerDeclarationSyntax indexer => indexer.ExpressionBody,
            ArrowExpressionClauseSyntax arrow => arrow.Expression, // 用于属性表达式体
            _ => node.DescendantNodesAndSelf()
                .OfType<BaseMethodDeclarationSyntax>()
                .FirstOrDefault()?.Body
        };
    }

    private static void CollectInvocations(
        SyntaxNode bodyNode,
        SemanticModel semanticModel,
        Dictionary<IMethodSymbol, SymbolCalleeInfo> callees,
        CancellationToken cancellationToken)
    {
        if (bodyNode == null) return;

        // 1. 普通方法调用：Method() 或 obj.Method()
        foreach (var invocation in bodyNode.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var symbolInfo = ModelExtensions.GetSymbolInfo(semanticModel, invocation, cancellationToken);

            if (symbolInfo.Symbol is IMethodSymbol method)
            {
                callees.TryAdd(method, new SymbolCalleeInfo(method, invocation.GetText(), invocation.GetFileLinePositionSpan()));
            }
            else if (symbolInfo.CandidateReason != CandidateReason.None)
            {
                foreach (var candidate in symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
                {
                    callees.TryAdd(candidate, new SymbolCalleeInfo(candidate, invocation.GetText(), invocation.GetFileLinePositionSpan()));
                }
            }
        }

        // 2. 构造函数调用：new Type()
        foreach (var creation in bodyNode.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>())
        {
            var symbolInfo = ModelExtensions.GetSymbolInfo(semanticModel, creation, cancellationToken);
            if (symbolInfo.Symbol is IMethodSymbol ctor)
            {
                callees.TryAdd(ctor, new SymbolCalleeInfo(ctor, creation.GetText(), creation.GetFileLinePositionSpan()));
            }
        }

        // 3. 属性访问（getter/setter）
        foreach (var memberAccess in bodyNode.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            var symbolInfo = ModelExtensions.GetSymbolInfo(semanticModel, memberAccess, cancellationToken);
            if (symbolInfo.Symbol is IPropertySymbol property)
            {
                // 根据上下文判断是 getter 还是 setter
                if (IsSetterContext(memberAccess))
                {
                    if (property.SetMethod != null)
                        callees.TryAdd(property.SetMethod, new SymbolCalleeInfo(property.SetMethod, memberAccess.GetText(), memberAccess.GetFileLinePositionSpan()));
                }
                else
                {
                    if (property.GetMethod != null)
                        callees.TryAdd(property.GetMethod, new SymbolCalleeInfo(property.GetMethod, memberAccess.GetText(), memberAccess.GetFileLinePositionSpan()));
                }
            }
        }

        // 4. 隐式属性访问（如直接访问属性名）
        foreach (var identifier in bodyNode.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var symbolInfo = ModelExtensions.GetSymbolInfo(semanticModel, identifier, cancellationToken);
            if (symbolInfo.Symbol is IPropertySymbol property)
            {
                if (property.GetMethod != null)
                    callees.TryAdd(property.GetMethod, new SymbolCalleeInfo(property.GetMethod, identifier.GetText(), identifier.GetFileLinePositionSpan()));
            }
        }

        // 5. 构造函数初始化器 : this() 或 : base()
        if (bodyNode.Parent is ConstructorDeclarationSyntax ctorDecl &&
            ctorDecl.Initializer != null)
        {
            var symbolInfo = ModelExtensions.GetSymbolInfo(semanticModel, ctorDecl.Initializer, cancellationToken);
            if (symbolInfo.Symbol is IMethodSymbol init)
            {
                callees.TryAdd(init, new SymbolCalleeInfo(init, bodyNode.GetText(), bodyNode.GetFileLinePositionSpan()));
            }
        }

        // 6. 事件订阅/取消订阅（调用 add/remove 访问器）
        foreach (var assignment in bodyNode.DescendantNodesAndSelf()
                     .OfType<AssignmentExpressionSyntax>()
                     .Where(a => a.Kind() is SyntaxKind.AddAssignmentExpression
                         or SyntaxKind.SubtractAssignmentExpression))
        {
            var symbolInfo = ModelExtensions.GetSymbolInfo(semanticModel, assignment.Left, cancellationToken);
            if (symbolInfo.Symbol is IEventSymbol evt)
            {
                var accessor = assignment.Kind() == SyntaxKind.AddAssignmentExpression
                    ? evt.AddMethod
                    : evt.RemoveMethod;
                if (accessor != null)
                    callees.TryAdd(accessor, new SymbolCalleeInfo(accessor, assignment.GetText(), assignment.GetFileLinePositionSpan()));
            }
        }
    }

    private static bool IsSetterContext(MemberAccessExpressionSyntax memberAccess)
    {
        // 简单判断：如果父节点是赋值表达式左侧，则是 setter
        return memberAccess.Parent is AssignmentExpressionSyntax assignment
               && assignment.Left == memberAccess;
    }

    // ========== Symbol Resolution ==========
    public static async Task<IEnumerable<ISymbol>> GetDeclaredSymbolsAsync(
        this Document document, CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);

        return root.DescendantNodes()
            .Select(n => semanticModel.GetDeclaredSymbol(n))
            .Where(s => s != null);
    }
}
