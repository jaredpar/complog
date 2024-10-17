#if NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit.Abstractions;

namespace Basic.CompilerLog.UnitTests;

public static class AppDomainUtils
{
    private static readonly object s_lock = new object();
    private static bool s_hookedResolve;

    public static AppDomain Create(string? name = null, string? basePath = null)
    {
        name = name ?? "Custom AppDomain";
        basePath = basePath ?? Path.GetDirectoryName(typeof(AppDomainUtils).Assembly.Location);

        lock (s_lock)
        {
            if (!s_hookedResolve)
            {
                AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
                s_hookedResolve = true;
            }
        }

        return AppDomain.CreateDomain(name, null, new AppDomainSetup()
        {
            ConfigurationFile = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile,
            ApplicationBase = basePath
        });
    }

    /// <summary>
    /// When run under xunit without AppDomains all DLLs get loaded via the AssemblyResolve
    /// event.  In some cases the xunit, AppDomain marshalling, xunit doesn't fully hook
    /// the event and we need to do it for our assemblies.
    /// </summary>
    private static Assembly? OnResolve(object sender, ResolveEventArgs e)
    {
        var assemblyName = new AssemblyName(e.Name);
        var fullPath = Path.Combine(
            Path.GetDirectoryName(typeof(AppDomainUtils).Assembly.Location),
            assemblyName.Name + ".dll");
        if (File.Exists(fullPath))
        {
            return Assembly.LoadFrom(fullPath);
        }

        return null;
    }
}

public sealed class AppDomainTestOutputHelper : MarshalByRefObject, ITestOutputHelper
{
    public ITestOutputHelper TestOutputHelper { get; }

    public AppDomainTestOutputHelper(ITestOutputHelper testOutputHelper)
    {
        TestOutputHelper = testOutputHelper;
    }

    public void WriteLine(string message) =>
        TestOutputHelper.WriteLine(message);

    public void WriteLine(string format, params object[] args) =>
        TestOutputHelper.WriteLine(format, args);
}

public sealed class InvokeUtil : MarshalByRefObject
{
    internal void Invoke<T>(string typeName, string methodName, ITestOutputHelper testOutputHelper, T state)
    {
        var type = typeof(AppDomainUtils).Assembly.GetType(typeName, throwOnError: false)!;
        var member = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;

        // A static lambda will still be an instance method so we need to create the closure
        // here.
        var obj = member.IsStatic
            ? null
            : type.Assembly.CreateInstance(typeName);

        try
        {
            member.Invoke(obj, [testOutputHelper, state]);
        }
        catch (TargetInvocationException ex)
        {
            throw new Exception(ex.InnerException.Message);
        }
    }
}

#endif
