using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Model.Authoring;

namespace Cohesive.Model;

/// <summary>
/// Builds shape graphs from CLR POCOs.
/// </summary>
public sealed class ClrShapeGraphBuilder
{
    readonly List<RootShapeRegistration> roots = [];
    readonly List<IClrShapeMetadataProvider> metadataProviders = [ClrShapeAttributeMetadataProvider.Instance];
    readonly Dictionary<TypeId, TypeDefinition> contributedNamedTypes = [];

    /// <summary>
    /// Adds a CLR metadata provider used while deriving shapes, named types, and fields.
    /// </summary>
    /// <param name="provider">Provider appended at the highest current metadata precedence.</param>
    /// <returns>This builder for continued configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    public ClrShapeGraphBuilder AddMetadataProvider(IClrShapeMetadataProvider provider)
    {
        metadataProviders.Add(Guard.RequireNotNull(provider));
        return this;
    }

    /// <summary>
    /// Adds CLR metadata providers used while deriving shapes, named types, and fields.
    /// </summary>
    /// <param name="providers">Providers appended in increasing precedence order.</param>
    /// <returns>This builder for continued configuration.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="providers"/> or one of its entries is <see langword="null"/>.
    /// </exception>
    public ClrShapeGraphBuilder AddMetadataProviders(IEnumerable<IClrShapeMetadataProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (var provider in providers)
            AddMetadataProvider(provider);
        return this;
    }

    /// <summary>
    /// Registers a CLR type as a root shape.
    /// </summary>
    /// <typeparam name="T">CLR object type to infer as a root shape.</typeparam>
    /// <param name="role">Default semantic shape role.</param>
    /// <returns>This builder for continued root registration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="role"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="role"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> cannot be inferred as a root shape or is already registered with another role.
    /// </exception>
    public ClrShapeGraphBuilder AddShape<T>(string role = ShapeRoles.ValueObject) where T : notnull
        => AddShape(typeof(T), role);

    /// <summary>
    /// Registers a CLR type as a root shape.
    /// </summary>
    /// <param name="clrType">CLR object type to infer as a root shape.</param>
    /// <param name="role">Default semantic shape role.</param>
    /// <returns>This builder for continued root registration.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="clrType"/> or <paramref name="role"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="role"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="clrType"/> cannot be inferred as a root shape or is already registered with another role.
    /// </exception>
    public ClrShapeGraphBuilder AddShape(Type clrType, string role = ShapeRoles.ValueObject)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var normalized = UnwrapNullable(clrType);
        EnsureSupportedRootType(normalized);

        for (var i = 0; i < roots.Count; i++)
        {
            if (roots[i].ClrType != normalized)
                continue;

            if (!string.Equals(roots[i].Role, role, StringComparison.Ordinal))
                throw new InvalidOperationException($"CLR type '{normalized.Name}' is already registered as shape role '{roots[i].Role}'.");

            return this;
        }

