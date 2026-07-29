using System.Collections.Immutable;
using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Stable diagnostic codes emitted while authoring PostgreSQL relation/query storage bindings.</summary>
public static class PostgresRelationQueryBindingAuthoringDiagnosticCodes
{
    /// <summary>The authored placement, source, or target profile does not match the exact plan.</summary>
    public const string PlacementMismatch = "relationQuery.authoring.postgres.placementMismatch";

    /// <summary>A required database, table, identity, relationship reference, or field mapping is absent.</summary>
    public const string BindingMissing = "relationQuery.authoring.postgres.bindingMissing";

    /// <summary>A table or semantic selector is unknown for the exact authored placement.</summary>
    public const string SelectorUnknown = "relationQuery.authoring.postgres.selectorUnknown";

    /// <summary>A same-tier database, table, field, identity, or evidence setting is repeated.</summary>
    public const string BindingDuplicate = "relationQuery.authoring.postgres.bindingDuplicate";

    /// <summary>A semantic path, physical identifier, type, or value encoding cannot be interpreted exactly.</summary>
    public const string SelectorInvalid = "relationQuery.authoring.postgres.selectorInvalid";

    /// <summary>Effective configuration cannot prove canonical PostgreSQL value semantics.</summary>
    public const string SemanticEvidenceMissing = "relationQuery.authoring.postgres.semanticEvidenceMissing";

    /// <summary>The normalized effective facts could not construct an immutable storage binding.</summary>
    public const string ArtifactInvalid = "relationQuery.authoring.postgres.artifactInvalid";
}

/// <summary>Convention used to map demanded semantic fields without local column overrides.</summary>
public enum PostgresRelationQueryColumnMappingConvention
{
    /// <summary>Map a top-level semantic field to a column with the exact same identifier.</summary>
    SemanticFieldName = 0,

    /// <summary>Require every demanded field and relationship reference to be mapped explicitly.</summary>
    Explicit = 1
}

/// <summary>Optional physical column semantics overriding values inferred from the canonical field contract.</summary>
public sealed record PostgresRelationQueryColumnOptions
{
    /// <summary>Creates optional PostgreSQL column-semantic overrides.</summary>
    /// <param name="scalarType">Explicit physical scalar type, or <see langword="null"/> to infer it.</param>
    /// <param name="missingValueEncoding">Explicit missing encoding, or <see langword="null"/> to infer it.</param>
    /// <param name="nullValueEncoding">Explicit null encoding, or <see langword="null"/> to infer it.</param>
    /// <param name="textSemantics">Explicit text collation evidence, or <see langword="null"/>.</param>
    /// <param name="ordering">Explicit ordering evidence, or <see langword="null"/> to infer no ordering evidence.</param>
    /// <param name="numericDomain">Explicit finite CLR-decimal domain evidence.</param>
    /// <param name="decimalAggregates">Explicit plan-affine decimal SUM/AVG evidence.</param>
    /// <param name="temporalDomain">Explicit finite canonical CLR temporal-domain evidence.</param>
    /// <exception cref="ArgumentOutOfRangeException">An enum value or ordering flag is unsupported.</exception>
    public PostgresRelationQueryColumnOptions(
        PostgresRelationQueryScalarType? scalarType = null,
        PostgresRelationQueryMissingValueEncoding? missingValueEncoding = null,
        PostgresRelationQueryNullValueEncoding? nullValueEncoding = null,
        PostgresRelationQueryTextSemantics? textSemantics = null,
        PostgresRelationQueryOrderingCapability? ordering = null,
        PostgresRelationQueryNumericDomainEvidence? numericDomain = null,
        PostgresRelationQueryDecimalAggregateAttestation? decimalAggregates = null,
        PostgresRelationQueryTemporalDomainEvidence? temporalDomain = null)
    {
        if (scalarType is { } scalar && !Enum.IsDefined(scalar))
            throw new ArgumentOutOfRangeException(nameof(scalarType), scalarType, "Unsupported PostgreSQL scalar type.");
        if (missingValueEncoding is { } missing && !Enum.IsDefined(missing))
            throw new ArgumentOutOfRangeException(nameof(missingValueEncoding), missingValueEncoding, "Unsupported missing encoding.");
        if (nullValueEncoding is { } @null && !Enum.IsDefined(@null))
            throw new ArgumentOutOfRangeException(nameof(nullValueEncoding), nullValueEncoding, "Unsupported null encoding.");
        const PostgresRelationQueryOrderingCapability all =
            PostgresRelationQueryOrderingCapability.Exact | PostgresRelationQueryOrderingCapability.StableUnique;
        if (ordering is { } explicitOrdering && (explicitOrdering & ~all) != 0)
            throw new ArgumentOutOfRangeException(nameof(ordering), ordering, "Unsupported PostgreSQL ordering evidence.");
        ScalarType = scalarType;
        MissingValueEncoding = missingValueEncoding;
        NullValueEncoding = nullValueEncoding;
        TextSemantics = textSemantics;
        Ordering = ordering ?? PostgresRelationQueryOrderingCapability.None;
        HasOrderingOverride = ordering is not null;
        NumericDomain = numericDomain;
        DecimalAggregates = decimalAggregates;
        TemporalDomain = temporalDomain;
    }

    /// <summary>Explicit physical scalar type, or <see langword="null"/>.</summary>
    public PostgresRelationQueryScalarType? ScalarType { get; }

    /// <summary>Explicit semantic-missing encoding, or <see langword="null"/>.</summary>
    public PostgresRelationQueryMissingValueEncoding? MissingValueEncoding { get; }

    /// <summary>Explicit semantic-null encoding, or <see langword="null"/>.</summary>
    public PostgresRelationQueryNullValueEncoding? NullValueEncoding { get; }

    /// <summary>Explicit text collation evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTextSemantics? TextSemantics { get; }

    /// <summary>Exact and stable-unique ordering evidence.</summary>
    public PostgresRelationQueryOrderingCapability Ordering { get; }

    /// <summary>Explicit finite CLR-decimal domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryNumericDomainEvidence? NumericDomain { get; }

    /// <summary>Explicit plan-affine decimal SUM/AVG evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryDecimalAggregateAttestation? DecimalAggregates { get; }

    /// <summary>Explicit finite canonical CLR temporal-domain evidence, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTemporalDomainEvidence? TemporalDomain { get; }

    internal bool HasOrderingOverride { get; }
}

/// <summary>Scoped PostgreSQL binding-authoring values applied between adapter conventions and local declarations.</summary>
/// <remarks>
/// Invalid target-specific identifiers and combinations are retained as configuration input and reported through
/// structured diagnostics when the builder resolves them against an exact authored placement.
/// </remarks>
public sealed class PostgresRelationQueryBindingAuthoringOptions
{
    /// <summary>Creates a named immutable PostgreSQL authoring profile.</summary>
    /// <param name="authority">Stable profile identity and version.</param>
    /// <param name="bindingId">Optional scoped binding identity.</param>
    /// <param name="database">Optional scoped physical database identity.</param>
    /// <param name="defaultSchemaName">Default schema for tables without local schema declarations.</param>
    /// <param name="columnMappingConvention">Default demanded-field column convention.</param>
    /// <param name="conventionSetVersion">Optional convention-set attribution override.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="authority"/> is empty or white space.</exception>
    public PostgresRelationQueryBindingAuthoringOptions(
        string authority,
        PostgresRelationQueryBindingId? bindingId = null,
        PostgresRelationQueryDatabaseId? database = null,
        string? defaultSchemaName = null,
        PostgresRelationQueryColumnMappingConvention? columnMappingConvention = null,
        string? conventionSetVersion = null)
    {
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        BindingId = bindingId;
        Database = database;
        DefaultSchemaName = defaultSchemaName;
        ColumnMappingConvention = columnMappingConvention;
        ConventionSetVersion = conventionSetVersion;
    }

    /// <summary>Stable profile identity and version.</summary>
    public string Authority { get; }

    /// <summary>Optional scoped storage-binding identity.</summary>
    public PostgresRelationQueryBindingId? BindingId { get; }

    /// <summary>Optional scoped physical database identity.</summary>
    public PostgresRelationQueryDatabaseId? Database { get; }

