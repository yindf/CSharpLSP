# C# MCP Server - Parameter Documentation

## Enum Values Reference

### SymbolKind Enum Values

The `filterKinds` parameter accepts string enum values (not numeric):

| Value | Description |
|-------|-------------|
| `Class` | Classes |
| `Struct` | Structs |
| `Interface` | Interfaces |
| `Enum` | Enums |
| `Record` | Records |
| `Delegate` | Delegates |
| `Attribute` | Attributes |
| `Method` | Methods |
| `Property` | Properties |
| `Field` | Fields |
| `Event` | Events |
| `Constructor` | Constructors |
| `Destructor` | Destructors |
| `Namespace` | Namespaces |
| `Parameter` | Parameters |
| `Local` | Local variables |
| `TypeParameter` | Type parameters |
| `Unknown` | Unknown symbol types |

**Example Usage**:
```json
{
  "filterKinds": ["Method", "Property"]
}
```

### CallGraphDirection Enum Values

The `direction` parameter accepts numeric values:

| Value | Name | Description |
|-------|------|-------------|
| 0 | Both | Returns both callers and callees |
| 1 | In | Returns only callers (methods that call this method) |
| 2 | Out | Returns only callees (methods called by this method) |

**Example Usage**:
```json
{
  "direction": 1  // Get callers only
}
```

### DetailLevel Enum Values

The `detailLevel` parameter accepts numeric values:

| Value | Description |
|-------|-------------|
| 0 | Summary - Basic information only |
| 1 | Standard - Detailed information |
| 2 | Verbose - Complete information including documentation |
| 3 | Complete - All available information |

### SymbolCompleteSections Enum Values

The `sections` parameter is a bitmask flag that can be combined:

| Value | Name | Description |
|-------|------|-------------|
| 1 | Basic | Basic information (name, type, location) |
| 2 | Signature | Signature information (parameters, return value) |
| 4 | Documentation | XML documentation comments |
| 8 | SourceCode | Source code snippet |
| 16 | References | Reference locations |
| 32 | Inheritance | Inheritance hierarchy |
| 64 | CallGraph | Call graph information |
| 127 | All | All sections combined |

**Example Usage**:
```json
{
  "sections": 73  // Basic (1) + Signature (2) + SourceCode (8) + CallGraph (64) = 73
}
```

## Common Issues and Solutions

### Issue: "No Symbol Found" Error

**Cause**: The line number doesn't point to a valid symbol declaration.

**Solution**:
1. Use `GetSymbols` first to find valid line numbers
2. Ensure the line number points to a type/method declaration (not inside the body)
3. Try using `symbolName` parameter instead

### Issue: GetTypeMembers Returns 0 Members

**Cause**: The `filterKinds` parameter is using wrong values.

**Solution**: Use string enum values instead of numeric values:
```json
{
  "filterKinds": ["Method", "Property"]  // Correct
}
```

NOT:
```json
{
  "filterKinds": [7, 8]  // Incorrect - numeric values don't work
}
```

### Issue: GetCallGraph Returns Empty or Fails

**Cause**:
1. The line number doesn't point to a method
2. The method is from external library (no source available)
3. Wrong direction value

**Solution**:
1. Ensure the symbol is a method (not property, field, etc.)
2. Use direction 1 (callers) which is more reliable
3. For callees (direction 2), the method must have source code available

## Tool-Specific Tips

### SearchSymbols
- Requires workspace to be loaded first using `LoadWorkspace`
- Supports wildcards: `*Manager*`, `MyClass*`, `*Service`
- Returns symbols from all projects in the solution

### GetSymbols
- Use `lineNumber` without `symbolName` to get all symbols in file
- Use `lineNumber` with `symbolName` to get specific symbol details
- Set `includeBody: true` to get source code

### FindReferences
- Set `includeContext: true` to see surrounding code
- Adjust `contextLines` to control how much context is shown

### GetCallGraph
- Use `direction: 1` (callers) - most reliable
- Use `direction: 2` (callees) - requires source code
- Use `direction: 0` (both) - may return empty if no source available

### GetTypeMembers
- Must point to a type (class, struct, interface, enum)
- Use `filterKinds` to filter by member type
- Set `includeInherited: true` to include inherited members

### GetInheritanceHierarchy
- Must point to a type declaration
- Set `includeDerived: true` to find derived classes
- Use `maxDerivedDepth` to limit how deep to search

### BatchGetSymbols
- All line numbers must be valid symbol positions
- Returns partial results even if some symbols fail
- Use `maxConcurrency` to control parallel processing

### GetDiagnostics
- Call without `filePath` to get all workspace diagnostics
- Call with `filePath` to get diagnostics for specific file
- Use `includeWarnings: true` to see warnings

### LoadWorkspace
- Accepts .sln, .csproj, or directory path
- Auto-detects solution/project if directory is provided
- Must be called before most other tools
