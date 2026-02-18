using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 调用图分析器实现
/// </summary>
public class CallGraphAnalyzer : ICallGraphAnalyzer
{
    private readonly ILogger<CallGraphAnalyzer> _logger;

    public CallGraphAnalyzer(ILogger<CallGraphAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IMethodSymbol>> GetCallersAsync(
        IMethodSymbol method,
        Solution solution,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting callers for: {MethodName}", method.ToDisplayString());

        var callers = new List<IMethodSymbol>();
        var visited = new HashSet<IMethodSymbol>();
        var queue = new Queue<(IMethodSymbol Method, int Depth)>();
        queue.Enqueue((method, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (depth >= maxDepth)
            {
                continue;
            }

            // Find callers of the current method
            var callerSymbols = await SymbolFinder.FindCallersAsync(
                current,
                solution,
                cancellationToken);

            foreach (var callerSymbol in callerSymbols)
            {
                if (callerSymbol.CallingSymbol is not IMethodSymbol callerMethod)
                {
                    continue;
                }

                if (visited.Contains(callerMethod))
                {
                    continue;
                }

                // Skip the original method
                if (callerMethod.Equals(method))
                {
                    continue;
                }

                visited.Add(callerMethod);
                callers.Add(callerMethod);

                // Add to queue for next level
                queue.Enqueue((callerMethod, depth + 1));
            }
        }

        _logger.LogDebug("Found {Count} callers for {MethodName}",
            callers.Count, method.ToDisplayString());

        return callers;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IMethodSymbol>> GetCalleesAsync(
        IMethodSymbol method,
        Document document,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting callees for: {MethodName}", method.ToDisplayString());

        var callees = new List<IMethodSymbol>();
        var visited = new HashSet<IMethodSymbol>();
        var queue = new Queue<(IMethodSymbol Method, int Depth)>();
        queue.Enqueue((method, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (depth >= maxDepth)
            {
                continue;
            }

            // Get the syntax for the method
            var syntaxRef = current.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef == null)
            {
                continue;
            }

            var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
            if (syntax == null)
            {
                continue;
            }

            // Find all invocation expressions in the method
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                continue;
            }

            var invocations = syntax.DescendantNodes()
                .OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
                if (symbolInfo.Symbol is not IMethodSymbol callee)
                {
                    continue;
                }

                if (visited.Contains(callee))
                {
                    continue;
                }

                // Skip the original method
                if (callee.Equals(method))
                {
                    continue;
                }

                // Skip external methods (not in the solution)
                if (callee.Locations.Any(loc => loc.IsInMetadata) || callee.DeclaringSyntaxReferences.Length == 0)
                {
                    continue;
                }

                visited.Add(callee);
                callees.Add(callee);

                // Add to queue for next level
                queue.Enqueue((callee, depth + 1));
            }
        }

        _logger.LogDebug("Found {Count} callees for {MethodName}",
            callees.Count, method.ToDisplayString());

        return callees;
    }

    /// <inheritdoc />
    public async Task<int> CalculateCyclomaticComplexityAsync(
        IMethodSymbol method,
        Document document,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Calculating cyclomatic complexity for: {MethodName}",
            method.ToDisplayString());

        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
        {
            return 1;
        }

        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
        if (syntax == null)
        {
            return 1;
        }

        // Cyclomatic complexity = number of decision points + 1
        var complexity = 1;

        // Count decision points
        var decisionPoints = syntax.DescendantNodes()
            .Where(n => n.IsKind(SyntaxKind.IfStatement)
                      || n.IsKind(SyntaxKind.WhileStatement)
                      || n.IsKind(SyntaxKind.ForStatement)
                      || n.IsKind(SyntaxKind.ForEachStatement)
                      || n.IsKind(SyntaxKind.DoStatement)
                      || n.IsKind(SyntaxKind.SwitchStatement)
                      || n.IsKind(SyntaxKind.ConditionalExpression)
                      || n.IsKind(SyntaxKind.CatchClause)
                      || n.IsKind(SyntaxKind.AndPattern)
                      || n.IsKind(SyntaxKind.OrPattern));

        complexity += decisionPoints.Count();

        // Count case statements (each case is a decision point)
        var caseStatements = syntax.DescendantNodes()
            .Count(n => n.IsKind(SyntaxKind.CaseSwitchLabel)
                    || n.IsKind(SyntaxKind.CasePatternSwitchLabel));

        complexity += caseStatements;

        _logger.LogDebug("Cyclomatic complexity for {MethodName}: {Complexity}",
            method.ToDisplayString(), complexity);

        return complexity;
    }

    /// <inheritdoc />
    public async Task<CallGraphResult> GetCallGraphAsync(
        IMethodSymbol method,
        Solution solution,
        CallGraphDirection direction,
        int maxDepth,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting call graph for: {MethodName}, direction: {Direction}",
            method.ToDisplayString(), direction);

        var document = await GetDocumentForMethodAsync(method, solution, cancellationToken);

        List<CallRelationship> callers = new();
        List<CallRelationship> callees = new();

