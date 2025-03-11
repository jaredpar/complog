# Development Commands

## Build Commands
```bash
dotnet build
dotnet build -bl  # Build with binary log
```

## Test Commands
```bash
dotnet test
dotnet test --filter "FullyQualifiedName=Basic.CompilerLog.UnitTests.SomeTest.MethodName"  # Run single test
```

# Code Style Guidelines

- **Naming**: PascalCase for classes/methods/properties, camelCase for parameters/locals, `_camelCase` for private fields
- **Types**: Use explicit types for method signatures, `var` only when type is obvious
- **Error Handling**: Use exceptions with specific types, include detailed error messages
- **Formatting**: 4-space indentation, braces on new lines, ~120 char line length
- **Nullability**: Use nullable reference types (`string?`) appropriately
- **Collections**: Prefer collection expressions (`[item1, item2]`) over array initializers
- **Strings**: Use string interpolation (`$"text {variable}"`) and raw string literals for multiline
- **Imports**: Order by System → third-party → project namespaces
- **Documentation**: Use XML documentation with `<summary>` tags
- **Testing**: xUnit with AAA pattern (Arrange, Act, Assert)