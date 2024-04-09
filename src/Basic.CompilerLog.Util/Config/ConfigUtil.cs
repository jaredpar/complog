using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basic.CompilerLog.Util.Config;

public static class ConfigUtil
{
    /// <summary>
    /// Gets the set of <see cref="ConfigOptions"/> if the analyzers included code style analyzers.
    /// </summary>
    public static ConfigOptions? GetConfigOptions(IEnumerable<DiagnosticAnalyzer> analyzers)
    {
        var codeStyleAssembly = analyzers
            .Select(x => x.GetType().Assembly)
            .Where(x => x.GetName().Name == "Microsoft.CodeAnalysis.CodeStyle")
            .FirstOrDefault();
        if (codeStyleAssembly is null)
        {
            return null;
        }

        var codeStlyeDescriptors = analyzers
            .Where(x => IsCodeStyleAssembly(x.GetType().Assembly))
            .SelectMany(x => x.SupportedDiagnostics);
        var ideMapType = codeStyleAssembly.GetType("Microsoft.CodeAnalysis.Diagnostics.IDEDiagnosticIdToOptionMappingHelper")!;
        var groupMap = new Dictionary<string, OptionGroup>();
        var definitionMap = new Dictionary<string, OptionDefinition>();
        var descriptorMap = new Dictionary<DiagnosticDescriptor, ImmutableArray<OptionDefinition>>();

        foreach (var descriptor in codeStlyeDescriptors)
        {
            var options = GetOptions(ideMapType, descriptor.Id);
            if (options is null)
            {
                continue;
            }

            var builder = ImmutableArray.CreateBuilder<OptionDefinition>();
            foreach (var option in options)
            {
                builder.Add(CreateFromOption(option));
            }

            descriptorMap[descriptor] = builder.ToImmutable();
        }

        GetCSharpFormattingOptions();
        GetVisualBasicFormattingOptions();

        return new ConfigOptions(
            codeStlyeDescriptors.ToImmutableArray(),
            definitionMap.ToImmutableDictionary(),
            descriptorMap.ToImmutableDictionary());

        OptionDefinition CreateFromOption(object option)
        {
            return CreateFromDefinition(ReflectionUtil.ReadProperty<object>(option, "Definition"));
        }

        OptionDefinition CreateFromDefinition(object definition)
        {
            var configName = ReflectionUtil.ReadProperty<string>(definition, "ConfigName");
            if (definitionMap.TryGetValue(configName, out var optionDefinition))
            {
                return optionDefinition;
            }

            var isEditorConfigOption = ReflectionUtil.ReadProperty<bool>(definition, "IsEditorConfigOption");
            var group = GetOrCreateGroup(ReflectionUtil.ReadProperty<object>(definition, "Group"));
            optionDefinition = new OptionDefinition(group, configName, isEditorConfigOption);
            definitionMap[configName] = optionDefinition;
            return optionDefinition;
        }

        OptionGroup GetOrCreateGroup(object group)
        {
            var groupName = ReflectionUtil.ReadProperty<string>(group, "Name");
            if (groupMap.TryGetValue(groupName, out var optionGroup))
            {
                return optionGroup;
            }

            var description = ReflectionUtil.ReadProperty<string>(group, "Description");
            var priority = ReflectionUtil.ReadProperty<int>(group, "Priority");
            var parentObj = ReflectionUtil.ReadProperty<object?>(group, "Parent");
            var parent = parentObj is null ? null : GetOrCreateGroup(parentObj);
            optionGroup = new OptionGroup(parent, groupName, description, priority);
            groupMap[groupName] = optionGroup;
            return optionGroup;
        }

        static bool IsCodeStyleAssembly(Assembly assembly) => assembly.GetName().Name switch 
        {
            "Microsoft.CodeAnalysis.CodeStyle" => true,
            "Microsoft.CodeAnalysis.CSharp.CodeStyle" => true,
            "Microsoft.CodeAnalysis.VisualBasic.CodeStyle" => true,
            _ => false,
        };

        static IEnumerable<object>? GetOptions(Type ideMapType, string diagnosticId)
        {
            var methodInfo = ideMapType.GetMethod("TryGetMappedOptions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var arguments = new object?[3]
            {
                diagnosticId,
                LanguageNames.CSharp,
                null
            };

            var result = methodInfo.Invoke(null, arguments);
            if (result is true)
            {
                return (IEnumerable<object>)arguments[2]!;
            }
            
            return null;
        }

        void GetCSharpFormattingOptions()
        {
            var assembly = analyzers
                .Select(x => x.GetType().Assembly)
                .Where(x => x.GetName().Name == "Microsoft.CodeAnalysis.CSharp.CodeStyle")
                .FirstOrDefault();
            if (assembly is null)
            {
                return;
            }

            var type = assembly.GetType("Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions2")!;
            var value = ReflectionUtil.ReadStaticProperty<IEnumerable<object>>(type, "AllOptions");
            foreach (var option in value)
            {
                _ = CreateFromOption(option);
            }
        }

        void GetVisualBasicFormattingOptions()
        {
            var assembly = analyzers
                .Select(x => x.GetType().Assembly)
                .Where(x => x.GetName().Name == "Microsoft.CodeAnalysis.CodeStyle")
                .FirstOrDefault();
            if (assembly is null)
            {
                return;
            }

            var type = assembly.GetType("Microsoft.CodeAnalysis.VisualBasic.CodeStyle.VisualBasicCodeStyleOptions")!;
            var value = ReflectionUtil.ReadStaticProperty<IEnumerable<object>>(type, "AllOptions");
            foreach (var option in value)
            {
                _ = CreateFromOption(option);
            }
        }
    }
}

public sealed class ConfigOptions(
    ImmutableArray<DiagnosticDescriptor> descriptors,
    ImmutableDictionary<string, OptionDefinition> optionDefinitions,
    ImmutableDictionary<DiagnosticDescriptor, ImmutableArray<OptionDefinition>> descriptorToOptionsMap)
{
    public ImmutableArray<DiagnosticDescriptor> Descriptors { get; } = descriptors;
    public ImmutableDictionary<string, OptionDefinition> OptionDefinitions { get; } = optionDefinitions;
    public ImmutableDictionary<DiagnosticDescriptor, ImmutableArray<OptionDefinition>> DescriptorToOptionsMap { get; } = descriptorToOptionsMap;
}

public sealed class OptionDefinition(OptionGroup group, string configName, bool isEditorConfigOption)
{
    public OptionGroup OptionGroup { get; } = group;
    public string ConfigName { get; } = configName;
    public bool IsEditorConfigOption { get; } = isEditorConfigOption;

    public override string ToString() => ConfigName;
}

public sealed class OptionGroup(OptionGroup? parent, string name, string description, int priority)
{
    public static readonly OptionGroup Default = new OptionGroup(null, string.Empty, string.Empty, 0);

    public OptionGroup? Parent { get; } = parent;
    public string Name { get; } = name;
    public string Desscription { get; } = description;
    public int Priority { get; } = priority;

    public override string ToString() => Name;
}
