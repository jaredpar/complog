SET COMPlus_StressLog=1
SET COMPlus_LogLevel=7
SET COMPlus_LogFacility=80103
SET COMPlus_StressLogSize=2000000
SET COMPlus_TotalStressLogSize=40000000

REM dotnet exec C:\Users\jaredpar\code\complog\artifacts\bin\Basic.CompilerLog.UnitTests\debug_net8.0\Basic.CompilerLog.UnitTests.dll -method Basic.CompilerLog.UnitTests.UsingAllCompilerLogTests.EmitToMemory
REM dotnet run --framework net8.0  -- -method Basic.CompilerLog.UnitTests.UsingAllCompilerLogTests.EmitToMemory
