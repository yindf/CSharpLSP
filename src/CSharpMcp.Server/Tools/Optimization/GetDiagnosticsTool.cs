using System;
using System.Collections.Generic;
using System.ComponentModel;
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

[McpServerToolType]
public class GetDiagnosticsTool
{
    [McpServerTool, Description("Get compiler diagnostics (errors, warnings, info) for files or the entire workspace")]
    public static async Task<string> GetDiagnostics(
        IWorkspaceManager workspaceManager,
        ILogger<GetDiagnosticsTool> logger,
        CancellationToken cancellationToken,
        [Description("Path to specific file to check (null = entire workspace)")] string filePath = null,
        [Description("Whether to include warnings in output")] bool includeWarnings = true,
        [Description("Whether to include info messages in output")] bool includeInfo = false,
        [Description("Whether to include hidden diagnostics")] bool includeHidden = false)
    {
        try
        {
            var workspaceError = WorkspaceErrorHelper.CheckWorkspaceLoaded(workspaceManager, "Get Diagnostics");
            if (workspaceError != null)
            {
                return workspaceError;
            }

            logger.LogInformation("Getting diagnostics for: {FilePath}", filePath ?? "entire workspace");

            var diagnostics = new List<DiagnosticItem>();
            var filesWithDiagnostics = new HashSet<string>();

            var compilation = await workspaceManager.GetCompilationAsync(cancellationToken: cancellationToken);
            var solution = workspaceManager.GetCurrentSolution();
            if (solution == null)
            {
                return GetErrorHelpResponse("Workspace not loaded. Please call LoadWorkspace first to load a C# solution or project.");
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                var document = await workspaceManager.GetDocumentAsync(filePath, cancellationToken);
                if (document == null)
                {
                    return GetErrorHelpResponse($"File not found: `{filePath}`\n\nMake sure the file path is correct and the workspace is loaded.");
                }

                var result = await ProcessDocumentAsync(document, includeWarnings, includeInfo, includeHidden, filesWithDiagnostics, logger, cancellationToken);
                diagnostics.AddRange(result);
            }
            else
            {
                foreach (var project in solution.Projects)
                {
                    foreach (var document in project.Documents)
                    {
                        var result = await ProcessDocumentAsync(document, includeWarnings, includeInfo, includeHidden, filesWithDiagnostics, logger, cancellationToken);
                        diagnostics.AddRange(result);
                    }
                }
            }

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

            logger.LogInformation("Retrieved {Count} diagnostics: {Errors} errors, {Warnings} warnings",
                diagnostics.Count, totalErrors, totalWarnings);

            return new GetDiagnosticsResponse(summary, diagnostics).ToMarkdown();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing GetDiagnosticsTool");
            return GetErrorHelpResponse($"Failed to get diagnostics: {ex.Message}");
        }
    }

    private static string GetErrorHelpResponse(string message)
    {
        return MarkdownHelper.BuildErrorResponse(
            "Get Diagnostics",
            message,
            "GetDiagnostics()\nGetDiagnostics(filePath: \"path/to/File.cs\")",
            "- `GetDiagnostics()` - Get all workspace diagnostics\n- `GetDiagnostics(filePath: \"C:/MyProject/Program.cs\", includeWarnings: true)`"
        );
    }

    private static async Task<List<DiagnosticItem>> ProcessDocumentAsync(
        Document document,
        bool includeWarnings,
        bool includeInfo,
        bool includeHidden,
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

        var compilation = semanticModel.Compilation;
        var compilationDiagnostics = compilation.GetDiagnostics(cancellationToken);

        diagnostics.AddRange(ProcessDiagnostics(compilationDiagnostics, document, includeWarnings, includeInfo, includeHidden, filesWithDiagnostics));

        var syntaxRoot = await document.GetSyntaxRootAsync(cancellationToken);
        if (syntaxRoot != null)
        {
            var syntaxDiagnostics = syntaxRoot.GetDiagnostics();
            diagnostics.AddRange(ProcessDiagnostics(syntaxDiagnostics, document, includeWarnings, includeInfo, includeHidden, filesWithDiagnostics));
        }

        return diagnostics;
    }

    private static List<DiagnosticItem> ProcessDiagnostics(
        IEnumerable<Microsoft.CodeAnalysis.Diagnostic> diagnostics,
        Document document,
        bool includeWarnings,
        bool includeInfo,
        bool includeHidden,
        HashSet<string> filesWithDiagnostics)
    {
        var result = new List<DiagnosticItem>();

        foreach (var diagnostic in diagnostics)
        {
            if (!ShouldIncludeDiagnostic(diagnostic, includeWarnings, includeInfo, includeHidden))
                continue;

            if (diagnostic.Location.SourceTree == null)
                continue;

            var filePath = diagnostic.Location.SourceTree.FilePath;
            if (!filePath.Equals(document.FilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            var lineSpan = diagnostic.Location.GetLineSpan();
            var severity = GetSeverity(diagnostic.Severity);

            filesWithDiagnostics.Add(filePath);

            result.Add(new DiagnosticItem(
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

        return result;
    }

    private static bool ShouldIncludeDiagnostic(Microsoft.CodeAnalysis.Diagnostic diagnostic, bool includeWarnings, bool includeInfo, bool includeHidden)
    {
        if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning && !includeWarnings)
        {
            return false;
        }

        if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Info && !includeInfo)
        {
            return false;
        }

        if (!diagnostic.IsSuppressed && !includeHidden && diagnostic.DefaultSeverity == Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden)
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
