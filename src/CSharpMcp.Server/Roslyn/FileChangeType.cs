namespace CSharpMcp.Server.Roslyn;

/// <summary>
/// 文件变化类型
/// </summary>
public enum FileChangeType
{
    /// <summary>
    /// 解决方案文件 (.sln)
    /// </summary>
    Solution,

    /// <summary>
    /// 项目文件 (.csproj)
    /// </summary>
    Project,

    /// <summary>
    /// 源代码文件 (.cs)
    /// </summary>
    SourceFile,
}