    /// <summary>Optional scoped default schema name.</summary>
    public string? DefaultSchemaName { get; }

    /// <summary>Optional scoped demanded-field column convention.</summary>
    public PostgresRelationQueryColumnMappingConvention? ColumnMappingConvention { get; }

    /// <summary>Optional scoped convention-set attribution.</summary>
    public string? ConventionSetVersion { get; }
}

/// <summary>Adapter-owned entry point for authoring a multi-table PostgreSQL binding.</summary>
public static class PostgresRelationQueryBinding
{
    /// <summary>Stable authority used for local declarations when the consumer supplies none.</summary>
    public const string LocalDeclarationAuthority = "cohesive.relations.authoring/local/v1";

    /// <summary>Starts PostgreSQL binding authoring for one exact authored placement.</summary>
    /// <param name="placement">Exact plan-bound authored placement whose acquired inputs will be bound.</param>
    /// <param name="options">Optional scoped PostgreSQL authoring profile.</param>
    /// <param name="explicitAuthority">Stable authority attributed to explicit local declarations.</param>
    /// <returns>A mutable, session-local PostgreSQL storage-binding builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> or <paramref name="explicitAuthority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="explicitAuthority"/> is empty or white space.</exception>
    public static PostgresRelationQueryStorageBindingBuilder For(
        RelationQueryAuthoredPlacement placement,
        PostgresRelationQueryBindingAuthoringOptions? options = null,
        string explicitAuthority = LocalDeclarationAuthority) =>
        new(placement, options, explicitAuthority);
}

/// <summary>Mutable structural authoring session for one exact multi-table PostgreSQL storage binding.</summary>
public sealed class PostgresRelationQueryStorageBindingBuilder
{
    const string DerivedIdAuthority = "cohesive.relations.postgres/binding-id-convention/v1";
    const string AdapterAuthority = PostgresRelationQueryStorageBinding.SemanticPathConventionSet;
    const string TargetSetting = "target";
    const string TargetProfileSetting = "targetProfile";
    const string DatabaseSetting = "database";
    const string ConventionSetting = "conventionSetVersion";
    const string BindingIdSetting = "bindingId";
    const string DefaultSchema = "public";

    readonly RelationQueryAuthoredPlacement placement;
    readonly PostgresRelationQueryBindingAuthoringOptions? options;
    readonly string explicitAuthority;
    readonly List<TableDeclaration> tables = [];
    readonly List<RelationQueryArtifactAuthoringDiagnostic> diagnostics = [];
    Effective<PostgresRelationQueryDatabaseId>? database;
    Effective<PostgresRelationQueryBindingId>? bindingId;
    Effective<string>? conventionSetVersion;

    internal PostgresRelationQueryStorageBindingBuilder(
        RelationQueryAuthoredPlacement placement,
        PostgresRelationQueryBindingAuthoringOptions? options,
        string explicitAuthority)
    {
        this.placement = Guard.RequireNotNull(placement);
        this.options = options;
        this.explicitAuthority = Guard.RequireNotNullOrWhiteSpace(explicitAuthority);
    }

    /// <summary>Exact authored placement being bound.</summary>
    public RelationQueryAuthoredPlacement Placement => placement;

