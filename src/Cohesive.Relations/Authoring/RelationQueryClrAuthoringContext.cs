using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Identifies an immutable set of CLR shape metadata providers used by relation/query authoring.
/// </summary>
/// <remarks>
/// The profile identity is an explicit cache and provenance boundary. Providers should behave
/// deterministically for a given reflection context; changing provider behavior requires a new
/// profile version.
/// </remarks>
public sealed class RelationQueryClrMetadataProfile
{
    /// <summary>
    /// Gets the default v1 profile, which applies built-in shape attributes and the default
    /// System.Text.Json contract, including <c>JsonPropertyName</c> attributes.
    /// </summary>
    public static RelationQueryClrMetadataProfile Default { get; } = new(
        id: "cohesive.relations.clr/default",
        version: "v1",
        providers: [new SystemTextJsonClrShapeMetadataProvider(new JsonSerializerOptions())]);

    /// <summary>
    /// Creates a metadata profile.
    /// </summary>
    /// <param name="id">Stable profile identity.</param>
    /// <param name="version">Stable version of the provider behavior represented by this profile.</param>
    /// <param name="providers">
    /// Additional metadata providers, in precedence order. Later providers override earlier ones;
    /// the built-in CLR attribute provider always runs first.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> or <paramref name="version"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="version"/> is empty, or <paramref name="providers"/>
    /// contains a null entry.
    /// </exception>
    public RelationQueryClrMetadataProfile(
        string id,
        string version,
        IEnumerable<IClrShapeMetadataProvider>? providers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Id = id;
        Version = version;

        var selectedProviders = providers?.ToImmutableArray() ?? [];
        if (selectedProviders.Any(static provider => provider is null))
            throw new ArgumentException("A CLR metadata profile cannot contain a null provider.", nameof(providers));

        Providers = selectedProviders;
    }

    /// <summary>
    /// Gets the stable profile identity.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the stable profile version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the additional metadata providers in increasing precedence order.
    /// </summary>
    public ImmutableArray<IClrShapeMetadataProvider> Providers { get; }
}

/// <summary>
/// Identifies the effective authority that selected a CLR-backed shape or field identity.
/// </summary>
public enum RelationQueryClrIdentityOrigin
{
    /// <summary>
    /// The identity was selected by the deterministic built-in CLR convention.
    /// </summary>
    Convention = 0,

    /// <summary>
    /// The identity was selected by an attribute or configured metadata provider.
    /// </summary>
    Metadata = 1,

    /// <summary>
    /// The identity was supplied by an explicit authoring declaration or member-path override.
    /// </summary>
    Explicit = 2,

    /// <summary>
    /// The identity is authoritative because it belongs to an imported persisted shape document.
    /// </summary>
    Imported = 3
}

/// <summary>
/// Explains an effective CLR member path and the authority that selected each semantic path segment.
/// </summary>
public sealed class RelationQueryClrMemberPathResolution
{
    /// <summary>
    /// Creates a member-path resolution.
    /// </summary>
    /// <param name="path">Effective non-empty canonical field path.</param>
    /// <param name="segmentOrigins">
    /// Origin of each segment in <paramref name="path"/>, in matching order.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is default, the origin count does not match the path segment count,
    /// or an origin is not a defined <see cref="RelationQueryClrIdentityOrigin"/> value.
    /// </exception>
    public RelationQueryClrMemberPathResolution(
        FieldPath path,
        ImmutableArray<RelationQueryClrIdentityOrigin> segmentOrigins)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A resolved CLR member path cannot be empty.", nameof(path));

        var normalizedOrigins = segmentOrigins.IsDefault ? [] : segmentOrigins;
        if (normalizedOrigins.Length != path.Segments.Length)
        {
            throw new ArgumentException(
                "A resolved CLR member path requires one identity origin per semantic path segment.",
                nameof(segmentOrigins));
        }
        foreach (var origin in normalizedOrigins)
        {
            if (!Enum.IsDefined(origin))
            {
                throw new ArgumentException(
                    $"Unsupported CLR identity origin '{origin}'.",
                    nameof(segmentOrigins));
            }
        }

        Path = path;
        SegmentOrigins = normalizedOrigins;
    }

    /// <summary>
    /// Gets the effective canonical field path.
    /// </summary>
    public FieldPath Path { get; }

    /// <summary>
    /// Gets the effective identity origin for every semantic path segment.
    /// </summary>
    public ImmutableArray<RelationQueryClrIdentityOrigin> SegmentOrigins { get; }
}

/// <summary>
/// Owns deterministic CLR shape registration, effective member metadata, and shape-graph snapshots
/// for one relation/query authoring session.
/// </summary>
/// <remarks>
/// The context is thread-safe. Its caches are instance-owned so profile-specific reflection metadata
/// cannot leak between authoring sessions. Convention-authored shapes use the assembly-scoped v1 graph
/// identity from <see cref="ClrRelationshipShapeConvention"/> for the default profile; non-default
/// profile identities and versions further qualify that graph identity so different field semantics cannot
/// claim the same graph. Explicit qualified identifiers and imported documents are authoritative when supplied.
/// </remarks>
public sealed class RelationQueryClrAuthoringContext
{
    const int ConventionPrecedence = 0;
    const int ExplicitPrecedence = 1;
    const int ImportedPrecedence = 2;

    readonly object gate = new();
    readonly Dictionary<GraphId, InferredGraphState> inferredGraphs = [];
    readonly Dictionary<GraphId, ImportedGraphState> importedGraphs = [];
    readonly Dictionary<RegistrationKey, ShapeRegistration> registrations = [];
    readonly ClrShapeGraphBuildResult scalarTypeResolver = new ClrShapeGraphBuilder()
        .BuildResult(new GraphId("cohesive.relations.clr/type-resolver/v1"));

    /// <summary>
    /// Creates a CLR authoring context.
    /// </summary>
    /// <param name="profile">
    /// Metadata profile used for inferred identities and field names, or <see langword="null"/> to use
    /// <see cref="RelationQueryClrMetadataProfile.Default"/>.
    /// </param>
    public RelationQueryClrAuthoringContext(RelationQueryClrMetadataProfile? profile = null)
    {
        Profile = profile ?? RelationQueryClrMetadataProfile.Default;
    }

    /// <summary>
    /// Gets the metadata profile isolated by this context.
    /// </summary>
    public RelationQueryClrMetadataProfile Profile { get; }

