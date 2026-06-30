using Xunit;

// These fixtures build a shared set of compiler logs / solutions that are expensive to create.
// Registering them as assembly fixtures (rather than collection fixtures) means they are built
// exactly once for the whole assembly while still allowing the individual test classes to run in
// parallel collections. See docs/overview.md for details on the test fixture design.
[assembly: AssemblyFixture(typeof(Basic.CompilerLog.UnitTests.CompilerLogFixture))]
[assembly: AssemblyFixture(typeof(Basic.CompilerLog.UnitTests.SolutionFixture))]