    /// <summary>Overrides the convention-derived physical database identity.</summary>
    /// <param name="id">Stable non-secret physical database identity.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    public PostgresRelationQueryStorageBindingBuilder Database(PostgresRelationQueryDatabaseId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A PostgreSQL database identity cannot be default.", nameof(id));
        SetOnce(ref database, Explicit(id), DatabaseSetting);
        return this;
    }

    /// <summary>Overrides the deterministic storage-binding identity.</summary>
    /// <param name="id">Stable explicit binding identity.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    public PostgresRelationQueryStorageBindingBuilder WithId(PostgresRelationQueryBindingId id)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A PostgreSQL binding identity cannot be default.", nameof(id));
        SetOnce(ref bindingId, Explicit(id), BindingIdSetting);
        return this;
    }

    /// <summary>Overrides convention-set attribution for this artifact.</summary>
    /// <param name="version">Stable convention-set identity and version.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="version"/> is empty or white space.</exception>
    public PostgresRelationQueryStorageBindingBuilder ConventionSetVersion(string version)
    {
        SetOnce(ref conventionSetVersion, Explicit(Guard.RequireNotNullOrWhiteSpace(version)), ConventionSetting);
        return this;
    }

    /// <summary>Adds a structural table binding for one exact placed input.</summary>
    /// <param name="input">Exact plan-bound placed source or traversal input.</param>
    /// <param name="tableName">Physical PostgreSQL table or view name.</param>
    /// <param name="configure">Optional additional table configuration.</param>
    /// <returns>This multi-table builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="tableName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is empty or contains a null character.</exception>
    public PostgresRelationQueryStorageBindingBuilder Table(
        RelationQueryPlacedInput input,
        string tableName,
        Action<PostgresRelationQueryTableBindingBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        tableName = PostgresRelationQueryStorageBinding.RequireIdentifier(tableName, nameof(tableName));
        var declaration = new TableDeclaration(input, Explicit(tableName));
        tables.Add(declaration);
        configure?.Invoke(new(declaration, this));
        return this;
    }

    /// <summary>Adds a typed table binding for one exact CLR-backed placed input.</summary>
    /// <typeparam name="T">CLR type represented by the placed semantic shape.</typeparam>
    /// <param name="input">Exact typed plan-bound placed input.</param>
    /// <param name="tableName">Physical PostgreSQL table or view name.</param>
    /// <param name="configure">Optional typed table configuration.</param>
    /// <returns>This multi-table builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="tableName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is empty or contains a null character.</exception>
    public PostgresRelationQueryStorageBindingBuilder Table<T>(
        RelationQueryPlacedInput<T> input,
        string tableName,
        Action<PostgresRelationQueryTableBindingBuilder<T>>? configure = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        tableName = PostgresRelationQueryStorageBinding.RequireIdentifier(tableName, nameof(tableName));
        var declaration = new TableDeclaration(input, Explicit(tableName));
        tables.Add(declaration);
        configure?.Invoke(new(input, declaration, this));
        return this;
    }

    /// <summary>Builds a normalized, plan-affine PostgreSQL multi-table storage binding.</summary>
    /// <returns>A complete immutable artifact or structured fail-closed diagnostics.</returns>
    /// <exception cref="InvalidOperationException">The exact compiled-plan reference cannot be fingerprinted.</exception>
    /// <exception cref="System.Text.Json.JsonException">A shape snapshot cannot be serialized for plan attribution.</exception>
    /// <exception cref="NotSupportedException">A shape snapshot contains an unsupported portable value.</exception>
    public RelationQueryArtifactAuthoringResult<PostgresRelationQueryStorageBinding> Build()
    {
        ValidatePlacement();
        ValidateTableDeclarations();
        var effectiveDatabase = database
            ?? (options?.Database is { } scopedDatabase
                ? Scoped(scopedDatabase, options.Authority)
                : Adapter(new PostgresRelationQueryDatabaseId(DeriveDatabaseId())));
        var effectiveConvention = conventionSetVersion
            ?? (options?.ConventionSetVersion is { } scopedConvention
                ? Scoped(scopedConvention, options.Authority)
                : Adapter(PostgresRelationQueryStorageBinding.SemanticPathConventionSet));

        List<EffectiveConfigurationDecision> decisions =
        [
            Decision(TargetSetting, EffectiveConfigurationOrigin.AdapterConvention, PostgresRelationQueryTargetProfile.ProfileId.Value),
            Decision(TargetProfileSetting, EffectiveConfigurationOrigin.AdapterConvention, PostgresRelationQueryTargetProfile.ProfileId.Value),
            Configuration(DatabaseSetting, effectiveDatabase),
            Configuration(ConventionSetting, effectiveConvention)
        ];
        List<PostgresRelationQueryTableBinding> builtTables = [];
        foreach (var declaration in tables)
        {
            var table = BuildTable(declaration, decisions);
            if (table is not null)
                builtTables.Add(table);
        }

        if (HasErrors())
            return Failure();

        var planFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(
            RelationQueryCompiledPlanReference.From(placement.Plan));
        var placementFingerprint = placement.Placement.Fingerprint;
        var effectiveId = bindingId
            ?? (options?.BindingId is { } scopedId
                ? Scoped(scopedId, options.Authority)
                : new Effective<PostgresRelationQueryBindingId>(
                    DeriveBindingId(
                        effectiveDatabase.Value,
                        planFingerprint,
                        placementFingerprint,
                        effectiveConvention.Value,
                        builtTables),
                    EffectiveConfigurationOrigin.AdapterConvention,
                    DerivedIdAuthority));
        decisions.Add(Configuration(BindingIdSetting, effectiveId));
        try
        {
            var origin = decisions.Any(static decision => decision.Origin is
                EffectiveConfigurationOrigin.Explicit or EffectiveConfigurationOrigin.ScopedProfile)
                ? PostgresRelationQueryBindingOrigin.Explicit
                : PostgresRelationQueryBindingOrigin.Convention;
            var artifact = new PostgresRelationQueryStorageBinding(
                effectiveId.Value,
                effectiveDatabase.Value,
                PostgresRelationQueryTargetProfile.Target,
                PostgresRelationQueryTargetProfile.ProfileId,
                [.. builtTables],
                origin,
                effectiveConvention.Value,
                [.. decisions],
                planFingerprint,
                placementFingerprint);
            return new(artifact, [.. diagnostics]);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.ArtifactInvalid,
                $"PostgreSQL storage-binding construction failed: {exception.Message}");
            return Failure();
        }
    }

    PostgresRelationQueryTableBinding? BuildTable(
        TableDeclaration declaration,
        ICollection<EffectiveConfigurationDecision> decisions)
    {
        HashSet<FieldPath> consumedColumns = [];
        HashSet<RelationQueryInputId> consumedRelationshipReferences = [];
        var input = declaration.Input;
        var prefix = TableSetting(input.Binding.Id, string.Empty);
        var schema = declaration.Schema
            ?? (options?.DefaultSchemaName is { } scopedSchema
                ? Scoped(scopedSchema, options.Authority)
                : Adapter(DefaultSchema));
        var convention = declaration.ColumnConvention
            ?? (options?.ColumnMappingConvention is { } scopedConvention
                ? Scoped(scopedConvention, options.Authority)
                : Adapter(PostgresRelationQueryColumnMappingConvention.SemanticFieldName));
        if (!TryIdentifier(schema.Value, prefix + "schemaName", input.Binding.Input))
            return null;
        if (!Enum.IsDefined(convention.Value))
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                $"Unsupported PostgreSQL column convention '{convention.Value}'.", input.Binding.Input,
                setting: prefix + "columnMappingConvention");
            return null;
        }

        decisions.Add(Configuration(prefix + "schemaName", schema));
        decisions.Add(Configuration(prefix + "tableName", declaration.TableName));
        decisions.Add(Configuration(prefix + "columnMappingConvention", convention));
        var fields = BuildFields(declaration, convention, consumedColumns, decisions);
        var identity = BuildIdentity(declaration, convention, consumedColumns, decisions);
        var references = BuildRelationshipReferences(
            declaration,
            convention,
            consumedColumns,
            consumedRelationshipReferences,
            decisions);
        var intervalValidities = fields is null
            ? []
            : BuildIntervalValidities(declaration, fields.Value, decisions);
        ValidateExplicitSelectors(declaration, consumedColumns, consumedRelationshipReferences);
        if (fields is null || HasErrorsFor(input.Binding.Input))
            return null;
        try
        {
            return new(
                input.Source.Id,
                input.Binding.Id,
                input.Binding.Input,
                input.Shape,
                schema.Value,
                declaration.TableName.Value,
                identity,
                fields.Value,
                references,
                intervalValidities);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.ArtifactInvalid,
                $"PostgreSQL table-binding construction failed: {exception.Message}", input.Binding.Input);
            return null;
        }
    }

    ImmutableArray<PostgresRelationQueryFieldBinding>? BuildFields(
        TableDeclaration declaration,
        Effective<PostgresRelationQueryColumnMappingConvention> convention,
        ISet<FieldPath> consumedColumns,
        ICollection<EffectiveConfigurationDecision> decisions)
    {
        var builder = ImmutableArray.CreateBuilder<PostgresRelationQueryFieldBinding>(declaration.Input.Fields.Length);
        foreach (var field in declaration.Input.Fields)
        {
            var path = field.Input.Field.Path;
            if (!TryResolveColumn(
                    declaration,
                    path,
                    convention,
                    consumedColumns,
                    out var column,
                    out var attribution))
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                    $"Demanded field '{path}' has no PostgreSQL column mapping.", field.Input.Id, path,
                    FieldSetting(declaration.Input.Binding.Id, field.Input.Id, "columnName"));
                continue;
            }
            var overrides = declaration.ColumnOptions.GetValueOrDefault(path);
            if (!TryResolveValueSemantics(field.Input.ValueContract, overrides, field.Input.Id, path, out var value))
                continue;
            try
            {
                builder.Add(new(
                    field.Input.Id,
                    path,
                    column,
                    value.ScalarType.Value,
                    value.Missing.Value,
                    value.Null.Value,
                    value.Text.Value,
                    value.Ordering.Value,
                    value.NumericDomain.Value,
                    value.DecimalAggregates.Value,
                    value.TemporalDomain.Value));
                AppendColumnDecisions(declaration.Input.Binding.Id, field.Input.Id, attribution, value, decisions);
            }
            catch (ArgumentException exception)
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                    exception.Message, field.Input.Id, path,
                    FieldSetting(declaration.Input.Binding.Id, field.Input.Id, "semantics"));
            }
        }
        return builder.Count == declaration.Input.Fields.Length ? builder.ToImmutable() : null;
    }

    PostgresRelationQueryIdentityBinding? BuildIdentity(
        TableDeclaration declaration,
        Effective<PostgresRelationQueryColumnMappingConvention> convention,
        ISet<FieldPath> consumedColumns,
        ICollection<EffectiveConfigurationDecision> decisions)
    {
        var required = declaration.Input.Binding.Identity is not null;
        var selected = declaration.IdentityPath ?? InferIdentityPath(declaration.Input);
        if (selected is null)
        {
            if (required)
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                    "The placed PostgreSQL input requires an unambiguous physical identity column.",
                    declaration.Input.Binding.Input,
                    setting: TableSetting(declaration.Input.Binding.Id, "identityColumn"));
            }
            return null;
        }
        if (!TryResolveColumn(
                declaration,
                selected.Path,
                convention,
                consumedColumns,
                out var column,
                out var columnAttribution,
                selected.Column))
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                $"Identity path '{selected.Path}' has no PostgreSQL column mapping.",
                declaration.Input.Binding.Input, selected.Path,
                TableSetting(declaration.Input.Binding.Id, "identityColumn"));
            return null;
        }
        var identityOptions = selected.Options ?? declaration.ColumnOptions.GetValueOrDefault(selected.Path);
        if (!TryResolveShapeFieldContract(declaration.Input, selected.Path, out var contract)
            || !TryResolveValueSemantics(contract, identityOptions, declaration.Input.Binding.Input,
                selected.Path, out var value))
        {
            return null;
        }
        if (value.Missing.Value != PostgresRelationQueryMissingValueEncoding.Prohibited
            || value.Null.Value != PostgresRelationQueryNullValueEncoding.Prohibited)
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                "A PostgreSQL observation identity must be physically unique, present, and non-null.",
                declaration.Input.Binding.Input, selected.Path,
                TableSetting(declaration.Input.Binding.Id, "identityColumn"));
            return null;
        }
        var attribution = declaration.IdentityPath is not null ? ExplicitMarker() : columnAttribution;
        decisions.Add(Configuration(TableSetting(declaration.Input.Binding.Id, "identityColumn"), attribution));
        AppendIdentityValueDecisions(declaration.Input.Binding.Id, value, decisions);
        try
        {
            return new(
                selected.Path,
                column,
                value.ScalarType.Value,
                value.Text.Value,
                value.NumericDomain.Value,
                value.TemporalDomain.Value);
        }
        catch (ArgumentException exception)
        {
            Error(
                PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                exception.Message,
                declaration.Input.Binding.Input,
                selected.Path,
                TableSetting(declaration.Input.Binding.Id, "identity/semantics"));
            return null;
        }
    }

    ImmutableArray<PostgresRelationQueryRelationshipReferenceBinding> BuildRelationshipReferences(
        TableDeclaration declaration,
        Effective<PostgresRelationQueryColumnMappingConvention> convention,
        ISet<FieldPath> consumedColumns,
        ISet<RelationQueryInputId> consumedRelationshipReferences,
        ICollection<EffectiveConfigurationDecision> decisions)
    {
        var builder = ImmutableArray.CreateBuilder<PostgresRelationQueryRelationshipReferenceBinding>();
        foreach (var traversal in placement.Plan.InputContract.Traversals)
        {
            if (!OwnsRelationshipReference(declaration.Input, traversal))
                continue;
            var path = traversal.Definition.SourceReference;
            declaration.RelationshipReferences.TryGetValue(traversal.Input.Id, out var explicitReference);
            if (explicitReference is not null)
                consumedRelationshipReferences.Add(traversal.Input.Id);
            if (explicitReference is not null && explicitReference.Path != path)
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                    $"Explicit relationship reference '{explicitReference.Path}' does not match canonical path '{path}'.",
                    traversal.Input.Id, explicitReference.Path,
                    RelationshipSetting(declaration.Input.Binding.Id, traversal.Input.Id, "semanticPath"));
                continue;
            }
            if (!TryResolveColumn(
                    declaration,
                    path,
                    convention,
                    consumedColumns,
                    out var column,
                    out var attribution,
                    explicitReference?.Column))
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                    $"Relationship '{traversal.Definition.Id.Value}' source reference '{path}' has no PostgreSQL column mapping.",
                    traversal.Input.Id, path,
                    RelationshipSetting(declaration.Input.Binding.Id, traversal.Input.Id, "columnName"));
                continue;
            }
            var referenceOptions = explicitReference?.Options ?? declaration.ColumnOptions.GetValueOrDefault(path);
            if (!TryResolveShapeFieldContract(declaration.Input, path, out var contract)
                || !TryResolveValueSemantics(contract, referenceOptions, traversal.Input.Id, path, out var value))
            {
                continue;
            }
            try
            {
                builder.Add(new(
                    traversal.Input.Id,
                    path,
                    column,
                    value.ScalarType.Value,
                    traversal.Definition.SourceReferenceUniqueness,
                    value.Missing.Value,
                    value.Null.Value,
                    value.Text.Value,
                    value.NumericDomain.Value,
                    value.TemporalDomain.Value));
                decisions.Add(Configuration(
                    RelationshipSetting(declaration.Input.Binding.Id, traversal.Input.Id, "columnName"),
                    explicitReference is null ? attribution : ExplicitMarker()));
                AppendRelationshipValueDecisions(
                    declaration.Input.Binding.Id,
                    traversal.Input.Id,
                    value,
                    decisions);
            }
            catch (ArgumentException exception)
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                    exception.Message, traversal.Input.Id, path,
                    RelationshipSetting(declaration.Input.Binding.Id, traversal.Input.Id, "semantics"));
            }
        }
        return builder.ToImmutable();
    }

    ImmutableArray<PostgresRelationQueryIntervalValidityBinding> BuildIntervalValidities(
        TableDeclaration declaration,
        ImmutableArray<PostgresRelationQueryFieldBinding> fields,
        ICollection<EffectiveConfigurationDecision> decisions)
    {
        var builder = ImmutableArray.CreateBuilder<PostgresRelationQueryIntervalValidityBinding>(
            declaration.IntervalValidities.Count);
        foreach (var interval in declaration.IntervalValidities.Values)
        {
            var lower = fields.SingleOrDefault(field => field.SemanticPath == interval.LowerPath);
            var upper = fields.SingleOrDefault(field => field.SemanticPath == interval.UpperPath);
            var setting = IntervalSetting(
                declaration.Input.Binding.Id,
                interval.LowerPath,
                interval.UpperPath,
                "validatedCheckConstraintName");
            if (lower is null || upper is null)
            {
                Error(
                    PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorUnknown,
                    "Interval validity endpoints must be demanded fields of the exact placed input.",
                    declaration.Input.Binding.Input,
                    lower is null ? interval.LowerPath : interval.UpperPath,
                    setting);
                continue;
            }
            if (lower.ScalarType != upper.ScalarType || lower.ScalarType is not (
                    PostgresRelationQueryScalarType.Date
                    or PostgresRelationQueryScalarType.Timestamp
                    or PostgresRelationQueryScalarType.TimestampWithTimeZone))
            {
                Error(
                    PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                    "Interval validity endpoints must share one exact PostgreSQL temporal scalar type.",
                    declaration.Input.Binding.Input,
                    interval.LowerPath,
                    setting);
                continue;
            }
            if (lower.MissingValueEncoding != PostgresRelationQueryMissingValueEncoding.Prohibited
                || upper.MissingValueEncoding != PostgresRelationQueryMissingValueEncoding.Prohibited)
            {
                Error(
                    PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                    "Interval validity evidence cannot preserve endpoints whose semantic missing value is encoded as SQL NULL.",
                    declaration.Input.Binding.Input,
                    interval.LowerPath,
                    setting);
                continue;
            }
            if (interval.LowerNullBehavior == TemporalNullBoundBehavior.Invalid
                && lower.NullValueEncoding != PostgresRelationQueryNullValueEncoding.Prohibited
                || interval.UpperNullBehavior == TemporalNullBoundBehavior.Invalid
                && upper.NullValueEncoding != PostgresRelationQueryNullValueEncoding.Prohibited)
            {
                Error(
                    PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                    "An interval endpoint with Invalid null behavior must be physically non-null.",
                    declaration.Input.Binding.Input,
                    interval.LowerPath,
                    setting);
                continue;
            }

            builder.Add(new(
                lower.Input,
                lower.SemanticPath,
                interval.LowerNullBehavior,
                upper.Input,
                upper.SemanticPath,
                interval.UpperNullBehavior,
                interval.ValidatedCheckConstraintName));
            decisions.Add(Configuration(setting, interval.Attribution));
            decisions.Add(Configuration(
                IntervalSetting(
                    declaration.Input.Binding.Id,
                    interval.LowerPath,
                    interval.UpperPath,
                    "lowerNullBehavior"),
                interval.Attribution));
            decisions.Add(Configuration(
                IntervalSetting(
                    declaration.Input.Binding.Id,
                    interval.LowerPath,
                    interval.UpperPath,
                    "upperNullBehavior"),
                interval.Attribution));
        }
        return builder.ToImmutable();
    }

    void ValidatePlacement()
    {
        if (!ReferenceEquals(placement.Plan, placement.Inputs.FirstOrDefault()?.Plan)
            || !Equals(
                RelationQueryCompiledPlanReferenceFingerprinter.Compute(placement.Placement.Plan),
                RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                    RelationQueryCompiledPlanReference.From(placement.Plan))))
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                "PostgreSQL binding authoring requires one internally aligned authored placement.");
        }
        foreach (var source in placement.Placement.SourceInstances)
        {
            if (source.TargetProfile.Target != PostgresRelationQueryTargetProfile.Target
                || source.TargetProfile.Id != PostgresRelationQueryTargetProfile.ProfileId)
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                    $"Placed source '{source.Id.Value}' does not use the canonical PostgreSQL target profile.");
            }
        }
    }

    void ValidateTableDeclarations()
    {
        var expected = placement.Inputs
            .Where(static input => input.Binding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied)
            .Select(static input => input.Binding.Input)
            .ToHashSet();
        foreach (var group in tables.GroupBy(static table => table.Input.Binding.Input))
        {
            if (group.Count() > 1)
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                    $"Compiled input '{group.Key.Value}' has more than one PostgreSQL table binding.", group.Key);
            }
        }
        foreach (var input in expected.Except(tables.Select(static table => table.Input.Binding.Input)))
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing,
                $"Acquired compiled input '{input.Value}' has no PostgreSQL table binding.", input);
        }
        foreach (var declaration in tables)
        {
            var exact = placement.Inputs.SingleOrDefault(candidate => candidate.Binding.Input == declaration.Input.Binding.Input);
            if (exact is null
                || !ReferenceEquals(exact.Plan, declaration.Input.Plan)
                || !ReferenceEquals(exact.Placement, declaration.Input.Placement)
                || exact.Binding.Id != declaration.Input.Binding.Id
                || declaration.Input.Binding.Acquisition == RelationQuerySourceAcquisitionKind.Supplied)
            {
                Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                    "A PostgreSQL table declaration is foreign, stale, or represents a supplied relation root.",
                    declaration.Input.Binding.Input);
            }
        }
        var databaseSources = tables.Select(static table => table.Input.Source).DistinctBy(static source => source.Id).ToArray();
        if (databaseSources.Select(static source => source.ExecutionDomain).Distinct().Skip(1).Any())
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch,
                "PostgreSQL v1 table bindings must share one exact execution domain.");
        }
    }

    void ValidateExplicitSelectors(
        TableDeclaration declaration,
        IReadOnlySet<FieldPath> consumedColumns,
        IReadOnlySet<RelationQueryInputId> consumedRelationshipReferences)
    {
        foreach (var path in declaration.Columns.Keys.Where(path => !consumedColumns.Contains(path)))
        {
            Error(
                PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorUnknown,
                $"Explicit PostgreSQL column selector '{path}' is not consumed by the exact placement.",
                declaration.Input.Binding.Input,
                path,
                TableSetting(declaration.Input.Binding.Id, "columnSelector"));
        }

        foreach (var (input, reference) in declaration.RelationshipReferences
                     .Where(pair => !consumedRelationshipReferences.Contains(pair.Key)))
        {
            Error(
                PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorUnknown,
                $"Explicit PostgreSQL relationship selector '{input.Value}' is not owned by this placed input.",
                input,
                reference.Path,
                RelationshipSetting(declaration.Input.Binding.Id, input, "selector"));
        }
    }

    bool TryResolveColumn(
        TableDeclaration declaration,
        FieldPath path,
        Effective<PostgresRelationQueryColumnMappingConvention> convention,
        ISet<FieldPath> consumedColumns,
        out string column,
        out Effective<string> attribution,
        string? explicitColumn = null)
    {
        if (explicitColumn is not null)
        {
            column = explicitColumn;
            attribution = Explicit(column);
            return TryIdentifier(column, TableSetting(declaration.Input.Binding.Id, "column"), declaration.Input.Binding.Input);
        }
        if (declaration.Columns.TryGetValue(path, out var configured))
        {
            consumedColumns.Add(path);
            column = configured.Value;
            attribution = configured;
            return true;
        }
        if (convention.Value == PostgresRelationQueryColumnMappingConvention.SemanticFieldName
            && path.Segments.Length == 1
            && path.Segments[0].TryGetFieldIdentity(out var fieldName))
        {
            column = fieldName;
            attribution = new(column, convention.Origin, convention.Authority);
            return true;
        }
        column = string.Empty;
        attribution = default;
        return false;
    }

    bool TryResolveValueSemantics(
        ValueContract? contract,
        PostgresRelationQueryColumnOptions? overrides,
        RelationQueryInputId input,
        FieldPath path,
        out EffectiveValueSemantics value)
    {
        if (contract is null || !TryInferScalarType(contract.Type, out var inferredScalar))
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                $"Semantic field '{path}' has no supported scalar PostgreSQL representation.", input, path);
            value = default;
            return false;
        }
        var scalar = overrides?.ScalarType is { } explicitScalar
            ? Explicit(explicitScalar)
            : Adapter(inferredScalar);
        if (scalar.Value != inferredScalar)
        {
            Error(
                PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                $"PostgreSQL scalar type '{scalar.Value}' cannot exactly represent canonical type '{contract.Type}'. Expected '{inferredScalar}'.",
                input,
                path);
            value = default;
            return false;
        }

        var inferredMissing = contract.Presence == FieldPresence.Required
            ? PostgresRelationQueryMissingValueEncoding.Prohibited
            : PostgresRelationQueryMissingValueEncoding.SqlNull;
        var missing = overrides?.MissingValueEncoding is { } explicitMissing
            ? Explicit(explicitMissing)
            : Adapter(inferredMissing);
        var inferredNull = contract.Nullability == FieldNullability.NonNullable
            ? PostgresRelationQueryNullValueEncoding.Prohibited
            : PostgresRelationQueryNullValueEncoding.SqlNull;
        var @null = overrides?.NullValueEncoding is { } explicitNull
            ? Explicit(explicitNull)
            : Adapter(inferredNull);
        var text = overrides?.TextSemantics is { } explicitText
            ? Explicit<PostgresRelationQueryTextSemantics?>(explicitText)
            : Adapter<PostgresRelationQueryTextSemantics?>(null);
        var ordering = overrides is { HasOrderingOverride: true }
            ? Explicit(overrides.Ordering)
            : Adapter(PostgresRelationQueryOrderingCapability.None);
        var numericDomain = overrides?.NumericDomain is { } explicitNumericDomain
            ? Explicit<PostgresRelationQueryNumericDomainEvidence?>(explicitNumericDomain)
            : Adapter<PostgresRelationQueryNumericDomainEvidence?>(null);
        var decimalAggregates = overrides?.DecimalAggregates is { } explicitDecimalAggregates
            ? Explicit<PostgresRelationQueryDecimalAggregateAttestation?>(explicitDecimalAggregates)
            : Adapter<PostgresRelationQueryDecimalAggregateAttestation?>(null);
        var temporalDomain = overrides?.TemporalDomain is { } explicitTemporalDomain
            ? Explicit<PostgresRelationQueryTemporalDomainEvidence?>(explicitTemporalDomain)
            : Adapter<PostgresRelationQueryTemporalDomainEvidence?>(null);
        value = new(
            scalar,
            missing,
            @null,
            text,
            ordering,
            numericDomain,
            decimalAggregates,
            temporalDomain);
        try
        {
            PostgresRelationQueryFieldBinding.RequireValueSemantics(
                scalar.Value,
                missing.Value,
                @null.Value,
                text.Value,
                ordering.Value);
            return true;
        }
        catch (ArgumentException exception)
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SemanticEvidenceMissing,
                exception.Message, input, path);
            return false;
        }
    }

    bool TryResolveShapeFieldContract(RelationQueryPlacedInput input, FieldPath path, out ValueContract? contract)
    {
        var demanded = input.Fields.SingleOrDefault(field => field.Input.Field.Path == path);
        if (demanded is not null)
        {
            contract = demanded.Input.ValueContract;
            return contract is not null;
        }
        if (path.Segments.Length != 1 || !path.Segments[0].TryGetFieldIdentity(out var name))
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid,
                "PostgreSQL v1 identity and relationship selectors must be top-level scalar fields.",
                input.Binding.Input, path);
            contract = null;
            return false;
        }
        var shape = input.Plan.Provenance.ShapeDocuments
            .SingleOrDefault(document => document.Graph.Id == input.Shape.GraphId)
            ?.Graph.TryGetShape(input.Shape);
        if (shape is null || !shape.TryGetField(name, out var field))
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorUnknown,
                $"Semantic path '{path}' is absent from shape '{input.Shape}'.", input.Binding.Input, path);
            contract = null;
            return false;
        }
        contract = new(field.Type, cardinality: field.Cardinality, presence: field.Presence, nullability: field.Nullability);
        return true;
    }

    IdentityDeclaration? InferIdentityPath(RelationQueryPlacedInput input)
    {
        var selector = input.Binding.Identity?.SourceSelector;
        if (!string.IsNullOrWhiteSpace(selector) && !selector.StartsWith('$'))
        {
            try
            {
                return new(FieldPath.Parse(selector), Column: null, Options: null);
            }
            catch (ArgumentException)
            {
                // Fall through to semantic shape-role inference and report only if it is ambiguous.
            }
        }
        var shape = input.Plan.Provenance.ShapeDocuments
            .SingleOrDefault(document => document.Graph.Id == input.Shape.GraphId)
            ?.Graph.TryGetShape(input.Shape);
        var identities = shape?.Fields.Where(static field => field.Role == FieldRole.Identity).ToArray() ?? [];
        return identities.Length == 1
            ? new(FieldPath.FromField(identities[0].Name.Value), Column: null, Options: null)
            : null;
    }

    static bool OwnsRelationshipReference(RelationQueryPlacedInput input, RelationQueryTraversalInputContract traversal) =>
        traversal.Input.Direction == RelationshipTraversalDirection.Forward
            ? input.Binding.Binding == traversal.From && input.Shape == traversal.Definition.SourceShape
            : input.Binding.Input == traversal.Input.Id && input.Shape == traversal.Definition.SourceShape;

    static bool TryInferScalarType(TypeRef? type, out PostgresRelationQueryScalarType scalar)
    {
        scalar = type switch
        {
            ScalarTypeRef { Kind: ScalarTypeKind.Bool } => PostgresRelationQueryScalarType.Boolean,
            ScalarTypeRef { Kind: ScalarTypeKind.Int32 } => PostgresRelationQueryScalarType.Int32,
            ScalarTypeRef { Kind: ScalarTypeKind.Int64 } => PostgresRelationQueryScalarType.Int64,
            ScalarTypeRef { Kind: ScalarTypeKind.Decimal } => PostgresRelationQueryScalarType.Numeric,
            ScalarTypeRef { Kind: ScalarTypeKind.String } => PostgresRelationQueryScalarType.Text,
            ScalarTypeRef { Kind: ScalarTypeKind.Guid } => PostgresRelationQueryScalarType.Uuid,
            ScalarTypeRef { Kind: ScalarTypeKind.Date } => PostgresRelationQueryScalarType.Date,
            ScalarTypeRef { Kind: ScalarTypeKind.DateTime } => PostgresRelationQueryScalarType.Timestamp,
            ScalarTypeRef { Kind: ScalarTypeKind.Instant } => PostgresRelationQueryScalarType.TimestampWithTimeZone,
            ScalarTypeRef { Kind: ScalarTypeKind.Bytes } => PostgresRelationQueryScalarType.Bytea,
            EntityReferenceTypeRef => PostgresRelationQueryScalarType.Text,
            EnumTypeRef => PostgresRelationQueryScalarType.Text,
            _ => default
        };
        return type is ScalarTypeRef or EntityReferenceTypeRef or EnumTypeRef;
    }

    void AppendColumnDecisions(
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQueryInputId input,
        Effective<string> column,
        EffectiveValueSemantics value,
        ICollection<EffectiveConfigurationDecision> decisions)
    {
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "columnName"), column));
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "scalarType"), value.ScalarType));
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "missingValueEncoding"), value.Missing));
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "nullValueEncoding"), value.Null));
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "textSemantics"), value.Text));
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "ordering"), value.Ordering));
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "numericDomain"), value.NumericDomain));
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "decimalAggregates"), value.DecimalAggregates));
        decisions.Add(Configuration(FieldSetting(placementBinding, input, "temporalDomain"), value.TemporalDomain));
    }

    static void AppendIdentityValueDecisions(
        RelationQuerySourcePlacementBindingId placementBinding,
        EffectiveValueSemantics value,
        ICollection<EffectiveConfigurationDecision> decisions)
    {
        decisions.Add(Configuration(TableSetting(placementBinding, "identity/scalarType"), value.ScalarType));
        decisions.Add(Configuration(TableSetting(placementBinding, "identity/textSemantics"), value.Text));
        decisions.Add(Configuration(TableSetting(placementBinding, "identity/numericDomain"), value.NumericDomain));
        decisions.Add(Configuration(TableSetting(placementBinding, "identity/temporalDomain"), value.TemporalDomain));
    }

    static void AppendRelationshipValueDecisions(
        RelationQuerySourcePlacementBindingId placementBinding,
        RelationQueryInputId input,
        EffectiveValueSemantics value,
        ICollection<EffectiveConfigurationDecision> decisions)
    {
        decisions.Add(Configuration(RelationshipSetting(placementBinding, input, "scalarType"), value.ScalarType));
        decisions.Add(Configuration(RelationshipSetting(placementBinding, input, "missingValueEncoding"), value.Missing));
        decisions.Add(Configuration(RelationshipSetting(placementBinding, input, "nullValueEncoding"), value.Null));
        decisions.Add(Configuration(RelationshipSetting(placementBinding, input, "textSemantics"), value.Text));
        decisions.Add(Configuration(RelationshipSetting(placementBinding, input, "numericDomain"), value.NumericDomain));
        decisions.Add(Configuration(RelationshipSetting(placementBinding, input, "temporalDomain"), value.TemporalDomain));
    }

    string DeriveDatabaseId()
    {
        var domains = placement.Placement.SourceInstances.Select(static source => source.ExecutionDomain.Value)
            .Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal);
        return "postgres/database/" + Hash(string.Join("\n", domains));
    }

    static PostgresRelationQueryBindingId DeriveBindingId(
        PostgresRelationQueryDatabaseId database,
        RelationQueryPlanComponentFingerprint plan,
        RelationQuerySourcePlacementFingerprint placement,
        string conventionSetVersion,
        IEnumerable<PostgresRelationQueryTableBinding> tables)
        => new("postgres-binding/" + PostgresRelationQueryBindingFingerprinter.ComputeDerivedIdentity(
            database,
            plan,
            placement,
            conventionSetVersion,
            tables));

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    bool TryIdentifier(string value, string setting, RelationQueryInputId? input = null)
    {
        try
        {
            PostgresRelationQueryStorageBinding.RequireIdentifier(value, setting);
            return true;
        }
        catch (ArgumentException exception)
        {
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid, exception.Message, input, setting: setting);
            return false;
        }
    }

    void SetOnce<T>(ref Effective<T>? field, Effective<T> value, string setting)
    {
        if (field is not null)
            Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
                $"PostgreSQL setting '{setting}' is declared more than once.", setting: setting);
        else
            field = value;
    }

    internal void Duplicate(RelationQueryPlacedInput input, string setting) =>
        Error(PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate,
            $"PostgreSQL table setting '{setting}' is declared more than once.", input.Binding.Input,
            setting: TableSetting(input.Binding.Id, setting));

    internal Effective<T> CreateExplicit<T>(T value) => Explicit(value);

    bool HasErrors() => diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    bool HasErrorsFor(RelationQueryInputId input) => diagnostics.Any(diagnostic =>
        diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Input == input);

    RelationQueryArtifactAuthoringResult<PostgresRelationQueryStorageBinding> Failure() => new(null, [.. diagnostics]);

    void Error(
        string code,
        string message,
        RelationQueryInputId? input = null,
        FieldPath? path = null,
        string? setting = null) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, input, path, setting));

    Effective<T> Explicit<T>(T value) => new(value, EffectiveConfigurationOrigin.Explicit, explicitAuthority);
    static Effective<T> Scoped<T>(T value, string authority) => new(value, EffectiveConfigurationOrigin.ScopedProfile, authority);
    static Effective<T> Adapter<T>(T value) => new(value, EffectiveConfigurationOrigin.AdapterConvention, AdapterAuthority);
    Effective<string> ExplicitMarker() => Explicit("explicit");
    static Effective<string> AdapterMarker() => Adapter("inferred");

    static EffectiveConfigurationDecision Configuration<T>(string setting, Effective<T> effective) =>
        Decision(setting, effective.Origin, effective.Authority);

    static EffectiveConfigurationDecision Decision(
        string setting,
        EffectiveConfigurationOrigin origin,
        string authority) => new(setting, origin, authority);

    static string TableSetting(RelationQuerySourcePlacementBindingId binding, string setting) =>
        $"table/{Uri.EscapeDataString(binding.Value)}/{setting}";

    static string FieldSetting(RelationQuerySourcePlacementBindingId binding, RelationQueryInputId input, string setting) =>
        TableSetting(binding, $"field/{Uri.EscapeDataString(input.Value)}/{setting}");

    static string RelationshipSetting(RelationQuerySourcePlacementBindingId binding, RelationQueryInputId input, string setting) =>
        TableSetting(binding, $"relationship/{Uri.EscapeDataString(input.Value)}/{setting}");

    static string IntervalSetting(
        RelationQuerySourcePlacementBindingId binding,
        FieldPath lowerPath,
        FieldPath upperPath,
        string setting) =>
        TableSetting(
            binding,
            $"interval/{Uri.EscapeDataString(lowerPath.ToString())}/{Uri.EscapeDataString(upperPath.ToString())}/{setting}");

    internal sealed class TableDeclaration(RelationQueryPlacedInput input, Effective<string> tableName)
    {
        public RelationQueryPlacedInput Input { get; } = input;
        public Effective<string> TableName { get; } = tableName;
        public Effective<string>? Schema { get; set; }
        public Effective<PostgresRelationQueryColumnMappingConvention>? ColumnConvention { get; set; }
        public Dictionary<FieldPath, Effective<string>> Columns { get; } = [];
        public Dictionary<FieldPath, PostgresRelationQueryColumnOptions> ColumnOptions { get; } = [];
        public IdentityDeclaration? IdentityPath { get; set; }
        public Dictionary<RelationQueryInputId, RelationshipReferenceDeclaration> RelationshipReferences { get; } = [];
        public Dictionary<(FieldPath Lower, FieldPath Upper), IntervalValidityDeclaration> IntervalValidities { get; } = [];
    }

    internal readonly record struct Effective<T>(T Value, EffectiveConfigurationOrigin Origin, string Authority);
    readonly record struct EffectiveValueSemantics(
        Effective<PostgresRelationQueryScalarType> ScalarType,
        Effective<PostgresRelationQueryMissingValueEncoding> Missing,
        Effective<PostgresRelationQueryNullValueEncoding> Null,
        Effective<PostgresRelationQueryTextSemantics?> Text,
        Effective<PostgresRelationQueryOrderingCapability> Ordering,
        Effective<PostgresRelationQueryNumericDomainEvidence?> NumericDomain,
        Effective<PostgresRelationQueryDecimalAggregateAttestation?> DecimalAggregates,
        Effective<PostgresRelationQueryTemporalDomainEvidence?> TemporalDomain);
    internal sealed record IdentityDeclaration(FieldPath Path, string? Column, PostgresRelationQueryColumnOptions? Options);
    internal sealed record RelationshipReferenceDeclaration(FieldPath Path, string Column, PostgresRelationQueryColumnOptions? Options);
    internal sealed record IntervalValidityDeclaration(
        FieldPath LowerPath,
        TemporalNullBoundBehavior LowerNullBehavior,
        FieldPath UpperPath,
        TemporalNullBoundBehavior UpperNullBehavior,
        string ValidatedCheckConstraintName,
        Effective<string> Attribution);
}