    /// <summary>
    /// Registers a CLR root using deterministic assembly-scoped graph identity and profile-derived shape identity.
    /// </summary>
    /// <typeparam name="T">CLR type represented by the root shape.</typeparam>
    /// <param name="role">Semantic role assigned when metadata does not override it.</param>
    /// <returns>An immutable typed handle for the registered shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="role"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="role"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// The type cannot be inferred as a root shape, or its graph already has an incompatible registration.
    /// </exception>
    public RelationQueryClrShape<T> Shape<T>(string role = ShapeRoles.ValueObject) where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var convention = GetConventionShapeId(typeof(T));
        lock (gate)
        {
            var normalized = UnwrapNullable(typeof(T));
            var preferred = registrations.Values
                .Where(registration => registration.ClrType == normalized)
                .OrderByDescending(static registration => registration.Precedence)
                .ThenBy(static registration => registration.Id.ToString(), StringComparer.Ordinal)
                .FirstOrDefault();
            if (preferred is not null)
            {
                RequireCompatibleInferredRole(preferred, normalized, role);
                return new(this, preferred);
            }

            var registration = RegisterInferred(
                normalized,
                convention.GraphId,
                explicitShapeId: null,
                role,
                ConventionPrecedence);
            return new(this, registration);
        }
    }

    /// <summary>
    /// Registers a CLR root using an explicit authoritative graph-qualified shape identity.
    /// </summary>
    /// <typeparam name="T">CLR type represented by the root shape.</typeparam>
    /// <param name="id">Authoritative graph and shape identity.</param>
    /// <param name="role">Semantic role assigned when metadata does not override it.</param>
    /// <returns>An immutable typed handle for the registered shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="role"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default, <paramref name="role"/> is empty, or the type already has an
    /// incompatible registration in the same graph.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The type cannot be inferred as a root shape, or the graph identity is already owned by an imported document.
    /// </exception>
    public RelationQueryClrShape<T> Shape<T>(
        QualifiedShapeId id,
        string role = ShapeRoles.ValueObject)
        where T : notnull
    {
        RequireQualifiedShapeId(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        lock (gate)
        {
            var key = new RegistrationKey(UnwrapNullable(typeof(T)), id);
            if (registrations.TryGetValue(key, out var existing))
            {
                RequireCompatibleInferredRole(existing, key.ClrType, role);
                return new(this, existing);
            }

            var registration = RegisterInferred(
                typeof(T),
                id.GraphId,
                id.ShapeId,
                role,
                ExplicitPrecedence);
            return new(this, registration);
        }
    }

    static void RequireCompatibleInferredRole(
        ShapeRegistration registration,
        Type clrType,
        string role)
    {
        if (registration.Inferred is not { } graph
            || !graph.Roots.TryGetValue(clrType, out var root)
            || string.Equals(root.Role, role, StringComparison.Ordinal))
        {
            return;
        }

        throw new ArgumentException(
            $"CLR type '{clrType}' is already registered with semantic role '{root.Role}', which is incompatible "
            + $"with requested role '{role}'.",
            nameof(role));
    }

    /// <summary>
    /// Registers a CLR type against an imported authoritative shape-graph document.
    /// </summary>
    /// <typeparam name="T">CLR type represented by the imported root shape.</typeparam>
    /// <param name="document">Persisted shape-graph document that remains the source of truth.</param>
    /// <param name="id">Qualified identity of the shape within <paramref name="document"/>.</param>
    /// <param name="overrides">
    /// Optional CLR-property path overrides. Overrides take precedence over profile metadata, and
    /// multi-segment paths compose when several properties form a nested member chain.
    /// </param>
    /// <returns>An immutable typed handle for the imported shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> does not belong to <paramref name="document"/>, the shape is absent, an
    /// override key is null, an override path is empty, an override property is unreachable from
    /// <typeparamref name="T"/>, or the graph/type is already registered incompatibly.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The CLR type cannot be inspected for fallback field metadata, or the graph identity is already
    /// owned by inferred shapes.
    /// </exception>
    public RelationQueryClrShape<T> Shape<T>(
        ShapeGraphDocument document,
        QualifiedShapeId id,
        IReadOnlyDictionary<PropertyInfo, FieldPath>? overrides = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(document);
        RequireQualifiedShapeId(id, nameof(id));
        if (document.Graph.Id != id.GraphId)
        {
            throw new ArgumentException(
                $"Imported graph '{document.Graph.Id.Value}' does not own qualified shape '{id}'.",
                nameof(id));
        }
        if (document.Graph.TryGetShape(id) is null)
            throw new ArgumentException($"Imported graph does not contain shape '{id.ShapeId.Value}'.", nameof(id));
        if (document.Graph.HasErrors)
        {
            throw new ArgumentException(
                $"Imported graph '{document.Graph.Id.Value}' contains semantic errors.",
                nameof(document));
        }

        var selectedOverrides = NormalizeOverrides(overrides);
        lock (gate)
        {
            var registration = RegisterImported(typeof(T), document, id, selectedOverrides);
            return new(this, registration);
        }
    }

    /// <summary>
    /// Resolves an arbitrary supported CLR type to its portable semantic type reference.
    /// </summary>
    /// <param name="clrType">
    /// CLR type to resolve. Named types and enums must be reachable from at least one registered root.
    /// </param>
    /// <returns>The profile-consistent portable semantic type reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The type is unsupported, is not reachable from a registered root, or registrations select
    /// conflicting named-type identities.
    /// </exception>
    public TypeRef GetTypeRef(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        lock (gate)
            return GetTypeRefCore(clrType);
    }

    /// <summary>
    /// Resolves a CLR property chain using the context's authoritative registration for its root type.
    /// </summary>
    /// <param name="rootType">CLR type from which the member chain starts.</param>
    /// <param name="members">Ordered properties from the root toward the terminal value.</param>
    /// <returns>The effective semantic field path, with explicit imported overrides applied first.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rootType"/> or <paramref name="members"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="members"/> is empty or is not a valid chain rooted at <paramref name="rootType"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="rootType"/> has not been registered, or a member has no inferred or overridden path.
    /// </exception>
    public FieldPath ResolveMemberPath(Type rootType, IReadOnlyList<PropertyInfo> members) =>
        ResolveMemberPathWithProvenance(rootType, members).Path;

    /// <summary>
    /// Resolves a CLR property chain and explains the authority that selected each semantic segment.
    /// </summary>
    /// <param name="rootType">CLR type from which the member chain starts.</param>
    /// <param name="members">Ordered properties from the root toward the terminal value.</param>
    /// <returns>The effective semantic path and one identity origin per path segment.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rootType"/> or <paramref name="members"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="members"/> is empty or is not a valid chain rooted at <paramref name="rootType"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="rootType"/> has not been registered, a member has no effective path, or an
    /// imported document is definitely incompatible with the resolved path or CLR value type.
    /// </exception>
    public RelationQueryClrMemberPathResolution ResolveMemberPathWithProvenance(
        Type rootType,
        IReadOnlyList<PropertyInfo> members)
    {
        ArgumentNullException.ThrowIfNull(rootType);
        ArgumentNullException.ThrowIfNull(members);
        lock (gate)
        {
            var normalized = UnwrapNullable(rootType);
            List<(ShapeRegistration Registration, RelationQueryClrMemberPathResolution Resolution)> candidates = [];
            foreach (var registration in registrations.Values)
            {
                var mapping = GetMapping(registration);
                if (!mapping.TypeIds.ContainsKey(normalized))
                    continue;

                candidates.Add((
                    registration,
                    ResolveMemberPathWithProvenance(registration, normalized, members)));
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"CLR type '{normalized}' is not reachable from a registered shape in this relation/query authoring context.");
            }

            var selectedPrecedence = candidates.Max(static candidate => candidate.Registration.Precedence);
            var selected = candidates
                .Where(candidate => candidate.Registration.Precedence == selectedPrecedence)
                .OrderBy(static candidate => candidate.Registration.Id.ToString(), StringComparer.Ordinal)
                .ToArray();
            var resolution = selected[0].Resolution;
            for (var index = 1; index < selected.Length; index++)
            {
                if (selected[index].Resolution.Path != resolution.Path)
                {
                    throw new InvalidOperationException(
                        $"CLR type '{normalized}' resolves to conflicting member paths in this authoring context.");
                }
            }

            return resolution;
        }
    }

    /// <summary>
    /// Gets the authoritative document containing a registered qualified shape.
    /// </summary>
    /// <param name="shape">Qualified shape whose document is requested.</param>
    /// <returns>The imported document or deterministic inferred document containing <paramref name="shape"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="shape"/> is default.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="shape"/> is not registered in this context.</exception>
    public ShapeGraphDocument GetShapeDocument(QualifiedShapeId shape)
    {
        RequireQualifiedShapeId(shape, nameof(shape));
        lock (gate)
        {
            if (importedGraphs.TryGetValue(shape.GraphId, out var imported)
                && imported.Document.Graph.TryGetShape(shape) is not null)
            {
                return imported.Document;
            }

            if (inferredGraphs.TryGetValue(shape.GraphId, out var inferred))
            {
                var result = Build(inferred);
                if (result.Graph.TryGetShape(shape) is not null)
                    return ShapeGraphDocument.FromGraph(result.Graph);
            }

            throw new KeyNotFoundException($"No registered shape document contains '{shape}'.");
        }
    }

    /// <summary>
    /// Gets deterministic graph documents for all registered CLR shapes, ordered by graph identity.
    /// </summary>
    public ImmutableArray<ShapeGraphDocument> ShapeDocuments
    {
        get
        {
            lock (gate)
            {
                var documents = new List<ShapeGraphDocument>(inferredGraphs.Count + importedGraphs.Count);
                foreach (var graph in inferredGraphs.Values)
                    documents.Add(ShapeGraphDocument.FromGraph(Build(graph).Graph));
                foreach (var graph in importedGraphs.Values)
                    documents.Add(graph.Document);

                return [.. documents.OrderBy(static document => document.Graph.Id.Value, StringComparer.Ordinal)];
            }
        }
    }

    ShapeRegistration RegisterInferred(
        Type clrType,
        GraphId graphId,
        ShapeId? explicitShapeId,
        string role,
        int precedence)
    {
        var normalized = UnwrapNullable(clrType);
        if (importedGraphs.ContainsKey(graphId))
        {
            throw new InvalidOperationException(
                $"Graph identity '{graphId.Value}' is already owned by an imported shape document.");
        }

        var addedGraph = false;
        if (!inferredGraphs.TryGetValue(graphId, out var graph))
        {
            graph = new(graphId);
            inferredGraphs.Add(graphId, graph);
            addedGraph = true;
        }

        if (graph.Roots.TryGetValue(normalized, out var existingRoot))
        {
            if (!string.Equals(existingRoot.Role, role, StringComparison.Ordinal)
                || existingRoot.ExplicitShapeId != explicitShapeId)
            {
                throw new ArgumentException(
                    $"CLR type '{normalized}' already has an incompatible registration in graph '{graphId.Value}'.",
                    nameof(clrType));
            }

            return registrations[new(normalized, existingRoot.QualifiedId)];
        }

        var root = new InferredRoot(normalized, role, explicitShapeId);
        graph.Roots.Add(normalized, root);
        graph.Invalidate();
        try
        {
            var result = Build(graph);
            if (result.Graph.HasErrors)
            {
                throw new InvalidOperationException(
                    $"CLR shape inference for graph '{graphId.Value}' produced errors: "
                    + string.Join("; ", result.Graph.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            }

            var effectiveShapeId = result.ShapeIds[normalized];
            if (explicitShapeId is { } expected && effectiveShapeId != expected)
            {
                throw new InvalidOperationException(
                    $"Explicit shape id '{expected.Value}' was not preserved for CLR type '{normalized}'.");
            }

            root.QualifiedId = new(graphId, effectiveShapeId);
            if (registrations.Values.Any(existing => existing.Id == root.QualifiedId))
            {
                throw new InvalidOperationException(
                    $"Shape identity '{root.QualifiedId}' is already registered for another CLR type.");
            }

            var registration = new ShapeRegistration(
                normalized,
                root.QualifiedId,
                precedence,
                precedence == ExplicitPrecedence
                    ? RelationQueryClrIdentityOrigin.Explicit
                    : ResolveInferredShapeOrigin(normalized, graphId, result),
                graph,
                imported: null,
                ImmutableDictionary<PropertyInfo, FieldPath>.Empty);
            registrations.Add(new(normalized, root.QualifiedId), registration);
            return registration;
        }
        catch
        {
            graph.Roots.Remove(normalized);
            graph.Invalidate();
            if (addedGraph)
                inferredGraphs.Remove(graphId);
            throw;
        }
    }

    ShapeRegistration RegisterImported(
        Type clrType,
        ShapeGraphDocument document,
        QualifiedShapeId id,
        ImmutableDictionary<PropertyInfo, FieldPath> overrides)
    {
        var normalized = UnwrapNullable(clrType);
        if (inferredGraphs.ContainsKey(id.GraphId))
        {
            throw new InvalidOperationException(
                $"Graph identity '{id.GraphId.Value}' is already owned by inferred CLR shapes.");
        }

        var addedGraph = false;
        if (!importedGraphs.TryGetValue(id.GraphId, out var importedGraph))
        {
            importedGraph = new(document);
            importedGraphs.Add(id.GraphId, importedGraph);
            addedGraph = true;
        }
        else if (!ReferenceEquals(importedGraph.Document, document))
        {
            throw new ArgumentException(
                $"Graph identity '{id.GraphId.Value}' is already associated with another imported document.",
                nameof(document));
        }

        var key = new RegistrationKey(normalized, id);
        if (registrations.TryGetValue(key, out var existing))
        {
            if (!AreOverridesEqual(existing.Overrides, overrides))
            {
                throw new ArgumentException(
                    $"CLR type '{normalized}' and shape '{id}' are already registered with different member overrides.",
                    nameof(overrides));
            }

            return existing;
        }

        if (registrations.Values.Any(existing => existing.Id == id))
        {
            throw new ArgumentException(
                $"Shape identity '{id}' is already registered for another CLR type.",
                nameof(id));
        }

        try
        {
            var mappingBuilder = CreateBuilder();
            mappingBuilder.AddShape(normalized);
            mappingBuilder.AddMetadataProvider(new ExplicitShapeIdMetadataProvider(
                new Dictionary<Type, ShapeId> { [normalized] = id.ShapeId }));
            var mapping = mappingBuilder.BuildResult(id.GraphId);
            if (mapping.Graph.HasErrors)
            {
                throw new InvalidOperationException(
                    $"CLR metadata mapping for imported shape '{id}' produced errors: "
                    + string.Join("; ", mapping.Graph.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            }
            foreach (var property in overrides.Keys)
            {
                if (!ContainsProperty(mapping.FieldNames, property))
                {
                    throw new ArgumentException(
                        $"Override property '{property.DeclaringType?.FullName}.{property.Name}' is not reachable from CLR type '{normalized}'.",
                        nameof(overrides));
                }
            }

            var imported = new ImportedRegistration(document, mapping);
            var registration = new ShapeRegistration(
                normalized,
                id,
                ImportedPrecedence,
                RelationQueryClrIdentityOrigin.Imported,
                inferred: null,
                imported,
                overrides);
            ValidateImportedRootShape(registration);
            registrations.Add(key, registration);
            importedGraph.Registrations.Add(registration);
            return registration;
        }
        catch
        {
            if (addedGraph)
                importedGraphs.Remove(id.GraphId);
            throw;
        }
    }

    TypeRef GetTypeRefCore(Type clrType)
    {
        try
        {
            return scalarTypeResolver.GetTypeRef(clrType);
        }
        catch (InvalidOperationException)
        {
            // Named CLR types require one of the profile-specific registered builds below.
        }

        List<TypeRef> candidates = [];
        foreach (var graph in inferredGraphs.Values)
        {
            var result = Build(graph);
            TryAddTypeRef(result, clrType, candidates);
        }
        foreach (var graph in importedGraphs.Values)
        {
            foreach (var registration in graph.Registrations)
                TryAddImportedTypeRef(registration, clrType, candidates);
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType}' is unsupported or is not reachable from a registered CLR shape.");
        }

        var selected = candidates[0];
        for (var index = 1; index < candidates.Count; index++)
        {
            if (!Equals(selected, candidates[index]))
            {
                throw new InvalidOperationException(
                    $"CLR type '{clrType}' resolves to conflicting semantic types in this authoring context.");
            }
        }

        return selected;
    }

    static void TryAddTypeRef(ClrShapeGraphBuildResult result, Type clrType, ICollection<TypeRef> candidates)
    {
        try
        {
            candidates.Add(result.GetTypeRef(clrType));
        }
        catch (InvalidOperationException)
        {
            // This graph did not discover every named CLR type required by the requested type.
        }
    }

    static void TryAddImportedTypeRef(
        ShapeRegistration registration,
        Type clrType,
        ICollection<TypeRef> candidates)
    {
        var imported = registration.Imported!;
        TypeRef candidate;
        try
        {
            candidate = imported.Mapping.GetTypeRef(clrType);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (ReferencesOnlyImportedTypes(candidate, imported.Document.Graph))
        {
            candidates.Add(candidate);
            return;
        }

        if (UnwrapNullable(clrType) == registration.ClrType)
            candidates.Add(GetRegisteredTypeRefCore(registration));
    }

    static bool ReferencesOnlyImportedTypes(TypeRef type, ShapeGraph graph) => type switch
    {
        NamedTypeRef named => graph.TryGetType(named.TypeId, out _),
        ArrayTypeRef array => ReferencesOnlyImportedTypes(array.ElementType, graph),
        ObjectTypeRef objectType => objectType.Fields.All(field => ReferencesOnlyImportedTypes(field.Type, graph)),
        _ => true
    };

    RelationQueryClrMemberPathResolution ResolveMemberPathWithProvenance(
        ShapeRegistration registration,
        Type rootType,
        IReadOnlyList<PropertyInfo> members)
    {
        if (members.Count == 0)
            throw new ArgumentException("A member path requires at least one CLR property.", nameof(members));

        var mapping = GetMapping(registration);
        var segments = ImmutableArray.CreateBuilder<FieldPathSegment>();
        var origins = ImmutableArray.CreateBuilder<RelationQueryClrIdentityOrigin>();
        var inferredOrigins = ImmutableArray.CreateBuilder<RelationQueryClrIdentityOrigin>();
        var currentType = UnwrapNullable(rootType);
        for (var index = 0; index < members.Count; index++)
        {
            var property = members[index]
                ?? throw new ArgumentException("A member path cannot contain a null CLR property.", nameof(members));
            var declaringType = property.DeclaringType;
            if (declaringType is null || !declaringType.IsAssignableFrom(currentType))
            {
                throw new ArgumentException(
                    $"Property '{property.Name}' is not reachable from CLR type '{currentType}'.",
                    nameof(members));
            }

            if (TryGetOverride(registration.Overrides, property, out var memberOverride))
            {
                segments.AddRange(memberOverride.Segments);
                for (var segmentIndex = 0; segmentIndex < memberOverride.Segments.Length; segmentIndex++)
                {
                    origins.Add(RelationQueryClrIdentityOrigin.Explicit);
                    inferredOrigins.Add(RelationQueryClrIdentityOrigin.Explicit);
                }
            }
            else
            {
                var single = mapping.ResolveMemberPath(currentType, [property]);
                segments.AddRange(single.Segments);
                var inferredOrigin = ResolveInferredFieldOrigin(mapping, property);
                var origin = registration.Imported is not null
                    ? RelationQueryClrIdentityOrigin.Imported
                    : inferredOrigin;
                for (var segmentIndex = 0; segmentIndex < single.Segments.Length; segmentIndex++)
                {
                    origins.Add(origin);
                    inferredOrigins.Add(inferredOrigin);
                }
            }

            currentType = UnwrapNullable(property.PropertyType);
        }

        var path = new FieldPath(segments.ToImmutable());
        if (ValidateImportedMemberPath(registration, rootType, members, path) is { } openIndex)
        {
            for (var index = openIndex; index < origins.Count; index++)
            {
                if (origins[index] == RelationQueryClrIdentityOrigin.Imported)
                    origins[index] = inferredOrigins[index];
            }
        }
        return new(path, origins.ToImmutable());
    }

    RelationQueryClrIdentityOrigin ResolveInferredShapeOrigin(
        Type clrType,
        GraphId graphId,
        ClrShapeGraphBuildResult result)
    {
        var conventionGraphId = ClrRelationshipShapeConvention.GetQualifiedShapeId(clrType).GraphId;
        if (graphId != conventionGraphId)
            return RelationQueryClrIdentityOrigin.Metadata;

        if (!result.ShapeIdentityOrigins.TryGetValue(clrType, out var origin))
        {
            throw new InvalidOperationException(
                $"CLR shape inference did not retain an identity origin for '{clrType}'.");
        }

        return origin switch
        {
            ClrShapeIdentityOrigin.Convention => RelationQueryClrIdentityOrigin.Convention,
            ClrShapeIdentityOrigin.Metadata => RelationQueryClrIdentityOrigin.Metadata,
            _ => throw new InvalidOperationException($"Unsupported CLR shape identity origin '{origin}'.")
        };
    }

    static RelationQueryClrIdentityOrigin ResolveInferredFieldOrigin(
        ClrShapeGraphBuildResult mapping,
        PropertyInfo property)
    {
        if (!TryGetPropertyValue(mapping.FieldIdentityOrigins, property, out var origin))
        {
            throw new InvalidOperationException(
                $"CLR shape inference did not retain a field identity origin for "
                + $"'{property.DeclaringType?.FullName}.{property.Name}'.");
        }

        return origin switch
        {
            ClrShapeIdentityOrigin.Convention => RelationQueryClrIdentityOrigin.Convention,
            ClrShapeIdentityOrigin.Metadata => RelationQueryClrIdentityOrigin.Metadata,
            _ => throw new InvalidOperationException($"Unsupported CLR field identity origin '{origin}'.")
        };
    }

    void ValidateImportedRootShape(ShapeRegistration registration)
    {
        HashSet<Type> validatedTypes = [];
        ValidateImportedClrType(registration, registration.ClrType, validatedTypes);
    }

    void ValidateImportedClrType(
        ShapeRegistration registration,
        Type clrType,
        ISet<Type> validatedTypes)
    {
        var normalized = UnwrapNullable(clrType);
        if (!validatedTypes.Add(normalized))
            return;

        foreach (var property in ShapeTypeInspector.GetReadableProperties(normalized))
        {
            _ = ResolveMemberPathWithProvenance(registration, normalized, [property]);
            if (TryGetReachableStructuralClrType(registration, property.PropertyType, out var structuralType))
                ValidateImportedClrType(registration, structuralType, validatedTypes);
        }
    }

    int? ValidateImportedMemberPath(
        ShapeRegistration registration,
        Type rootType,
        IReadOnlyList<PropertyInfo> members,
        FieldPath path)
    {
        if (registration.Imported is not { } imported)
            return null;

        var graph = imported.Document.Graph;
        Shape? rootShape = null;
        TypeRef? rootSemanticType = null;
        if (UnwrapNullable(rootType) == registration.ClrType)
        {
            rootShape = graph.GetShape(registration.Id);
        }
        else
        {
            TypeRef inferredRootType;
            try
            {
                inferredRootType = imported.Mapping.GetTypeRef(rootType);
            }
            catch (InvalidOperationException)
            {
                return null;
            }

            if (inferredRootType is not NamedTypeRef named || !graph.TryGetType(named.TypeId, out _))
                return null;

            rootSemanticType = inferredRootType;
        }

        var resolution = ResolveImportedPath(graph, rootShape, rootSemanticType, path);
        if (resolution.Error is not null)
        {
            throw new InvalidOperationException(
                $"Imported shape '{registration.Id}' is incompatible with CLR member path '{path}': "
                + resolution.Error
                + " Supply a member-path override or import a compatible shape document.");
        }
        if (resolution.OpenSegmentIndex is { } openSegmentIndex)
            return openSegmentIndex;

        var expectedPath = imported.Mapping.ResolveMemberPath(rootType, members);
        var expectedRootShape = UnwrapNullable(rootType) == registration.ClrType
            ? imported.Mapping.Graph.GetShape(registration.Id)
            : null;
        var expectedRootType = expectedRootShape is null
            ? imported.Mapping.GetTypeRef(rootType)
            : null;
        var expectedResolution = ResolveImportedPath(
            imported.Mapping.Graph,
            expectedRootShape,
            expectedRootType,
            expectedPath);
        if (expectedResolution.Error is not null || expectedResolution.OpenSegmentIndex is not null)
        {
            throw new InvalidOperationException(
                $"CLR metadata mapping could not establish the contract for member path '{expectedPath}': "
                + (expectedResolution.Error ?? "the inferred path unexpectedly became open."));
        }

        var actualType = resolution.Cardinality == FieldCardinality.Many
            ? new ArrayTypeRef(resolution.Type!)
            : resolution.Type!;
        var expectedType = expectedResolution.Cardinality == FieldCardinality.Many
            ? new ArrayTypeRef(expectedResolution.Type!)
            : expectedResolution.Type!;
        var compatibleType = HasReachableOverride(registration, members[^1].PropertyType)
            && AreSameStructuralContainer(
                expectedType,
                imported.Mapping.Graph,
                actualType,
                imported.Document.Graph)
            || AreDefinitelyCompatible(
                expectedType,
                imported.Mapping.Graph,
                actualType,
                imported.Document.Graph);
        if (!compatibleType
            || expectedResolution.Presence == FieldPresence.Required
            && resolution.Presence != FieldPresence.Required
            || expectedResolution.Nullability == FieldNullability.NonNullable
            && resolution.Nullability != FieldNullability.NonNullable)
        {
            throw new InvalidOperationException(
                $"Imported shape '{registration.Id}' resolves CLR member path '{path}' to "
                + $"'{actualType}' ({resolution.Presence}, {resolution.Nullability}), which is incompatible "
                + $"with the CLR contract '{expectedType}' ({expectedResolution.Presence}, "
                + $"{expectedResolution.Nullability}). Supply a member-path override or import a compatible shape document.");
        }

        return null;
    }

    bool HasReachableOverride(ShapeRegistration registration, Type clrType)
    {
        HashSet<Type> visited = [];
        return HasReachableOverride(registration, clrType, visited);
    }

    bool HasReachableOverride(
        ShapeRegistration registration,
        Type clrType,
        ISet<Type> visited)
    {
        if (!TryGetReachableStructuralClrType(registration, clrType, out var structuralType)
            || !visited.Add(structuralType))
        {
            return false;
        }

        foreach (var property in ShapeTypeInspector.GetReadableProperties(structuralType))
        {
            if (TryGetOverride(registration.Overrides, property, out _)
                || HasReachableOverride(registration, property.PropertyType, visited))
            {
                return true;
            }
        }

        return false;
    }

    static bool TryGetReachableStructuralClrType(
        ShapeRegistration registration,
        Type clrType,
        out Type structuralType)
    {
        var candidate = UnwrapNullable(clrType);
        if (candidate.IsArray)
        {
            candidate = candidate.GetElementType()
                ?? throw new InvalidOperationException($"Array CLR type '{candidate}' has no element type.");
        }
        else if (candidate != typeof(string))
        {
            var elementTypes = candidate.GetInterfaces()
                .Append(candidate)
                .Where(static type => type.IsGenericType
                    && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .Select(static type => UnwrapNullable(type.GetGenericArguments()[0]))
                .Distinct()
                .ToArray();
            if (elementTypes.Length == 1)
                candidate = elementTypes[0];
        }

        TypeRef semanticType;
        try
        {
            semanticType = registration.Imported!.Mapping.GetTypeRef(candidate);
        }
        catch (InvalidOperationException)
        {
            structuralType = null!;
            return false;
        }

        if (semanticType is NamedTypeRef named
            && registration.Imported.Mapping.Graph.TryGetType(named.TypeId, out var definition)
            && definition is TypeDefinition.Structural)
        {
            structuralType = candidate;
            return true;
        }

        structuralType = null!;
        return false;
    }

    static bool AreSameStructuralContainer(
        TypeRef expected,
        ShapeGraph expectedGraph,
        TypeRef actual,
        ShapeGraph actualGraph)
    {
        if (expected is ArrayTypeRef expectedArray && actual is ArrayTypeRef actualArray)
        {
            return AreSameStructuralContainer(
                expectedArray.ElementType,
                expectedGraph,
                actualArray.ElementType,
                actualGraph);
        }

        return expected is NamedTypeRef expectedNamed
            && actual is NamedTypeRef actualNamed
            && expectedNamed.TypeId == actualNamed.TypeId
            && expectedGraph.TryGetType(expectedNamed.TypeId, out var expectedDefinition)
            && expectedDefinition is TypeDefinition.Structural
            && actualGraph.TryGetType(actualNamed.TypeId, out var actualDefinition)
            && actualDefinition is TypeDefinition.Structural;
    }

    static ImportedPathResolution ResolveImportedPath(
        ShapeGraph graph,
        Shape? rootShape,
        TypeRef? rootType,
        FieldPath path)
    {
        var currentShape = rootShape;
        var currentType = rootType;
        var currentCardinality = FieldCardinality.Single;
        var currentPresence = FieldPresence.Required;
        var currentNullability = FieldNullability.NonNullable;
        for (var index = 0; index < path.Segments.Length; index++)
        {
            var segment = path.Segments[index];
            if (segment.Kind == SegmentKind.Element)
            {
                if (currentShape is not null)
                    return ImportedPathResolution.Failure("An element segment cannot navigate directly from a shape root.");
                if (currentCardinality == FieldCardinality.Many)
                {
                    currentCardinality = FieldCardinality.Single;
                    continue;
                }
                if (currentType is ArrayTypeRef array)
                {
                    currentType = array.ElementType;
                    continue;
                }
                if (currentType is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Array })
                    return ImportedPathResolution.Open(index);

                return ImportedPathResolution.Failure(
                    $"segment {index + 1} requires a collection but the imported type is '{currentType}'.");
            }

            var fieldName = segment.Segment!;
            if (currentShape is not null)
            {
                if (!currentShape.TryGetField(fieldName, out var field))
                {
                    return ImportedPathResolution.Failure(
                        $"the root shape does not contain field '{fieldName}'.");
                }

                currentShape = null;
                currentType = field.Type;
                currentCardinality = field.Cardinality;
                currentPresence = LeastStrict(currentPresence, field.Presence);
                currentNullability = LeastStrict(currentNullability, field.Nullability);
                continue;
            }

            if (currentCardinality == FieldCardinality.Many)
            {
                return ImportedPathResolution.Failure(
                    $"field '{fieldName}' requires an element segment after a many-valued field.");
            }

            switch (currentType)
            {
                case JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Object }:
                    return ImportedPathResolution.Open(index);
                case NamedTypeRef named:
                    if (!graph.TryGetType(named.TypeId, out var definition)
                        || definition is not TypeDefinition.Structural structural)
                    {
                        return ImportedPathResolution.Failure(
                            $"named type '{named.TypeId.Value}' is absent or is not structural.");
                    }
                    if (!structural.TryGetField(fieldName, out var structuralField))
                    {
                        return ImportedPathResolution.Failure(
                            $"structural type '{named.TypeId.Value}' does not contain field '{fieldName}'.");
                    }

                    currentType = structuralField.Type;
                    currentCardinality = structuralField.Cardinality;
                    currentPresence = LeastStrict(currentPresence, structuralField.Presence);
                    currentNullability = LeastStrict(currentNullability, structuralField.Nullability);
                    break;
                case ObjectTypeRef objectType:
                    var objectField = objectType.Fields.FirstOrDefault(
                        field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));
                    if (objectField is null)
                    {
                        return ImportedPathResolution.Failure(
                            $"inline object type does not contain field '{fieldName}'.");
                    }

                    currentType = objectField.Type;
                    currentCardinality = FieldCardinality.Single;
                    currentPresence = LeastStrict(currentPresence, objectField.Presence);
                    break;
                default:
                    return ImportedPathResolution.Failure(
                        $"field '{fieldName}' cannot navigate through imported type '{currentType}'.");
            }
        }

        return currentType is null
            ? ImportedPathResolution.Failure("the path did not resolve to an imported field type.")
            : new(
                currentType,
                currentCardinality,
                currentPresence,
                currentNullability,
                OpenSegmentIndex: null,
                Error: null);
    }

    static FieldPresence LeastStrict(FieldPresence left, FieldPresence right) =>
        left == FieldPresence.Optional || right == FieldPresence.Optional
            ? FieldPresence.Optional
            : FieldPresence.Required;

    static FieldNullability LeastStrict(FieldNullability left, FieldNullability right) =>
        left == FieldNullability.Nullable || right == FieldNullability.Nullable
            ? FieldNullability.Nullable
            : FieldNullability.NonNullable;

    static bool AreDefinitelyCompatible(
        TypeRef expected,
        ShapeGraph expectedGraph,
        TypeRef actual,
        ShapeGraph actualGraph) =>
        AreDefinitelyCompatible(
            expected,
            expectedGraph,
            actual,
            actualGraph,
            new HashSet<(TypeId Expected, TypeId Actual)>());

    static bool AreDefinitelyCompatible(
        TypeRef expected,
        ShapeGraph expectedGraph,
        TypeRef actual,
        ShapeGraph actualGraph,
        ISet<(TypeId Expected, TypeId Actual)> comparedNamedTypes)
    {
        if (expected is JsonTypeRef expectedJson)
            return IsCompatibleWithExpectedJson(expectedJson.Kind, actual, actualGraph);
        if (actual is JsonTypeRef actualJson)
            return IsCompatibleWithActualJson(expected, expectedGraph, actualJson.Kind);

        if (expected is ArrayTypeRef expectedArray && actual is ArrayTypeRef actualArray)
        {
            return AreDefinitelyCompatible(
                expectedArray.ElementType,
                expectedGraph,
                actualArray.ElementType,
                actualGraph,
                comparedNamedTypes);
        }

        if (expected is NamedTypeRef expectedNamed)
        {
            if (!expectedGraph.TryGetType(expectedNamed.TypeId, out var expectedDefinition))
                return false;

            if (actual is NamedTypeRef actualNamed)
            {
                if (!actualGraph.TryGetType(actualNamed.TypeId, out var actualDefinition))
                    return false;
                if (!comparedNamedTypes.Add((expectedNamed.TypeId, actualNamed.TypeId)))
                    return true;

                return AreDefinitionsCompatible(
                    expectedDefinition,
                    expectedGraph,
                    actualDefinition,
                    actualGraph,
                    comparedNamedTypes);
            }

            return expectedDefinition is TypeDefinition.Structural expectedStructural
                   && actual is ObjectTypeRef actualObject
                   && AreStructuralFieldsCompatible(
                       expectedStructural.Fields,
                       expectedGraph,
                       actualObject.Fields,
                       actualGraph,
                       comparedNamedTypes);
        }

        if (actual is NamedTypeRef actualNamedType)
        {
            return expected is ObjectTypeRef expectedObject
                   && actualGraph.TryGetType(actualNamedType.TypeId, out var actualDefinition)
                   && actualDefinition is TypeDefinition.Structural actualStructural
                   && AreStructuralFieldsCompatible(
                       expectedObject.Fields,
                       expectedGraph,
                       actualStructural.Fields,
                       actualGraph,
                       comparedNamedTypes);
        }

        return (expected, actual) switch
        {
            (ScalarTypeRef expectedScalar, ScalarTypeRef actualScalar) => expectedScalar == actualScalar,
            (ObjectTypeRef expectedObject, ObjectTypeRef actualObject) =>
                AreObjectFieldsCompatible(
                    expectedObject.Fields,
                    expectedGraph,
                    actualObject.Fields,
                    actualGraph,
                    comparedNamedTypes),
            _ => expected == actual
        };
    }

    static bool AreDefinitionsCompatible(
        TypeDefinition expected,
        ShapeGraph expectedGraph,
        TypeDefinition actual,
        ShapeGraph actualGraph,
        ISet<(TypeId Expected, TypeId Actual)> comparedNamedTypes) =>
        (expected, actual) switch
        {
            (TypeDefinition.Structural expectedStructural, TypeDefinition.Structural actualStructural) =>
                AreStructuralFieldsCompatible(
                    expectedStructural.Fields,
                    expectedGraph,
                    actualStructural.Fields,
                    actualGraph,
                    comparedNamedTypes),
            (TypeDefinition.Enum expectedEnum, TypeDefinition.Enum actualEnum) =>
                expectedEnum.Underlying == actualEnum.Underlying
                && expectedEnum.Values.SequenceEqual(actualEnum.Values),
            (TypeDefinition.Union expectedUnion, TypeDefinition.Union actualUnion) =>
                expectedUnion == actualUnion,
            _ => false
        };

    static bool AreStructuralFieldsCompatible(
        ImmutableArray<StructuralField> expected,
        ShapeGraph expectedGraph,
        ImmutableArray<StructuralField> actual,
        ShapeGraph actualGraph,
        ISet<(TypeId Expected, TypeId Actual)> comparedNamedTypes)
    {
        foreach (var expectedField in expected)
        {
            var actualField = actual.FirstOrDefault(field => field.Name == expectedField.Name);
            if (actualField is null
                || expectedField.Cardinality != actualField.Cardinality
                || expectedField.Presence == FieldPresence.Required
                && actualField.Presence != FieldPresence.Required
                || expectedField.Nullability == FieldNullability.NonNullable
                && actualField.Nullability != FieldNullability.NonNullable
                || !AreDefinitelyCompatible(
                    expectedField.Type,
                    expectedGraph,
                    actualField.Type,
                    actualGraph,
                    comparedNamedTypes))
            {
                return false;
            }
        }

        return true;
    }

    static bool AreStructuralFieldsCompatible(
        ImmutableArray<StructuralField> expected,
        ShapeGraph expectedGraph,
        ImmutableArray<ObjectFieldTypeDef> actual,
        ShapeGraph actualGraph,
        ISet<(TypeId Expected, TypeId Actual)> comparedNamedTypes)
    {
        foreach (var expectedField in expected)
        {
            var actualField = actual.FirstOrDefault(field =>
                string.Equals(field.Name, expectedField.Name.Value, StringComparison.Ordinal));
            if (actualField is null
                || expectedField.Cardinality != FieldCardinality.Single
                || expectedField.Presence == FieldPresence.Required
                && actualField.Presence != FieldPresence.Required
                || !AreDefinitelyCompatible(
                    expectedField.Type,
                    expectedGraph,
                    actualField.Type,
                    actualGraph,
                    comparedNamedTypes))
            {
                return false;
            }
        }

        return true;
    }

    static bool AreStructuralFieldsCompatible(
        ImmutableArray<ObjectFieldTypeDef> expected,
        ShapeGraph expectedGraph,
        ImmutableArray<StructuralField> actual,
        ShapeGraph actualGraph,
        ISet<(TypeId Expected, TypeId Actual)> comparedNamedTypes)
    {
        foreach (var expectedField in expected)
        {
            var actualField = actual.FirstOrDefault(field =>
                string.Equals(field.Name.Value, expectedField.Name, StringComparison.Ordinal));
            if (actualField is null
                || actualField.Cardinality != FieldCardinality.Single
                || expectedField.Presence == FieldPresence.Required
                && actualField.Presence != FieldPresence.Required
                || !AreDefinitelyCompatible(
                    expectedField.Type,
                    expectedGraph,
                    actualField.Type,
                    actualGraph,
                    comparedNamedTypes))
            {
                return false;
            }
        }

        return true;
    }

    static bool AreObjectFieldsCompatible(
        ImmutableArray<ObjectFieldTypeDef> expected,
        ShapeGraph expectedGraph,
        ImmutableArray<ObjectFieldTypeDef> actual,
        ShapeGraph actualGraph,
        ISet<(TypeId Expected, TypeId Actual)> comparedNamedTypes)
    {
        foreach (var expectedField in expected)
        {
            var actualField = actual.FirstOrDefault(field =>
                string.Equals(field.Name, expectedField.Name, StringComparison.Ordinal));
            if (actualField is null
                || expectedField.Presence == FieldPresence.Required
                && actualField.Presence != FieldPresence.Required
                || !AreDefinitelyCompatible(
                    expectedField.Type,
                    expectedGraph,
                    actualField.Type,
                    actualGraph,
                    comparedNamedTypes))
            {
                return false;
            }
        }

        return true;
    }

    static bool IsCompatibleWithExpectedJson(
        JsonTypeKind expected,
        TypeRef actual,
        ShapeGraph actualGraph) => expected switch
    {
        JsonTypeKind.Any => true,
        JsonTypeKind.Object => actual is ObjectTypeRef
            || actual is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Object }
            || actual is NamedTypeRef named
            && actualGraph.TryGetType(named.TypeId, out var definition)
            && definition is TypeDefinition.Structural,
        JsonTypeKind.Array => actual is ArrayTypeRef
            || actual is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Array },
        JsonTypeKind.String => actual is ScalarTypeRef
            {
                Kind: ScalarTypeKind.String
                or ScalarTypeKind.Guid
                or ScalarTypeKind.Date
                or ScalarTypeKind.DateTime
                or ScalarTypeKind.Instant
                or ScalarTypeKind.Bytes
            } || actual is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.String },
        JsonTypeKind.Number => actual is ScalarTypeRef
            { Kind: ScalarTypeKind.Int32 or ScalarTypeKind.Int64 or ScalarTypeKind.Decimal }
            || actual is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Number },
        JsonTypeKind.Boolean => actual is ScalarTypeRef { Kind: ScalarTypeKind.Bool }
            || actual is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Boolean },
        _ => false
    };

    static bool IsCompatibleWithActualJson(
        TypeRef expected,
        ShapeGraph expectedGraph,
        JsonTypeKind actual) => actual switch
    {
        JsonTypeKind.Any => true,
        JsonTypeKind.Object => expected is ObjectTypeRef
            || expected is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Object }
            || expected is NamedTypeRef named
            && expectedGraph.TryGetType(named.TypeId, out var definition)
            && definition is TypeDefinition.Structural,
        JsonTypeKind.Array => expected is ArrayTypeRef
            || expected is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Array },
        JsonTypeKind.String => expected is ScalarTypeRef
            {
                Kind: ScalarTypeKind.String
                or ScalarTypeKind.Guid
                or ScalarTypeKind.Date
                or ScalarTypeKind.DateTime
                or ScalarTypeKind.Instant
                or ScalarTypeKind.Bytes
            } || expected is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.String },
        JsonTypeKind.Number => expected is ScalarTypeRef
            { Kind: ScalarTypeKind.Int32 or ScalarTypeKind.Int64 or ScalarTypeKind.Decimal }
            || expected is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Number },
        JsonTypeKind.Boolean => expected is ScalarTypeRef { Kind: ScalarTypeKind.Bool }
            || expected is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Boolean },
        _ => false
    };

    ClrShapeGraphBuildResult GetMapping(ShapeRegistration registration) =>
        registration.Inferred is { } inferred
            ? Build(inferred)
            : registration.Imported!.Mapping;

    ClrShapeGraphBuildResult Build(InferredGraphState graph)
    {
        if (graph.Build is not null)
            return graph.Build;

        var builder = CreateBuilder();
        foreach (var root in graph.Roots.Values.OrderBy(
                     static root => ClrShapeIdentityConvention.GetTypeId(root.ClrType).Value,
                     StringComparer.Ordinal))
        {
            builder.AddShape(root.ClrType, root.Role);
        }

        var overrides = graph.Roots.Values
            .Where(static root => root.ExplicitShapeId.HasValue)
            .ToDictionary(static root => root.ClrType, static root => root.ExplicitShapeId!.Value);
        if (overrides.Count > 0)
            builder.AddMetadataProvider(new ExplicitShapeIdMetadataProvider(overrides));

        graph.Build = builder.BuildResult(graph.Id);
        return graph.Build;
    }

    ClrShapeGraphBuilder CreateBuilder() => new ClrShapeGraphBuilder()
        .AddMetadataProviders(Profile.Providers);

    internal TypeRef GetTypeRef(ShapeRegistration registration)
    {
        lock (gate)
            return GetRegisteredTypeRefCore(registration);
    }

    internal TypeRef GetTypeRef(ShapeRegistration registration, Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        lock (gate)
        {
            return UnwrapNullable(clrType) == registration.ClrType
                ? GetRegisteredTypeRefCore(registration)
                : GetMapping(registration).GetTypeRef(clrType);
        }
    }

    internal RelationQueryClrShape<T>? TryGetRegisteredShape<T>() where T : notnull
    {
        lock (gate)
        {
            var normalized = UnwrapNullable(typeof(T));
            var registration = registrations.Values
                .Where(candidate => candidate.ClrType == normalized)
                .OrderByDescending(static candidate => candidate.Precedence)
                .ThenBy(static candidate => candidate.Id.ToString(), StringComparer.Ordinal)
                .FirstOrDefault();
            return registration is null ? null : new(this, registration);
        }
    }

    internal bool IsReachableFromImportedShape(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        lock (gate)
        {
            foreach (var graph in importedGraphs.Values)
            {
                foreach (var registration in graph.Registrations)
                {
                    TypeRef candidate;
                    try
                    {
                        candidate = registration.Imported!.Mapping.GetTypeRef(clrType);
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    if (ReferencesOnlyImportedTypes(candidate, registration.Imported.Document.Graph))
                        return true;
                }
            }

            return false;
        }
    }

    static TypeRef GetRegisteredTypeRefCore(ShapeRegistration registration)
    {
        var mapping = registration.Inferred is { } inferredState
            ? inferredState.Build
              ?? throw new InvalidOperationException("The inferred CLR graph must be built before resolving its root type.")
            : registration.Imported!.Mapping;
        var inferred = mapping.GetTypeRef(registration.ClrType);
        if (registration.Imported is not { } imported)
            return inferred;
        if (ReferencesOnlyImportedTypes(inferred, imported.Document.Graph))
            return inferred;

        var shape = imported.Document.Graph.GetShape(registration.Id);
        if (shape.Fields.IsDefaultOrEmpty)
            return new JsonTypeRef(JsonTypeKind.Object);
        return new ObjectTypeRef(
        [
            .. shape.Fields.Select(static field => new ObjectFieldTypeDef(
                field.Name.Value,
                field.Cardinality == FieldCardinality.Many
                    ? new ArrayTypeRef(field.Type)
                    : field.Type,
                field.Presence,
                field.Annotations))
        ]);
    }

    internal FieldPath ResolveMemberPath(
        ShapeRegistration registration,
        IReadOnlyList<PropertyInfo> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        lock (gate)
            return ResolveMemberPathWithProvenance(registration, registration.ClrType, members).Path;
    }

    internal FieldPath ResolveMemberPath(
        ShapeRegistration registration,
        Type rootType,
        IReadOnlyList<PropertyInfo> members)
    {
        ArgumentNullException.ThrowIfNull(rootType);
        ArgumentNullException.ThrowIfNull(members);
        lock (gate)
            return ResolveMemberPathWithProvenance(registration, rootType, members).Path;
    }

    internal RelationQueryClrMemberPathResolution ResolveMemberPathWithProvenance(
        ShapeRegistration registration,
        IReadOnlyList<PropertyInfo> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        lock (gate)
            return ResolveMemberPathWithProvenance(registration, registration.ClrType, members);
    }

    internal ShapeGraphDocument GetShapeDocument(ShapeRegistration registration) =>
        GetShapeDocument(registration.Id);

    static ImmutableDictionary<PropertyInfo, FieldPath> NormalizeOverrides(
        IReadOnlyDictionary<PropertyInfo, FieldPath>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
            return ImmutableDictionary<PropertyInfo, FieldPath>.Empty;

        var builder = ImmutableDictionary.CreateBuilder<PropertyInfo, FieldPath>();
        foreach (var pair in overrides)
        {
            if (pair.Key is null)
                throw new ArgumentException("A member-path override cannot have a null CLR property.", nameof(overrides));
            if (pair.Value.Segments.IsDefaultOrEmpty)
                throw new ArgumentException("A member-path override cannot have an empty semantic path.", nameof(overrides));
            builder.Add(pair.Key, pair.Value);
        }
        return builder.ToImmutable();
    }

    static bool TryGetOverride(
        IReadOnlyDictionary<PropertyInfo, FieldPath> overrides,
        PropertyInfo property,
        out FieldPath path)
    {
        if (overrides.TryGetValue(property, out path))
            return true;

        foreach (var pair in overrides)
        {
            if (ShapeTypeInspector.IsSameProperty(pair.Key, property))
            {
                path = pair.Value;
                return true;
            }
        }

        path = default;
        return false;
    }

    static bool TryGetPropertyValue<TValue>(
        IReadOnlyDictionary<PropertyInfo, TValue> values,
        PropertyInfo property,
        out TValue value)
    {
        if (values.TryGetValue(property, out value!))
            return true;

        foreach (var pair in values)
        {
            if (ShapeTypeInspector.IsSameProperty(pair.Key, property))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    static bool ContainsProperty(
        IReadOnlyDictionary<PropertyInfo, FieldName> fields,
        PropertyInfo property)
    {
        if (fields.ContainsKey(property))
            return true;
        foreach (var candidate in fields.Keys)
        {
            if (ShapeTypeInspector.IsSameProperty(candidate, property))
                return true;
        }
        return false;
    }

    static bool AreOverridesEqual(
        IReadOnlyDictionary<PropertyInfo, FieldPath> left,
        IReadOnlyDictionary<PropertyInfo, FieldPath> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var pair in left)
        {
            if (!TryGetOverride(right, pair.Key, out var path) || path != pair.Value)
                return false;
        }
        return true;
    }

    static void RequireQualifiedShapeId(QualifiedShapeId id, string paramName)
    {
        if (string.IsNullOrWhiteSpace(id.GraphId.Value) || string.IsNullOrWhiteSpace(id.ShapeId.Value))
            throw new ArgumentException("A graph-qualified shape identifier is required.", paramName);
    }

    QualifiedShapeId GetConventionShapeId(Type clrType)
    {
        var convention = ClrRelationshipShapeConvention.GetQualifiedShapeId(clrType);
        var defaultProfile = RelationQueryClrMetadataProfile.Default;
        if (string.Equals(Profile.Id, defaultProfile.Id, StringComparison.Ordinal)
            && string.Equals(Profile.Version, defaultProfile.Version, StringComparison.Ordinal))
        {
            return convention;
        }

        var idLength = Profile.Id.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var versionLength = Profile.Version.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(
            new GraphId(
                $"{convention.GraphId.Value}:metadata-profile/"
                + $"{idLength}:{Profile.Id}/{versionLength}:{Profile.Version}"),
            convention.ShapeId);
    }

    static Type UnwrapNullable(Type clrType) => Nullable.GetUnderlyingType(clrType) ?? clrType;

    readonly record struct RegistrationKey(Type ClrType, QualifiedShapeId Id);

    readonly record struct ImportedPathResolution(
        TypeRef? Type,
        FieldCardinality Cardinality,
        FieldPresence Presence,
        FieldNullability Nullability,
        int? OpenSegmentIndex,
        string? Error)
    {
        public static ImportedPathResolution Open(int segmentIndex) => new(
            Type: null,
            FieldCardinality.Single,
            FieldPresence.Optional,
            FieldNullability.Nullable,
            OpenSegmentIndex: segmentIndex,
            Error: null);

        public static ImportedPathResolution Failure(string error) => new(
            Type: null,
            FieldCardinality.Single,
            FieldPresence.Optional,
            FieldNullability.Nullable,
            OpenSegmentIndex: null,
            Error: error);
    }

    internal sealed class ShapeRegistration
    {
        public ShapeRegistration(
            Type clrType,
            QualifiedShapeId id,
            int precedence,
            RelationQueryClrIdentityOrigin identityOrigin,
            InferredGraphState? inferred,
            ImportedRegistration? imported,
            ImmutableDictionary<PropertyInfo, FieldPath> overrides)
        {
            ClrType = clrType;
            Id = id;
            Precedence = precedence;
            IdentityOrigin = identityOrigin;
            Inferred = inferred;
            Imported = imported;
            Overrides = overrides;
        }

        public Type ClrType { get; }
        public QualifiedShapeId Id { get; }
        public int Precedence { get; }
        public RelationQueryClrIdentityOrigin IdentityOrigin { get; }
        public InferredGraphState? Inferred { get; }
        public ImportedRegistration? Imported { get; }
        public ImmutableDictionary<PropertyInfo, FieldPath> Overrides { get; }
    }

    internal sealed class InferredGraphState(GraphId id)
    {
        public GraphId Id { get; } = id;
        public Dictionary<Type, InferredRoot> Roots { get; } = [];
        public ClrShapeGraphBuildResult? Build { get; set; }
        public void Invalidate() => Build = null;
    }

    internal sealed class InferredRoot(Type clrType, string role, ShapeId? explicitShapeId)
    {
        public Type ClrType { get; } = clrType;
        public string Role { get; } = role;
        public ShapeId? ExplicitShapeId { get; } = explicitShapeId;
        public QualifiedShapeId QualifiedId { get; set; }
    }

    sealed class ImportedGraphState(ShapeGraphDocument document)
    {
        public ShapeGraphDocument Document { get; } = document;
        public List<ShapeRegistration> Registrations { get; } = [];
    }

    internal sealed record ImportedRegistration(
        ShapeGraphDocument Document,
        ClrShapeGraphBuildResult Mapping);

    sealed class ExplicitShapeIdMetadataProvider(IReadOnlyDictionary<Type, ShapeId> shapeIds)
        : IClrShapeMetadataProvider
    {
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context)
        {
            if (context.Target == ClrShapeMetadataTarget.Shape
                && shapeIds.TryGetValue(context.ClrType, out var shapeId))
            {
                return new() { ShapeId = shapeId };
            }

            return ClrShapeMetadata.Empty;
        }
    }
}

