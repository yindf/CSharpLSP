using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using CSharpMcp.Server.Cache;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 工作区管理器实现
/// </summary>
internal sealed partial class WorkspaceManager : IWorkspaceManager, IDisposable
{
    private readonly ILogger<WorkspaceManager> _logger;
    private readonly MSBuildWorkspace _workspace;
    private readonly ICompilationCache _compilationCache;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Solution? _currentSolution;
    private string? _loadedPath;
    private DateTime _lastUpdate;
    private FileWatcherService? _fileWatcher;
    private int _isCompiling;

    public WorkspaceManager(ILogger<WorkspaceManager> logger)
    {
        _logger = logger;
        _workspace = MSBuildWorkspace.Create();
        _compilationCache = CacheFactory.CreateCompilationCache();

        _workspace.WorkspaceFailed += (s, e) =>
        {
            _logger.LogWarning("Workspace failed: {Diagnostic}", e.Diagnostic.Message);
        };
    }

    /// <summary>
    /// 加载解决方案或项目
    /// </summary>
    public async Task<WorkspaceInfo> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path cannot be null or empty.", nameof(path));
            }

            _logger.LogInformation("Loading workspace from: {Path}", path);

            // First, check if it's a directory (search for .sln, .slnx, or .csproj)
            if (Directory.Exists(path))
            {
                // First, try to find .sln or .slnx file in top level only
                var slnFile = Directory.GetFiles(path, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                    ?? Directory.GetFiles(path, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (slnFile != null)
                {
                    path = slnFile;
                    _logger.LogInformation("Found solution file: {Path}", path);
                }
                else
                {
                    // If no .sln/.slnx, look for .csproj in top level only (not recursive)
                    var csprojFile = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (csprojFile != null)
                    {
                        path = csprojFile;
                        _logger.LogInformation("Found project file: {Path}", path);
                    }
                    else
                    {
                        throw new FileNotFoundException($"No .sln, .slnx, or .csproj file found in directory: {path}");
                    }
                }
            }
            // Not a directory, check if it's an existing file
            else if (File.Exists(path))
            {
                var fileExtension = Path.GetExtension(path).ToLowerInvariant();
                if (fileExtension != ".sln" && fileExtension != ".slnx" && fileExtension != ".csproj")
                {
                    throw new NotSupportedException($"Unsupported file type: {fileExtension}. Expected .sln, .slnx, or .csproj file.");
                }
            }
            // Neither a directory nor an existing file
            else
            {
                throw new FileNotFoundException($"Path not found: {path}");
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            WorkspaceKind kind;

            // Normalize the path to use full path
            var normalizedPath = Path.GetFullPath(path);
            _logger.LogInformation("Opening {Extension} file: {Path}", extension, normalizedPath);

            if (extension == ".sln" || extension == ".slnx")
            {
                try
                {
                    _currentSolution = await _workspace.OpenSolutionAsync(normalizedPath, cancellationToken: cancellationToken);
                    kind = WorkspaceKind.Solution;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to open solution: {Path}", normalizedPath);
                    throw new InvalidOperationException($"Failed to open solution file: {normalizedPath}. Error: {ex.Message}", ex);
                }
            }
            else if (extension == ".csproj")
            {
                try
                {
                    var project = await _workspace.OpenProjectAsync(normalizedPath, cancellationToken: cancellationToken);
                    _currentSolution = project.Solution;
                    kind = WorkspaceKind.Project;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to open project: {Path}", normalizedPath);
                    throw new InvalidOperationException($"Failed to open project file: {normalizedPath}. Error: {ex.Message}", ex);
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported file type: {extension}");
            }

            _loadedPath = Path.GetFullPath(path);
            _lastUpdate = DateTime.UtcNow;

            // Clear cache when loading new workspace
            _compilationCache.Clear();

            // Start file watcher for automatic workspace updates
            StartFileWatcher();

            var info = new WorkspaceInfo(
                _loadedPath,
                kind,
                _currentSolution.Projects.Count(),
                _currentSolution.Projects.Sum(p => p.DocumentIds.Count)
            );

            _logger.LogInformation(
                "Workspace loaded: {Kind} with {ProjectCount} projects and {DocumentCount} documents",
                kind,
                info.ProjectCount,
                info.DocumentCount
            );

            return info;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// 获取文档
    /// </summary>
    public async Task<Document?> GetDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (_currentSolution == null)
        {
            _logger.LogInformation("Workspace not loaded, attempting auto-load");
            try
            {
                // Try to find a solution near the target file
                string searchPath = Directory.GetCurrentDirectory();
                bool solutionFound = false;

                if (!string.IsNullOrEmpty(filePath))
                {
                    var fileDir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(fileDir) && Directory.Exists(fileDir))
                    {
                        // Search up the directory tree for a .sln file
                        var currentDir = new DirectoryInfo(fileDir);
                        while (currentDir != null && !solutionFound)
                        {
                            _logger.LogInformation("Searching for solution in: {Path}", currentDir.FullName);

                            // Check for .sln file in current directory
                            var slnFile = currentDir.GetFiles("*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                            if (slnFile != null)
                            {
                                searchPath = slnFile.FullName;
                                solutionFound = true;
                                _logger.LogInformation("Found solution file: {Path}", searchPath);
                                break;
                            }

                            // Move up to parent directory (stop at drive root or a limit)
                            if (currentDir.Parent == null || currentDir.FullName.Length < 10)
                            {
                                break;
                            }
                            currentDir = currentDir.Parent;
                        }
                    }
                }

                await LoadAsync(searchPath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-load workspace");
                return null;
            }
        }

        // Try exact path match first
        var documentIds = _currentSolution.GetDocumentIdsWithFilePath(filePath);
        if (documentIds.Any())
        {
            return _currentSolution.GetDocument(documentIds.First());
        }

        // Try relative path
        if (_loadedPath != null)
        {
            var baseDir = Path.GetDirectoryName(_loadedPath);
            if (baseDir != null)
            {
                var fullPath = Path.IsPathRooted(filePath)
                    ? filePath
                    : Path.Combine(baseDir, filePath);

                documentIds = _currentSolution.GetDocumentIdsWithFilePath(fullPath);
                if (documentIds.Any())
                {
                    return _currentSolution.GetDocument(documentIds.First());
                }
            }
        }

        // Try file name only
        var fileName = Path.GetFileName(filePath);
        var docs = _currentSolution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => string.Equals(
                Path.GetFileName(d.FilePath),
                fileName,
                StringComparison.OrdinalIgnoreCase));

        var doc = docs.FirstOrDefault();
        if (doc != null)
        {
            _logger.LogDebug("Found document by file name: {FilePath}", filePath);
            return doc;
        }

        _logger.LogWarning("Document not found: {FilePath}", filePath);
        return null;
    }

    /// <summary>
    /// 获取编译
    /// </summary>
    public async Task<Compilation?> GetCompilationAsync(string? projectPath = null, CancellationToken cancellationToken = default)
    {
        if (_currentSolution == null)
        {
            _logger.LogInformation("Workspace not loaded, attempting auto-load from current directory");
            try
            {
                // Auto-load workspace from current directory
                await LoadAsync(Directory.GetCurrentDirectory(), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-load workspace");
                return null;
            }
        }

        Project? project;

        if (string.IsNullOrEmpty(projectPath))
        {
            // Get the first project that can compile
            project = _currentSolution.Projects.FirstOrDefault(p =>
            {
                try
                {
                    return p.SupportsCompilation;
                }
                catch
                {
                    return false;
                }
            });

            if (project == null)
            {
                _logger.LogWarning("No compilable project found");
                return null;
            }
        }
        else
        {
            // Find specific project
            project = _currentSolution.Projects.FirstOrDefault(p =>
                string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    Path.GetFileName(p.FilePath),
                    Path.GetFileName(projectPath),
                    StringComparison.OrdinalIgnoreCase));

            if (project == null)
            {
                _logger.LogWarning("Project not found: {ProjectPath}", projectPath);
                return null;
            }
        }

        var cacheKey = $"{project.Id.Id}";

        return await _compilationCache.GetOrAddAsync(
            cacheKey,
            async () =>
            {
                try
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation != null)
                    {
                        _logger.LogDebug("Created compilation for project: {ProjectName}", project.Name);
                    }
                    return compilation;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create compilation for project: {ProjectName}", project.Name);
                    return null;
                }
            },
            cancellationToken
        );
    }

    /// <summary>
    /// 获取语义模型
    /// </summary>
    public async Task<SemanticModel?> GetSemanticModelAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var document = await GetDocumentAsync(filePath, cancellationToken);
        if (document == null)
        {
            return null;
        }

        try
        {
            return await document.GetSemanticModelAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get semantic model for: {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// 获取源文本
    /// </summary>
    public async Task<Microsoft.CodeAnalysis.Text.SourceText?> GetSourceTextAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var document = await GetDocumentAsync(filePath, cancellationToken);
        if (document == null)
        {
            return null;
        }

        try
        {
            return await document.GetTextAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get source text for: {FilePath}", filePath);
            return null;
        }
    }

    /// <summary>
    /// 刷新工作区
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_loadedPath == null)
        {
            return;
        }

        _logger.LogInformation("Refreshing workspace: {Path}", _loadedPath);
        _compilationCache.Clear();

        try
        {
            // Reopen the solution to refresh MSBuildWorkspace's internal cache
            if (File.Exists(_loadedPath))
            {
                var extension = Path.GetExtension(_loadedPath).ToLowerInvariant();
                if (extension == ".sln")
                {
                    _currentSolution = await _workspace.OpenSolutionAsync(_loadedPath, cancellationToken: cancellationToken);
                }
                else if (extension == ".csproj")
                {
                    var project = await _workspace.OpenProjectAsync(_loadedPath, cancellationToken: cancellationToken);
                    _currentSolution = project.Solution;
                }
            }

            _lastUpdate = DateTime.UtcNow;
            _logger.LogInformation("Workspace refreshed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh workspace");
        }
    }

    /// <summary>
    /// 获取工作区状态
    /// </summary>
    public WorkspaceStatus GetStatus()
    {
        var cacheStats = _compilationCache.GetStatistics();

        return new WorkspaceStatus(
            _currentSolution != null,
            _currentSolution?.Projects.Count() ?? 0,
            _currentSolution?.Projects.Sum(p => p.DocumentIds.Count) ?? 0,
            cacheStats.HitRate,
            _lastUpdate
        );
    }

    /// <summary>
    /// 获取当前解决方案
    /// </summary>
    public Solution? GetCurrentSolution() => _currentSolution;

    /// <summary>
    /// 获取所有项目
    /// </summary>
    public IReadOnlyList<Project> GetProjects()
    {
        return _currentSolution?.Projects.ToList() ?? [];
    }

    public void Dispose()
    {
        StopFileWatcher();
        _loadLock.Dispose();
        _workspace.Dispose();
    }
}
