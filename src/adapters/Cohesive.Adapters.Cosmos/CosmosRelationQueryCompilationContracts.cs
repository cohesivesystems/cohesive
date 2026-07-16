using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Options that participate in canonical Cosmos SQL artifact identity.</summary>
public sealed record CosmosRelationQueryCompilerOptions
{
    /// <summary>Current canonical Cosmos SQL compiler profile.</summary>
    public const string CurrentCompilerProfile = "cohesive.adapters.cosmos.sql/compiler-v1";

    /// <summary>Creates canonical Cosmos compiler options.</summary>
    /// <param name="compilerProfile">Stable compiler implementation/profile identity.</param>
    /// <param name="conventionSetVersion">Stable convention set applied to non-semantic lowering decisions.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compilerProfile"/> or <paramref name="conventionSetVersion"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="compilerProfile"/> or <paramref name="conventionSetVersion"/> is empty or white space.
    /// </exception>
    public CosmosRelationQueryCompilerOptions(
        string compilerProfile = CurrentCompilerProfile,
        string conventionSetVersion = CosmosRelationQueryStorageBinding.SemanticPathConventionSet)
    {
        CompilerProfile = Guard.RequireNotNullOrWhiteSpace(compilerProfile);
        ConventionSetVersion = Guard.RequireNotNullOrWhiteSpace(conventionSetVersion);
    }

    /// <summary>Stable compiler implementation/profile identity.</summary>
    public string CompilerProfile { get; }

    /// <summary>Stable convention set applied to non-semantic lowering decisions.</summary>
    public string ConventionSetVersion { get; }
}

/// <summary>Deterministic identity of one normalized canonical Cosmos SQL artifact.</summary>
public sealed record CosmosRelationQueryArtifactFingerprint
{
    /// <summary>Creates an artifact fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public CosmosRelationQueryArtifactFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>One exact compiled semantic field selected from a Cosmos document.</summary>
public sealed record CosmosRelationQuerySelectedField
{
    /// <summary>Creates selected-field metadata.</summary>
    /// <param name="input">Exact compiled field-input identity.</param>
    /// <param name="field">Canonical graph-qualified semantic field.</param>
    /// <param name="documentPath">
    /// Physical Cosmos selector relative to the configured document root, including element segments for expanded arrays.
    /// </param>
    /// <exception cref="ArgumentException">An identity or path is invalid.</exception>
    public CosmosRelationQuerySelectedField(
        RelationQueryInputId input,
        RelationQueryFieldReference field,
        FieldPath documentPath)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A selected Cosmos field requires a compiled input identity.", nameof(input));
        if (string.IsNullOrWhiteSpace(field.Shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(field.Shape.ShapeId.Value)
            || field.Path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A selected Cosmos field requires a graph-qualified semantic field.", nameof(field));
        }
        Input = input;
        Field = field;
        DocumentPath = CosmosRelationQueryStorageBinding.RequireDocumentSelectorPath(documentPath, nameof(documentPath));
    }

    /// <summary>Exact compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Canonical graph-qualified semantic field.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Physical Cosmos selector relative to the configured document root.</summary>
    public FieldPath DocumentPath { get; }
}

/// <summary>Physical Cosmos JSON encoding retained for one canonical result value.</summary>
public enum CosmosRelationQueryResultValueEncoding
{
    /// <summary>A JSON Boolean decoded as canonical <see cref="ScalarTypeKind.Bool"/>.</summary>
    JsonBoolean = 0,

    /// <summary>An exactly representable JSON number decoded as canonical <see cref="ScalarTypeKind.Int32"/>.</summary>
    JsonInt32 = 1,

    /// <summary>A JSON string decoded without further scalar normalization.</summary>
    JsonString = 2,

    /// <summary>A canonical GUID string.</summary>
    CanonicalGuidString = 3,

    /// <summary>A canonical ISO date string.</summary>
    CanonicalDateString = 4,

    /// <summary>A round-trip date-time-offset string interpreted using the retained temporal contract.</summary>
    RoundTripDateTimeOffsetString = 5,