/// <summary>
/// Immutable typed authoring handle for one CLR-backed semantic shape.
/// </summary>
/// <typeparam name="T">CLR type represented by the semantic shape.</typeparam>
public sealed class RelationQueryClrShape<T> where T : notnull
{
    readonly RelationQueryClrAuthoringContext context;
    readonly RelationQueryClrAuthoringContext.ShapeRegistration registration;

    internal RelationQueryClrShape(
        RelationQueryClrAuthoringContext context,
        RelationQueryClrAuthoringContext.ShapeRegistration registration)
    {
        this.context = context;
        this.registration = registration;
    }

    /// <summary>
    /// Gets the exact graph-qualified semantic shape identity.
    /// </summary>
    public QualifiedShapeId Id => registration.Id;

    /// <summary>
    /// Gets the effective authority that selected <see cref="Id"/>.
    /// </summary>
    public RelationQueryClrIdentityOrigin IdentityOrigin => registration.IdentityOrigin;

    /// <summary>
    /// Gets the portable semantic type reference inferred for <typeparamref name="T"/>.
    /// </summary>
    public TypeRef Type => context.GetTypeRef(registration);

    /// <summary>
    /// Gets the authoritative shape-graph document containing <see cref="Id"/>.
    /// </summary>
    public ShapeGraphDocument Document => context.GetShapeDocument(registration);

