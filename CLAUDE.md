# CLAUDE.md

This file provides guidance for AI assistants working with the complog (Compiler Logs) repository.

## Project Overview

**complog** is a .NET tool and library for creating, consuming, and analyzing compiler log files. These files are built from MSBuild binary logs and contain everything needed to recreate Roslyn `Compilation` instances. The project is licensed under MIT.

Repository: https://github.com/jaredpar/complog

## Build & Test Commands

```bash
# Restore, build, and test (the main workflow)
dotnet build
dotnet test

# Build the full solution
dotnet build BasicCompilerLog.slnx

# Run tests (Linux: net10.0 only; Windows: net10.0 + net472)
dotnet test src/Basic.CompilerLog.UnitTests/Basic.CompilerLog.UnitTests.csproj

# Run tests for a specific framework
dotnet test src/Basic.CompilerLog.UnitTests/Basic.CompilerLog.UnitTests.csproj -f net10.0

# Build with binary log (used for CI and dogfooding)
dotnet build -bl

# Pack NuGet packages
dotnet pack
```

Build warnings are treated as errors in CI (`-warnaserror`). The restore step also uses `-warnaserror`.

## Solution Structure

Solution file: `BasicCompilerLog.slnx`

| Project | Purpose | Target Frameworks |
|---------|---------|-------------------|
| `src/Basic.CompilerLog/` | CLI entry point (`complog` global tool) | net9.0, net10.0 |
| `src/Basic.CompilerLog.App/` | CLI application logic and command handling | net9.0, net10.0 |
| `src/Basic.CompilerLog.Util/` | Core library (reading, writing, serialization) | net9.0, net10.0, net472, netstandard2.0 |
| `src/Basic.CompilerLog.UnitTests/` | Test suite | net10.0, net472 |
| `src/Scratch/` | Benchmarks and scratch development | - |
| `src/Shared/` | Shared utility files (linked into projects) | - |

### Key Source Files

- `CompilerLogReader.cs` - Main reader for .complog files (core component)
- `CompilerLogBuilder.cs` - Builder for creating .complog files
- `BinaryLogReader.cs` - MSBuild .binlog parsing
- `CompilerLogApp.cs` - CLI command dispatch and handling
- `RoslynUtil.cs` - Roslyn compiler integration utilities
- `ExportUtil.cs` - Export functionality for compilations
- `SolutionReader.cs` - Creates Roslyn workspaces from logs
- `Impl/` - Analyzer host implementations (memory and disk variants)
- `Serialize/` - MessagePack-based serialization

## Coding Conventions

### Language

- **C# with preview features** (LangVersion: preview, currently C# 14)
- **Nullable reference types** enabled and enforced throughout
- File-scoped namespace declarations
- Pattern matching and switch expressions preferred
- Use `nameof()` instead of string literals for member names
- Use `is null` / `is not null` instead of `== null` / `!= null`
- No `#region` directives
- Comments explain **why**, not **what**

### Naming (enforced via .editorconfig)

| Symbol | Convention | Example |
|--------|-----------|---------|
| Private instance fields | `_camelCase` | `_reader` |
| Private static fields | `s_camelCase` | `s_instance` |
| Constants | `PascalCase` | `MaxRetries` |
| Public fields/properties | `PascalCase` | `FilePath` |
| Methods | `PascalCase` | `ReadCompilation` |
| Interfaces | `IPascalCase` | `ICompilerCallReader` |
| Type parameters | `TPascalCase` | `TResult` |
| Parameters/locals | `camelCase` | `filePath` |

### Formatting

- 4-space indentation (no tabs)
- UTF-8 encoding, LF line endings (CRLF for .cmd/.bat files)
- Final newline required
- Trailing whitespace trimmed
- Newline before opening brace of code blocks

## Testing

- **Framework:** xUnit SDK v3 (`xunit.v3` package)
- **Coverage:** Coverlet (Cobertura format, output to `artifacts/coverage/`)
- Do **not** use "Arrange", "Act", "Assert" section comments
- Follow the naming style of nearby existing tests
- Test resources are embedded in the test assembly (see `Resources/` directory)
- Tests reference `Basic.Reference.Assemblies.Net90` for compilation scenarios

## Build Configuration

- **Central package management** via `src/Directory.Packages.props` (all package versions defined centrally)
- **Global build properties** via `src/Directory.Build.props`
- **Assembly signing** enabled with `key.snk`
- **Artifacts output** to `artifacts/` directory (relative to repo root)
- Do **not** modify `global.json` or `NuGet.config` unless explicitly asked

### Key Dependencies

| Package | Purpose |
|---------|---------|
| Microsoft.CodeAnalysis (Roslyn) | Compiler APIs (reference version: 4.8.0, build version: 5.0.0) |
| MSBuild.StructuredLogger | Binary log parsing |
| MessagePack | Serialization format for .complog files |
| Mono.Options | Command-line argument parsing |
| Microsoft.Extensions.ObjectPool | Object pooling |

## CI/CD

GitHub Actions (`.github/workflows/dotnet.yml`):
- Runs on push/PR to `main`
- Matrix: ubuntu-latest + windows-latest
- .NET SDKs: 8.0.x, 9.0.x, 10.0.x
- Linux tests: net10.0
- Windows tests: net10.0 + net472
- Uploads compiler logs, test results (.trx), and coverage to Codecov

## Architecture Notes

- **Multi-targeting**: The Util library targets net9.0/net10.0/net472/netstandard2.0 for broad compatibility
- **Roslyn version split**: Reference assemblies use version 4.8.0 (`RoslynReferenceVersion`) while build uses 5.0.0 — this is intentional for compatibility
- **AssemblyLoadContext**: Used to isolate compiler implementations for version compatibility (see `CustomCompilerLoadContext.cs`)
- **Path normalization**: Handles Windows/Unix path differences for cross-platform .complog portability
- **Streaming serialization**: MessagePack-based format for efficient .complog storage
