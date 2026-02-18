using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.Generic;
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
    /// 获取符号的行号范围
    /// </summary>
    public static (int startLine, int endLine) GetLineRange(this ISymbol symbol)
    {
        var locations = symbol.Locations;
        if (locations.Length > 0)
        {
            var lineSpan = locations[0].GetLineSpan();
            return (lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1);
        }

        return (0, 0);
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

    private static string FormatMethodSignature(IMethodSymbol method)
    {
        // 跳过属性访问器
        if (method.AssociatedSymbol != null && method.MethodKind == MethodKind.PropertyGet)
            return null!;

        var returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString();
        var parameters = string.Join(", ", method.Parameters.ToArray().Select(p => p.ToDisplayString()));
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

    private static string HtmlDecode(string text) => HttpUtility.HtmlDecode(text);

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

    public static async Task<IReadOnlyList<ISymbol>> SearchSymbolsAsync(this string query)
    {
        return new List<ISymbol>();
    }

    /// <summary>
    /// 获取方法体的代码
    /// </summary>
    public static async Task<string?> GetBodyAsync(
        this ISymbol symbol,
        int? maxLines = null,
        CancellationToken cancellationToken = default)
    {
        if (symbol is not IMethodSymbol) return null;

        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return null;

        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
        if (syntax is not Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax methodSyntax)
            return null;

        var body = methodSyntax.Body?.ToString();
        if (string.IsNullOrEmpty(body)) return null;

        if (maxLines.HasValue && maxLines.Value > 0)
        {
            var lines = body.Split('\n');
            if (lines.Length > maxLines.Value)
            {
                body = string.Join('\n', lines.Take(maxLines.Value));
            }
        }

        return body;
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

    /// <summary>
    /// 获取基类型名称
    /// </summary>
    public static string? GetBaseType(this INamedTypeSymbol type) =>
        type.BaseType?.ToDisplayString();

    /// <summary>
    /// 获取实现的接口列表
    /// </summary>
    public static IEnumerable<string> GetInterfaces(this INamedTypeSymbol type) =>
        type.AllInterfaces.Select(i => i.ToDisplayString());

    /// <summary>
    /// 获取类型的所有成员
    /// </summary>
    public static IEnumerable<ISymbol> GetMembers(this INamedTypeSymbol type) =>
        type.GetMembers().Where(m => !m.IsImplicitlyDeclared);

    // ========== 引用查找 ==========

    /// <summary>
    /// 查找符号的所有引用
    /// </summary>
    public static async Task<IEnumerable<ReferencedSymbol>> FindReferencesAsync(
        this ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken = default)
    {
        return await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
    }

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
    /// 获取符号类型字符串
    /// </summary>
    public static string GetKindString(this ISymbol symbol)
    {
        return symbol.Kind.ToString();
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

    /// <summary>
    /// 判断是否为构造函数
    /// </summary>
    public static bool IsConstructor(this ISymbol symbol)
    {
        return symbol is IMethodSymbol method && method.MethodKind == MethodKind.Constructor;
    }

    /// <summary>
    /// 获取方法签名详细信息
    /// </summary>
    public static (string? returnType, IReadOnlyList<string> parameters) GetSignatureDetails(this ISymbol symbol)
    {
        string? returnType = null;
        var parameters = new List<string>();

        switch (symbol)
        {
            case IMethodSymbol method:
                returnType = method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString();
                parameters = method.Parameters.ToArray().Select(p => p.ToDisplayString()).ToList();
                break;
            case IPropertySymbol property:
                returnType = property.Type.ToDisplayString();
                parameters = property.Parameters.ToArray().Select(p => p.ToDisplayString()).ToList();
                break;
            case IFieldSymbol field:
                returnType = field.Type.ToDisplayString();
                break;
            case IEventSymbol evt:
                returnType = evt.Type.ToDisplayString();
                break;
        }

        return (returnType, parameters);
    }


    public static async Task<string> GetCallGraphMarkdown(this IMethodSymbol method,
        Solution solution, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var methodName = method.Name;

        sb.AppendLine($"## Call Graph: `{methodName}`");
        sb.AppendLine();

        var callers = (await SymbolFinder.FindCallersAsync(method, solution, cancellationToken)).ToArray();
        var callees = (await method.GetCalleesAsync(solution, cancellationToken)).ToArray();

        sb.AppendLine($"- Callers: {callers.Length}");
        sb.AppendLine();

        // Callers
        foreach (var caller in callers)
        {
            var displayName = caller.CallingSymbol.Name;

            sb.AppendLine($"- **{displayName}** ({caller.CallingSymbol.Locations})");
            foreach (var location in caller.Locations)
            {
                sb.AppendLine($"{location.SourceSpan} L:{location.GetLineSpan()}");
            }
        }

        sb.AppendLine();

        sb.AppendLine($"- Callees: {callers.Length}");
        sb.AppendLine();

        // Callees
        foreach (var callee in callees)
        {
            sb.AppendLine(callee.Method.GetSignature());
            sb.AppendLine($"  - Called At {callee.FileLinePositionSpan} {callee.SourceText}");
        }

        sb.AppendLine();

        return sb.ToString();
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

}