        if (direction is CallGraphDirection.Both or CallGraphDirection.In)
        {
            var callerSymbols = await GetCallersAsync(method, solution, maxDepth, cancellationToken);
            callers = await BuildCallRelationshipsAsync(callerSymbols, solution, cancellationToken);
        }

        if (direction is CallGraphDirection.Both or CallGraphDirection.Out)
        {
            if (document != null)
            {
                var calleeSymbols = await GetCalleesAsync(method, document, maxDepth, cancellationToken);
                callees = await BuildCallRelationshipsAsync(calleeSymbols, solution, cancellationToken);
            }
        }

        var complexity = 1;
        if (document != null)
        {
            complexity = await CalculateCyclomaticComplexityAsync(method, document, cancellationToken);
        }

        var statistics = new CallStatistics(
            callers.Sum(c => c.CallLocations.Count),
            callees.Count,
            complexity
        );

        return new CallGraphResult(
            method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            callers,
            callees,
            statistics
        );
    }

    private async Task<List<CallRelationship>> BuildCallRelationshipsAsync(
        IReadOnlyList<IMethodSymbol> methods,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var relationships = new List<CallRelationship>();

        foreach (var callerMethod in methods)
        {
            var symbolInfo = await ToSymbolInfoAsync(callerMethod, cancellationToken);

            var callLocations = new List<CallLocation>();

            // Find where this method calls the target
            var syntaxRefs = callerMethod.DeclaringSyntaxReferences;
            foreach (var syntaxRef in syntaxRefs)
            {
                var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
                if (syntax == null) continue;

                var document = solution.GetDocument(syntax.SyntaxTree);
                if (document == null) continue;

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (semanticModel == null) continue;

                var invocations = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    var location = new Models.SymbolLocation(
                        document.FilePath,
                        invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        invocation.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                        invocation.GetLocation().GetLineSpan().StartLinePosition.Character + 1,
                        invocation.GetLocation().GetLineSpan().EndLinePosition.Character + 1
                    );

                    var containingSymbol = callerMethod.ContainingType?.Name ?? "Unknown";

                    callLocations.Add(new CallLocation(containingSymbol, location));
                }
            }

            relationships.Add(new CallRelationship(symbolInfo, callLocations));
        }

        return relationships;
    }

    private async Task<Models.SymbolInfo> ToSymbolInfoAsync(
        IMethodSymbol method,
        CancellationToken cancellationToken)
    {
        var location = method.Locations.FirstOrDefault();
        var symbolLocation = new Models.SymbolLocation(
            location?.SourceTree?.FilePath ?? "",
            location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
            location?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
            location?.GetLineSpan().StartLinePosition.Character + 1 ?? 0,
            location?.GetLineSpan().EndLinePosition.Character + 1 ?? 0
        );

        var parameters = method.Parameters
            .Select(p => p.Type?.ToDisplayString() ?? "object")
            .ToList();

        var typeParameters = method.TypeParameters
            .Select(tp => tp.Name)
            .ToList();

        var signature = new Models.SymbolSignature(
            method.Name,
            method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            method.ReturnType?.ToDisplayString() ?? "void",
            parameters,
            typeParameters
        );

        return new Models.SymbolInfo
        {
            Name = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Kind = method.MethodKind switch
            {
                MethodKind.Ordinary => Models.SymbolKind.Method,
                MethodKind.Constructor => Models.SymbolKind.Constructor,
                MethodKind.StaticConstructor => Models.SymbolKind.Constructor,
                MethodKind.Destructor => Models.SymbolKind.Destructor,
                MethodKind.PropertyGet => Models.SymbolKind.Property,
                MethodKind.PropertySet => Models.SymbolKind.Property,
                _ => Models.SymbolKind.Method
            },
            Accessibility = method.DeclaredAccessibility switch
            {
                Microsoft.CodeAnalysis.Accessibility.Public => Models.Accessibility.Public,
                Microsoft.CodeAnalysis.Accessibility.Internal => Models.Accessibility.Internal,
                Microsoft.CodeAnalysis.Accessibility.Protected => Models.Accessibility.Protected,
                Microsoft.CodeAnalysis.Accessibility.Private => Models.Accessibility.Private,
                Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Models.Accessibility.ProtectedInternal,
                Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Models.Accessibility.PrivateProtected,
                _ => Models.Accessibility.NotApplicable
            },
            Namespace = method.ContainingNamespace?.ToDisplayString() ?? "",
            ContainingType = method.ContainingType?.ToDisplayString(
                SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "",
            IsStatic = method.IsStatic,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            IsAbstract = method.IsAbstract,
            IsAsync = method.IsAsync,
            Location = symbolLocation,
            Signature = signature
        };
    }

    private async Task<Document?> GetDocumentForMethodAsync(
        IMethodSymbol method,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null)
        {
            return null;
        }

        var syntax = await syntaxRef.GetSyntaxAsync(cancellationToken);
        if (syntax == null)
        {
            return null;
        }

        var syntaxTree = syntax.SyntaxTree;
        return solution.GetDocument(syntaxTree);
    }
}