    /// <summary>
    /// Resolves an ordered CLR property chain to its effective semantic field path.
    /// </summary>
    /// <param name="members">Ordered properties from <typeparamref name="T"/> toward the terminal value.</param>
    /// <returns>The metadata-derived or explicitly overridden semantic field path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="members"/> is empty or is not a valid property chain rooted at <typeparamref name="T"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">A member has no inferred or overridden semantic path.</exception>
    public FieldPath ResolveMemberPath(IReadOnlyList<PropertyInfo> members) =>
        context.ResolveMemberPath(registration, members);

    internal FieldPath ResolveMemberPath(
        Type rootType,
        IReadOnlyList<PropertyInfo> members) =>
        context.ResolveMemberPath(registration, rootType, members);

    internal TypeRef ResolveType(Type clrType) =>
        context.GetTypeRef(registration, clrType);

    /// <summary>
    /// Resolves an ordered CLR property chain and explains the authority behind each path segment.
    /// </summary>
    /// <param name="members">Ordered properties from <typeparamref name="T"/> toward the terminal value.</param>
    /// <returns>The effective field path and one identity origin per path segment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="members"/> is empty or is not a valid property chain rooted at <typeparamref name="T"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A member has no effective semantic path, or an imported shape is definitely incompatible
    /// with the resolved path or CLR value type.
    /// </exception>
    public RelationQueryClrMemberPathResolution ResolveMemberPathWithProvenance(
        IReadOnlyList<PropertyInfo> members) =>
        context.ResolveMemberPathWithProvenance(registration, members);

    internal RelationQueryClrAuthoringContext AuthoringContext => context;
}
