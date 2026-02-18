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
        List<Models.SymbolInfo>? derivedTypes = null;
        if (includeDerived)
        {
            var derivedSymbols = await FindDerivedTypesAsync(type, solution, cancellationToken);

            var derivedSet = new HashSet<INamedTypeSymbol>();

            await CollectDerivedTypesBfsAsync(
                derivedSymbols,
                solution,
                derivedSet,
                maxDerivedDepth,
                0,
                cancellationToken);

            derivedTypes = new List<Models.SymbolInfo>();
            foreach (var derived in derivedSet)
            {
                var info = await ToSymbolInfoAsync(derived, cancellationToken);
                derivedTypes.Add(info);
            }
        }

        // Convert to symbol info
        var baseTypeInfos = new List<Models.SymbolInfo>();
        foreach (var baseType in baseTypes)
        {
            var info = await ToSymbolInfoAsync(baseType, cancellationToken);
            baseTypeInfos.Add(info);
        }

        var interfaceInfos = new List<Models.SymbolInfo>();
        foreach (var iface in interfaces)
        {
            var info = await ToSymbolInfoAsync(iface, cancellationToken);
            interfaceInfos.Add(info);
        }

        var depth = includeDerived ? maxDerivedDepth : 0;

        _logger.LogDebug(
            "Inheritance tree for {TypeName}: {BaseCount} base types, {InterfaceCount} interfaces, {DerivedCount} derived types",
            type.ToDisplayString(), baseTypeInfos.Count, interfaceInfos.Count,
            derivedTypes?.Count ?? 0);

        return new InheritanceTree(
            baseTypeInfos,
            interfaceInfos,
            derivedTypes ?? (IReadOnlyList<Models.SymbolInfo>)Array.Empty<Models.SymbolInfo>(),
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

        var derivedTypes = new List<INamedTypeSymbol>();
        var derivedSet = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        // Build a "key" for the target type that can be compared across compilations
        var targetName = type.Name;
        var targetNamespace = type.ContainingNamespace?.ToString();
        var targetKind = type.TypeKind;

        _logger.LogDebug("Target: Name={Name}, Namespace={Namespace}, Kind={Kind}",
            targetName, targetNamespace, targetKind);
        _logger.LogDebug("Solution has {ProjectCount} projects", solution.Projects.Count());

        int documentsScanned = 0;
        int classesFound = 0;
        int projectsScanned = 0;

        // For interfaces, find classes that implement it
        // For classes, find derived classes
        foreach (var project in solution.Projects)
        {
            _logger.LogDebug("Scanning project: {ProjectName} ({DocCount} documents)",
                project.Name, project.DocumentIds.Count);
            projectsScanned++;

            foreach (var document in project.Documents)
            {
                documentsScanned++;
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (semanticModel == null) continue;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null) continue;

                // Find all class declarations
                var classDeclarations = root.DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>();

                foreach (var classDecl in classDeclarations)
                {
                    classesFound++;
                    var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken);
                    if (classSymbol == null) continue;

                    // Skip if it's the same type (by name and namespace)
                    if (classSymbol.Name == targetName &&
                        classSymbol.ContainingNamespace?.ToString() == targetNamespace)
                    {
                        _logger.LogDebug("Skipping same type: {ClassName}", classSymbol.Name);
                        continue;
                    }

                    bool isDerived = false;

                    // Check class inheritance (for class targets)
                    if (targetKind == TypeKind.Class)
                    {
                        var current = (classSymbol as INamedTypeSymbol)?.BaseType;
                        while (current != null)
                        {
                            if (current.Name == targetName &&
                                current.ContainingNamespace?.ToString() == targetNamespace)
                            {
                                isDerived = true;
                                break;
                            }
                            current = current.BaseType;
                        }
                    }

                    // Check interface implementations via base list syntax OR AllInterfaces
                    if (!isDerived && targetKind == TypeKind.Interface)
                    {
                        // Method 1: Check base list syntax (more reliable for explicit implementations)
                        if (classDecl.BaseList != null)
                        {
                            foreach (var baseTypeSyntax in classDecl.BaseList.Types)
                            {
                                var baseSymbolInfo = semanticModel.GetSymbolInfo(baseTypeSyntax.Type, cancellationToken);
                                if (baseSymbolInfo.Symbol is INamedTypeSymbol namedType)
                                {
                                    // Match by name only for interfaces - this is most reliable
                                    if (namedType.Name == targetName)
                                    {
                                        isDerived = true;
                                        _logger.LogDebug("Found via BaseList: {ClassName} implements {InterfaceName} (from {FilePath})",
                                            classSymbol.Name, namedType.Name, document.FilePath);
                                        break;
                                    }
                                }
                            }
                        }

                        // Method 2: Check AllInterfaces (catches inherited implementations)
                        if (!isDerived && classSymbol is INamedTypeSymbol namedClassSymbol)
                        {
                            foreach (var iface in namedClassSymbol.AllInterfaces)
                            {
                                if (iface.Name == targetName)
                                {
                                    isDerived = true;
                                    _logger.LogDebug("Found via AllInterfaces: {ClassName} implements {InterfaceName} (from {FilePath})",
                                        classSymbol.Name, iface.Name, document.FilePath);
                                    break;
                                }
                            }
                        }
                    }

                    if (isDerived && classSymbol is INamedTypeSymbol namedClass && !derivedSet.Contains(namedClass))
                    {
                        derivedSet.Add(namedClass);
                        derivedTypes.Add(namedClass);
                        _logger.LogDebug("Added derived type: {ClassName} (from {Document})",
                            namedClass.Name, document.Name);
                    }
                }
            }
        }

        _logger.LogDebug(
            "Scanned {Projects} projects, {Documents} documents, found {Classes} classes, found {Derived} derived types for {TypeName}",
            projectsScanned, documentsScanned, classesFound, derivedTypes.Count, type.ToDisplayString());

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

    private bool InheritsFromOrImplements(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type;

        while (current != null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }

            // Check if current type implements the base interface
            foreach (var iface in current.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, baseType))
                {
                    return true;
                }
            }

            current = current.BaseType;
        }

        return false;
    }

    private bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type;

        while (current != null)
        {
            if (current.Equals(baseType))
            {
                return true;
            }

            // Check interfaces
            foreach (var iface in current.AllInterfaces)
            {
                if (iface.Equals(baseType))
                {
                    return true;
                }
            }

            current = current.BaseType;
        }

        return false;
    }

    private async Task<Models.SymbolInfo> ToSymbolInfoAsync(
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
    {
        var location = type.Locations.FirstOrDefault();
        var symbolLocation = new Models.SymbolLocation(
            location?.SourceTree?.FilePath ?? "",
            location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
            location?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
            location?.GetLineSpan().StartLinePosition.Character + 1 ?? 0,
            location?.GetLineSpan().EndLinePosition.Character + 1 ?? 0
        );

        return new Models.SymbolInfo
        {
            Name = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Kind = type.TypeKind switch
            {
                TypeKind.Class => Models.SymbolKind.Class,
                TypeKind.Interface => Models.SymbolKind.Interface,
                TypeKind.Struct => Models.SymbolKind.Struct,
                TypeKind.Enum => Models.SymbolKind.Enum,
                TypeKind.Delegate => Models.SymbolKind.Delegate,
                _ => Models.SymbolKind.Unknown
            },
            Accessibility = type.DeclaredAccessibility switch
            {
                Microsoft.CodeAnalysis.Accessibility.Public => Models.Accessibility.Public,
                Microsoft.CodeAnalysis.Accessibility.Internal => Models.Accessibility.Internal,
                Microsoft.CodeAnalysis.Accessibility.Protected => Models.Accessibility.Protected,
                Microsoft.CodeAnalysis.Accessibility.Private => Models.Accessibility.Private,
                Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Models.Accessibility.ProtectedInternal,
                Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Models.Accessibility.PrivateProtected,
                _ => Models.Accessibility.NotApplicable
            },
            Namespace = type.ContainingNamespace?.ToDisplayString() ?? "",
            ContainingType = type.ContainingType?.ToDisplayString(
                SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "",
            IsStatic = type.IsStatic,
            IsAbstract = type.IsAbstract,
            Location = symbolLocation
        };
    }
}