        roots.Add(new(ClrType: normalized, Role: role));
        return this;
    }

    /// <summary>
    /// Builds an immutable shape graph for the registered CLR roots.
    /// </summary>
    /// <param name="graphId">
    /// Optional stable graph identifier. When omitted, the returned runtime graph receives an ephemeral identifier.
    /// </param>
    /// <returns>The immutable shape graph derived from the registered CLR roots.</returns>
    /// <exception cref="InvalidOperationException">
    /// A registered or referenced CLR type cannot be inferred, or metadata providers contribute
    /// conflicting semantic definitions.
    /// </exception>
    public ShapeGraph Build(GraphId? graphId = null) => BuildResult(graphId).Graph;

    /// <summary>
    /// Builds an immutable shape graph together with the effective CLR-to-semantic metadata used to create it.
    /// </summary>
    /// <param name="graphId">
    /// Optional stable graph identifier. When omitted, the returned runtime graph receives an ephemeral identifier.
    /// </param>
    /// <returns>
    /// The immutable graph and the effective type, shape, and field identities selected by all metadata providers.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A registered or referenced CLR type cannot be inferred, or metadata providers contribute
    /// conflicting semantic definitions or inconsistent field names.
    /// </exception>
    public ClrShapeGraphBuildResult BuildResult(GraphId? graphId = null)
    {
        contributedNamedTypes.Clear();
        var selectedGraphId = graphId ?? GraphId.New();
        Dictionary<Type, ShapeId> shapeIds = [];
        Dictionary<PropertyInfo, FieldName> fieldNames = [];
        Dictionary<Type, ClrShapeIdentityOrigin> shapeIdentityOrigins = [];
        Dictionary<PropertyInfo, ClrShapeIdentityOrigin> fieldIdentityOrigins = [];

        if (roots.Count == 0)
        {
            return new(
                graph: new ShapeGraph(id: selectedGraphId, shapes: [], namedTypes: []),
                typeIds: ImmutableDictionary<Type, TypeId>.Empty,
                shapeIds: ImmutableDictionary<Type, ShapeId>.Empty,
                fieldNames: ImmutableDictionary<PropertyInfo, FieldName>.Empty,
                shapeIdentityOrigins: ImmutableDictionary<Type, ClrShapeIdentityOrigin>.Empty,
                fieldIdentityOrigins: ImmutableDictionary<PropertyInfo, ClrShapeIdentityOrigin>.Empty);
        }

        var discoveredTypes = DiscoverTypes();
        var identities = BuildTypeIdentities(discoveredTypes);

        var namedTypes = new TypeDefinition[discoveredTypes.Count];
        for (var i = 0; i < discoveredTypes.Count; i++)
        {
            namedTypes[i] = BuildNamedType(
                discoveredTypes[i],
                identities,
                fieldNames,
                fieldIdentityOrigins);
        }

        var shapes = new Shape[roots.Count];
        for (var i = 0; i < roots.Count; i++)
        {
            shapes[i] = BuildShape(
                roots[i],
                identities,
                shapeIds,
                fieldNames,
                shapeIdentityOrigins,
                fieldIdentityOrigins);
        }

        HashSet<TypeId> discoveredTypeIds = [.. namedTypes.Select(static x => x.Id)];
        var additionalNamedTypes = contributedNamedTypes.Values
            .Where(type => !discoveredTypeIds.Contains(type.Id))
            .OrderBy(static type => type.Id.Value, StringComparer.Ordinal);

        var graph = new ShapeGraph(
            id: selectedGraphId,
            shapes: [.. shapes],
            namedTypes: [.. namedTypes, .. additionalNamedTypes]
            );

        return new(
            graph,
            typeIds: identities.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.TypeId),
            shapeIds: shapeIds.ToImmutableDictionary(),
            fieldNames: fieldNames.ToImmutableDictionary(),
            shapeIdentityOrigins: shapeIdentityOrigins.ToImmutableDictionary(),
            fieldIdentityOrigins: fieldIdentityOrigins.ToImmutableDictionary());
    }

    List<Type> DiscoverTypes()
    {
        var discovered = new HashSet<Type>();
        var ordered = new List<Type>();
        var pending = new Queue<Type>();

        for (var i = 0; i < roots.Count; i++)
            EnqueueIfNamedType(roots[i].ClrType, discovered, ordered, pending);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (current.IsEnum)
                continue;

            if (TryGetEitherCaseTypes(current, out var caseTypes))
            {
                for (var i = 0; i < caseTypes.Length; i++)
                    CollectReferencedTypes(caseTypes[i], discovered, ordered, pending);

                continue;
            }

            if (TryGetJsonPolymorphicCases(current, out var polymorphicCases))
            {
                for (var i = 0; i < polymorphicCases.Length; i++)
                    CollectReferencedTypes(polymorphicCases[i].ClrType, discovered, ordered, pending);

                continue;
            }

            var properties = ShapeTypeInspector.GetReadablePropertyMetadata(current);
            if (properties.Length == 0)
            {
                if (IsJsonPolymorphicDerivedType(current))
                    continue;

                throw new InvalidOperationException($"CLR type '{current.Name}' does not expose any readable public instance properties.");
            }

            for (var i = 0; i < properties.Length; i++)
                CollectReferencedTypes(properties[i].PropertyType, discovered, ordered, pending);
        }

        return ordered;
    }

    static void CollectReferencedTypes(
        Type clrType,
        HashSet<Type> discovered,
        List<Type> ordered,
        Queue<Type> pending)
    {
        var normalized = UnwrapNullable(clrType);
        if (TryMapJsonType(normalized, out _))
            return;

        if (TryGetEnumerableElementType(normalized, out var elementType))
        {
            CollectReferencedTypes(elementType, discovered, ordered, pending);
            return;
        }

        if (TryGetKeyValuePairTypes(normalized, out var keyType, out var valueType))
        {
            CollectReferencedTypes(keyType, discovered, ordered, pending);
            CollectReferencedTypes(valueType, discovered, ordered, pending);
            return;
        }

        if (TryGetEitherCaseTypes(normalized, out var caseTypes))
        {
            EnqueueIfNamedType(normalized, discovered, ordered, pending);
            for (var i = 0; i < caseTypes.Length; i++)
                CollectReferencedTypes(caseTypes[i], discovered, ordered, pending);
            return;
        }

        if (TryGetJsonPolymorphicCases(normalized, out var polymorphicCases))
        {
            EnqueueIfNamedType(normalized, discovered, ordered, pending);
            for (var i = 0; i < polymorphicCases.Length; i++)
                CollectReferencedTypes(polymorphicCases[i].ClrType, discovered, ordered, pending);
            return;
        }

        EnqueueIfNamedType(normalized, discovered, ordered, pending);
    }

    static void EnqueueIfNamedType(
        Type clrType,
        HashSet<Type> discovered,
        List<Type> ordered,
        Queue<Type> pending
        )
    {
        if (!ShouldCreateNamedType(clrType))
            return;

        if (!discovered.Add(clrType))
            return;

        ordered.Add(clrType);
        pending.Enqueue(clrType);
    }

    static bool ShouldCreateNamedType(Type clrType)
    {
        var normalized = UnwrapNullable(clrType);
        if (IsScalarLike(normalized) || IsQuantityType(normalized))
            return false;

        if (TryMapJsonType(normalized, out _))
            return false;

        if (TryGetEitherCaseTypes(normalized, out _))
            return true;

        if (TryGetJsonPolymorphicCases(normalized, out _))
            return true;

        if (normalized.IsEnum)
            return true;

        if (IsJsonPolymorphicDerivedType(normalized))
            return true;

        return IsObjectShapeType(normalized);
    }

    static void EnsureSupportedRootType(Type clrType)
    {
        if (!IsObjectShapeType(clrType))
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType.Name}' cannot be registered as a root shape.");
        }
    }

    static bool IsObjectShapeType(Type clrType)
    {
        if (TryMapJsonType(UnwrapNullable(clrType), out _))
            return false;

        if (clrType == typeof(object))
            return false;

        if (clrType.IsPrimitive || clrType.IsPointer || clrType.IsByRef)
            return false;

        if (clrType == typeof(string) || clrType == typeof(byte[]))
            return false;

        return ShapeTypeInspector.GetReadableProperties(clrType).Length > 0;
    }

    TypeDefinition BuildNamedType(
        Type clrType,
        Dictionary<Type, ClrTypeIdentity> identities,
        Dictionary<PropertyInfo, FieldName> fieldNames,
        Dictionary<PropertyInfo, ClrShapeIdentityOrigin> fieldIdentityOrigins)
    {
        var identity = identities[clrType];
        var typeMetadata = GetMetadata(ClrShapeMetadataContext.ForType(clrType));
        AddContributedNamedTypes(typeMetadata.NamedTypes);

        if (clrType.IsEnum)
            return BuildEnumType(clrType, identity, typeMetadata);

        if (TryGetEitherCaseTypes(clrType, out var caseTypes))
            return BuildUnionType(clrType, caseTypes, identity, typeMetadata, identities);

        if (TryGetJsonPolymorphicCases(clrType, out var polymorphicCases))
            return BuildJsonPolymorphicUnionType(clrType, polymorphicCases, identity, typeMetadata, identities);

        var properties = ShapeTypeInspector.GetReadablePropertyMetadata(clrType);
        var fields = new StructuralField[properties.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            fields[i] = BuildStructuralField(
                properties[i],
                identities,
                fieldNames,
                fieldIdentityOrigins);
        }

        return new TypeDefinition.Structural(
            id: identity.TypeId,
            name: GetSimpleTypeName(clrType),
            fields: [.. fields],
            constraints: typeMetadata.Constraints,
            annotations: typeMetadata.Annotations);
    }

    static TypeDefinition.Union BuildUnionType(
        Type clrType,
        Type[] caseTypes,
        ClrTypeIdentity identity,
        ClrShapeMetadata typeMetadata,
        Dictionary<Type, ClrTypeIdentity> identities
        )
    {
        var cases = new UnionCase[caseTypes.Length];
        HashSet<string> caseNames = new(StringComparer.Ordinal);
        for (var i = 0; i < caseTypes.Length; i++)
        {
            var caseName = GetUnionCaseName(caseTypes[i], identities);
            if (!caseNames.Add(caseName))
                caseName = $"Case{i + 1}";

            cases[i] = new UnionCase(
                name: caseName,
                type: MapTypeRef(caseTypes[i], identities),
                discriminatorValue: caseName);
        }

        return new TypeDefinition.Union(
            id: identity.TypeId,
            name: GetSimpleTypeName(clrType),
            discriminator: new UnionDiscriminator("Type"),
            cases: [.. cases],
            annotations: typeMetadata.Annotations);
    }

    static TypeDefinition.Union BuildJsonPolymorphicUnionType(
        Type clrType,
        JsonPolymorphicCase[] cases,
        ClrTypeIdentity identity,
        ClrShapeMetadata typeMetadata,
        Dictionary<Type, ClrTypeIdentity> identities
        )
    {
        var unionCases = new UnionCase[cases.Length];
        HashSet<string> caseNames = new(StringComparer.Ordinal);
        var discriminatorType = PrimitiveType.String;

        for (var i = 0; i < cases.Length; i++)
        {
            var current = cases[i];
            var caseName = GetUnionCaseName(current.ClrType, identities);
            if (!caseNames.Add(caseName))
                caseName = $"Case{i + 1}";

            if (current.Discriminator is int or long)
                discriminatorType = PrimitiveType.Int64;

            unionCases[i] = new UnionCase(
                name: caseName,
                type: MapTypeRef(current.ClrType, identities),
                discriminatorValue: FormatDiscriminatorValue(current.Discriminator));
        }

        return new TypeDefinition.Union(
            id: identity.TypeId,
            name: GetSimpleTypeName(clrType),
            discriminator: new UnionDiscriminator(ResolveJsonPolymorphicDiscriminatorName(clrType), discriminatorType),
            cases: [.. unionCases],
            annotations: typeMetadata.Annotations);
    }

    Shape BuildShape(
        RootShapeRegistration root,
        Dictionary<Type, ClrTypeIdentity> identities,
        Dictionary<Type, ShapeId> shapeIds,
        Dictionary<PropertyInfo, FieldName> fieldNames,
        Dictionary<Type, ClrShapeIdentityOrigin> shapeIdentityOrigins,
        Dictionary<PropertyInfo, ClrShapeIdentityOrigin> fieldIdentityOrigins)
    {
        var identity = identities[root.ClrType];
        var shapeMetadata = GetMetadata(ClrShapeMetadataContext.ForShape(root.ClrType));
        AddContributedNamedTypes(shapeMetadata.NamedTypes);

        var properties = ShapeTypeInspector.GetReadablePropertyMetadata(root.ClrType);
        var fields = new FieldDefinition[properties.Length];
        for (var i = 0; i < properties.Length; i++)
        {
            fields[i] = BuildFieldDefinition(
                properties[i],
                identities,
                fieldNames,
                fieldIdentityOrigins);
        }

        var shapeId = shapeMetadata.ShapeId ?? identity.ShapeId;
        shapeIds.Add(root.ClrType, shapeId);
        shapeIdentityOrigins.Add(
            root.ClrType,
            shapeMetadata.ShapeId is null
                ? ClrShapeIdentityOrigin.Convention
                : ClrShapeIdentityOrigin.Metadata);

        return new Shape(
            id: shapeId,
            fields: [.. fields],
            constraints: shapeMetadata.Constraints,
            annotations: shapeMetadata.Annotations,
            role: shapeMetadata.ShapeRole ?? root.Role);
    }

    static TypeDefinition.Enum BuildEnumType(Type clrType, ClrTypeIdentity identity, ClrShapeMetadata typeMetadata)
    {
        var names = Enum.GetNames(clrType);
        var underlyingType = Enum.GetUnderlyingType(clrType);
        var values = new EnumValue[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var raw = Enum.Parse(clrType, names[i], ignoreCase: false);
            var converted = Convert.ChangeType(raw, underlyingType, System.Globalization.CultureInfo.InvariantCulture);
            var field = clrType.GetField(names[i], BindingFlags.Public | BindingFlags.Static);
            values[i] = new EnumValue(
                Name: names[i],
                Value: Convert.ToString(converted, System.Globalization.CultureInfo.InvariantCulture),
                Label: GetEnumValueLabel(field),
                Description: GetEnumValueDescription(field));
        }

        return new TypeDefinition.Enum(
            id: identity.TypeId,
            name: GetSimpleTypeName(clrType),
            underlying: MapEnumUnderlyingType(Enum.GetUnderlyingType(clrType)),
            values: [.. values],
            annotations: typeMetadata.Annotations);
    }

    static string? GetEnumValueLabel(FieldInfo? field)
    {
        if (field is null)
            return null;

        return NormalizeOptional(field.GetCustomAttribute<CodeAttribute>(inherit: false)?.Label)
               ?? NormalizeOptional(field.GetCustomAttribute<CodeSetAttribute>(inherit: false)?.Label);
    }

    static string? GetEnumValueDescription(FieldInfo? field)
    {
        if (field is null)
            return null;

        return NormalizeOptional(field.GetCustomAttribute<CodeAttribute>(inherit: false)?.Description)
               ?? NormalizeOptional(field.GetCustomAttribute<CodeSetAttribute>(inherit: false)?.Description)
               ?? NormalizeOptional(field.GetCustomAttribute<DescriptionAttribute>(inherit: false)?.Description);
    }

    StructuralField BuildStructuralField(
        ClrPropertyShapeMetadata propertyMetadata,
        Dictionary<Type, ClrTypeIdentity> identities,
        Dictionary<PropertyInfo, FieldName> fieldNames,
        Dictionary<PropertyInfo, ClrShapeIdentityOrigin> fieldIdentityOrigins)
    {
        var fieldMetadata = GetMetadata(ClrShapeMetadataContext.ForField(propertyMetadata.Property));
        AddContributedNamedTypes(fieldMetadata.NamedTypes);

        var isJsonType = TryMapJsonType(UnwrapNullable(propertyMetadata.PropertyType), out _);
        var elementType = typeof(void);
        var isMany = !isJsonType && TryGetEnumerableElementType(propertyMetadata.PropertyType, out elementType);
        var cardinality = isMany
            ? FieldCardinality.Many
            : FieldCardinality.Single;
        var effectiveType = isMany ? elementType : propertyMetadata.PropertyType;
        var fieldName = fieldMetadata.FieldName ?? new FieldName(propertyMetadata.Name);
        RegisterFieldIdentity(
            propertyMetadata.Property,
            fieldName,
            fieldMetadata.FieldName is null
                ? ClrShapeIdentityOrigin.Convention
                : ClrShapeIdentityOrigin.Metadata,
            fieldNames,
            fieldIdentityOrigins);

        return new(
            name: fieldName,
            type: fieldMetadata.TypeRef ?? MapTypeRef(effectiveType, identities),
            cardinality: cardinality,
            presence: propertyMetadata.IsOptional ? FieldPresence.Optional : FieldPresence.Required,
            nullability: propertyMetadata.IsOptional ? FieldNullability.Nullable : FieldNullability.NonNullable,
            constraints: fieldMetadata.Constraints,
            annotations: fieldMetadata.Annotations
            );
    }

    FieldDefinition BuildFieldDefinition(
        ClrPropertyShapeMetadata propertyMetadata,
        Dictionary<Type, ClrTypeIdentity> identities,
        Dictionary<PropertyInfo, FieldName> fieldNames,
        Dictionary<PropertyInfo, ClrShapeIdentityOrigin> fieldIdentityOrigins)
    {
        var fieldMetadata = GetMetadata(ClrShapeMetadataContext.ForField(propertyMetadata.Property));
        AddContributedNamedTypes(fieldMetadata.NamedTypes);

        var isJsonType = TryMapJsonType(UnwrapNullable(propertyMetadata.PropertyType), out _);
        var elementType = typeof(void);
        var isMany = !isJsonType && TryGetEnumerableElementType(propertyMetadata.PropertyType, out elementType);
        var cardinality = isMany
            ? FieldCardinality.Many
            : FieldCardinality.Single;
        var effectiveType = isMany ? elementType : propertyMetadata.PropertyType;
        var fieldName = fieldMetadata.FieldName ?? new FieldName(propertyMetadata.Name);
        RegisterFieldIdentity(
            propertyMetadata.Property,
            fieldName,
            fieldMetadata.FieldName is null
                ? ClrShapeIdentityOrigin.Convention
                : ClrShapeIdentityOrigin.Metadata,
            fieldNames,
            fieldIdentityOrigins);

        return new(
            name: fieldName,
            type: fieldMetadata.TypeRef ?? MapTypeRef(effectiveType, identities),
            cardinality: cardinality,
            presence: propertyMetadata.IsOptional ? FieldPresence.Optional : FieldPresence.Required,
            nullability: propertyMetadata.IsOptional ? FieldNullability.Nullable : FieldNullability.NonNullable,
            constraints: fieldMetadata.Constraints,
            annotations: fieldMetadata.Annotations
            );
    }

    static void RegisterFieldIdentity(
        PropertyInfo property,
        FieldName fieldName,
        ClrShapeIdentityOrigin origin,
        IDictionary<PropertyInfo, FieldName> fieldNames,
        IDictionary<PropertyInfo, ClrShapeIdentityOrigin> fieldIdentityOrigins)
    {
        if (!fieldNames.TryGetValue(property, out var existing))
        {
            fieldNames.Add(property, fieldName);
            fieldIdentityOrigins.Add(property, origin);
            return;
        }

        if (existing != fieldName)
        {
            throw new InvalidOperationException(
                $"CLR metadata providers produced inconsistent field names for '{property.DeclaringType?.Name}.{property.Name}'.");
        }

        if (fieldIdentityOrigins[property] != origin)
        {
            throw new InvalidOperationException(
                $"CLR metadata providers produced inconsistent field identity origins for '{property.DeclaringType?.Name}.{property.Name}'.");
        }
    }

    void AddContributedNamedTypes(ImmutableArray<TypeDefinition> namedTypes)
    {
        if (namedTypes.IsDefaultOrEmpty)
            return;

        foreach (var namedType in namedTypes)
        {
            if (!contributedNamedTypes.TryGetValue(namedType.Id, out var existing))
            {
                contributedNamedTypes.Add(namedType.Id, namedType);
                continue;
            }

            if (!EqualityComparer<TypeDefinition>.Default.Equals(existing, namedType))
            {
                throw new InvalidOperationException(
                    $"CLR metadata providers contributed conflicting named type definitions for '{namedType.Id.Value}'.");
            }
        }
    }

    static TypeRef MapTypeRef(Type clrType, Dictionary<Type, ClrTypeIdentity> identities) =>
        MapTypeRef(
            clrType,
            normalized => identities.TryGetValue(normalized, out var identity)
                ? identity.TypeId
                : null);

    internal static TypeRef ResolveTypeRef(Type clrType, IReadOnlyDictionary<Type, TypeId> typeIds)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        ArgumentNullException.ThrowIfNull(typeIds);
        return MapTypeRef(
            clrType,
            normalized => typeIds.TryGetValue(normalized, out var typeId)
                ? typeId
                : null);
    }

    static TypeRef MapTypeRef(Type clrType, Func<Type, TypeId?> resolveNamedType)
    {
        var normalized = UnwrapNullable(clrType);
        if (TryMapScalarType(normalized, out var scalar))
            return scalar;

        if (TryMapJsonType(normalized, out var json))
            return json;

        if (normalized.IsEnum)
        {
            var enumTypeId = resolveNamedType(normalized)
                ?? throw new InvalidOperationException(
                    $"CLR enum type '{normalized.Name}' is not present in the built shape graph.");
            return new NamedTypeRef(enumTypeId);
        }

        if (TryGetStructuredQuantity(normalized, out var quantity))
            return quantity;

        if (TryGetKeyValuePairTypes(normalized, out var keyType, out var valueType))
        {
            var fields = new ObjectFieldTypeDef[2];
            fields[0] = new ObjectFieldTypeDef(name: "Key", type: MapTypeRef(keyType, resolveNamedType));
            fields[1] = new ObjectFieldTypeDef(name: "Value", type: MapTypeRef(valueType, resolveNamedType));
            return new ObjectTypeRef([.. fields]);
        }

        if (TryGetEnumerableElementType(normalized, out var elementType))
            return new ArrayTypeRef(MapTypeRef(elementType, resolveNamedType));

        if (resolveNamedType(normalized) is { } typeId)
            return new NamedTypeRef(typeId);

        throw new InvalidOperationException($"CLR type '{normalized.Name}' is not supported for shape inference.");
    }

    Dictionary<Type, ClrTypeIdentity> BuildTypeIdentities(IReadOnlyList<Type> discoveredTypes)
    {
        var identities = new Dictionary<Type, ClrTypeIdentity>(discoveredTypes.Count);
        for (var i = 0; i < discoveredTypes.Count; i++)
        {
            var clrType = discoveredTypes[i];
            var typeMetadata = GetMetadata(ClrShapeMetadataContext.ForType(clrType));
            identities[clrType] = new(
                TypeId: typeMetadata.TypeId ?? ClrShapeIdentityConvention.GetTypeId(clrType),
                ShapeId: ClrShapeIdentityConvention.GetShapeId(clrType)
                );
        }

        return identities;
    }

    ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context)
    {
        var metadata = ClrShapeMetadata.Empty;
        for (var i = 0; i < metadataProviders.Count; i++)
            metadata = metadata.Merge(metadataProviders[i].GetMetadata(context));
        return metadata;
    }

    static string GetSimpleTypeName(Type clrType)
    {
        if (!clrType.IsGenericType)
            return clrType.Name;

        var typeName = clrType.Name;
        var tickIndex = typeName.IndexOf('`');
        if (tickIndex >= 0)
            typeName = typeName[..tickIndex];

        var arguments = clrType.GetGenericArguments();
        var builder = new System.Text.StringBuilder(typeName);
        builder.Append("Of");
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
                builder.Append("And");

            builder.Append(GetSimpleTypeName(arguments[i]));
        }

        return builder.ToString();
    }

    static string GetUnionCaseName(Type clrType, Dictionary<Type, ClrTypeIdentity> identities)
    {
        var normalized = UnwrapNullable(clrType);
        return SanitizeIdentifier(GetSimpleTypeName(normalized));
    }

    static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "_";

        Span<char> initial = stackalloc char[128];
        var builder = new ValueStringBuilder(initial);
        var needsPrefix = true;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (char.IsLetterOrDigit(current) || current == '_')
            {
                if (needsPrefix && char.IsDigit(current))
                    builder.Append('_');

                builder.Append(current);
                needsPrefix = false;
                continue;
            }

            if (builder.Length == 0 || builder.AsSpan()[builder.Length - 1] != '_')
                builder.Append('_');
        }

        var sanitized = builder.ToString();
        return sanitized.Length == 0 ? "_" : sanitized;
    }

    static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    static Type UnwrapNullable(Type clrType) => Nullable.GetUnderlyingType(clrType) ?? clrType;

    static bool TryGetEnumerableElementType(Type clrType, out Type elementType)
    {
        if (clrType == typeof(string) || clrType == typeof(byte[]))
        {
            elementType = typeof(void);
            return false;
        }

        if (clrType.IsArray)
        {
            elementType = clrType.GetElementType() ?? typeof(void);
            return elementType != typeof(void);
        }

        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = clrType.GetGenericArguments()[0];
            return true;
        }

        Type? selectedElementType = null;
        var interfaces = clrType.GetInterfaces();
        for (var i = 0; i < interfaces.Length; i++)
        {
            var @interface = interfaces[i];
            if (!@interface.IsGenericType || @interface.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            var candidate = @interface.GetGenericArguments()[0];
            if (selectedElementType is not null && selectedElementType != candidate)
            {
                throw new InvalidOperationException(
                    $"CLR type '{clrType}' implements IEnumerable<T> for multiple distinct element types "
                    + $"('{selectedElementType}' and '{candidate}'); shape inference requires one unambiguous collection contract.");
            }

            selectedElementType = candidate;
        }

        elementType = selectedElementType ?? typeof(void);
        return selectedElementType is not null;
    }

    static bool TryGetKeyValuePairTypes(Type clrType, out Type keyType, out Type valueType)
    {
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            var arguments = clrType.GetGenericArguments();
            keyType = arguments[0];
            valueType = arguments[1];
            return true;
        }

        keyType = typeof(void);
        valueType = typeof(void);
        return false;
    }

    static bool TryGetEitherCaseTypes(Type clrType, out Type[] caseTypes)
    {
        var normalized = UnwrapNullable(clrType);
        if (!normalized.IsGenericType)
        {
            caseTypes = [];
            return false;
        }

        var definition = normalized.GetGenericTypeDefinition();
        if (!string.Equals(definition.Namespace, "Cohesive.Prelude", StringComparison.Ordinal)
            || !definition.Name.StartsWith("Either`", StringComparison.Ordinal))
        {
            caseTypes = [];
            return false;
        }

        caseTypes = normalized.GetGenericArguments();
        return caseTypes.Length >= 2;
    }

    static bool TryGetJsonPolymorphicCases(Type clrType, out JsonPolymorphicCase[] cases)
    {
        var normalized = UnwrapNullable(clrType);
        var attributes = normalized.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).ToArray();
        if (attributes.Length == 0)
        {
            cases = [];
            return false;
        }

        cases = new JsonPolymorphicCase[attributes.Length];
        for (var i = 0; i < attributes.Length; i++)
        {
            var attribute = attributes[i];
            cases[i] = new(
                ClrType: attribute.DerivedType,
                Discriminator: attribute.TypeDiscriminator ?? attribute.DerivedType.Name);
        }

        return true;
    }

    static bool IsJsonPolymorphicDerivedType(Type clrType)
    {
        var normalized = UnwrapNullable(clrType);
        for (var current = normalized.BaseType; current is not null; current = current.BaseType)
        {
            var attributes = current.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false);
            foreach (var attribute in attributes)
            {
                if (attribute.DerivedType == normalized)
                    return true;
            }
        }

        return false;
    }

    static string ResolveJsonPolymorphicDiscriminatorName(Type clrType)
    {
        var attribute = UnwrapNullable(clrType).GetCustomAttribute<JsonPolymorphicAttribute>(inherit: false);
        return string.IsNullOrWhiteSpace(attribute?.TypeDiscriminatorPropertyName)
            ? "$type"
            : attribute.TypeDiscriminatorPropertyName;
    }

    static string FormatDiscriminatorValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    static bool TryMapScalarType(Type clrType, out TypeRef typeRef)
    {
        if (clrType == typeof(TimeOnly))
        {
            typeRef = new OpaqueRuntimeTypeRef("TimeOnly");
            return true;
        }

        if (DefaultClrTypeRefMapper.TryMapScalarTypeKind(clrType, out var kind))
        {
            typeRef = new ScalarTypeRef(kind);
            return true;
        }

        typeRef = default!;
        return false;
    }

    static bool IsScalarLike(Type clrType) => TryMapScalarType(clrType, out _);

    static bool TryMapJsonType(Type clrType, out TypeRef typeRef)
    {
        if (clrType == typeof(ObservationValue))
        {
            typeRef = new JsonTypeRef(JsonTypeKind.Any);
            return true;
        }

        if (clrType == typeof(JsonObject))
        {
            typeRef = new JsonTypeRef(JsonTypeKind.Object);
            return true;
        }

        if (clrType == typeof(JsonArray))
        {
            typeRef = new JsonTypeRef(JsonTypeKind.Array);
            return true;
        }

        if (clrType == typeof(JsonNode)
            || clrType == typeof(JsonValue)
            || clrType == typeof(JsonElement)
            || clrType == typeof(JsonDocument))
        {
            typeRef = new JsonTypeRef(JsonTypeKind.Any);
            return true;
        }

        typeRef = default!;
        return false;
    }

    static bool TryGetStructuredQuantity(Type clrType, out TypeRef typeRef)
    {
        var interfaces = clrType.GetInterfaces();
        for (var i = 0; i < interfaces.Length; i++)
        {
            var @interface = interfaces[i];
            if (!@interface.IsGenericType || @interface.GetGenericTypeDefinition() != typeof(IStructuredQuantity<,,>))
                continue;

            var arguments = @interface.GetGenericArguments();
            if (arguments[0] != clrType)
                continue;

            if (!TryMapScalarKind(arguments[2], out var baseKind))
            {
                throw new InvalidOperationException(
                    $"Quantity type '{clrType.Name}' uses unsupported representation '{arguments[2].Name}'.");
            }

            typeRef = new QuantityTypeRef(quantity: clrType.Name, baseKind: baseKind);
            return true;
        }

        typeRef = default!;
        return false;
    }

    static bool IsQuantityType(Type clrType) => TryGetStructuredQuantity(clrType, out _);

    static bool TryMapScalarKind(Type clrType, out ScalarTypeKind kind) =>
        DefaultClrTypeRefMapper.TryMapScalarTypeKind(clrType, out kind);

    static PrimitiveType MapEnumUnderlyingType(Type clrType)
    {
        clrType = UnwrapNullable(clrType);
        if (clrType == typeof(long) || clrType == typeof(ulong))
            return PrimitiveType.Int64;

        return PrimitiveType.Int32;
    }

    readonly record struct RootShapeRegistration(Type ClrType, string Role);

    readonly record struct JsonPolymorphicCase(Type ClrType, object? Discriminator);

    readonly record struct ClrTypeIdentity(TypeId TypeId, ShapeId ShapeId);
}
