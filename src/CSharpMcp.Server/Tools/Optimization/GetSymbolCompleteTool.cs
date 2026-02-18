using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Models.Output;
using CSharpMcp.Server.Models.Tools;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server.Tools.Optimization;

/// <summary>
/// get_symbol_complete 工具 - 整合多个信息源获取完整符号信息
/// 减少API调用次数，一次性返回所有需要的信息
/// </summary>
[McpServerToolType]
public class GetSymbolCompleteTool
{
    /// <summary>
    /// Get complete symbol information from multiple sources in one call
    /// </summary>
    [McpServerTool]
    public static async Task<string> GetSymbolComplete(
        GetSymbolCompleteParams parameters,
        IWorkspaceManager workspaceManager,
        ISymbolAnalyzer symbolAnalyzer,
        IInheritanceAnalyzer inheritanceAnalyzer,
        ICallGraphAnalyzer callGraphAnalyzer,
        ILogger<GetSymbolCompleteTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            logger.LogDebug("Getting complete symbol info: {FilePath}:{LineNumber} - {SymbolName}",
                parameters.FilePath, parameters.LineNumber, parameters.SymbolName);

            // Resolve the symbol
            var (symbol, document) = await ResolveSymbolAsync(parameters, workspaceManager, symbolAnalyzer, cancellationToken);
            if (symbol == null)
            {
                logger.LogWarning("Symbol not found: {SymbolName}", parameters.SymbolName ?? "at specified location");
                throw new FileNotFoundException($"Symbol not found: {parameters.SymbolName ?? "at specified location"}");
            }

            // Get basic info
            var info = await symbolAnalyzer.ToSymbolInfoAsync(
                symbol,
                parameters.DetailLevel,
                parameters.Sections.HasFlag(SymbolCompleteSections.SourceCode) ? parameters.GetBodyMaxLines() : null,
                cancellationToken);

            // Get documentation
            string? documentation = null;
            if (parameters.Sections.HasFlag(SymbolCompleteSections.Documentation))
            {
                documentation = info.Documentation;
            }

            // Get source code (always include for methods, otherwise based on sections)
            string? sourceCode = null;
            bool isSourceTruncated = false;
            int totalSourceLines = 0;
            bool isMethod = symbol.Kind == SymbolKind.Method;
            if (parameters.Sections.HasFlag(SymbolCompleteSections.SourceCode) || isMethod)
            {
                // For methods, always include source code
                sourceCode = await symbolAnalyzer.ExtractSourceCodeAsync(
                    symbol,
                    isMethod, // include body for methods
                    parameters.GetBodyMaxLines(),
                    cancellationToken);

                // Calculate truncation info
                if (isMethod && !string.IsNullOrEmpty(sourceCode))
                {
                    var locations = symbol.Locations;
                    if (locations.Length > 0)
                    {
                        var lineSpan = locations[0].GetLineSpan();
                        totalSourceLines = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
                        var linesShown = sourceCode.Split('\n').Length;
                        isSourceTruncated = parameters.GetBodyMaxLines() > 0 && parameters.GetBodyMaxLines() < totalSourceLines;
                    }
                }
            }

            // Get references
            List<Models.SymbolReference> references = new();
            if (parameters.Sections.HasFlag(SymbolCompleteSections.References) && parameters.IncludeReferences)
            {
                try
                {
                    var solution = document.Project.Solution;
                    var referencedSymbols = await symbolAnalyzer.FindReferencesAsync(
                        symbol,
                        solution,
                        cancellationToken);

                    foreach (var refSym in referencedSymbols.Take(parameters.GetMaxReferences()))
                    {
                        foreach (var loc in refSym.Locations)
                        {
                            var location = new Models.SymbolLocation(
                                loc.Document.FilePath,
                                loc.Location.GetLineSpan().StartLinePosition.Line + 1,
                                loc.Location.GetLineSpan().EndLinePosition.Line + 1,
                                loc.Location.GetLineSpan().StartLinePosition.Character + 1,
                                loc.Location.GetLineSpan().EndLinePosition.Character + 1
                            );

                            // Extract line text
                            var lineText = await ExtractLineTextAsync(loc.Document, location.StartLine, cancellationToken);

                            references.Add(new Models.SymbolReference(
                                location,
                                refSym.Definition?.Name ?? "Unknown",
                                null,
                                lineText
                            ));
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to get references for symbol: {SymbolName}", symbol.Name);
                }
            }

            // Get inheritance info
            InheritanceHierarchyData? inheritance = null;
            if (parameters.Sections.HasFlag(SymbolCompleteSections.Inheritance) && parameters.IncludeInheritance)
            {
                if (symbol is INamedTypeSymbol type)
                {
                    try
                    {
                        var solution = document.Project.Solution;
                        var tree = await inheritanceAnalyzer.GetInheritanceTreeAsync(
                            type,
                            solution,
                            includeDerived: false,
                            maxDerivedDepth: 0,
                            cancellationToken);

                        inheritance = new InheritanceHierarchyData(
                            TypeName: type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            Kind: type.TypeKind switch
                            {
                                TypeKind.Class => Models.SymbolKind.Class,
                                TypeKind.Interface => Models.SymbolKind.Interface,
                                TypeKind.Struct => Models.SymbolKind.Struct,
                                TypeKind.Enum => Models.SymbolKind.Enum,
                                TypeKind.Delegate => Models.SymbolKind.Delegate,
                                _ => Models.SymbolKind.Unknown
                            },
                            BaseTypes: tree.BaseTypes,
                            Interfaces: tree.Interfaces,
                            DerivedTypes: Array.Empty<Models.SymbolInfo>(),
                            Depth: 0
                        );
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to get inheritance for symbol: {SymbolName}", symbol.Name);
                    }
                }
            }

            // Get call graph
            CallGraphResponse? callGraph = null;
            if (parameters.Sections.HasFlag(SymbolCompleteSections.CallGraph) && parameters.IncludeCallGraph)
            {
                if (symbol is IMethodSymbol method)
                {
                    try
                    {
                        var solution = document.Project.Solution;
                        var graph = await callGraphAnalyzer.GetCallGraphAsync(
                            method,
                            solution,
                            CallGraphDirection.Both,
                            parameters.GetCallGraphMaxDepth(),
                            cancellationToken);

                        var callers = graph.Callers.Select(c => new CallRelationshipItem(
                            c.Symbol,
                            c.CallLocations.Select(loc => new CallLocationItem(
                                loc.ContainingSymbol,
                                loc.Location
                            )).ToList()
                        )).ToList();

                        var callees = graph.Callees.Select(c => new CallRelationshipItem(
                            c.Symbol,
                            c.CallLocations.Select(loc => new CallLocationItem(
                                loc.ContainingSymbol,
                                loc.Location
                            )).ToList()
                        )).ToList();

                        var statistics = new CallStatisticsItem(
                            graph.Statistics.TotalCallers,
                            graph.Statistics.TotalCallees,
                            graph.Statistics.CyclomaticComplexity
                        );

                        callGraph = new CallGraphResponse(
                            graph.MethodName,
                            callers,
                            callees,
                            statistics
                        );
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to get call graph for symbol: {SymbolName}", symbol.Name);
                    }
                }
            }

            var completeData = new SymbolCompleteData(
                BasicInfo: info,
                Documentation: documentation,
                SourceCode: sourceCode,
                References: references,
                Inheritance: inheritance,
                CallGraph: callGraph,
                IsSourceTruncated: isSourceTruncated,
                TotalSourceLines: totalSourceLines
            );

            logger.LogDebug("Retrieved complete symbol info for: {SymbolName}", symbol.Name);

            return new GetSymbolCompleteResponse(symbol.Name, completeData, false).ToMarkdown();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetSymbolCompleteTool");
            throw;
        }
    }

    private static async Task<(ISymbol? symbol, Document document)> ResolveSymbolAsync(
        GetSymbolCompleteParams parameters,
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
        }

        // Try by name
        if (symbol == null && !string.IsNullOrEmpty(parameters.SymbolName))
        {
            var symbols = await symbolAnalyzer.FindSymbolsByNameAsync(
                document,
                parameters.SymbolName,
                parameters.LineNumber,
                cancellationToken);

            symbol = symbols.FirstOrDefault();
        }

        return (symbol, document);
    }

    private static async Task<string?> ExtractLineTextAsync(
        Document document,
        int lineNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceText = await document.GetTextAsync(cancellationToken);
            var lines = sourceText.Lines;

            if (lineNumber < 1 || lineNumber > lines.Count)
                return null;

            var lineIndex = lineNumber - 1;
            return lines[lineIndex].ToString();
        }
        catch
        {
            return null;
        }
    }
}