/// <summary>Mutable structural configuration for one exact PostgreSQL table binding.</summary>
public class PostgresRelationQueryTableBindingBuilder
{
    readonly PostgresRelationQueryStorageBindingBuilder.TableDeclaration declaration;
    readonly PostgresRelationQueryStorageBindingBuilder owner;

    internal PostgresRelationQueryTableBindingBuilder(
        PostgresRelationQueryStorageBindingBuilder.TableDeclaration declaration,
        PostgresRelationQueryStorageBindingBuilder owner
        )
    {
        this.declaration = declaration;
        this.owner = owner;
    }

    /// <summary>Overrides the scoped or conventional PostgreSQL schema name.</summary>
    /// <param name="schemaName">Physical schema name.</param>
    /// <returns>This table builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schemaName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="schemaName"/> is empty or contains a null character.</exception>
    public virtual PostgresRelationQueryTableBindingBuilder Schema(string schemaName)
    {
        schemaName = PostgresRelationQueryStorageBinding.RequireIdentifier(schemaName, nameof(schemaName));
        if (declaration.Schema is not null)
            owner.Duplicate(declaration.Input, "schemaName");
        else
            declaration.Schema = owner.CreateExplicit(schemaName);
        return this;
    }

    /// <summary>Maps otherwise-unmapped demanded top-level fields by exact semantic field identity.</summary>
    /// <returns>This table builder.</returns>
    public virtual PostgresRelationQueryTableBindingBuilder ColumnsBySemanticPath()
    {
        SetConvention(PostgresRelationQueryColumnMappingConvention.SemanticFieldName);
        return this;
    }

