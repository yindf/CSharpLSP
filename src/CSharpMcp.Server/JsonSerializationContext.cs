using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CSharpMcp.Server.Models;
using CSharpMcp.Server.Models.Tools;
using CSharpMcp.Server.Models.Output;
using CSharpMcp.Server.Roslyn;

namespace CSharpMcp.Server;

/// <summary>
/// JSON serialization context for MCP SDK source generation.
/// Includes all parameter types and response types used by MCP tools.
/// </summary>

// Parameter types
[JsonSerializable(typeof(FileLocationParams))]
[JsonSerializable(typeof(GetSymbolsParams))]
[JsonSerializable(typeof(GoToDefinitionParams))]
[JsonSerializable(typeof(FindReferencesParams))]
[JsonSerializable(typeof(ResolveSymbolParams))]
[JsonSerializable(typeof(SearchSymbolsParams))]
[JsonSerializable(typeof(GetInheritanceHierarchyParams))]
[JsonSerializable(typeof(GetCallGraphParams))]
[JsonSerializable(typeof(GetTypeMembersParams))]
[JsonSerializable(typeof(GetSymbolCompleteParams))]
[JsonSerializable(typeof(BatchGetSymbolsParams))]
[JsonSerializable(typeof(GetDiagnosticsParams))]
[JsonSerializable(typeof(LoadWorkspaceParams))]

// Response types (only remaining types after refactoring)
[JsonSerializable(typeof(GetDiagnosticsResponse))]
[JsonSerializable(typeof(DiagnosticItem))]
[JsonSerializable(typeof(DiagnosticsSummary))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(LoadWorkspaceResponse))]

// Enums (from Tools namespace)
[JsonSerializable(typeof(DiagnosticSeverity))]
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
