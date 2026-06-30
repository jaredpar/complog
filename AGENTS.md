# AGENTS.md

This file tells AI assistants how to work in the complog (Compiler Logs) repository: how it is
structured, how to build and test it, how style is enforced, and how CI/CD works. For details about
the code itself — internal architecture, source layout, coding style, and how the test suite is
designed — see [docs/overview.md](docs/overview.md).

## Project Overview

**complog** is a .NET tool and library for creating, consuming, and analyzing compiler log files.
These files, using the extension .complog, are built from MSBuild binary logs and contain everything needed 
to recreate Roslyn `Compilation` instances. The project is licensed under MIT.

Repository: https://github.com/jaredpar/complog

## Build & Test Commands

```bash
# Restore, build, and test (the main workflow), use -bl for binary logs
dotnet build -bl
dotnet test --framework net10.0

# Build the full solution
dotnet build Basic.CompilerLog.slnx

# Run the test suite (AI agents should focus on the net10.0 TFM for initial validation)
dotnet test src/Basic.CompilerLog.UnitTests/Basic.CompilerLog.UnitTests.csproj --framework net10.0

# Run tests with blame-hang to detect hanging tests (always use this when running full suite)
dotnet test src/Basic.CompilerLog.UnitTests/Basic.CompilerLog.UnitTests.csproj --framework net10.0 --blame-hang --blame-hang-timeout 60s

# Build with binary log (used for CI and dogfooding)
dotnet build -bl

# Pack NuGet packages
dotnet pack
```

When running the full test suite, use `--blame-hang --blame-hang-timeout 60s` to detect hanging
tests:

```bash
dotnet test src/Basic.CompilerLog.UnitTests/Basic.CompilerLog.UnitTests.csproj --framework net10.0 --blame-hang --blame-hang-timeout 60s
```

Build warnings are treated as errors in CI (`-warnaserror`). The restore step also uses
`-warnaserror`.

## Solution Structure

Solution file: `Basic.CompilerLog.slnx`

| Project | Purpose | Target Frameworks |
|---------|---------|-------------------|
| `src/Basic.CompilerLog/` | CLI entry point (`complog` global tool) | net9.0, net10.0 |
| `src/Basic.CompilerLog.App/` | CLI application logic and command handling | net9.0, net10.0 |
| `src/Basic.CompilerLog.Util/` | Core library (reading, writing, serialization) | net9.0, net10.0, net472, netstandard2.0 |
| `src/Basic.CompilerLog.UnitTests/` | Test suite | net10.0, net472 |
| `src/Scratch/` | Benchmarks and scratch development | - |
| `src/Shared/` | Shared utility files (linked into projects) | - |

## How Style Is Enforced

- Style and naming rules are enforced via `.editorconfig` at the repo root.
- The language is **C# with preview features** (`LangVersion: preview`) and **nullable reference
  types** enabled and enforced throughout.
- Build warnings are errors in CI, so style/analyzer violations fail the build.
- For the full set of coding conventions (naming, formatting, string comparison, documentation),
  see [docs/overview.md](docs/overview.md).

## Build Configuration

- **Central package management** via `src/Directory.Packages.props` (all package versions defined
  centrally).
- **Global build properties** via `src/Directory.Build.props`.
- **Assembly signing** enabled with `key.snk`.
- **Artifacts output** to the `artifacts/` directory (relative to repo root).
- Do **not** modify `global.json` or `NuGet.config` unless explicitly asked.

## Testing Notes

- Framework is xUnit SDK v3 (`xunit.v3` package); coverage is collected with Coverlet (Cobertura
  format, output to `artifacts/coverage/`).
- Tests do **not** run in parallel — parallelism is disabled.
- In clean environments, `dotnet test Basic.CompilerLog.slnx` fails unless `TEST_ARTIFACTS_PATH` is
  set for the `Basic.CompilerLog.UnitTests` fixtures. Prefer running the test project directly.
- For how the test suite is structured (fixtures, collections, `[WindowsFact]`/`[UnixFact]`, test
  resources), see [docs/overview.md](docs/overview.md).

## CI/CD

GitHub Actions (`.github/workflows/dotnet.yml`):
- Runs on push/PR to `main`.
- Matrix: ubuntu-latest + windows-latest.
- .NET SDKs: 8.0.x, 9.0.x, 10.0.x.
- Linux tests: net10.0.
- Windows tests: net10.0 + net472.
- Uploads compiler logs, test results (.trx), and coverage to Codecov.

## Further Reading

- [docs/overview.md](docs/overview.md) — internal architecture, source layout, coding style, and
  test design.
- [docs/investigating.md](docs/investigating.md) — debugging notes for specific issues.
