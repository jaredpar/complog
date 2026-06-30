# Overview

This document describes the internals of complog: the source layout, key components, coding style,
and how the test suite is designed.

## Source Layout

The core library lives in `src/Basic.CompilerLog.Util/`. Key source files:

- `CompilerLogReader.cs` — main reader for `.complog` files (core component).
- `CompilerLogBuilder.cs` — builder for creating `.complog` files.
- `BinaryLogReader.cs` — MSBuild `.binlog` parsing.
- `RoslynUtil.cs` — Roslyn compiler integration utilities.
- `ExportUtil.cs` — export functionality for compilations.
- `SolutionReader.cs` — creates Roslyn workspaces from logs.
- `Impl/` — analyzer host implementations (in-memory and on-disk variants).
- `Serialize/` — MessagePack-based serialization.

CLI logic lives in `src/Basic.CompilerLog.App/` (`CompilerLogApp.cs` handles command dispatch),
with `src/Basic.CompilerLog/` as the thin `complog` global-tool entry point.

## Architecture Notes

- **Multi-targeting**: The Util library targets net9.0/net10.0/net472/netstandard2.0 for broad
  compatibility.
- **Roslyn version split**: Reference assemblies use version 4.8.0 (`RoslynReferenceVersion`) while
  the build uses 5.0.0 — this is intentional for compatibility.
- **AssemblyLoadContext**: Used to isolate compiler implementations for version compatibility (see
  `CustomCompilerLoadContext.cs`).
- **Streaming serialization**: MessagePack-based format for efficient `.complog` storage.

### Path normalization

Compiler logs are meant to be portable across different operating systems and environments. To
achieve this the `CompilerLogReader` normalizes paths when producing Roslyn API objects. In
particular it maps Unix to Windows paths (and vice versa) using the `PathNormalizationUtil` type.
The consumer of the `CompilerLogReader` is unaware of the original path format.

In cases where the API is ambiguous as to whether it returns raw or normalized content, `Raw` or
`Normalized` is included in the API name.

### Key Dependencies

| Package | Purpose |
|---------|---------|
| Microsoft.CodeAnalysis | Compiler APIs (reference version: 4.8.0, build version: 5.0.0) |
| MSBuild.StructuredLogger | Binary log parsing |
| MessagePack | Serialization format for `.complog` files |
| Mono.Options | Command-line argument parsing |
| Microsoft.Extensions.ObjectPool | Object pooling |

## Coding Conventions

### Language

- **C# with preview features** (`LangVersion: preview`, currently C# 14).
- **Nullable reference types** enabled and enforced throughout.
- File-scoped namespace declarations.
- Pattern matching and switch expressions preferred.
- Use `nameof()` instead of string literals for member names.
- Use `is null` / `is not null` instead of `== null` / `!= null`.
- No `#region` directives.
- Comments explain **why**, not **what**.
- Use XML documentation comments (`/// <summary>`) on all methods and types; plain `//` comments on
  the line immediately before a declaration are not acceptable.
- Nested types should be grouped at the top of the type.

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

### Comparing strings

- Use `PathUtil.Comparison` for file paths.
- Use `StringComparison.OrdinalIgnoreCase` for compiler arguments.
- Use `StringComparison.Ordinal` for all other string comparisons.

### Formatting

- 4-space indentation (no tabs).
- UTF-8 encoding, LF line endings (CRLF for `.cmd`/`.bat` files).
- Final newline required.
- Trailing whitespace trimmed.
- Newline before opening brace of code blocks.

## Test Suite Design

Tests live in `src/Basic.CompilerLog.UnitTests/` and use xUnit SDK v3 (`xunit.v3`).

### Conventions

- Do **not** use "Arrange", "Act", "Assert" section comments.
- Follow the naming style of nearby existing tests.
- Tests reference `Basic.Reference.Assemblies.Net90` for compilation scenarios.
- Test resources are embedded in the test assembly (see the `Resources/` directory).
- Tests do **not** run in parallel — parallelism is disabled.

### OS-conditional facts

Some behavior is platform-specific. The suite provides conditional test attributes (see
`ConditionalFacts.cs`) that skip tests on the wrong OS:

- `[WindowsFact]` / `[WindowsTheory]` — run only on Windows.
- `[UnixFact]` / `[UnixTheory]` — run only on non-Windows (Unix/macOS) platforms.

Use these instead of runtime OS checks inside a test body so skipped tests are reported correctly.

### Fixtures and collections

Generating compiler logs is expensive, so logs are built once per test run and shared via fixtures:

- `CompilerLogFixture` builds a set of sample projects (console apps, projects with references,
  generators, etc.) and exposes them as `Lazy<LogData>` values. Tests should reuse these existing
  projects for validation rather than building new ones.
- `SolutionFixture` provides a sample solution for workspace/solution-reader scenarios.
- `FixtureBase` provides shared helpers such as `RunDotnetCommand`, which logs diagnostics to the
  xUnit message sink.

Fixtures are wired up through xUnit collections (`[CollectionDefinition]` /
`[Collection(...)]`), for example `CompilerLogCollection` and `SolutionFixtureCollection`. Add a
test class to the appropriate collection to consume the corresponding fixture.

### Test artifacts directory

Tests write generated artifacts to a test-artifacts directory. Locally this defaults to a
`test-artifacts` folder next to the test assembly. In GitHub Actions the `TEST_ARTIFACTS_PATH`
environment variable must be set; in clean environments, running the full solution test target can
fail without it, so prefer running the test project directly.

## See Also

- [docs/investigating.md](investigating.md) — debugging notes for specific issues (e.g.,
  `AssemblyLoadContext` unload investigations).
