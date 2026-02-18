using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// Helper methods for resolving symbols from file location information
/// </summary>
public static class SymbolResolver
{
    /// <summary>
    /// Resolve a single symbol from file location info with fuzzy matching
    /// </summary>
    public static async Task<ISymbol?> ResolveSymbolAsync(
        string filePath,
        int lineNumber,
        string symbolName,
        IWorkspaceManager workspaceManager,
        SymbolFilter filter,
        CancellationToken cancellationToken)
    {
        var symbols = await workspaceManager.SearchSymbolsAsync(symbolName, filter, cancellationToken);

        if (!symbols.Any())
        {
            symbols = await workspaceManager.SearchSymbolsWithPatternAsync(symbolName, filter, cancellationToken);
        }

        if (string.IsNullOrEmpty(filePath))
        {
            return symbols.FirstOrDefault();
        }

        return OrderSymbolsByProximity(symbols, filePath, lineNumber).FirstOrDefault();
    }

    /// <summary>
    /// Resolve multiple symbols from file location info with fuzzy matching
    /// </summary>
    public static async Task<IEnumerable<ISymbol>> ResolveSymbolsAsync(
        string filePath,
        int lineNumber,
        string symbolName,
        IWorkspaceManager workspaceManager,
        CancellationToken cancellationToken)
    {
        var symbols = await workspaceManager.SearchSymbolsAsync(symbolName, SymbolFilter.TypeAndMember, cancellationToken);

        if (!symbols.Any())
        {
            symbols = await workspaceManager.SearchSymbolsWithPatternAsync(symbolName, SymbolFilter.TypeAndMember, cancellationToken);
        }

        if (string.IsNullOrEmpty(filePath))
        {
            return symbols;
        }

        return OrderSymbolsByProximity(symbols, filePath, lineNumber);
    }

    /// <summary>
    /// Order symbols by proximity to a given file and line number.
    /// Symbols in the matching file and closest to the line number are ranked highest.
    /// </summary>
    private static IEnumerable<ISymbol> OrderSymbolsByProximity(
        IEnumerable<ISymbol> symbols,
        string filePath,
        int lineNumber)
    {
        var filename = Path.GetFileName(filePath)?.ToLowerInvariant();

        return symbols.OrderBy(s =>
        {
            var totalScore = 0;

            foreach (var location in s.Locations)
            {
                var lineSpan = location.GetLineSpan();
                var path = lineSpan.Path.ToLowerInvariant();

                // Large penalty if not in matching file
                var fileMatch = path.Contains(filename, StringComparison.InvariantCultureIgnoreCase);
                var fileScore = fileMatch ? 0 : 10000;

                // Distance from target line number
                var lineScore = Math.Abs(lineSpan.StartLinePosition.Line - lineNumber);

                totalScore += fileScore + lineScore;
            }

            return totalScore;
        });
    }
}
