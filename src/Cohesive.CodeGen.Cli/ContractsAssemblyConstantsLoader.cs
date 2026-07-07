using System.Collections.Immutable;
using System.Reflection;
using Cohesive.CodeGen;

namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Loads public literal constants from a contracts assembly.
/// </summary>
public static class ContractsAssemblyConstantsLoader
{
    /// <summary>
    /// Builds a target-neutral constant set from public static classes in the contracts assembly.
    /// </summary>
    public static ContractConstantSet Load(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Contracts assembly was not found.", assemblyPath);

        var loadContext = new ContractsAssemblyLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            return DiscoverConstants(assembly);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    internal static ContractConstantSet DiscoverConstants(Assembly assembly)
    {
        var exportedTypes = assembly.GetExportedTypes();
        Array.Sort(exportedTypes, static (left, right) =>
            string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));

        var groups = ImmutableArray.CreateBuilder<ContractConstantGroup>();
        for (var i = 0; i < exportedTypes.Length; i++)
        {
            var type = exportedTypes[i];
            if (!IsConstantGroupType(type))
                continue;

            var constants = DiscoverConstants(type);
            if (constants.Length == 0)
                continue;

            groups.Add(new(
                name: type.Name,
                namespaceName: type.Namespace,
                constants: constants
                ));
        }

        return new ContractConstantSet(groups.ToImmutable());
    }

    static ImmutableArray<ContractConstant> DiscoverConstants(Type type)
    {
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
        Array.Sort(fields, static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));

        var constants = ImmutableArray.CreateBuilder<ContractConstant>();
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (!IsSupportedConstantField(field))
                continue;

            var value = field.GetRawConstantValue();
            if (value is null)
                continue;

            constants.Add(new(field.Name, field.FieldType, value));
        }

        return constants.ToImmutable();
    }

    static bool IsConstantGroupType(Type type)
    {
        if (!type.IsClass || !type.IsAbstract || !type.IsSealed || type.IsGenericTypeDefinition)
            return false;

        if (!(type.IsPublic || type.IsNestedPublic))
            return false;

        if (type.IsDefined(typeof(ObsoleteAttribute), inherit: false))
            return false;

        if (type.Name.Contains('<', StringComparison.Ordinal))
            return false;

        return true;
    }

    static bool IsSupportedConstantField(FieldInfo field)
    {
        if (!field.IsPublic || !field.IsStatic || !field.IsLiteral || field.IsInitOnly)
            return false;

        if (field.IsDefined(typeof(ObsoleteAttribute), inherit: false))
            return false;

        return IsSupportedConstantType(field.FieldType);
    }

    static bool IsSupportedConstantType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string)
               || type == typeof(bool)
               || type == typeof(char)
               || type == typeof(byte)
               || type == typeof(sbyte)
               || type == typeof(short)
               || type == typeof(ushort)
               || type == typeof(int)
               || type == typeof(uint)
               || type == typeof(long)
               || type == typeof(ulong)
               || type == typeof(float)
               || type == typeof(double)
               || type == typeof(decimal);
    }
}
