using System.Reflection;
using System.Text.Json;
using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Loads a contracts assembly and projects exported CLR types into a shape graph.
/// </summary>
public static class ContractsAssemblyShapeGraphLoader
{
    /// <summary>
    /// Builds a shape graph from exported contract types.
    /// </summary>
    /// <param name="assemblyPath">Path to the compiled contracts assembly.</param>
    /// <param name="moduleName">Logical module name used to qualify the graph.</param>
    /// <returns>A CLR-semantic contract shape graph.</returns>
    public static ShapeGraph Load(string assemblyPath, string moduleName) =>
        Load(assemblyPath, moduleName, metadataProvider: null);

    /// <summary>
    /// Builds a shape graph projected through an explicit System.Text.Json wire contract.
    /// </summary>
    /// <param name="assemblyPath">Path to the compiled contracts assembly.</param>
    /// <param name="moduleName">Logical module name used to qualify the graph.</param>
    /// <param name="jsonSerializerOptions">Serializer options defining property, enum, and converter representations.</param>
    /// <returns>A JSON-wire contract shape graph.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="jsonSerializerOptions"/> is <see langword="null"/>.
    /// </exception>
    public static ShapeGraph Load(
        string assemblyPath,
        string moduleName,
        JsonSerializerOptions jsonSerializerOptions)
    {
        ArgumentNullException.ThrowIfNull(jsonSerializerOptions);
        return Load(
            assemblyPath,
            moduleName,
            new SystemTextJsonClrShapeMetadataProvider(jsonSerializerOptions));
    }

    static ShapeGraph Load(
        string assemblyPath,
        string moduleName,
        IClrShapeMetadataProvider? metadataProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);

        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("Contracts assembly was not found.", assemblyPath);

        var loadContext = new ContractsAssemblyLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var roots = DiscoverRootTypes(assembly);
            if (roots.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Assembly '{Path.GetFileName(assemblyPath)}' does not expose any contract types with readable public instance properties.");
            }

            var builder = new ClrShapeGraphBuilder();
            if (metadataProvider is not null)
                builder.AddMetadataProvider(metadataProvider);
            for (var i = 0; i < roots.Count; i++)
                builder.AddShape(roots[i], ShapeRoles.ValueObject);

            return builder.Build(new GraphId($"{moduleName}:contracts"));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    static List<Type> DiscoverRootTypes(Assembly assembly)
    {
        var exportedTypes = assembly.GetExportedTypes();
        Array.Sort(exportedTypes, static (left, right) =>
            string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));

        var roots = new List<Type>(exportedTypes.Length);
        var seen = new HashSet<Type>();
        for (var i = 0; i < exportedTypes.Length; i++)
        {
            var type = exportedTypes[i];
            AddRootTypeCandidate(type, roots, seen, validateBuildable: false);
        }

        AddApiDefinitionRootTypes(assembly, roots, seen);
        roots.Sort(static (left, right) =>
            string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));
        return roots;
    }

    static void AddApiDefinitionRootTypes(Assembly assembly, List<Type> roots, HashSet<Type> seen)
    {
        var definitions = ContractsAssemblyApiDefinitionLoader.DiscoverDefinitions(assembly);
        for (var i = 0; i < definitions.Count; i++)
        {
            var operations = definitions[i].Operations;
            for (var j = 0; j < operations.Count; j++)
                AddApiOperationRootTypes(operations[j], roots, seen);
        }
    }

    static void AddApiOperationRootTypes(ApiOperation operation, List<Type> roots, HashSet<Type> seen)
    {
        AddRootTypeCandidate(operation.RequestType, roots, seen, validateBuildable: true);
        for (var i = 0; i < operation.Results.Count; i++)
            AddRootTypeCandidate(operation.Results[i].BodyType, roots, seen, validateBuildable: true);

        if (operation.Http is not { } http)
            return;

        AddRootTypeCandidate(http.Body?.BodyType, roots, seen, validateBuildable: true);
        AddRootTypeCandidate(http.Query?.QueryType, roots, seen, validateBuildable: true);

        var parameters = http.Parameters;
        for (var i = 0; i < parameters.Count; i++)
            AddRootTypeCandidate(parameters[i].Type, roots, seen, validateBuildable: true);
    }

    static void AddRootTypeCandidate(Type? type, List<Type> roots, HashSet<Type> seen, bool validateBuildable)
    {
        if (type is null)
            return;

        var normalized = UnwrapType(type);
        if (TryGetEnumerableElementType(normalized, out var elementType))
        {
            AddRootTypeCandidate(elementType, roots, seen, validateBuildable);
            return;
        }

        if (!IsRootContractType(normalized))
            return;

        if (validateBuildable)
            EnsureBuildableRootShape(normalized);

        if (seen.Add(normalized))
            roots.Add(normalized);
    }

    static void EnsureBuildableRootShape(Type type)
    {
        try
        {
            _ = new ClrShapeGraphBuilder()
                .AddShape(type, ShapeRoles.ValueObject)
                .Build();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"API contract type '{type.FullName ?? type.Name}' cannot be projected into a shape graph: {exception.Message}",
                exception);
        }
    }

    static bool IsRootContractType(Type type)
    {
        if (IsScalarContractType(type))
            return false;

        if (!type.IsPublic || type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
            return false;

        if (type.IsEnum || typeof(Attribute).IsAssignableFrom(type))
            return false;

        if (typeof(Delegate).IsAssignableFrom(type))
            return false;

        return ShapeTypeInspector.GetReadableProperties(type).Length > 0;
    }

    static Type UnwrapType(Type type)
    {
        var normalized = Nullable.GetUnderlyingType(type) ?? type;
        return normalized.IsByRef ? normalized.GetElementType() ?? normalized : normalized;
    }

    static bool TryGetEnumerableElementType(Type type, out Type? elementType)
    {
        if (type == typeof(string))
        {
            elementType = null;
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        var interfaces = type.GetInterfaces();
        for (var i = 0; i < interfaces.Length; i++)
        {
            var candidate = interfaces[i];
            if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            elementType = candidate.GetGenericArguments()[0];
            return true;
        }

        elementType = null;
        return false;
    }

    static bool IsScalarContractType(Type type)
    {
        type = UnwrapType(type);
        return type == typeof(void)
               || type == typeof(string)
               || type == typeof(bool)
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
               || type == typeof(decimal)
               || type == typeof(Guid)
               || type == typeof(DateTime)
               || type == typeof(DateTimeOffset)
               || type == typeof(DateOnly)
               || type == typeof(TimeOnly)
               || type == typeof(JsonElement);
    }
}