    /// <summary>Requires every demanded field and relationship reference to have an explicit mapping.</summary>
    /// <returns>This table builder.</returns>
    public virtual PostgresRelationQueryTableBindingBuilder ColumnsExplicitly()
    {
        SetConvention(PostgresRelationQueryColumnMappingConvention.Explicit);
        return this;
    }

    /// <summary>Maps one semantic path to a physical PostgreSQL column.</summary>
    /// <param name="semanticPath">Semantic field path on this table's shape.</param>
    /// <param name="columnName">Physical PostgreSQL column name.</param>
    /// <param name="options">Optional value-semantic and ordering evidence.</param>
    /// <returns>This table builder.</returns>
    /// <exception cref="ArgumentException">A path or column is invalid.</exception>
    public virtual PostgresRelationQueryTableBindingBuilder Column(
        FieldPath semanticPath,
        string columnName,
        PostgresRelationQueryColumnOptions? options = null)
    {
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A PostgreSQL semantic column path cannot be empty.", nameof(semanticPath));
        columnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        if (!declaration.Columns.TryAdd(semanticPath, owner.CreateExplicit(columnName)))
        {
            owner.Duplicate(declaration.Input, "column/" + semanticPath);
        }
        if (options is not null && !declaration.ColumnOptions.TryAdd(semanticPath, options))
            owner.Duplicate(declaration.Input, "column-options/" + semanticPath);
        return this;
    }

