using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CSharpMcp.Server.Models;
using CSharpMcp.Server.Models.Output;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server;

/// <summary>
/// JSON serialization context for MCP SDK source generation.
/// Includes all parameter types and response types used by MCP tools.
/// </summary>

// Response types (only remaining types after refactoring)
[JsonSerializable(typeof(GetDiagnosticsResponse))]
[JsonSerializable(typeof(DiagnosticItem))]
[JsonSerializable(typeof(DiagnosticsSummary))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(LoadWorkspaceResponse))]

// Enums (from Tools namespace)
[JsonSerializable(typeof(WorkspaceKind))]

public partial class JsonSerializationContext : JsonSerializerContext
{
}

/// <summary>
/// Provides the JSON serialization options with source generation for the MCP server.
/// </summary>
public static class McpJsonOptions
{
    /// <summary>
    /// Gets the configured JsonSerializerOptions with source generation support.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
    {
        TypeInfoResolver = JsonSerializationContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