    /// <summary>A row count proven to remain inside Cosmos's exact integer domain.</summary>
    ExactCountInteger = 6
}

/// <summary>Binding from one safe SQL result alias to one canonical output field.</summary>
public sealed record CosmosRelationQueryResultFieldBinding
{
    /// <summary>Creates a result-field binding.</summary>
    /// <param name="alias">Safe SQL result alias.</param>
    /// <param name="field">Canonical output field reconstructed from the alias.</param>
    /// <param name="valueContract">Exact semantic value contract used to decode and validate the result.</param>
    /// <param name="encoding">Physical Cosmos JSON encoding expected for the result value.</param>
    /// <param name="assignment">Producing canonical assignment, or <see langword="null"/> for a direct source field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="valueContract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An alias, field, or supplied assignment identity is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="encoding"/> is unsupported.</exception>
    public CosmosRelationQueryResultFieldBinding(
        string alias,
        RelationQueryFieldReference field,
        ExprValueContract valueContract,
        CosmosRelationQueryResultValueEncoding encoding,
        QueryAssignmentId? assignment = null)
    {
        if (!Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unsupported Cosmos result-value encoding.");
        Alias = CosmosSqlNames.RequireIdentifier(alias, nameof(alias));
        if (string.IsNullOrWhiteSpace(field.Shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(field.Shape.ShapeId.Value)
            || field.Path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A result binding requires a graph-qualified canonical field.", nameof(field));
        }
        if (assignment is { } assignmentId && string.IsNullOrWhiteSpace(assignmentId.Value))
            throw new ArgumentException("A result binding assignment identity cannot be empty.", nameof(assignment));
        Field = field;
        ValueContract = Guard.RequireNotNull(valueContract);
        Encoding = encoding;
        Assignment = assignment;
    }

    /// <summary>Safe SQL result alias.</summary>
    public string Alias { get; }

    /// <summary>Canonical output field reconstructed from the alias.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Exact semantic contract used to decode and validate the physical result value.</summary>
    public ExprValueContract ValueContract { get; }

    /// <summary>Physical Cosmos JSON encoding expected for the result value.</summary>
    public CosmosRelationQueryResultValueEncoding Encoding { get; }

    /// <summary>Producing canonical assignment, or <see langword="null"/> for a direct source field.</summary>
    public QueryAssignmentId? Assignment { get; }
}

/// <summary>Binding from a hidden SQL alias to canonical relation-output identity.</summary>
public sealed record CosmosRelationQueryResultIdentityBinding
{
    /// <summary>Creates relation-result identity metadata.</summary>
    /// <param name="alias">Safe hidden SQL result alias.</param>
    /// <param name="canonicalKey">Whether the alias evaluates the relation's explicit canonical output key.</param>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is invalid.</exception>
    public CosmosRelationQueryResultIdentityBinding(string alias, bool canonicalKey)
    {
        Alias = CosmosSqlNames.RequireIdentifier(alias, nameof(alias));
        CanonicalKey = canonicalKey;
    }

    /// <summary>Safe hidden SQL result alias.</summary>
    public string Alias { get; }

    /// <summary>Whether the alias evaluates the relation's explicit canonical output key.</summary>
    public bool CanonicalKey { get; }
}

/// <summary>Binding from one command-template slot to one canonical invocation parameter.</summary>
public sealed record CosmosRelationQueryParameterBinding
{
    /// <summary>Creates canonical parameter-binding metadata.</summary>
    /// <param name="sqlName">Allocated SQL parameter name.</param>
    /// <param name="definition">Canonical parameter declaration.</param>
    /// <param name="valueContract">Exact effective compiled value contract after default application.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="valueContract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sqlName"/> is invalid or <paramref name="valueContract"/> does not match the declaration.
    /// </exception>
    public CosmosRelationQueryParameterBinding(
        string sqlName,
        QueryParameterDefinition definition,
        ExprValueContract valueContract)
    {
        SqlName = CosmosSqlNames.RequireParameterName(sqlName, nameof(sqlName));
        Definition = Guard.RequireNotNull(definition);
        ValueContract = Guard.RequireNotNull(valueContract);
        if (ValueContract != Definition.EffectiveValueContract)
            throw new ArgumentException("The parameter value contract must match the canonical declaration.", nameof(valueContract));
    }

    /// <summary>Allocated SQL parameter name.</summary>
    public string SqlName { get; }

    /// <summary>Canonical parameter declaration.</summary>
    public QueryParameterDefinition Definition { get; }

    /// <summary>Exact effective compiled value contract after default application.</summary>
    public ExprValueContract ValueContract { get; }

    /// <summary>Stable canonical parameter identity.</summary>
    public QueryParameterId Parameter => Definition.Id;
}

/// <summary>Exact offset-page contract retained by a compiled artifact.</summary>
public sealed record CosmosRelationQueryPagingContract
{
    /// <summary>Creates paging metadata.</summary>
    /// <param name="offset">Number of ordered rows skipped.</param>
    /// <param name="limit">Maximum number of rows returned.</param>
    /// <param name="stableUniquePath">Physical final ordering path proving stable page boundaries.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="limit"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="stableUniquePath"/> is invalid.</exception>
    public CosmosRelationQueryPagingContract(int offset, int limit, FieldPath stableUniquePath)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "A Cosmos page offset cannot be negative.");
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A Cosmos page limit must be positive.");
        Offset = offset;
        Limit = limit;
        StableUniquePath = CosmosRelationQueryStorageBinding.RequirePropertyPath(
            stableUniquePath,
            nameof(stableUniquePath));
    }