    /// <summary>Declares the unique, non-null observation identity column.</summary>
    /// <param name="semanticPath">Semantic identity field path.</param>
    /// <param name="columnName">Physical column name, or <see langword="null"/> to use effective field mapping.</param>
    /// <param name="options">Optional scalar and text evidence.</param>
    /// <returns>This table builder.</returns>
    /// <exception cref="ArgumentException">The path or supplied column is invalid.</exception>
    public virtual PostgresRelationQueryTableBindingBuilder Identity(
        FieldPath semanticPath,
        string? columnName = null,
        PostgresRelationQueryColumnOptions? options = null)
    {
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A PostgreSQL identity path cannot be empty.", nameof(semanticPath));
        if (columnName is not null)
            columnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        if (declaration.IdentityPath is not null)
            owner.Duplicate(declaration.Input, "identityColumn");
        else
            declaration.IdentityPath = new(semanticPath, columnName, options);
        return this;
    }

    /// <summary>Overrides the physical source-reference column for one exact relationship traversal.</summary>
    /// <param name="traversalInput">Exact compiled traversal-input identity.</param>
    /// <param name="semanticPath">Canonical source-reference field path.</param>
    /// <param name="columnName">Physical PostgreSQL reference column.</param>
    /// <param name="options">Optional scalar, missing, null, and text evidence.</param>
    /// <returns>This table builder.</returns>
    /// <exception cref="ArgumentException">An identity, path, or column is invalid.</exception>
    public virtual PostgresRelationQueryTableBindingBuilder RelationshipReference(
        RelationQueryInputId traversalInput,
        FieldPath semanticPath,
        string columnName,
        PostgresRelationQueryColumnOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(traversalInput.Value))
            throw new ArgumentException("A relationship reference requires a traversal input.", nameof(traversalInput));
        if (semanticPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A relationship-reference path cannot be empty.", nameof(semanticPath));
        columnName = PostgresRelationQueryStorageBinding.RequireIdentifier(columnName, nameof(columnName));
        if (!declaration.RelationshipReferences.TryAdd(
                traversalInput,
                new(semanticPath, columnName, options)))
        {
            owner.Duplicate(declaration.Input, "relationship/" + traversalInput.Value);
        }
        return this;
    }

