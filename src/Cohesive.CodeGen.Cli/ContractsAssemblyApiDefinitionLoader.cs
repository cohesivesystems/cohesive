using System.Reflection;
using Cohesive.Api;

namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Loads exported <see cref="ApiDefinition"/> instances from a contracts assembly.
/// </summary>
public static class ContractsAssemblyApiDefinitionLoader
{
    /// <summary>
    /// Loads and combines all exported API definitions from an assembly.
    /// </summary>
    public static ApiDefinition Load(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Contracts assembly was not found.", assemblyPath);

        // ApiDefinition carries CLR Type references consumed by later emitters, so the
        // contract load context must remain alive for the rest of the CLI process.
        var loadContext = new ContractsAssemblyLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var definitions = DiscoverDefinitions(assembly);
        if (definitions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Assembly '{Path.GetFileName(assemblyPath)}' does not expose any public static ApiDefinition members.");
        }

        return definitions.Count == 1 ? definitions[0] : ApiDefinition.Combine(definitions);
    }

    internal static List<ApiDefinition> DiscoverDefinitions(Assembly assembly)
    {
        var exportedTypes = assembly.GetExportedTypes();
        Array.Sort(exportedTypes, static (left, right) =>
            string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));

        var definitions = new List<ApiDefinition>();
        for (var i = 0; i < exportedTypes.Length; i++)
        {
            var type = exportedTypes[i];
            DiscoverFields(type, definitions);
            DiscoverProperties(type, definitions);
            DiscoverMethods(type, definitions);
        }

        return definitions;
    }

    static void DiscoverFields(Type type, List<ApiDefinition> definitions)
    {
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
        Array.Sort(fields, static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (!IsDefinitionMember(field.FieldType, field))
                continue;

            if (field.GetValue(null) is ApiDefinition definition)
                definitions.Add(definition);
        }
    }

    static void DiscoverProperties(Type type, List<ApiDefinition> definitions)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Static);
        Array.Sort(properties, static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));

        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            if (property.GetIndexParameters().Length != 0)
                continue;

            if (!IsDefinitionMember(property.PropertyType, property))
                continue;

            if (property.GetValue(null) is ApiDefinition definition)
                definitions.Add(definition);
        }
    }

    static void DiscoverMethods(Type type, List<ApiDefinition> definitions)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
        Array.Sort(methods, static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));

        for (var i = 0; i < methods.Length; i++)
        {
            var method = methods[i];
            if (method.ContainsGenericParameters || method.GetParameters().Length != 0)
                continue;

            if (!IsDefinitionMember(method.ReturnType, method))
                continue;

            if (method.Invoke(null, null) is ApiDefinition definition)
                definitions.Add(definition);
        }
    }

    static bool IsDefinitionMember(Type memberType, MemberInfo member)
    {
        if (memberType != typeof(ApiDefinition))
            return false;

        if (member.IsDefined(typeof(ApiDefinitionAttribute), inherit: false))
            return true;

        return string.Equals(member.Name, "Definition", StringComparison.Ordinal)
               || string.Equals(member.Name, "Api", StringComparison.Ordinal)
               || string.Equals(member.Name, "ApiDefinition", StringComparison.Ordinal);
    }
}
