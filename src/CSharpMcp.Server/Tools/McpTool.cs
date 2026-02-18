using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CSharpMcp.Server.Models.Output;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server.Tools;

/// <summary>
/// MCP 工具基类
/// </summary>
public abstract class McpTool
{
    protected readonly IWorkspaceManager WorkspaceManager;
    protected readonly ILogger Logger;


    protected McpTool(
        IWorkspaceManager workspaceManager,
        ILogger logger)
    {
        WorkspaceManager = workspaceManager;
        Logger = logger;
    }

    /// <summary>
    /// 执行工具逻辑
    /// </summary>
    public abstract Task<ToolResponse> ExecuteAsync<TParams>(
        TParams parameters,
        CancellationToken cancellationToken) where TParams : notnull;

    /// <summary>
    /// 验证参数
    /// </summary>
    protected virtual void ValidateParams<TParams>(TParams parameters)
        where TParams : notnull
    {
        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }
    }

    /// <summary>
    /// 处理错误
    /// </summary>
    protected Task<ToolResponse> HandleErrorAsync(Exception ex, string context = "")
    {
        var contextStr = string.IsNullOrEmpty(context) ? "" : $" in {context}";
        Logger.LogError(ex, "Error executing tool {ToolName}{Context}", GetType().Name, contextStr);
        return Task.FromResult<ToolResponse>(new ErrorResponse(ex.Message));
    }

    /// <summary>
    /// 提取上下文代码
    /// </summary>
    protected async Task<string?> ExtractContextCodeAsync(
        Microsoft.CodeAnalysis.Document document,
        int startLine,
        int endLine,
        int contextLines,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceText = await document.GetTextAsync(cancellationToken);
            var lines = sourceText.Lines;

            var start = Math.Max(0, startLine - contextLines - 1);
            var end = Math.Min(lines.Count - 1, endLine + contextLines - 1);

            if (startLine >= endLine)
                return null;

            var text = sourceText.GetSubText(
                Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                    lines[startLine].Start,
                    lines[endLine].End
                )
            ).ToString();

            return text;
        }
        catch
        {
            return null;
        }
    }
}

