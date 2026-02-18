# CSharp MCP Server

> 基于 Roslyn 的 Model Context Protocol Server，提供强大的 C# 代码分析和导航功能

[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## 简介

CSharp MCP Server 是一个使用 Roslyn API 开发的 Model Context Protocol (MCP) Server，为 AI 助手提供强大的 C# 代码分析能力。相比基于 LSP 的实现，本服务器能够访问完整的语义信息，提供更准确和深入的代码分析。

## 特性

### 核心优势

- **🎯 完整语义分析**: 直接使用 Roslyn API，获取完整的符号范围信息和类型推断
- **🌲 继承层次分析**: 支持查看类型的完整继承链和派生类
- **📊 调用图分析**: 分析方法的调用者和被调用者，计算圈复杂度
- **⚡ Token 优化**: 分层响应、智能截断、批量查询，减少 Token 使用
- **🔍 高级代码导航**: 支持模糊匹配、批量操作、按需加载

### 工具分类

| 类别 | 工具 | 描述 |
|------|------|------|
| **Essential** | `get_symbols` | 获取文档中的所有符号 |
| | `go_to_definition` | 跳转到符号定义 |
| | `find_references` | 查找符号的所有引用 |
| | `resolve_symbol` | 获取符号的完整信息 |
| | `search_symbols` | 搜索整个工作区中的符号 |
| **HighValue** | `get_inheritance_hierarchy` | 获取类型的继承层次结构 |
| | `get_call_graph` | 获取方法的调用图 |
| | `get_type_members` | 获取类型的成员 |
| **Optimization** | `get_symbol_complete` | 整合多个信息源获取完整符号信息 |
| | `batch_get_symbols` | 批量获取符号信息 |
| | `get_diagnostics` | 获取编译诊断信息 |

## 快速开始

### 安装

```bash
# 克隆仓库
git clone https://github.com/your-org/CSharpMcp.git
cd CSharpMcp

# 还原依赖
dotnet restore

# 构建
dotnet build
```

### 使用

1. **启动服务器**:

```bash
dotnet run --project src/CSharpMcp.Server
```

2. **在 MCP 客户端中配置**:

```json
{
  "mcpServers": {
    "csharp": {
      "command": "dotnet",
      "args": ["run", "--project", "path/to/CSharpMcp.Server/CSharpMcp.Server.csproj"]
    }
  }
}
```

### 配置工作区

首次使用时，需要加载工作区：

```json
{
  "method": "workspace/load",
  "params": {
    "path": "path/to/your/solution.sln"
  }
}
```

## 工具使用示例

### 获取文件中的所有方法

```json
{
  "name": "get_symbols",
  "arguments": {
    "file_path": "MyClass.cs",
    "filter_kinds": ["Method"],
    "detail_level": "Summary"
  }
}
```

### 查看方法的调用图

```json
{
  "name": "get_call_graph",
  "arguments": {
    "file_path": "MyClass.cs",
    "line_number": 25,
    "symbol_name": "MyMethod",
    "direction": "Both",
    "max_depth": 2
  }
}
```

### 批量获取符号信息

```json
{
  "name": "batch_get_symbols",
  "arguments": {
    "symbols": [
      {"file_path": "MyClass.cs", "line_number": 25},
      {"file_path": "MyClass.cs", "line_number": 50}
    ],
    "detail_level": "Standard"
  }
}
```

### 获取编译诊断

```json
{
  "name": "get_diagnostics",
  "arguments": {
    "file_path": "MyClass.cs",
    "include_warnings": true
  }
}
```

## 项目结构

```
CSharpMcp/
├── src/
│   ├── CSharpMcp.Server/          # MCP Server 主项目
│   │   ├── Tools/                 # MCP 工具实现
│   │   │   ├── Essential/         # 核心工具
│   │   │   ├── HighValue/         # 高级工具
│   │   │   └── Optimization/      # 优化工具
│   │   ├── Roslyn/                # Roslyn 封装层
│   │   │   ├── WorkspaceManager.cs
│   │   │   ├── SymbolAnalyzer.cs
│   │   │   ├── InheritanceAnalyzer.cs
│   │   │   └── CallGraphAnalyzer.cs
│   │   ├── Models/                # 数据模型
│   │   ├── Cache/                 # 缓存层
│   │   └── Program.cs
│   │
│   ├── CSharpMcp.Tests/           # 单元测试
│   └── CSharpMcp.IntegrationTests/ # 集成测试
│
├── docs/
│   ├── API.md                     # API 文档
│   └── ARCHITECTURE.md            # 架构设计
│
├── CSharpMcp.sln
├── PROJECT_PLAN.md                # 项目计划
├── IMPLEMENTATION_PLAN.md         # 实现计划
└── README.md
```

## 技术栈

| 组件 | 版本 | 说明 |
|------|------|------|
| .NET | 10.0 | 最新 LTS 版本 |
| Roslyn | 4.* | Microsoft.CodeAnalysis |
| MCP SDK | 0.2.0-preview | Model Context Protocol |
| Serilog | 3.* | 结构化日志 |
| xUnit | 2.* | 测试框架 |

## Token 优化

本服务器针对 Token 使用进行了多种优化：

### 1. 分层响应

使用 `detail_level` 参数控制输出详细程度：

- `Compact` - 仅名称和位置
- `Summary` - 简要信息
- `Standard` - 标准信息
- `Full` - 完整信息

### 2. 智能截断

- `body_max_lines` - 限制源代码行数
- `max_references` - 限制引用数量
- `max_results` - 限制搜索结果

### 3. 批量操作

- `batch_get_symbols` - 并行处理多个查询
- `get_symbol_complete` - 一次性获取所有信息

### 4. 按需加载

- `sections` 参数 - 指定需要的信息部分
- `filter_kinds` 参数 - 过滤符号类型

## 开发

### 环境要求

- .NET 10.0 SDK
- Visual Studio 2022 或 Rider

### 构建

```bash
dotnet build
```

### 运行测试

```bash
# 单元测试
dotnet test tests/CSharpMcp.Tests

# 集成测试
dotnet test tests/CSharpMcp.IntegrationTests
```

## 文档

- [API 文档](docs/API.md) - 详细的 API 参考
- [项目计划](PROJECT_PLAN.md) - 8周开发计划
- [实现计划](IMPLEMENTATION_PLAN.md) - 详细实现计划

## 路线图

### 已完成 ✅

- [x] Phase 1: 基础设施（WorkspaceManager, SymbolAnalyzer, 缓存）
- [x] Phase 2: Essential 工具（5个核心工具）
- [x] Phase 3: HighValue 工具（3个高级分析工具）
- [x] Phase 4: Optimization 工具（3个优化工具）

## Debug

npx @modelcontextprotocol/inspector CSharpMcp.Server.exe

## 贡献

欢迎贡献！请查看 [CONTRIBUTING.md](CONTRIBUTING.md) 了解详情。

## 许可证

MIT License - 详见 [LICENSE](LICENSE) 文件

## 致谢

- [Microsoft.CodeAnalysis](https://github.com/dotnet/roslyn) - Roslyn 编译器平台
- [Model Context Protocol](https://modelcontextprotocol.io) - MCP 规范
