using Microsoft.Extensions.Logging;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 文件监控服务 - 使用单一 FileSystemWatcher 监控整个解决方案目录
/// </summary>
internal sealed class FileWatcherService : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _debounceTimer;
    private readonly string _solutionPath;
    private readonly string _solutionDirectory;
    private readonly Func<IReadOnlyDictionary<string, FileChangeType>, CancellationToken, Task> _onFileChanged;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly object _pendingChangesLock = new();

    private readonly Dictionary<string, FileChangeType> _pendingFileChanges = new();
    private bool _disposed;

    public FileWatcherService(
        string solutionPath,
        string solutionDirectory,
        Func<IReadOnlyDictionary<string, FileChangeType>, CancellationToken, Task> onFileChanged,
        ILogger logger)
    {
        _solutionPath = solutionPath;
        _solutionDirectory = solutionDirectory;
        _onFileChanged = onFileChanged;
        _logger = logger;

        // 创建防抖定时器（1秒延迟）
        _debounceTimer = new System.Threading.Timer(OnDebounce, null, Timeout.Infinite, Timeout.Infinite);

        // 创建单一的 FileSystemWatcher 监控整个解决方案目录
        _watcher = new FileSystemWatcher(_solutionDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
        };

        // 注册事件处理器
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;

        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("FileWatcherService initialized for: {Directory}", _solutionDirectory);
        _logger.LogInformation("Watching: *.sln, *.csproj, *.cs, *.editorconfig");
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        var changeType = GetChangeType(e.FullPath);
        if (changeType.HasValue)
        {
            _logger.LogTrace("File changed: {Type} - {Path}", changeType.Value, e.FullPath);
            ScheduleChange(changeType.Value, e.FullPath);
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        var changeType = GetChangeType(e.FullPath);
        if (changeType.HasValue)
        {
            _logger.LogDebug("File renamed: {Type} - {OldPath} -> {NewPath}", changeType.Value, e.OldFullPath, e.FullPath);
            ScheduleChange(changeType.Value, e.FullPath);
        }
    }

    /// <summary>
    /// 根据文件扩展名确定变化类型
    /// </summary>
    private FileChangeType? GetChangeType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".sln" => FileChangeType.Solution,
            ".csproj" => FileChangeType.Project,
            ".cs" => FileChangeType.SourceFile,
            ".editorconfig" => FileChangeType.Config,
            _ => null
        };
    }

    /// <summary>
    /// 安排文件变化处理（带防抖）
    /// </summary>
    private void ScheduleChange(FileChangeType changeType, string filePath)
    {
        lock (_pendingChangesLock)
        {
            _pendingFileChanges[filePath] = changeType;
        }

        // 重置防抖定时器（1秒后执行）
        _debounceTimer.Change(1000, Timeout.Infinite);
    }

    /// <summary>
    /// 防抖定时器回调
    /// </summary>
    private void OnDebounce(object? state)
    {
        if (_disposed)
            return;

        // 获取所有待处理的文件（在锁内复制并清空）
        Dictionary<string, FileChangeType> changesToProcess;
        lock (_pendingChangesLock)
        {
            if (_pendingFileChanges.Count == 0)
                return;

            changesToProcess = new Dictionary<string, FileChangeType>(_pendingFileChanges);
            _pendingFileChanges.Clear();
        }

        _logger.LogInformation("Processing {Count} file change(s)", changesToProcess.Count);

        // 确保不会同时处理多个变化
        if (!_processingLock.Wait(0))
            return;

        try
        {
            // 在后台线程中执行异步操作
            _ = Task.Run(async () =>
            {
                try
                {
                    await _onFileChanged(changesToProcess, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing file changes");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scheduling file change processing");
        }
        finally
        {
            _processingLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _debounceTimer.Dispose();
        _processingLock.Dispose();

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing file watcher");
        }

        _logger.LogDebug("FileWatcherService disposed");
    }
}
