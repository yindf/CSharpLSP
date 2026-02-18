using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// WorkspaceManager 文件监控部分类
/// </summary>
internal sealed partial class WorkspaceManager
{
    /// <summary>
    /// 启动文件监控服务
    /// </summary>
    private void StartFileWatcher()
    {
        try
        {
            // 停止旧的监控器
            _fileWatcher?.Dispose();

            if (_currentSolution == null || string.IsNullOrEmpty(_loadedPath))
            {
                _logger.LogWarning("Cannot start file watcher: no solution loaded");
                return;
            }

            // 获取解决方案根目录
            var solutionDirectory = Path.GetDirectoryName(_loadedPath);

            // 创建新的文件监控器（简化版 - 单一监控器监控整个解决方案目录）
            _fileWatcher = new FileWatcherService(
                _loadedPath,
                solutionDirectory!,
                OnFileChangedAsync,
                _logger
            );

            _logger.LogInformation("File watcher started for: {Path}", _loadedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start file watcher");
        }
    }

    /// <summary>
    /// 停止文件监控服务
    /// </summary>
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

    /// <summary>
    /// 文件变化回调处理（使用 TryApplyChanges 实现线程安全）
    /// </summary>
    private async Task OnFileChangedAsync(FileChangeType changeType, string filePath, CancellationToken cancellationToken)
    {
        // 如果正在编译，跳过这次变化（会被防抖机制重新触发）
        if (Interlocked.CompareExchange(ref _isCompiling, 1, 0) == 1)
        {
            _logger.LogDebug("Compilation in progress, skipping file change: {Type} - {Path}", changeType, filePath);
            return;
        }

        try
        {
            _logger.LogInformation("Processing file change: {Type} - {Path}", changeType, filePath);

            Solution newSolution;
            bool applied;

            // 使用 compare-and-swap 循环处理并发修改
            do
            {
                // 获取最新的 Solution（不需要锁，Solution 是不可变的）
                var currentSolution = _currentSolution;

                if (currentSolution == null)
                {
                    _logger.LogWarning("No solution loaded, skipping file change");
                    return;
                }

                // 根据变化类型创建新的 Solution
                newSolution = await CreateNewSolutionAsync(currentSolution, changeType, filePath, cancellationToken);

                if (newSolution == null)
                {
                    _logger.LogDebug("Failed to create new solution, skipping file change");
                    return;
                }

                // 使用 TryApplyChanges 尝试应用更改
                // 这是 Roslyn 推荐的线程安全方式
                applied = _workspace.TryApplyChanges(newSolution);

                if (!applied)
                {
                    _logger.LogDebug("TryApplyChanges failed, retrying...");
                    // 短暂延迟后重试
                    await Task.Delay(50, cancellationToken);
                }

            } while (!applied && !cancellationToken.IsCancellationRequested);

            if (applied)
            {
                // 更新本地 Solution 引用（简单的指针赋值，不需要锁）
                _currentSolution = newSolution;

                // 清除缓存
                _compilationCache.Clear();
                _lastUpdate = DateTime.UtcNow;

                _logger.LogInformation("File change applied successfully: {Type} - {Path}", changeType, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file change: {Type} - {Path}", changeType, filePath);
        }
        finally
        {
            // 标记编译完成
            Interlocked.Exchange(ref _isCompiling, 0);
        }
    }

    /// <summary>
    /// 根据文件变化类型创建新的 Solution
    /// </summary>
    private async Task<Solution?> CreateNewSolutionAsync(
        Solution currentSolution,
        FileChangeType changeType,
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (changeType)
            {
                case FileChangeType.Solution:
                    // 重新加载整个解决方案
                    _logger.LogInformation("Reloading solution: {Path}", filePath);
                    var solution = await _workspace.OpenSolutionAsync(filePath, progress: null, cancellationToken);
                    return solution;

                case FileChangeType.Project:
                    // 重新加载特定项目
                    _logger.LogInformation("Reloading project: {Path}", filePath);
                    var project = await _workspace.OpenProjectAsync(filePath, progress: null, cancellationToken);
                    return project?.Solution;

                case FileChangeType.SourceFile:
                    // 更新文档内容
                    var documentIds = currentSolution.GetDocumentIdsWithFilePath(filePath);

                    if (documentIds.Length == 0)
                    {
                        _logger.LogDebug("No documents found for path: {Path}", filePath);
                        return null;
                    }

                    // 读取文件内容（只读操作，不修改源文件）
                    var sourceText = SourceText.From(File.ReadAllText(filePath), encoding: System.Text.Encoding.UTF8);

                    // 更新第一个文档（通常一个文件只在一个项目中）
                    // WithDocumentText 只更新内存中的 Solution，不修改磁盘上的文件
                    return currentSolution.WithDocumentText(documentIds[0], sourceText);

                case FileChangeType.Config:
                    // 配置文件变化，不需要更新 Solution，只需清除缓存
                    _logger.LogInformation("Config file changed, clearing cache: {Path}", filePath);
                    return currentSolution;

                default:
                    _logger.LogWarning("Unknown file change type: {Type}", changeType);
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create new solution for: {Type} - {Path}", changeType, filePath);
            return null;
        }
    }
}
