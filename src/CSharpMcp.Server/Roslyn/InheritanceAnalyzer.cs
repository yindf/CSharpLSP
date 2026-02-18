using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 继承分析器实现
/// </summary>
public class InheritanceAnalyzer : IInheritanceAnalyzer
{
    private readonly ILogger<InheritanceAnalyzer> _logger;

    public InheritanceAnalyzer(ILogger<InheritanceAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InheritanceTree> GetInheritanceTreeAsync(
        INamedTypeSymbol type,
        Solution solution,
        bool includeDerived,
        int maxDerivedDepth,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting inheritance tree for: {TypeName}, includeDerived={IncludeDerived}, maxDepth={MaxDepth}",
            type.ToDisplayString(), includeDerived, maxDerivedDepth);

        // Get base types
        var baseTypes = GetBaseTypeChain(type);

        // Get interfaces
        var interfaces = type.AllInterfaces
            .Where(i => !i.IsImplicitlyDeclared)
            .ToList();

        // Get derived types
        IReadOnlyList<INamedTypeSymbol>? derivedTypes = null;
        if (includeDerived)
        {
            var derivedSymbols = await FindDerivedTypesAsync(type, solution, cancellationToken);

            var derivedSet = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            await CollectDerivedTypesBfsAsync(
                derivedSymbols,
                solution,
                derivedSet,
                maxDerivedDepth,
                0,
                cancellationToken);

            derivedTypes = derivedSet.ToList();
        }

        var depth = includeDerived ? maxDerivedDepth : 0;

        _logger.LogDebug(
            "Inheritance tree for {TypeName}: {BaseCount} base types, {InterfaceCount} interfaces, {DerivedCount} derived types",
            type.ToDisplayString(), baseTypes.Count, interfaces.Count,
            derivedTypes?.Count ?? 0);

        return new InheritanceTree(
            baseTypes,
            interfaces,
            derivedTypes ?? Array.Empty<INamedTypeSymbol>(),
            depth
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<INamedTypeSymbol>> FindDerivedTypesAsync(
        INamedTypeSymbol type,
        Solution solution,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Finding derived types for: {TypeName} (Kind: {TypeKind})",
            type.ToDisplayString(), type.TypeKind);

        IReadOnlyList<INamedTypeSymbol> derivedTypes;

        if (type.TypeKind == TypeKind.Interface)
        {
            // Find all types that implement this interface
            var implementations = await SymbolFinder.FindImplementationsAsync(
                type, solution, cancellationToken: cancellationToken);
            derivedTypes = implementations.OfType<INamedTypeSymbol>().ToList();
        }
        else
        {
            // Find all classes that derive from this class
            var derived = await SymbolFinder.FindDerivedClassesAsync(
                type, solution, cancellationToken: cancellationToken);
            derivedTypes = derived.ToList();
        }

        _logger.LogDebug("Found {Count} derived types for {TypeName}",
            derivedTypes.Count, type.ToDisplayString());

        return derivedTypes;
    }

    /// <inheritdoc />
    public IReadOnlyList<INamedTypeSymbol> GetBaseTypeChain(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        var current = type.BaseType;

        while (current != null)
        {
            chain.Add(current);
            current = current.BaseType;
        }

        return chain;
    }

    private async Task CollectDerivedTypesBfsAsync(
        IReadOnlyList<INamedTypeSymbol> currentLevel,
        Solution solution,
        HashSet<INamedTypeSymbol> collected,
        int maxDepth,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("CollectDerivedTypesBfsAsync: currentDepth={CurrentDepth}, maxDepth={MaxDepth}, currentLevelCount={Count}",
            currentDepth, maxDepth, currentLevel.Count);

        if (currentLevel.Count == 0)
        {
            _logger.LogDebug("CollectDerivedTypesBfsAsync: returning early (no types)");
            return;
        }

        var nextLevel = new List<INamedTypeSymbol>();

        foreach (var type in currentLevel)
        {
            if (collected.Contains(type))
            {
                _logger.LogDebug("CollectDerivedTypesBfsAsync: skipping {TypeName} (already collected)", type.Name);
                continue;
            }

            collected.Add(type);
            _logger.LogDebug("CollectDerivedTypesBfsAsync: added {TypeName} to collected set (total: {Count})",
                type.Name, collected.Count);

            // Only find deeper derived types if we haven't reached max depth
            // maxDepth=0 means only collect direct descendants (depth 0 only)
            // maxDepth=1 means collect direct descendants and their descendants (depths 0 and 1)
            if (currentDepth < maxDepth)
            {
                // Find types that derive from this type
                var derived = await FindDerivedTypesAsync(type, solution, cancellationToken);
                _logger.LogDebug("CollectDerivedTypesBfsAsync: found {Count} derived types for {TypeName}",
                    derived.Count, type.Name);
                nextLevel.AddRange(derived);
            }
            else
            {
                _logger.LogDebug("CollectDerivedTypesBfsAsync: reached maxDepth {MaxDepth}, not finding deeper types for {TypeName}",
                    maxDepth, type.Name);
            }
        }

        await CollectDerivedTypesBfsAsync(
            nextLevel,
            solution,
            collected,
            maxDepth,
            currentDepth + 1,
            cancellationToken);
    }

}
