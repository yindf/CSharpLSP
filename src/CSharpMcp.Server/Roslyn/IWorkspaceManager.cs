using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 工作区类型
/// </summary>
public enum WorkspaceKind
{
    Solution,
    Project,
    Folder
}

/// <summary>
/// 工作区信息
/// </summary>
public record WorkspaceInfo(
    string Path,
    WorkspaceKind Kind,
    int ProjectCount,
    int DocumentCount
);

/// <summary>
/// 工作区状态
/// </summary>
public record WorkspaceStatus(
    bool IsLoaded,
    int ProjectsLoaded,
    int DocumentsAnalyzed,
    double CacheHitRate,
    DateTime LastUpdate
);

/// <summary>
/// 工作区管理服务接口
/// </summary>
public interface IWorkspaceManager
{
    /// <summary>
    /// 加载解决方案或项目
    /// </summary>
    Task<WorkspaceInfo> LoadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取文档
    /// </summary>
    Task<Document?> GetDocumentAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取编译
    /// </summary>
    Task<Compilation?> GetCompilationAsync(string? projectPath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取语义模型
    /// </summary>
    Task<SemanticModel?> GetSemanticModelAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取源文本
    /// </summary>
    Task<SourceText?> GetSourceTextAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 刷新工作区（检测文件变化）
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取工作区状态
    /// </summary>
    WorkspaceStatus GetStatus();

    /// <summary>
    /// 获取当前解决方案
    /// </summary>
    Solution? GetCurrentSolution();

    /// <summary>
    /// 获取所有项目
    /// </summary>
    IReadOnlyList<Project> GetProjects();
}
