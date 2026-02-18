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
/// get_diagnostics 工具 - 获取编译诊断信息
/// 返回项目中的错误、警告和信息
/// </summary>
[McpServerToolType]
public class GetDiagnosticsTool
{
    /// <summary>
    /// Get compiler errors and warnings for a file or workspace
    /// </summary>
    [McpServerTool]
    public static async Task<string> GetDiagnostics(
        GetDiagnosticsParams parameters,
        IWorkspaceManager workspaceManager,
        ILogger<GetDiagnosticsTool> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            logger.LogDebug("Getting diagnostics for: {FilePath}", parameters.FilePath ?? "entire workspace");

            var diagnostics = new List<DiagnosticItem>();
            var filesWithDiagnostics = new HashSet<string>();

            // Trigger workspace auto-load by getting a compilation (this will load the workspace if needed)
            var compilation = await workspaceManager.GetCompilationAsync(cancellationToken: cancellationToken);
            var solution = workspaceManager.GetCurrentSolution();
            if (solution == null)
            {
                throw new InvalidOperationException("Workspace not loaded");
            }

            // Determine which projects/documents to analyze
            if (!string.IsNullOrEmpty(parameters.FilePath))
            {
                // Single file
                var document = await workspaceManager.GetDocumentAsync(parameters.FilePath, cancellationToken);
                if (document == null)
                {
                    throw new FileNotFoundException($"File not found: {parameters.FilePath}");
                }

                var result = await ProcessDocumentAsync(document, parameters, filesWithDiagnostics, logger, cancellationToken);
                diagnostics.AddRange(result);
            }
            else
            {
                // All documents in workspace
                foreach (var project in solution.Projects)
                {
                    foreach (var document in project.Documents)
                    {
                        var result = await ProcessDocumentAsync(document, parameters, filesWithDiagnostics, logger, cancellationToken);
                        diagnostics.AddRange(result);
                    }
                }
            }

            // Calculate summary
            int totalErrors = diagnostics.Count(d => d.Severity == Models.Tools.DiagnosticSeverity.Error);
            int totalWarnings = diagnostics.Count(d => d.Severity == Models.Tools.DiagnosticSeverity.Warning);
            int totalInfo = diagnostics.Count(d => d.Severity == Models.Tools.DiagnosticSeverity.Info);
            int totalHidden = diagnostics.Count(d => d.Severity == Models.Tools.DiagnosticSeverity.Hidden);

            var summary = new DiagnosticsSummary(
                TotalErrors: totalErrors,
                TotalWarnings: totalWarnings,
                TotalInfo: totalInfo,
                TotalHidden: totalHidden,
                FilesWithDiagnostics: filesWithDiagnostics.Count
            );

            logger.LogDebug("Retrieved {Count} diagnostics: {Errors} errors, {Warnings} warnings",
                diagnostics.Count, totalErrors, totalWarnings);

            return new GetDiagnosticsResponse(summary, diagnostics).ToMarkdown();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetDiagnosticsTool");
            throw;
        }
    }

    private static async Task<List<DiagnosticItem>> ProcessDocumentAsync(
        Document document,
        GetDiagnosticsParams parameters,
        HashSet<string> filesWithDiagnostics,
        ILogger<GetDiagnosticsTool> logger,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiagnosticItem>();

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return diagnostics;
        }

        // Get diagnostics from the compilation
        var compilation = semanticModel.Compilation;
        var compilationDiagnostics = compilation.GetDiagnostics(cancellationToken);

        foreach (var diagnostic in compilationDiagnostics)
        {
            if (!ShouldIncludeDiagnostic(diagnostic, parameters))
            {
                continue;
            }

            if (diagnostic.Location.SourceTree == null)
            {
                continue;
            }

            var filePath = diagnostic.Location.SourceTree.FilePath;
            if (!filePath.Equals(document.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lineSpan = diagnostic.Location.GetLineSpan();
            var severity = GetSeverity(diagnostic.Severity);

            filesWithDiagnostics.Add(filePath);

            diagnostics.Add(new DiagnosticItem(
                Id: diagnostic.Id,
                Message: diagnostic.GetMessage(),
                Severity: severity,
                FilePath: filePath,
                StartLine: lineSpan.StartLinePosition.Line + 1,
                EndLine: lineSpan.EndLinePosition.Line + 1,
                StartColumn: lineSpan.StartLinePosition.Character + 1,
                EndColumn: lineSpan.EndLinePosition.Character + 1,
                Category: diagnostic.Descriptor.Category
            ));
        }

        // Get syntactic diagnostics
        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        if (syntaxRoot != null)
        {
            var syntaxDiagnostics = syntaxRoot.GetDiagnostics();

            foreach (var diagnostic in syntaxDiagnostics)
            {
                if (!ShouldIncludeDiagnostic(diagnostic, parameters))
                {
                    continue;
                }

                if (diagnostic.Location.SourceTree == null)
                {
                    continue;
                }

                var filePath = diagnostic.Location.SourceTree.FilePath;
                if (!filePath.Equals(document.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var lineSpan = diagnostic.Location.GetLineSpan();
                var severity = GetSeverity(diagnostic.Severity);

                filesWithDiagnostics.Add(filePath);

                diagnostics.Add(new DiagnosticItem(
                    Id: diagnostic.Id,
                    Message: diagnostic.GetMessage(),
                    Severity: severity,
                    FilePath: filePath,
                    StartLine: lineSpan.StartLinePosition.Line + 1,
                    EndLine: lineSpan.EndLinePosition.Line + 1,
                    StartColumn: lineSpan.StartLinePosition.Character + 1,
                    EndColumn: lineSpan.EndLinePosition.Character + 1,
                    Category: diagnostic.Descriptor.Category
                ));
            }
        }

        return diagnostics;
    }

    private static bool ShouldIncludeDiagnostic(Microsoft.CodeAnalysis.Diagnostic diagnostic, GetDiagnosticsParams parameters)
    {
        // Check severity filter
        if (parameters.SeverityFilter != null && parameters.SeverityFilter.Count > 0)
        {
            var severity = GetSeverity(diagnostic.Severity);
            if (!parameters.SeverityFilter.Contains(severity))
            {
                return false;
            }
        }

        // Check warning inclusion
        if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning && !parameters.IncludeWarnings)
        {
            return false;
        }

        // Check info inclusion
        if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Info && !parameters.IncludeInfo)
        {
            return false;
        }

        // Check hidden inclusion
        if (!diagnostic.IsSuppressed && !parameters.IncludeHidden && diagnostic.DefaultSeverity == Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden)
        {
            return false;
        }

        return true;
    }

    private static Models.Tools.DiagnosticSeverity GetSeverity(Microsoft.CodeAnalysis.DiagnosticSeverity severity)
    {
        return severity switch
        {
            Microsoft.CodeAnalysis.DiagnosticSeverity.Error => Models.Tools.DiagnosticSeverity.Error,
            Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => Models.Tools.DiagnosticSeverity.Warning,
            Microsoft.CodeAnalysis.DiagnosticSeverity.Info => Models.Tools.DiagnosticSeverity.Info,
            Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden => Models.Tools.DiagnosticSeverity.Hidden,
            _ => Models.Tools.DiagnosticSeverity.Info
        };
    }
}
