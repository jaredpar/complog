# General

## Path normalization

Compiler logs are meant to be portable across different operating systems and environments. To achieve this the `CompilerLogReader` will normalize paths when producing Roslyn API objects. In particular it will map Unix to Windows paths (and vice versa) using the `PathNormalizationUtil` type. The consumer of the `CompilerLogReader` will be unaware of the original path format.

In cases where the API is ambiguous to whether it is returning raw or normalized content then `Raw` or `Normalized` will be included in the API name.
