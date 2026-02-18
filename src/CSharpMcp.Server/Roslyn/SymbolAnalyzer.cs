using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using CSharpMcp.Server.Models;
using ModelsSymbolInfo = CSharpMcp.Server.Models.SymbolInfo;
using ModelsSymbolKind = CSharpMcp.Server.Models.SymbolKind;
using ModelsAccessibility = CSharpMcp.Server.Models.Accessibility;
using RoslynReferencedSymbol = Microsoft.CodeAnalysis.FindSymbols.ReferencedSymbol;
using RoslynSymbolKind = Microsoft.CodeAnalysis.SymbolKind;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;
using System.Text;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 符号分析器实现
/// </summary>
internal sealed partial class SymbolAnalyzer : ISymbolAnalyzer
{
    private readonly ILogger<SymbolAnalyzer> _logger;

    public SymbolAnalyzer(ILogger<SymbolAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 获取文档中的所有符号
    /// </summary>
    public async Task<IReadOnlyList<ISymbol>> GetDocumentSymbolsAsync(
        Document document,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return [];
            }

            var root = await semanticModel.SyntaxTree.GetRootAsync(cancellationToken);
            var symbols = new List<ISymbol>();

            // Get all declared symbols in the document
            var namespaceDeclarations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax>();
            var typeDeclarations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>();
            var methodDeclarations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax>();
            var propertyDeclarations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>();
            var fieldDeclarations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>();
            var eventDeclarations = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.EventDeclarationSyntax>();

            foreach (var ns in namespaceDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(ns, cancellationToken);
                if (symbol != null) symbols.Add(symbol);
            }

            foreach (var type in typeDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(type, cancellationToken);
                if (symbol != null) symbols.Add(symbol);
            }

            foreach (var method in methodDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
                if (symbol != null)
                {
                    // Filter out property accessor methods (get/set)
                    if (symbol is IMethodSymbol methodSymbol)
                    {
                        if (methodSymbol.MethodKind == Microsoft.CodeAnalysis.MethodKind.PropertyGet ||
                            methodSymbol.MethodKind == Microsoft.CodeAnalysis.MethodKind.PropertySet)
                        {
                            continue; // Skip accessor methods
                        }
                    }
                    symbols.Add(symbol);
                }
            }

            foreach (var property in propertyDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(property, cancellationToken);
                if (symbol != null) symbols.Add(symbol);
            }

            foreach (var field in fieldDeclarations)
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                    if (symbol != null) symbols.Add(symbol);
                }
            }

            foreach (var evt in eventDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(evt, cancellationToken);
                if (symbol != null) symbols.Add(symbol);
            }

            _logger.LogDebug("Found {Count} symbols in document: {FilePath}", symbols.Count, document.FilePath);

            return symbols;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get symbols for document: {FilePath}", document.FilePath);
            return [];
        }
    }

    /// <summary>
    /// 在位置处解析符号
    /// </summary>
    public async Task<ISymbol?> ResolveSymbolAtPositionAsync(
        Document document,
        int lineNumber,
        int column,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return null;
            }

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (syntaxTree == null)
            {
                return null;
            }

            var text = await document.GetTextAsync(cancellationToken);
            var line = text.Lines[lineNumber - 1];
            var position = line.Start + Math.Min(column - 1, line.Span.Length);

            var root = await syntaxTree.GetRootAsync(cancellationToken);

            // Find the closest token to the position
            var token = root.FindToken(position, findInsideTrivia: true);

            if (token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None))
            {
                return null;
            }

            // Get the binding for the token's parent
            var symbolInfo = semanticModel.GetSymbolInfo(token.Parent!, cancellationToken);
            var symbol = symbolInfo.Symbol;
            if (symbol == null)
            {
                // Try candidate symbols
                symbol = symbolInfo.CandidateSymbols.FirstOrDefault();
            }

            if (symbol == null)
            {
                // Try declared symbol
                symbol = semanticModel.GetDeclaredSymbol(token.Parent!, cancellationToken);
            }

            return symbol;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve symbol at position: {LineNumber}:{Column}", lineNumber, column);
            return null;
        }
    }

    /// <summary>
    /// 按名称查找符号（支持模糊匹配）
    /// </summary>
    public async Task<IReadOnlyList<ISymbol>> FindSymbolsByNameAsync(
        Document document,
        string symbolName,
        int? approximateLineNumber = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                return [];
            }

            var allSymbols = await GetDocumentSymbolsAsync(document, cancellationToken);

            // Exact match first
            var exactMatches = allSymbols
                .Where(s => string.Equals(s.Name, symbolName, StringComparison.Ordinal))
                .ToList();

            if (exactMatches.Count > 0)
            {
                return exactMatches;
            }

            // Case-insensitive match
            var caseInsensitiveMatches = allSymbols
                .Where(s => string.Equals(s.Name, symbolName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (caseInsensitiveMatches.Count > 0)
            {
                return caseInsensitiveMatches;
            }

            // Partial match
            var partialMatches = allSymbols
                .Where(s => s.Name.Contains(symbolName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If line number is provided, find closest match
            if (approximateLineNumber.HasValue && partialMatches.Count > 1)
            {
                partialMatches = partialMatches
                    .OrderBy(s => Math.Abs(
                        GetLineNumber(s) - approximateLineNumber.Value))
                    .ToList();
            }

            return partialMatches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find symbols by name: {SymbolName}", symbolName);
            return [];
        }
    }

    /// <summary>
    /// 查找符号的所有引用
    /// </summary>
    public async Task<IReadOnlyList<RoslynReferencedSymbol>> FindReferencesAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken: cancellationToken);

            _logger.LogDebug("Found {Count} references for symbol: {SymbolName}",
                references.Count(), symbol.Name);

            return references.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find references for symbol: {SymbolName}", symbol.Name);
            return [];
        }
    }

    /// <summary>
    /// 获取符号的定义位置
    /// </summary>
    public async Task<SymbolLocation?> GetSymbolLocationAsync(
        ISymbol symbol,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Try to get the syntax node for accurate span information
            var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef != null)
            {
                var syntaxNode = await syntaxRef.GetSyntaxAsync(cancellationToken);
                if (syntaxNode != null)
                {
                    var lineSpan = syntaxNode.SyntaxTree.GetLineSpan(syntaxNode.Span);
                    return new SymbolLocation(
                        syntaxNode.SyntaxTree?.FilePath ?? "",
                        lineSpan.StartLinePosition.Line + 1,
                        lineSpan.EndLinePosition.Line + 1,
                        lineSpan.StartLinePosition.Character + 1,
                        lineSpan.EndLinePosition.Character + 1
                    );
                }
            }

            // Fallback to location from symbol
            var locations = symbol.Locations;
            var sourceLocation = locations.FirstOrDefault(l => l.IsInSource);
            if (sourceLocation != null)
            {
                var lineSpan = sourceLocation.GetLineSpan();
                return new SymbolLocation(
                    sourceLocation.SourceTree?.FilePath ?? "",
                    lineSpan.StartLinePosition.Line + 1,
                    lineSpan.EndLinePosition.Line + 1,
                    lineSpan.StartLinePosition.Character + 1,
                    lineSpan.EndLinePosition.Character + 1
                );
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get location for symbol: {SymbolName}", symbol.Name);
            return null;
        }
    }

    /// <summary>
    /// 转换为 SymbolInfo 模型
    /// </summary>
    public async Task<ModelsSymbolInfo> ToSymbolInfoAsync(
        ISymbol symbol,
        DetailLevel detailLevel = DetailLevel.Summary,
        int? bodyMaxLines = null,
        CancellationToken cancellationToken = default)
    {
        var location = await GetSymbolLocationAsync(symbol, cancellationToken) ??
            new SymbolLocation("", 0, 0, 0, 0);

        var containingType = symbol.ContainingType?.Name ?? "";
        var namespaceName = symbol.ContainingNamespace?.ToString() ?? "";

        ModelsSymbolInfo result = new()
        {
            Name = symbol.Name,
            Kind = MapSymbolKind(symbol.Kind),
            Location = location,
            ContainingType = containingType,
            Namespace = namespaceName,
            IsStatic = symbol.IsStatic,
            IsVirtual = symbol.IsVirtual,
            IsOverride = symbol.IsOverride,
            IsAbstract = symbol.IsAbstract,
            Accessibility = MapAccessibility(symbol.DeclaredAccessibility)
        };

        // Add signature for summary and above
        if (detailLevel >= DetailLevel.Summary)
        {
            result = result with
            {
                Signature = CreateSignature(symbol)
            };
        }

        // Add documentation for standard and above
        if (detailLevel >= DetailLevel.Standard)
        {
            var doc = await GetDocumentationCommentAsync(symbol, cancellationToken);
            result = result with
            {
                Documentation = doc
            };
        }

        // Add source code for full level
        if (detailLevel == DetailLevel.Full)
        {
            var fullMethodDecl = await ExtractFullMethodDeclarationAsync(symbol, bodyMaxLines, cancellationToken);
            if (fullMethodDecl != null)
            {
                result = result with
                {
                    FullDeclaration = fullMethodDecl.FullMethodText,
                    Attributes = fullMethodDecl.Attributes,
                    SourceCode = fullMethodDecl.Body  // Store body in SourceCode for backward compatibility
                };
            }
            else
            {
                // Fallback to old method if full extraction fails
                var sourceCode = await ExtractSourceCodeAsync(symbol, true, bodyMaxLines, cancellationToken);
                result = result with
                {
                    SourceCode = sourceCode
                };
            }
        }

        return result;
    }

    /// <summary>
    /// 获取符号的文档注释
    /// </summary>
    public Task<string?> GetDocumentationCommentAsync(
        ISymbol symbol,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var xmlComment = symbol.GetDocumentationCommentXml();
            if (string.IsNullOrEmpty(xmlComment))
            {
                return Task.FromResult<string?>(null);
            }

            // Extract only the summary content
            var summaryStart = xmlComment.IndexOf("<summary>");
            if (summaryStart < 0)
            {
                return Task.FromResult<string?>(null);
            }

            summaryStart += "<summary>".Length;
            var summaryEnd = xmlComment.IndexOf("</summary>", summaryStart);
            if (summaryEnd < 0)
            {
                return Task.FromResult<string?>(null);
            }

            var summary = xmlComment.Substring(summaryStart, summaryEnd - summaryStart);

            // Clean up XML tags within the summary (like <see>, <paramref>, etc.)
            summary = System.Text.RegularExpressions.Regex.Replace(summary, "<[^>]+>", "");

            // Decode HTML entities
            summary = System.Web.HttpUtility.HtmlDecode(summary);

            // Clean up whitespace
            summary = summary.Trim();
            // Collapse multiple spaces and newlines to single space
            summary = System.Text.RegularExpressions.Regex.Replace(summary, @"\s+", " ");

            return Task.FromResult<string?>(string.IsNullOrEmpty(summary) ? null : summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get documentation for symbol: {SymbolName}", symbol.Name);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// 提取符号的源代码
    /// </summary>
    public async Task<string?> ExtractSourceCodeAsync(
        ISymbol symbol,
        bool includeBody,
        int? maxLines,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
            {
                return null;
            }

            var syntaxNode = await syntaxRef.GetSyntaxAsync(cancellationToken);
            if (syntaxNode == null)
            {
                return null;
            }

            var lines = syntaxNode.SyntaxTree.GetText().Lines;
            TextSpan extractSpan;

            // For methods, properties with bodies, get the content inside the braces
            if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax methodSyntax)
            {
                if (!includeBody)
                {
                    // Just the signature (up to closing parenthesis of parameter list)
                    var signatureEnd = methodSyntax.ParameterList.Span.End;
                    extractSpan = TextSpan.FromBounds(methodSyntax.Span.Start, signatureEnd);
                }
                else
                {
                    // Get content inside the method body braces
                    var body = methodSyntax.Body;
                    if (body != null)
                    {
                        // Extract from after opening brace to before closing brace
                        var openBrace = body.ChildTokens().FirstOrDefault(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OpenBraceToken));
                        var closeBrace = body.ChildTokens().LastOrDefault(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.CloseBraceToken));

                        if (openBrace != default && closeBrace != default)
                        {
                            extractSpan = TextSpan.FromBounds(openBrace.Span.End, closeBrace.Span.Start);
                        }
                        else
                        {
                            // Fallback to body span
                            extractSpan = body.Span;
                        }
                    }
                    else
                    {
                        extractSpan = syntaxNode.Span;
                    }
                }
            }
            else if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BasePropertyDeclarationSyntax propertySyntax)
            {
                if (!includeBody)
                {
                    extractSpan = propertySyntax.Span;
                }
                else
                {
                    var accessorList = propertySyntax.AccessorList;
                    if (accessorList != null)
                    {
                        extractSpan = accessorList.Span;
                    }
                    else
                    {
                        extractSpan = syntaxNode.Span;
                    }
                }
            }
            else
            {
                // For other symbols, use the full span
                extractSpan = syntaxNode.Span;
            }

            var startLine = lines.GetLineFromPosition(extractSpan.Start).LineNumber;
            var endLine = lines.GetLineFromPosition(extractSpan.End).LineNumber;

            // Apply max lines limit
            if (maxLines.HasValue && (endLine - startLine + 1) > maxLines.Value)
            {
                endLine = startLine + maxLines.Value - 1;
            }

            // Extract source code with line numbers
            var sb = new StringBuilder();
            for (int i = startLine; i <= endLine; i++)
            {
                var line = lines[i];
                var lineText = line.ToString();
                sb.AppendLine($"{i + 1}: {lineText}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract source code for symbol: {SymbolName}", symbol.Name);
            return null;
        }
    }

    /// <summary>
    /// 提取完整的方法声明（包括 attributes、注释、签名、body）
    /// </summary>
    public async Task<FullMethodDeclaration?> ExtractFullMethodDeclarationAsync(
        ISymbol symbol,
        int? maxLines,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
            {
                return null;
            }

            var syntaxNode = await syntaxRef.GetSyntaxAsync(cancellationToken);
            if (syntaxNode == null)
            {
                return null;
            }

            var syntaxTree = syntaxNode.SyntaxTree;
            var sourceText = await syntaxTree.GetTextAsync(cancellationToken);
            var lines = sourceText.Lines;

            // Extract attributes
            var attributes = ExtractAttributes(syntaxNode);

            // Extract documentation (leading trivia) - do this BEFORE getting full span
            var documentation = ExtractDocumentationTrivia(syntaxNode);

            // Find the full span including attributes AND documentation
            var fullSpan = GetFullSpanIncludingAttributesAndDocumentation(syntaxNode);

            // Extract signature (from method start to body start)
            var signature = ExtractSignature(syntaxNode);

            // Extract body
            var body = await ExtractBodyAsync(syntaxNode, maxLines, cancellationToken);

            // Get full method text
            var startLine = lines.GetLineFromPosition(fullSpan.Start).LineNumber;
            var endLine = lines.GetLineFromPosition(fullSpan.End).LineNumber;

            // Apply max lines limit
            if (maxLines.HasValue && (endLine - startLine + 1) > maxLines.Value)
            {
                endLine = startLine + maxLines.Value - 1;
            }

            var fullMethodText = new StringBuilder();
            for (int i = startLine; i <= endLine; i++)
            {
                var line = lines[i];
                var lineText = line.ToString();
                fullMethodText.AppendLine(lineText.TrimEnd());
            }

            return new FullMethodDeclaration(
                fullMethodText.ToString().Trim(),
                attributes,
                documentation,
                signature,
                body ?? ""
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract full method declaration for symbol: {SymbolName}", symbol.Name);
            return null;
        }
    }

    /// <summary>
    /// 提取节点上的所有属性
    /// </summary>
    private static IReadOnlyList<string> ExtractAttributes(SyntaxNode syntaxNode)
    {
        var attributes = new List<string>();

        // Check for AttributeLists based on node type
        if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax methodSyntax)
        {
            foreach (var attributeList in methodSyntax.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    attributes.Add(attribute.ToString());
                }
            }
        }
        else if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BasePropertyDeclarationSyntax propertySyntax)
        {
            foreach (var attributeList in propertySyntax.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    attributes.Add(attribute.ToString());
                }
            }
        }

        return attributes;
    }

    /// <summary>
    /// 提取文档注释（XML 注释）
    /// </summary>
    private static string? ExtractDocumentationTrivia(Microsoft.CodeAnalysis.SyntaxNode syntaxNode)
    {
        var triviaList = syntaxNode.GetLeadingTrivia()
            .Reverse()
            .TakeWhile(t => t.Kind() is Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineCommentTrivia ||
                           t.Kind() is Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineCommentTrivia ||
                           t.Kind() is Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineDocumentationCommentTrivia ||
                           t.Kind() is Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineDocumentationCommentTrivia ||
                           t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EndOfLineTrivia))
            .Reverse();

        if (!triviaList.Any())
            return null;

        var docComments = new List<string>();
        foreach (var trivia in triviaList)
        {
            var text = trivia.ToString().Trim();
            if (string.IsNullOrWhiteSpace(text) || text == "//" || text == "/*" || text == "*/")
                continue;

            // Clean up documentation comment markers
            if (text.StartsWith("///"))
                docComments.Add(text.Substring(3).Trim());
            else if (text.StartsWith("//"))
                docComments.Add(text.Substring(2).Trim());
            else if (text.StartsWith("/*") && text.EndsWith("*/"))
                docComments.Add(text.Substring(2, text.Length - 4).Trim());
            else
                docComments.Add(text);
        }

        return docComments.Count > 0 ? string.Join("\n", docComments) : null;
    }

    /// <summary>
    /// 获取完整的 span（包括属性和文档注释）
    /// </summary>
    private static TextSpan GetFullSpanIncludingAttributesAndDocumentation(SyntaxNode syntaxNode)
    {
        var fullStart = syntaxNode.Span.Start;

        // Check for AttributeLists based on node type
        if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax methodSyntax)
        {
            if (methodSyntax.AttributeLists.Count > 0)
            {
                fullStart = methodSyntax.AttributeLists[0].FullSpan.Start;
            }

            // Check for documentation comments before attributes
            var leadingTrivia = methodSyntax.GetLeadingTrivia();
            foreach (var trivia in leadingTrivia)
            {
                if (trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                    trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    if (trivia.FullSpan.Start < fullStart)
                    {
                        fullStart = trivia.FullSpan.Start;
                    }
                }
            }
        }
        else if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BasePropertyDeclarationSyntax propertySyntax)
        {
            if (propertySyntax.AttributeLists.Count > 0)
            {
                fullStart = propertySyntax.AttributeLists[0].FullSpan.Start;
            }

            // Check for documentation comments before attributes
            var leadingTrivia = propertySyntax.GetLeadingTrivia();
            foreach (var trivia in leadingTrivia)
            {
                if (trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                    trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    if (trivia.FullSpan.Start < fullStart)
                    {
                        fullStart = trivia.FullSpan.Start;
                    }
                }
            }
        }

        return TextSpan.FromBounds(fullStart, syntaxNode.Span.End);
    }

    /// <summary>
    /// 提取方法签名
    /// </summary>
    private static string ExtractSignature(Microsoft.CodeAnalysis.SyntaxNode syntaxNode)
    {
        if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax methodSyntax)
        {
            // From method start to end of parameter list (or semicolon for abstract/interface methods)
            var signatureEnd = methodSyntax.Body != null
                ? methodSyntax.ParameterList.Span.End
                : methodSyntax.SemicolonToken != default && !methodSyntax.SemicolonToken.IsMissing
                    ? methodSyntax.SemicolonToken.Span.End
                    : methodSyntax.Span.End;

            var signatureSpan = TextSpan.FromBounds(methodSyntax.Span.Start, signatureEnd);
            return syntaxNode.SyntaxTree.GetText().ToString(signatureSpan).Trim();
        }
        else if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BasePropertyDeclarationSyntax propertySyntax)
        {
            // For properties, get up to the accessor list or end
            var signatureEnd = propertySyntax.AccessorList != null
                ? propertySyntax.AccessorList.Span.Start
                : propertySyntax.Span.End;

            var signatureSpan = TextSpan.FromBounds(propertySyntax.Span.Start, signatureEnd);
            return syntaxNode.SyntaxTree.GetText().ToString(signatureSpan).Trim();
        }

        return syntaxNode.ToString();
    }

    /// <summary>
    /// 提取方法 body
    /// </summary>
    private static async Task<string?> ExtractBodyAsync(
        Microsoft.CodeAnalysis.SyntaxNode syntaxNode,
        int? maxLines,
        CancellationToken cancellationToken)
    {
        if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax methodSyntax)
        {
            var body = methodSyntax.Body;
            if (body == null)
            {
                // Abstract or interface method
                return null;
            }

            var syntaxTree = syntaxNode.SyntaxTree;
            var sourceText = await syntaxTree.GetTextAsync(cancellationToken);
            var lines = sourceText.Lines;

            // Get content inside the braces
            var openBrace = body.ChildTokens().FirstOrDefault(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OpenBraceToken));
            var closeBrace = body.ChildTokens().LastOrDefault(t => t.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.CloseBraceToken));

            TextSpan bodySpan;
            if (openBrace != default && closeBrace != default)
            {
                bodySpan = TextSpan.FromBounds(openBrace.Span.End, closeBrace.Span.Start);
            }
            else
            {
                bodySpan = body.Span;
            }

            var startLine = lines.GetLineFromPosition(bodySpan.Start).LineNumber;
            var endLine = lines.GetLineFromPosition(bodySpan.End).LineNumber;

            // Apply max lines limit
            if (maxLines.HasValue && (endLine - startLine + 1) > maxLines.Value)
            {
                endLine = startLine + maxLines.Value - 1;
            }

            var sb = new StringBuilder();
            for (int i = startLine; i <= endLine; i++)
            {
                var line = lines[i];
                var lineText = line.ToString();
                sb.AppendLine(lineText.TrimEnd());
            }

            return sb.ToString().Trim();
        }
        else if (syntaxNode is Microsoft.CodeAnalysis.CSharp.Syntax.BasePropertyDeclarationSyntax propertySyntax)
        {
            var accessorList = propertySyntax.AccessorList;
            if (accessorList == null)
            {
                return null;
            }

            return accessorList.ToString();
        }

        return null;
    }

    private static ModelsSymbolKind MapSymbolKind(RoslynSymbolKind kind)
    {
        return kind switch
        {
            RoslynSymbolKind.NamedType => ModelsSymbolKind.Class,
            RoslynSymbolKind.Method => ModelsSymbolKind.Method,
            RoslynSymbolKind.Property => ModelsSymbolKind.Property,
            RoslynSymbolKind.Field => ModelsSymbolKind.Field,
            RoslynSymbolKind.Event => ModelsSymbolKind.Event,
            _ => ModelsSymbolKind.Method
        };
    }

    private static ModelsAccessibility MapAccessibility(RoslynAccessibility accessibility)
    {
        return accessibility switch
        {
            RoslynAccessibility.Public => ModelsAccessibility.Public,
            RoslynAccessibility.Internal => ModelsAccessibility.Internal,
            RoslynAccessibility.Protected => ModelsAccessibility.Protected,
            RoslynAccessibility.ProtectedOrInternal => ModelsAccessibility.ProtectedInternal,
            RoslynAccessibility.ProtectedAndInternal => ModelsAccessibility.PrivateProtected,
            RoslynAccessibility.Private => ModelsAccessibility.Private,
            _ => ModelsAccessibility.Private
        };
    }

    private static SymbolSignature CreateSignature(ISymbol symbol)
    {
        var parameters = new List<string>();
        var typeParameters = new List<string>();
        string? returnType = null;

        switch (symbol)
        {
            case IMethodSymbol method:
                returnType = method.ReturnType.ToDisplayString();
                parameters = method.Parameters.Select(p => p.ToDisplayString()).ToList();
                typeParameters = method.TypeParameters.Select(tp => tp.Name).ToList();
                break;
            case IPropertySymbol property:
                returnType = property.Type.ToDisplayString();
                parameters = property.Parameters.Select(p => p.ToDisplayString()).ToList();
                break;
            case IEventSymbol evt:
                returnType = evt.Type.ToDisplayString();
                break;
            case IFieldSymbol field:
                returnType = field.Type.ToDisplayString();
                break;
        }

        return new SymbolSignature(
            symbol.Name,
            symbol.ToDisplayString(),
            returnType,
            parameters,
            typeParameters
        );
    }

    private static int GetLineNumber(ISymbol symbol)
    {
        var locations = symbol.Locations;
        var sourceLocation = locations.FirstOrDefault(l => l.IsInSource);
        if (sourceLocation != null)
        {
            return sourceLocation.GetLineSpan().StartLinePosition.Line + 1;
        }
        return 0;
    }
}
