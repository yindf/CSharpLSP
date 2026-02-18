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
                if (approximateLineNumber.HasValue)
                {
                    return exactMatches.OrderBy(s => Math.Abs(
                            s.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0 - approximateLineNumber.Value))
                        .ToList();
                }
                else
                {
                    return exactMatches;
                }
            }

            // Case-insensitive match
            var caseInsensitiveMatches = allSymbols
                .Where(s => string.Equals(s.Name, symbolName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (caseInsensitiveMatches.Count > 0)
            {
                if (approximateLineNumber.HasValue)
                {
                    return caseInsensitiveMatches.OrderBy(s => Math.Abs(
                            s.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0 - approximateLineNumber.Value))
                        .ToList();
                }
                else
                {
                    return caseInsensitiveMatches;
                }
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
                        s.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0 - approximateLineNumber.Value))
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
}
