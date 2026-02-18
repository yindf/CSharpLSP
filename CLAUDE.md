# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

CSharp MCP Server is a Roslyn-based Model Context Protocol (MCP) server that provides powerful C# code analysis and navigation capabilities. Unlike LSP-based implementations, this server uses Roslyn API directly to access complete semantic information for more accurate and deep code analysis.

## Build and Test Commands

### Building
```bash
dotnet build
```

### Running tests
```bash
# Unit tests
dotnet test tests/CSharpMcp.Tests

# Integration tests
dotnet test tests/CSharpMcp.IntegrationTests

# Single test
dotnet test --filter "FullyQualifiedName~TestClassName"
```

### Running the server
```bash
dotnet run --project src/CSharpMcp.Server
```

### Publishing
```bash
dotnet publish src/CSharpMcp.Server -o publish
```

## Architecture

The project follows a layered architecture with clear separation of concerns:

### Core Components

1. **WorkspaceManager** (`Roslyn/WorkspaceManager.cs`) - Central service for managing Roslyn workspaces
   - Loads .sln, .slnx, or .csproj files
   - Provides document lookup with fuzzy matching
   - Auto-discovers solutions by searching up the directory tree
   - Handles Unity projects by filtering out Packages/PackageCache
   - Uses MSBuildWorkspace for compilation

2. **InheritanceAnalyzer** (`Roslyn/InheritanceAnalyzer.cs`) - Analyzes type hierarchies
   - Finds base types and interfaces
   - Uses BFS for derived type collection with depth limiting
   - Leverages `SymbolFinder.FindDerivedClassesAsync` and `SymbolFinder.FindImplementationsAsync`

3. **SymbolExtensions** (`Roslyn/SymbolExtensions.cs`) - Unified symbol operations
   - Extension methods for `ISymbol` to get signatures, documentation, source code
   - Call graph analysis via `GetCalleesAsync` and `SymbolFinder.FindCallersAsync`
   - Line range calculation including XML comments and attributes
   - Symbol resolution from `FileLocationParams`

### Tool Categories

Tools are organized in `Tools/` by category:

- **Essential** (`Tools/Essential/`) - Core tools: `get_symbols`, `go_to_definition`, `find_references`, `resolve_symbol`, `search_symbols`
- **HighValue** (`Tools/HighValue/`) - Advanced analysis: `get_inheritance_hierarchy`, `get_call_graph`, `get_type_members`
- **Optimization** (`Tools/Optimization/`) - Performance tools: `get_symbol_complete`, `batch_get_symbols`, `get_diagnostics`

### Key Design Patterns

1. **MCP Tool Registration**: Tools use `[McpServerTool]` attribute and are auto-discovered via `WithToolsFromAssembly`

2. **Parameter Models**: All tool parameters inherit from `FileLocationParams` (common: `symbolName`, `filePath`, `lineNumber`) in `Models/Tools/ToolParams.cs`

3. **JSON Serialization**: `JsonSerializationContext` uses source generation for all parameter types. When adding new tools, add `[JsonSerializable(typeof(YourParams))]` here

4. **Dependency Injection**: Core services (`IWorkspaceManager`, `IInheritanceAnalyzer`) registered as singletons in `Program.cs`

## Important Implementation Details

### File Path Resolution
The workspace manager supports multiple path formats:
- Absolute paths
- Relative paths from loaded workspace
- Filename-only (fuzzy match across workspace)
- Auto-discovers .sln files by traversing up directory tree

### Token Optimization Strategies
Tools use `detail_level` parameter (Compact, Summary, Standard, Full) to control output verbosity. Always prefer lower detail levels for quick exploration.

### Unity Project Support
When Unity is detected (UnityEngine namespace found), the workspace manager filters out projects in `Packages/` or `PackageCache/` directories via the `UserProjects` property.

### Symbol Resolution
Use `SymbolExtensions.FindSymbolAsync` for resolving symbols from `FileLocationParams` - it handles fuzzy matching by filename and line number proximity.

## Adding New Tools

1. Create parameter record in `Models/Tools/ToolParams.cs`
2. Add `[JsonSerializable(typeof(YourParams))]` to `JsonSerializationContext`
3. Create tool class in appropriate `Tools/` subdirectory with `[McpServerToolType]` and `[McpServerTool]` attributes
4. Inject `IWorkspaceManager` via constructor if needed
5. Return markdown-formatted strings for responses

## Environment Variables

- `CSHARPMCP_WORKSPACE` - Auto-loads workspace on startup if set

## Logging

All logs go to stderr (MCP stdio requirement) and file (`mcp.log` in project root). Use `ILogger` for diagnostics.
