# Investigating Issues

## AssemblyLoadContext won't fully unload

The tests validate that we're able to fully unload an `AssemblyLoadContext` when disposing of
`CompilerLogReader` and it's associated state.

### Getting a dump to investigate

Set the windows registry so that it will create crash dumps when programs crash. This particular entry creates heap dumps (type 2) and retains up to 10 of them.

```reg
[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps]
"DumpFolder"="C:\\Users\\jaredpar\\temp\\dumps"
"DumpCount"=dword:00000010
"DumpType"=dword:00000002
```

Then change the test to crash on failure which will create a dump file:

```cs
Environment.FailFast("The condition was hit");
```

### Investigating the failure

Install `dotnet-sos` to get the location for sos in WinDbg

```cmd
⚡🔨 > dotnet-sos install
Installing SOS to C:\Users\jaredpar\.dotnet\sos
Installing over existing installation...
Creating installation directory...
Copying files from C:\Users\jaredpar\.dotnet\tools\.store\dotnet-sos\9.0.607501\dotnet-sos\9.0.607501\tools\net6.0\any\win-x64
Copying files from C:\Users\jaredpar\.dotnet\tools\.store\dotnet-sos\9.0.607501\dotnet-sos\9.0.607501\tools\net6.0\any\lib
Execute '.load C:\Users\jaredpar\.dotnet\sos\sos.dll' to load SOS in your Windows debugger.
Cleaning up...
SOS install succeeded
```

The type `LoaderAllocatorScout` is what will root the types keeping the `AssemblyLoadContext` alive. 

```txt
0:004> !dumpheap -type LoaderAllocatorScout
         Address               MT           Size
    01735d1e9b18     7ffc3b9b86f8             24 
    01735d8dc0f0     7ffc3b9b86f8             24 

Statistics:
          MT Count TotalSize Class Name
7ffc3b9b86f8     2        48 System.Reflection.LoaderAllocatorScout
Total 2 objects, 48 bytes
```

Then run `!gcroot` on the instances to see what is rooting them.

```txt
0:004> !gcroot 1735d8dc0f0
Caching GC roots, this may take a while.
Subsequent runs of this command will be faster.

Found 0 unique roots.
0:004> !gcroot 01735d1e9b18 
HandleTable:
    000001735acc12c8 (strong handle)
          -> 01735b00ff88     System.Object[] 
          -> 01735d1d9880     System.Lazy<MessagePack.Internal.DynamicAssembly> (static variable: MessagePack.MessagePackSerializerOptions.Options)
          -> 01735d1e9aa0     MessagePack.Internal.DynamicAssembly 
          -> 01735d1e9ac0     System.Reflection.Emit.RuntimeAssemblyBuilder 
          -> 01735d1e9b30     System.Reflection.RuntimeAssembly 
          -> 01735d1e9ae8     System.Reflection.LoaderAllocator 
          -> 01735d1e9b18     System.Reflection.LoaderAllocatorScout 

Found 1 unique roots.
```