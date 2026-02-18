using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace CSharpMcp.Server.Roslyn;

internal sealed partial class WorkspaceManager
{
    private void StartFileWatcher()
    {
        try
        {
            _fileWatcher?.Dispose();

            if (_currentSolution == null || string.IsNullOrEmpty(_loadedPath))
            {
                _logger.LogWarning("Cannot start file watcher: no solution loaded");
                return;
            }

            var solutionDirectory = Path.GetDirectoryName(_loadedPath);

            _fileWatcher = new FileWatcherService(
                _loadedPath,
                solutionDirectory!,
                OnFilesChangedAsync,
                _logger
            );

            _logger.LogInformation("File watcher started for: {Path}", _loadedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start file watcher");
        }
    }

    private void StopFileWatcher()
    {
        try
        {
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _logger.LogInformation("File watcher stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping file watcher");
        }
    }

    private async Task OnFilesChangedAsync(IReadOnlyDictionary<string, FileChangeType> fileChanges, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isCompiling, 1, 0) == 1)
        {
            _logger.LogDebug("Compilation in progress, skipping {Count} file change(s)", fileChanges.Count);
            return;
        }

        try
        {
            _logger.LogInformation("Processing {Count} file change(s): {Files}", fileChanges.Count, string.Join(", ", fileChanges.Keys));

            Solution newSolution;
            bool applied;

            do
            {
                var currentSolution = _workspace.CurrentSolution;

                if (currentSolution == null)
                {
                    _logger.LogWarning("No solution loaded, skipping file changes");
                    return;
                }

                newSolution = await CreateNewSolutionAsync(currentSolution, fileChanges, cancellationToken);

                if (newSolution == null)
                {
                    _logger.LogDebug("Failed to create new solution, skipping file changes");
                    return;
                }

                applied = _workspace.TryApplyChanges(newSolution);

                if (!applied)
                {
                    _logger.LogDebug("TryApplyChanges failed, retrying...");
                    await Task.Delay(50, cancellationToken);
                }

            } while (!applied && !cancellationToken.IsCancellationRequested);

            if (applied)
            {
                _currentSolution = newSolution;
                _compilationCache.Clear();
                _lastUpdate = DateTime.UtcNow;
                _logger.LogInformation("Successfully applied {Count} file change(s): {Files}", fileChanges.Count, string.Join(", ", fileChanges.Keys));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file changes");
        }
        finally
        {
            Interlocked.Exchange(ref _isCompiling, 0);
        }
    }

    private async Task<Solution?> CreateNewSolutionAsync(
        Solution currentSolution,
        IReadOnlyDictionary<string, FileChangeType> fileChanges,
        CancellationToken cancellationToken)
    {
        try
        {
            var solutionFiles = new List<string>();
            var projectFiles = new List<string>();
            var sourceFiles = new List<string>();
            var configFiles = new List<string>();

            foreach (var (filePath, changeType) in fileChanges)
            {
                switch (changeType)
                {
                    case FileChangeType.Solution:
                        solutionFiles.Add(filePath);
                        break;
                    case FileChangeType.Project:
                        projectFiles.Add(filePath);
                        break;
                    case FileChangeType.SourceFile:
                        sourceFiles.Add(filePath);
                        break;
                    case FileChangeType.Config:
                        configFiles.Add(filePath);
                        break;
                }
            }

            if (solutionFiles.Count > 0)
            {
                _logger.LogInformation("Reloading solution due to {Count} solution file change(s)", solutionFiles.Count);
                var solutionPath = solutionFiles[0];
                var solution = await _workspace.OpenSolutionAsync(solutionPath, progress: null, cancellationToken);
                return solution;
            }

            if (projectFiles.Count > 0)
            {
                _logger.LogInformation("Reloading {Count} project(s)", projectFiles.Count);
                var solution = currentSolution;
                foreach (var projectPath in projectFiles)
                {
                    var project = await _workspace.OpenProjectAsync(projectPath, progress: null, cancellationToken);
                    if (project != null)
                    {
                        solution = project.Solution;
                    }
                }
                return solution;
            }

            if (sourceFiles.Count > 0)
            {
                _logger.LogInformation("Updating {Count} source file(s)", sourceFiles.Count);

                var documentUpdates = new List<(DocumentId documentId, SourceText sourceText)>();

                foreach (var filePath in sourceFiles)
                {
                    var documentIds = currentSolution.GetDocumentIdsWithFilePath(filePath);
                    if (documentIds.Length == 0)
                    {
                        _logger.LogDebug("No documents found for path: {Path}", filePath);
                        continue;
                    }

                    var sourceText = SourceText.From(await File.ReadAllTextAsync(filePath, cancellationToken), encoding: System.Text.Encoding.UTF8);
                    documentUpdates.Add((documentIds[0], sourceText));
                }

                if (documentUpdates.Count > 0)
                {
                    var solution = currentSolution;
                    foreach (var (documentId, sourceText) in documentUpdates)
                    {
                        solution = solution.WithDocumentText(documentId, sourceText);
                    }
                    return solution;
                }
            }

            if (configFiles.Count > 0)
            {
                _logger.LogInformation("Config files changed, clearing cache: {Count} file(s)", configFiles.Count);
                return currentSolution;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create new solution for {Count} file change(s)", fileChanges.Count);
            return null;
        }
    }
}
