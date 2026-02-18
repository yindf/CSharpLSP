using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using CSharpMcp.Server.Roslyn;
using System.Text.Json;

namespace CSharpMcp.Server;

/// <summary>
/// Roslyn MCP Server 主程序
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // CRITICAL: Configure all logs to go to stderr
        // MCP stdio transport requires ONLY JSON-RPC messages on stdout
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // Add file logging for debugging
        builder.Logging.AddProvider(new FileLoggerProvider("C:/Project/CSharpMcp/mcp.log"));

        // Core services (injected into tool methods by MCP SDK)
        builder.Services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
        builder.Services.AddSingleton<IInheritanceAnalyzer, InheritanceAnalyzer>();

        // Set up MCP Server with stdio transport
        // Tools are auto-discovered via [McpServerToolType] and [McpServerTool] attributes
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(Program).Assembly, serializerOptions: McpJsonOptions.Options);

        var host = builder.Build();

        // Auto-load workspace if CSHARPMCP_WORKSPACE environment variable is set
        var workspacePath = Environment.GetEnvironmentVariable("CSHARPMCP_WORKSPACE");
        if (!string.IsNullOrEmpty(workspacePath))
        {
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            var workspaceManager = host.Services.GetRequiredService<IWorkspaceManager>();

            logger.LogInformation("Auto-loading workspace from environment variable: {Path}", workspacePath);

            try
            {
                // Load in background without blocking server startup
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await workspaceManager.LoadAsync(workspacePath);
                        logger.LogInformation("Workspace auto-loaded successfully");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to auto-load workspace from environment variable");
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start workspace auto-load");
            }
        }

        await host.RunAsync();
    }
}