    /// <summary>Number of ordered rows skipped.</summary>
    public int Offset { get; }

    /// <summary>Maximum number of rows returned.</summary>
    public int Limit { get; }

    /// <summary>Physical final ordering path proving stable page boundaries.</summary>
    public FieldPath StableUniquePath { get; }
}

/// <summary>One exact canonical result branch compiled to a reusable Cosmos SQL command template.</summary>
public sealed class CosmosRelationQueryCompiledArtifact
{
    internal CosmosRelationQueryCompiledArtifact(
        RelationQueryNativeResultBranch branch,
        CosmosSqlCommandTemplate statement,
        CosmosRelationQueryStorageBinding storageBinding,
        ImmutableArray<CosmosRelationQuerySelectedField> selectedFields,
        ImmutableArray<CosmosRelationQueryResultFieldBinding> resultFields,
        CosmosRelationQueryResultIdentityBinding? resultIdentity,
        ImmutableArray<CosmosRelationQueryParameterBinding> parameters,
        CosmosRelationQueryPagingContract? paging,
        RelationQueryNativeCompilationProvenance provenance,
        CosmosRelationQueryArtifactFingerprint fingerprint)
    {
        Branch = Guard.RequireNotNull(branch);
        Statement = Guard.RequireNotNull(statement);
        StorageBinding = Guard.RequireNotNull(storageBinding);
        SelectedFields = NormalizeAndOrder(
            selectedFields,
            static field => field.Input.Value,
            nameof(selectedFields));
        ResultFields = NormalizePreservingOrder(
            resultFields,
            static field => field.Alias,
            nameof(resultFields));
        ResultIdentity = resultIdentity;
        Parameters = NormalizePreservingOrder(
            parameters,
            static parameter => parameter.SqlName,
            nameof(parameters));
        Paging = paging;
        Provenance = Guard.RequireNotNull(provenance);
        Fingerprint = Guard.RequireNotNull(fingerprint);

        var runtimeSlots = Statement.Parameters
            .Where(static slot => slot.Kind == CosmosSqlParameterBindingKind.Runtime)
            .Select(static slot => slot.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!runtimeSlots.SetEquals(Parameters.Select(static parameter => parameter.SqlName)))
            throw new ArgumentException("Canonical parameter bindings must cover every runtime statement slot.", nameof(parameters));
        if (ResultIdentity is { } identity
            && ResultFields.Any(field => string.Equals(field.Alias, identity.Alias, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The hidden result identity alias cannot collide with a result field alias.", nameof(resultIdentity));
        }
        if (Branch.Id != Provenance.Branch)
            throw new ArgumentException("Artifact branch and provenance branch identities must match.", nameof(provenance));
    }

    /// <summary>Canonical terminal branch compiled by this artifact.</summary>
    public RelationQueryNativeResultBranch Branch { get; }

    /// <summary>Reusable low-level Cosmos SQL command template shared with direct builder usage.</summary>
    public CosmosSqlCommandTemplate Statement { get; }

    /// <summary>Exact Cosmos storage binding used by compilation.</summary>
    public CosmosRelationQueryStorageBinding StorageBinding { get; }

    /// <summary>Exact compiled semantic fields selected from the document.</summary>
    public ImmutableArray<CosmosRelationQuerySelectedField> SelectedFields { get; }

    /// <summary>Bindings sufficient to reconstruct canonical result fields without rescanning IR.</summary>
    public ImmutableArray<CosmosRelationQueryResultFieldBinding> ResultFields { get; }

    /// <summary>Hidden canonical relation identity binding, or <see langword="null"/> for a query result.</summary>
    public CosmosRelationQueryResultIdentityBinding? ResultIdentity { get; }

    /// <summary>Canonical invocation parameters in allocated SQL-name order.</summary>
    public ImmutableArray<CosmosRelationQueryParameterBinding> Parameters { get; }

    /// <summary>Offset-page guarantees, or <see langword="null"/> for an unpaged branch.</summary>
    public CosmosRelationQueryPagingContract? Paging { get; }

    /// <summary>Exact target-neutral compilation provenance.</summary>
    public RelationQueryNativeCompilationProvenance Provenance { get; }

    /// <summary>Deterministic identity of all semantic and physical artifact inputs.</summary>
    public CosmosRelationQueryArtifactFingerprint Fingerprint { get; }

    /// <summary>Binds one canonical invocation without recompiling the static plan or SQL template.</summary>
    /// <param name="parameters">
    /// Invocation values keyed by canonical parameter identity. An undefined value is treated as absent so the
    /// parameter declaration's default, when present, can be applied.
    /// </param>
    /// <returns>A concrete Cosmos SQL statement suitable for <see cref="CosmosSqlStatement.ToQueryDefinition"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An unknown parameter is supplied, a required parameter is missing, or an effective value violates its compiled
    /// contract or cannot be represented by a Cosmos SQL parameter.
    /// </exception>
    public CosmosSqlStatement Bind(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var expected = Parameters.Select(static parameter => parameter.Parameter).ToHashSet();
        var unknown = parameters.Keys.Where(parameter => !expected.Contains(parameter)).ToArray();
        if (unknown.Length != 0)
            throw new ArgumentException("The invocation contains a parameter absent from this compiled artifact.", nameof(parameters));

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (var binding in Parameters)
        {
            ObservationValue value;
            if (parameters.TryGetValue(binding.Parameter, out var supplied)
                && supplied.Kind != ObservationValueKind.Undefined)
            {
                value = supplied;
            }
            else if (binding.Definition.DefaultKind == QueryParameterDefaultKind.Value)
            {
                value = binding.Definition.DefaultValue ?? ObservationValue.Null;
            }
            else
            {
                throw new ArgumentException(
                    $"Canonical query parameter '{binding.Parameter.Value}' is required by this artifact.",
                    nameof(parameters));
            }
            if (!binding.ValueContract.IsSatisfiedByConstant(value))
            {
                throw new ArgumentException(
                    $"Canonical query parameter '{binding.Parameter.Value}' does not satisfy its effective compiled value contract.",
                    nameof(parameters));
            }
            values.Add(binding.Parameter.Value, value);
        }
        return Statement.Bind(values);
    }

    static ImmutableArray<T> NormalizeAndOrder<T>(
        ImmutableArray<T> values,
        Func<T, string> key,
        string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null))
            throw new ArgumentException("Artifact metadata cannot contain null entries.", parameterName);
        if (normalized.GroupBy(key, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Artifact metadata identities cannot be repeated.", parameterName);
        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }

    static ImmutableArray<T> NormalizePreservingOrder<T>(
        ImmutableArray<T> values,
        Func<T, string> key,
        string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null))
            throw new ArgumentException("Artifact metadata cannot contain null entries.", parameterName);
        if (normalized.GroupBy(key, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Artifact metadata identities cannot be repeated.", parameterName);
        return normalized;
    }
}

/// <summary>Structured result of compiling selected canonical branches to Cosmos SQL.</summary>
public sealed class CosmosRelationQueryCompilationResult
{
    internal CosmosRelationQueryCompilationResult(
        RelationQueryNativeCompilationStatus status,
        ImmutableArray<CosmosRelationQueryCompiledArtifact> artifacts,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported native compilation status.");
        var normalizedArtifacts = artifacts.IsDefault ? [] : artifacts;
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (normalizedArtifacts.Any(static artifact => artifact is null))
            throw new ArgumentException("Compilation artifacts cannot contain null entries.", nameof(artifacts));
        if (normalizedArtifacts.GroupBy(static artifact => artifact.Branch.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Compilation artifacts cannot repeat a result branch.", nameof(artifacts));
        if (normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Compilation diagnostics cannot contain null entries.", nameof(diagnostics));
        var hasErrors = normalizedDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (status == RelationQueryNativeCompilationStatus.Exact && (normalizedArtifacts.IsDefaultOrEmpty || hasErrors))
            throw new ArgumentException("Exact compilation requires artifacts and no error diagnostics.", nameof(status));
        if (status != RelationQueryNativeCompilationStatus.Exact && !hasErrors)
            throw new ArgumentException("Unsuccessful compilation requires an error diagnostic.", nameof(status));

        Status = status;
        Artifacts = [.. normalizedArtifacts.OrderBy(static artifact => artifact.Branch.Id.Value, StringComparer.Ordinal)];
        Diagnostics =
        [
            .. normalizedDiagnostics
                .OrderBy(static diagnostic => diagnostic.Branch?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Node?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Requirement?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];
    }

    /// <summary>Overall native-compilation outcome.</summary>
    public RelationQueryNativeCompilationStatus Status { get; }

    /// <summary>Successfully compiled branch artifacts in stable branch order.</summary>
    public ImmutableArray<CosmosRelationQueryCompiledArtifact> Artifacts { get; }

    /// <summary>Structured diagnostics in deterministic attribution order.</summary>
    public ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics { get; }

    /// <summary>Whether every selected branch compiled exactly.</summary>
    public bool IsSuccessful => Status == RelationQueryNativeCompilationStatus.Exact;
}

/// <summary>Stable Cosmos-specific native-compilation diagnostic codes.</summary>
public static class CosmosRelationQueryCompilationDiagnosticCodes
{
    /// <summary>The Cosmos storage binding conflicts with placement or target facts.</summary>
    public const string StorageBindingMismatch = "REL2210";

    /// <summary>The selected branch does not have a supported single-container topology.</summary>
    public const string UnsupportedBranchTopology = "REL2211";

    /// <summary>A logical operator is unsupported or appears in an inexact pipeline position.</summary>
    public const string UnsupportedLogicalOperator = "REL2212";

    /// <summary>A canonical expression cannot be lowered exactly to the supported Cosmos SQL closure.</summary>
    public const string UnsupportedExpression = "REL2213";

    /// <summary>A compiled field input lacks an exact Cosmos document selector.</summary>
    public const string FieldBindingMissing = "REL2214";

    /// <summary>Missing/null or ordering semantics cannot be proven exact.</summary>
    public const string GuaranteeUnavailable = "REL2215";

    /// <summary>A canonical invocation parameter cannot be represented by the reusable template contract.</summary>
    public const string ParameterUnsupported = "REL2216";

    /// <summary>Aggregate semantics cannot be proven exact for the selected branch.</summary>
    public const string AggregateUnsupported = "REL2217";

    /// <summary>Offset-page stability cannot be proven from the binding and final ordering key.</summary>
    public const string PagingUnstable = "REL2218";

    /// <summary>Artifact construction failed an internal consistency check.</summary>
    public const string ArtifactInvalid = "REL2219";

    /// <summary>The requested runtime result observability cannot be produced by Cosmos SQL v1.</summary>
    public const string ResultObservabilityUnsupported = "REL2220";

    /// <summary>A relation terminal requires root, cardinality, key, or invariant semantics absent from the v1 artifact contract.</summary>
    public const string RelationTerminalUnsupported = "REL2221";
}
