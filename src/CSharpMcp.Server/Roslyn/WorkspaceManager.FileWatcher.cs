using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            _logger.LogInformation("Compilation in progress, skipping {Count} file change(s)", fileChanges.Count);
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
                    _logger.LogInformation("Failed to create new solution, skipping file changes");
                    return;
                }

                applied = _workspace.TryApplyChanges(newSolution);

                if (!applied)
                {
                    _logger.LogInformation("TryApplyChanges failed, retrying...");
                    await Task.Delay(50, cancellationToken);
                }

            } while (!applied && !cancellationToken.IsCancellationRequested);

            if (applied)
            {
                _currentSolution = newSolution;
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
                }
            }

            // Priority 1: Solution file changed - reload entire solution
            if (solutionFiles.Count > 0)
            {
                _logger.LogInformation("Reloading solution due to {Count} solution file change(s)", solutionFiles.Count);
                var solutionPath = solutionFiles[0];
                var solution = await _workspace.OpenSolutionAsync(solutionPath, progress: null, cancellationToken);
                _userProjects = null; // Reset cached user projects
                return solution;
            }

            // Priority 2: Project file changed - reload entire solution
            // Note: MSBuildWorkspace does not support removing projects, so we must reload the whole solution
            if (projectFiles.Count > 0)
            {
                _logger.LogInformation("Project file(s) changed, reloading entire solution");

                if (!string.IsNullOrEmpty(_loadedPath))
                {
                    var extension = Path.GetExtension(_loadedPath).ToLowerInvariant();
                    try
                    {
                        if (extension == ".sln" || extension == ".slnx")
                        {
                            var solution = await _workspace.OpenSolutionAsync(_loadedPath, progress: null, cancellationToken);
                            _userProjects = null;
                            _logger.LogInformation("Solution reloaded successfully");
                            return solution;
                        }
                        else if (extension == ".csproj")
                        {
                            // Single project mode - reload the project
                            var project = await _workspace.OpenProjectAsync(_loadedPath, progress: null, cancellationToken);
                            _userProjects = null;
                            _logger.LogInformation("Project reloaded successfully");
                            return project.Solution;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to reload workspace from: {Path}", _loadedPath);
                    }
                }

                return null;
            }

            // Priority 3: Source files changed
            if (sourceFiles.Count > 0)
            {
                _logger.LogInformation("Processing {Count} source file(s)", sourceFiles.Count);

                var documentUpdates = new List<(DocumentId documentId, SourceText sourceText)>();
                var newDocuments = new List<(ProjectId projectId, string filePath, SourceText sourceText)>();
                var deletedFiles = new List<(DocumentId documentId, string filePath)>();

                foreach (var filePath in sourceFiles)
                {
                    var documentIds = currentSolution.GetDocumentIdsWithFilePath(filePath);

                    // Check if file still exists (could be deleted)
                    if (!File.Exists(filePath))
                    {
                        // File was deleted
                        if (documentIds.Length > 0)
                        {
                            deletedFiles.Add((documentIds[0], filePath));
                            _logger.LogInformation("File deleted: {Path}", filePath);
                        }
                        continue;
                    }

                    if (documentIds.Length > 0)
                    {
                        // Existing document - update its content
                        var sourceText = SourceText.From(await File.ReadAllTextAsync(filePath, cancellationToken), encoding: System.Text.Encoding.UTF8);
                        documentUpdates.Add((documentIds[0], sourceText));
                        _logger.LogDebug("Updating existing document: {Path}", filePath);
                    }
                    else
                    {
                        // New document - need to add to appropriate project
                        var targetProject = FindProjectForFile(filePath, currentSolution);
                        if (targetProject != null)
                        {
                            var sourceText = SourceText.From(await File.ReadAllTextAsync(filePath, cancellationToken), encoding: System.Text.Encoding.UTF8);
                            newDocuments.Add((targetProject.Id, filePath, sourceText));
                            _logger.LogInformation("New file detected, will add to project {Project}: {Path}", targetProject.Name, filePath);
                        }
                        else
                        {
                            _logger.LogWarning("Could not find project for new file: {Path}", filePath);
                        }
                    }
                }

                // Handle deleted files
                if (deletedFiles.Count > 0)
                {
                    if (_isUnityProject)
                    {
                        // For Unity projects: modify .csproj to remove <Compile> entries
                        foreach (var (documentId, filePath) in deletedFiles)
                        {
                            var project = currentSolution.GetProject(documentId.ProjectId);
                            if (project != null)
                            {
                                RemoveCompileEntryFromCsproj(project.FilePath, filePath);
                            }
                        }
                    }

                    // For both Unity and non-Unity: trigger reload
                    // Unity: csproj was modified, will be detected as project change
                    // Non-Unity: SDK-style projects auto-exclude deleted files, just need to reload
                    _logger.LogInformation("Deleted {Count} file(s), triggering project reload", deletedFiles.Count);

                    if (!string.IsNullOrEmpty(_loadedPath))
                    {
                        var extension = Path.GetExtension(_loadedPath).ToLowerInvariant();
                        try
                        {
                            if (extension == ".sln" || extension == ".slnx")
                            {
                                var solution = await _workspace.OpenSolutionAsync(_loadedPath, progress: null, cancellationToken);
                                _userProjects = null;
                                _logger.LogInformation("Solution reloaded after file deletion");
                                return solution;
                            }
                            else if (extension == ".csproj")
                            {
                                var project = await _workspace.OpenProjectAsync(_loadedPath, progress: null, cancellationToken);
                                _userProjects = null;
                                _logger.LogInformation("Project reloaded after file deletion");
                                return project.Solution;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to reload workspace after file deletion");
                        }
                    }

                    return null;
                }

                // Apply updates for modified and new files
                if (documentUpdates.Count > 0 || newDocuments.Count > 0)
                {
                    var solution = currentSolution;

                    // Update existing documents
                    foreach (var (documentId, sourceText) in documentUpdates)
                    {
                        solution = solution.WithDocumentText(documentId, sourceText);
                    }

                    // Add new documents
                    foreach (var (projectId, filePath, sourceText) in newDocuments)
                    {
                        var project = solution.GetProject(projectId)!;
                        var documentName = Path.GetFileName(filePath);
                        var folders = GetFoldersForFile(filePath, project);
                        var newDocument = project.AddDocument(documentName, sourceText, folders, filePath);
                        solution = newDocument.Project.Solution;
                    }

                    _logger.LogInformation("Updated {UpdateCount} document(s), added {NewCount} new document(s)",
                        documentUpdates.Count, newDocuments.Count);

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

    /// <summary>
    /// Find the most appropriate project for a given file path.
    /// Uses directory path matching - finds the project whose directory is the closest parent.
    /// </summary>
    private Project? FindProjectForFile(string filePath, Solution solution)
    {
        var fileDir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(fileDir))
            return null;

        Project? bestMatch = null;
        int bestMatchLength = 0;

        foreach (var project in UserProjects)
        {
            var projectDir = Path.GetDirectoryName(project.FilePath);
            if (string.IsNullOrEmpty(projectDir))
                continue;

            projectDir = Path.GetFullPath(projectDir);

            // Check if file is under project directory
            if (fileDir.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
            {
                // Prefer the project with the longest matching path (most specific)
                if (projectDir.Length > bestMatchLength)
                {
                    bestMatch = project;
                    bestMatchLength = projectDir.Length;
                }
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// Get the folder hierarchy for a file relative to its project.
    /// </summary>
    private static IEnumerable<string>? GetFoldersForFile(string filePath, Project project)
    {
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (string.IsNullOrEmpty(projectDir))
            return null;

        projectDir = Path.GetFullPath(projectDir);
        var fullFilePath = Path.GetFullPath(filePath);
        var fileDir = Path.GetDirectoryName(fullFilePath);

        if (string.IsNullOrEmpty(fileDir) || !fileDir.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
            return null;

        var relativePath = fileDir.Substring(projectDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(relativePath))
            return null;

        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Remove a &lt;Compile&gt; entry from a Unity .csproj file.
    /// Unity projects use explicit &lt;Compile Include="..." /&gt; entries.
    /// </summary>
    private void RemoveCompileEntryFromCsproj(string? csprojPath, string deletedFilePath)
    {
        if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath))
        {
            _logger.LogWarning("Cannot modify csproj: file not found at {Path}", csprojPath);
            return;
        }

        try
        {
            // Get relative path from csproj directory to the deleted file
            var csprojDir = Path.GetDirectoryName(csprojPath);
            if (string.IsNullOrEmpty(csprojDir))
                return;

            var fullPath = Path.GetFullPath(deletedFilePath);
            var relativePath = fullPath.Substring(csprojDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Normalize to forward slashes (csproj uses forward slashes)
            relativePath = relativePath.Replace('\\', '/');

            var lines = File.ReadAllLines(csprojPath);
            var newLines = new List<string>();
            var removed = false;

            foreach (var line in lines)
            {
                // Match <Compile Include="relative/path/to/file.cs" /> or <Compile Include="relative/path/to/file.cs"/>
                var trimmed = line.Trim();
                if (trimmed.StartsWith("<Compile") && trimmed.Contains($"Include=\"{relativePath}\""))
                {
                    _logger.LogInformation("Removed <Compile> entry for {File} from {Csproj}", relativePath, csprojPath);
                    removed = true;
                    continue;
                }
                newLines.Add(line);
            }

            if (removed)
            {
                File.WriteAllLines(csprojPath, newLines);
                _logger.LogInformation("Updated csproj: {Path}", csprojPath);
            }
            else
            {
                _logger.LogDebug("No <Compile> entry found for {File} in {Csproj}", relativePath, csprojPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove <Compile> entry from {Path}", csprojPath);
        }
    }
}