    /// <summary>
    /// Attests that a trusted, validated PostgreSQL check constraint guarantees one semantic interval has
    /// <c>lower &lt;= upper</c> whenever both endpoints are bounded.
    /// </summary>
    /// <param name="lowerPath">Exact semantic lower-endpoint path.</param>
    /// <param name="upperPath">Exact semantic upper-endpoint path.</param>
    /// <param name="validatedCheckConstraintName">Trusted, validated PostgreSQL check-constraint name.</param>
    /// <param name="lowerNullBehavior">Canonical meaning of a null lower endpoint.</param>
    /// <param name="upperNullBehavior">Canonical meaning of a null upper endpoint.</param>
    /// <returns>This table builder.</returns>
    /// <exception cref="ArgumentException">
    /// A path or constraint name is invalid, both paths are equal, or the endpoint pair is repeated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A null behavior is unsupported.</exception>
    public virtual PostgresRelationQueryTableBindingBuilder ValidInterval(
        FieldPath lowerPath,
        FieldPath upperPath,
        string validatedCheckConstraintName,
        TemporalNullBoundBehavior lowerNullBehavior = TemporalNullBoundBehavior.Invalid,
        TemporalNullBoundBehavior upperNullBehavior = TemporalNullBoundBehavior.Unbounded)
    {
        if (lowerPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A PostgreSQL interval lower path cannot be empty.", nameof(lowerPath));
        if (upperPath.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A PostgreSQL interval upper path cannot be empty.", nameof(upperPath));
        if (lowerPath == upperPath)
            throw new ArgumentException("A PostgreSQL interval requires distinct endpoint paths.", nameof(upperPath));
        if (!Enum.IsDefined(lowerNullBehavior))
            throw new ArgumentOutOfRangeException(nameof(lowerNullBehavior), lowerNullBehavior, "Unsupported lower null behavior.");
        if (!Enum.IsDefined(upperNullBehavior))
            throw new ArgumentOutOfRangeException(nameof(upperNullBehavior), upperNullBehavior, "Unsupported upper null behavior.");
        validatedCheckConstraintName = PostgresRelationQueryStorageBinding.RequireIdentifier(
            validatedCheckConstraintName,
            nameof(validatedCheckConstraintName));
        var attribution = owner.CreateExplicit(validatedCheckConstraintName);
        if (!declaration.IntervalValidities.TryAdd(
                (lowerPath, upperPath),
                new(
                    lowerPath,
                    lowerNullBehavior,
                    upperPath,
                    upperNullBehavior,
                    validatedCheckConstraintName,
                    attribution)))
        {
            owner.Duplicate(declaration.Input, $"interval/{lowerPath}/{upperPath}");
        }
        return this;
    }

    void SetConvention(PostgresRelationQueryColumnMappingConvention convention)
    {
        if (declaration.ColumnConvention is not null)
            owner.Duplicate(declaration.Input, "columnMappingConvention");
        else
            declaration.ColumnConvention = owner.CreateExplicit(convention);
    }
}

/// <summary>Typed fluent facade over one PostgreSQL table-binding declaration.</summary>
/// <typeparam name="T">CLR type represented by the exact placed semantic input.</typeparam>
public sealed class PostgresRelationQueryTableBindingBuilder<T> : PostgresRelationQueryTableBindingBuilder
    where T : notnull
{
    readonly RelationQueryPlacedInput<T> input;

    internal PostgresRelationQueryTableBindingBuilder(
        RelationQueryPlacedInput<T> input,
        PostgresRelationQueryStorageBindingBuilder.TableDeclaration declaration,
        PostgresRelationQueryStorageBindingBuilder owner
        ) : base(declaration, owner) => this.input = input;

    /// <inheritdoc />
    public override PostgresRelationQueryTableBindingBuilder<T> Schema(string schemaName)
    {
        base.Schema(schemaName);
        return this;
    }

    /// <inheritdoc />
    public override PostgresRelationQueryTableBindingBuilder<T> ColumnsBySemanticPath()
    {
        base.ColumnsBySemanticPath();
        return this;
    }

    /// <inheritdoc />
    public override PostgresRelationQueryTableBindingBuilder<T> ColumnsExplicitly()
    {
        base.ColumnsExplicitly();
        return this;
    }

    /// <summary>Maps one typed semantic field to a physical PostgreSQL column.</summary>
    /// <typeparam name="TValue">CLR value selected by the readable property chain.</typeparam>
    /// <param name="selector">Typed semantic field selector.</param>
    /// <param name="columnName">Physical PostgreSQL column name.</param>
    /// <param name="options">Optional value-semantic and ordering evidence.</param>
    /// <returns>This typed table builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The selector or column is invalid.</exception>
    /// <exception cref="InvalidOperationException">CLR metadata cannot resolve the selected path.</exception>
    public PostgresRelationQueryTableBindingBuilder<T> Column<TValue>(
        Expression<Func<T, TValue>> selector,
        string columnName,
        PostgresRelationQueryColumnOptions? options = null)
    {
        base.Column(input.ResolveFieldPath(selector), columnName, options);
        return this;
    }

    /// <summary>Declares a typed unique, non-null observation identity column.</summary>
    /// <typeparam name="TValue">CLR identity value selected by the readable property chain.</typeparam>
    /// <param name="selector">Typed semantic identity selector.</param>
    /// <param name="columnName">Physical column name, or <see langword="null"/> to use effective mapping.</param>
    /// <param name="options">Optional scalar and text evidence.</param>
    /// <returns>This typed table builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The selector or supplied column is invalid.</exception>
    /// <exception cref="InvalidOperationException">CLR metadata cannot resolve the selected path.</exception>
    public PostgresRelationQueryTableBindingBuilder<T> Identity<TValue>(
        Expression<Func<T, TValue>> selector,
        string? columnName = null,
        PostgresRelationQueryColumnOptions? options = null)
    {
        base.Identity(input.ResolveFieldPath(selector), columnName, options);
        return this;
    }

    /// <summary>Overrides a typed relationship-reference column for one exact traversal input.</summary>
    /// <typeparam name="TValue">CLR reference value selected by the readable property chain.</typeparam>
    /// <param name="traversalInput">Exact compiled traversal-input identity.</param>
    /// <param name="selector">Typed semantic source-reference selector.</param>
    /// <param name="columnName">Physical PostgreSQL reference column.</param>
    /// <param name="options">Optional scalar, missing, null, and text evidence.</param>
    /// <returns>This typed table builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, selector, or column is invalid.</exception>
    /// <exception cref="InvalidOperationException">CLR metadata cannot resolve the selected path.</exception>
    public PostgresRelationQueryTableBindingBuilder<T> RelationshipReference<TValue>(
        RelationQueryInputId traversalInput,
        Expression<Func<T, TValue>> selector,
        string columnName,
        PostgresRelationQueryColumnOptions? options = null)
    {
        base.RelationshipReference(traversalInput, input.ResolveFieldPath(selector), columnName, options);
        return this;
    }

    /// <summary>
    /// Attests through typed endpoint selectors that a trusted, validated PostgreSQL check constraint guarantees
    /// <c>lower &lt;= upper</c> whenever both interval endpoints are bounded.
    /// </summary>
    /// <typeparam name="TLower">CLR lower-endpoint type.</typeparam>
    /// <typeparam name="TUpper">CLR upper-endpoint type.</typeparam>
    /// <param name="lowerSelector">Typed semantic lower-endpoint selector.</param>
    /// <param name="upperSelector">Typed semantic upper-endpoint selector.</param>
    /// <param name="validatedCheckConstraintName">Trusted, validated PostgreSQL check-constraint name.</param>
    /// <param name="lowerNullBehavior">Canonical meaning of a null lower endpoint.</param>
    /// <param name="upperNullBehavior">Canonical meaning of a null upper endpoint.</param>
    /// <returns>This typed table builder.</returns>
    /// <exception cref="ArgumentNullException">An endpoint selector is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A selector, constraint name, or endpoint pair is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A null behavior is unsupported.</exception>
    /// <exception cref="InvalidOperationException">CLR metadata cannot resolve an endpoint path.</exception>
    public PostgresRelationQueryTableBindingBuilder<T> ValidInterval<TLower, TUpper>(
        Expression<Func<T, TLower>> lowerSelector,
        Expression<Func<T, TUpper>> upperSelector,
        string validatedCheckConstraintName,
        TemporalNullBoundBehavior lowerNullBehavior = TemporalNullBoundBehavior.Invalid,
        TemporalNullBoundBehavior upperNullBehavior = TemporalNullBoundBehavior.Unbounded)
    {
        base.ValidInterval(
            input.ResolveFieldPath(lowerSelector),
            input.ResolveFieldPath(upperSelector),
            validatedCheckConstraintName,
            lowerNullBehavior,
            upperNullBehavior);
        return this;
    }
}
