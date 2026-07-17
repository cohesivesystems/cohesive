using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Elastic.Clients.Elasticsearch;

namespace Cohesive.Adapters.Elastic;

/// <summary>Options that participate in canonical Elasticsearch artifact identity.</summary>
public sealed record ElasticRelationQueryCompilerOptions
{
    /// <summary>Current canonical Elasticsearch compiler profile.</summary>
    public const string CurrentCompilerProfile = "cohesive.adapters.elastic/compiler-v1/sdk-request-materializer-v1";

    /// <summary>Current framework-wide lowering convention identity.</summary>
    public const string DefaultConventionSetVersion = ElasticRelationQueryStorageBinding.SemanticPathConventionSet;

    /// <summary>Creates canonical Elasticsearch compiler options.</summary>
    /// <param name="compilerProfile">Stable compiler implementation/profile identity.</param>
    /// <param name="conventionSetVersion">Stable convention set applied to non-semantic lowering decisions.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public ElasticRelationQueryCompilerOptions(
        string compilerProfile = CurrentCompilerProfile,
        string conventionSetVersion = DefaultConventionSetVersion)
    {
        CompilerProfile = Guard.RequireNotNullOrWhiteSpace(compilerProfile);
        ConventionSetVersion = Guard.RequireNotNullOrWhiteSpace(conventionSetVersion);
    }

    /// <summary>Stable compiler implementation/profile identity.</summary>
    public string CompilerProfile { get; }

    /// <summary>Stable convention set applied to non-semantic lowering decisions.</summary>
    public string ConventionSetVersion { get; }
}

/// <summary>Deterministic identity of one normalized canonical Elasticsearch artifact.</summary>
public sealed record ElasticRelationQueryArtifactFingerprint
{
    /// <summary>Creates an artifact fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public ElasticRelationQueryArtifactFingerprint(string algorithm, string canonicalization, string value)
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

/// <summary>One exact compiled semantic field consumed by an Elasticsearch request.</summary>
public sealed record ElasticRelationQuerySelectedField
{
    /// <summary>Creates selected-field metadata.</summary>
    /// <param name="input">Exact compiled field-input identity.</param>
    /// <param name="field">Canonical graph-qualified semantic field.</param>
    /// <param name="sourceField">Physical <c>_source</c> selector used for result retrieval, or <see langword="null"/>.</param>
    /// <param name="queryFields">Exact physical indexed fields used for filtering, ordering, or aggregation.</param>
    /// <exception cref="ArgumentException">An identity, semantic field, or supplied physical path is invalid.</exception>
    public ElasticRelationQuerySelectedField(
        RelationQueryInputId input,
        RelationQueryFieldReference field,
        FieldPath? sourceField,
        ImmutableArray<FieldPath> queryFields = default)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A selected Elasticsearch field requires a compiled input identity.", nameof(input));
        if (string.IsNullOrWhiteSpace(field.Shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(field.Shape.ShapeId.Value)
            || field.Path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A selected Elasticsearch field requires a graph-qualified semantic field.", nameof(field));
        }
        var normalizedQueryFields = queryFields.IsDefault ? [] : queryFields;
        if (sourceField is null && normalizedQueryFields.IsDefaultOrEmpty)
            throw new ArgumentException("A selected Elasticsearch field requires a retrieval or query field.", nameof(sourceField));
        Input = input;
        Field = field;
        SourceField = sourceField is { } source ? RequirePhysicalPath(source, nameof(sourceField)) : null;
        QueryFields =
        [
            .. normalizedQueryFields
                .Select(query => RequirePhysicalPath(query, nameof(queryFields)))
                .Distinct()
                .OrderBy(ElasticRelationQueryStorageBinding.FieldPathKey, StringComparer.Ordinal)
        ];
        if (QueryFields.Length != normalizedQueryFields.Length)
            throw new ArgumentException("Selected Elasticsearch query fields cannot be repeated.", nameof(queryFields));
    }

    /// <summary>Exact compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Canonical graph-qualified semantic field.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Physical <c>_source</c> selector, or <see langword="null"/> when the field is not retrieved.</summary>
    public FieldPath? SourceField { get; }

    /// <summary>Exact physical indexed query fields in deterministic physical-path order.</summary>
    public ImmutableArray<FieldPath> QueryFields { get; }

    internal static FieldPath RequirePhysicalPath(FieldPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty
            || path.Segments.Any(static segment => segment.Kind != SegmentKind.Field
                                                    || string.IsNullOrWhiteSpace(segment.Segment)))
        {
            throw new ArgumentException("An Elasticsearch physical field path must contain only named field segments.", parameterName);
        }
        return path;
    }

    internal static string PhysicalName(FieldPath path) =>
        string.Join('.', RequirePhysicalPath(path, nameof(path)).Segments.Select(static segment => segment.Segment));
}

/// <summary>Physical location from which one canonical result value is decoded.</summary>
public enum ElasticRelationQueryResultSourceKind
{
    /// <summary>The value is decoded from one filtered <c>_source</c> field.</summary>
    SourceField = 0,

    /// <summary>The value is retained as a canonical constant in artifact metadata.</summary>
    Constant = 1,

    /// <summary>The value is decoded from Elasticsearch's exact total-hit count.</summary>
    ExactTotalHits = 2,

    /// <summary>The value is decoded from one composite-aggregation bucket key.</summary>
    CompositeKey = 3,

    /// <summary>The value is decoded from one composite-aggregation bucket document count.</summary>
    CompositeDocumentCount = 4
}

/// <summary>Physical Elasticsearch JSON encoding expected for one canonical result value.</summary>
public enum ElasticRelationQueryResultValueEncoding
{
    /// <summary>A JSON Boolean.</summary>
    JsonBoolean = 0,

    /// <summary>A JSON integer represented in the canonical signed 64-bit domain.</summary>
    JsonInt64 = 1,

    /// <summary>A finite JSON binary floating-point number.</summary>
    JsonDouble = 2,

    /// <summary>A JSON string decoded without normalization.</summary>
    JsonString = 3,

    /// <summary>A canonical temporal value encoded as a JSON string.</summary>
    CanonicalTemporalString = 4,

    /// <summary>An exact non-negative Elasticsearch hit or bucket count.</summary>
    ExactCountInt64 = 5
}

/// <summary>Binding sufficient to reconstruct one canonical output field from an Elasticsearch response.</summary>
public sealed record ElasticRelationQueryResultFieldBinding
{
    /// <summary>Creates a result-field binding.</summary>
    /// <param name="field">Canonical output field.</param>
    /// <param name="valueContract">Exact semantic result value contract.</param>
    /// <param name="sourceKind">Physical result source.</param>
    /// <param name="encoding">Expected physical JSON encoding.</param>
    /// <param name="physicalName">Physical source selector or composite-key name, when required.</param>
    /// <param name="constant">Canonical constant when <paramref name="sourceKind"/> is <see cref="ElasticRelationQueryResultSourceKind.Constant"/>.</param>
    /// <param name="assignment">Producing canonical assignment, or <see langword="null"/> for a direct source field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="valueContract"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The field or source-specific metadata is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceKind"/> or <paramref name="encoding"/> is unsupported.</exception>
    public ElasticRelationQueryResultFieldBinding(
        RelationQueryFieldReference field,
        ExprValueContract valueContract,
        ElasticRelationQueryResultSourceKind sourceKind,
        ElasticRelationQueryResultValueEncoding encoding,
        string? physicalName = null,
        ObservationValue? constant = null,
        QueryAssignmentId? assignment = null)
    {
        var normalizedValueContract = Guard.RequireNotNull(valueContract);
        if (string.IsNullOrWhiteSpace(field.Shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(field.Shape.ShapeId.Value)
            || field.Path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An Elasticsearch result binding requires a graph-qualified canonical field.", nameof(field));
        }
        if (!Enum.IsDefined(sourceKind))
            throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, "Unsupported Elasticsearch result source.");
        if (!Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unsupported Elasticsearch result encoding.");
        if (assignment is { } assignmentId && string.IsNullOrWhiteSpace(assignmentId.Value))
            throw new ArgumentException("A result assignment identity cannot be empty.", nameof(assignment));
        var requiresPhysicalName = sourceKind is ElasticRelationQueryResultSourceKind.SourceField
            or ElasticRelationQueryResultSourceKind.CompositeKey;
        if (requiresPhysicalName != !string.IsNullOrWhiteSpace(physicalName))
            throw new ArgumentException("The selected Elasticsearch result source conflicts with its physical name.", nameof(physicalName));
        if ((sourceKind == ElasticRelationQueryResultSourceKind.Constant) != (constant is not null))
            throw new ArgumentException("Only a constant result source may retain a canonical constant.", nameof(constant));
        if (constant is { } value && !ElasticQueryValueTemplate.IsSupportedScalar(value))
            throw new ArgumentException("An Elasticsearch result constant must be a supported scalar value.", nameof(constant));
        if (constant is { } constantValue && !normalizedValueContract.IsSatisfiedByConstant(constantValue))
        {
            throw new ArgumentException(
                "An Elasticsearch result constant must satisfy its exact semantic value contract.",
                nameof(constant));
        }
        if (normalizedValueContract.Cardinality != FieldCardinality.Single)
            throw new ArgumentException("An Elasticsearch result binding requires a single-valued contract.", nameof(valueContract));

        var countSource = sourceKind is ElasticRelationQueryResultSourceKind.ExactTotalHits
            or ElasticRelationQueryResultSourceKind.CompositeDocumentCount;
        if (countSource)
        {
            if (encoding != ElasticRelationQueryResultValueEncoding.ExactCountInt64
                || normalizedValueContract.GetEffectiveType() is not ScalarTypeRef { Kind: ScalarTypeKind.Int64 }
                || normalizedValueContract.Presence != FieldPresence.Required
                || normalizedValueContract.Nullability != FieldNullability.NonNullable)
            {
                throw new ArgumentException(
                    "Elasticsearch hit and bucket counts require an exact required, non-null Int64 count encoding.",
                    nameof(encoding));
            }
        }
        else
        {
            var expectedEncoding = ExpectedValueEncoding(normalizedValueContract);
            if (encoding != expectedEncoding)
            {
                throw new ArgumentException(
                    $"Elasticsearch result encoding '{encoding}' does not match the semantic value contract encoding '{expectedEncoding}'.",
                    nameof(encoding));
            }
        }

        Field = field;
        ValueContract = normalizedValueContract;
        SourceKind = sourceKind;
        Encoding = encoding;
        PhysicalName = physicalName;
        Constant = constant;
        Assignment = assignment;
    }

    /// <summary>Canonical output field.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Exact semantic result value contract.</summary>
    public ExprValueContract ValueContract { get; }

    /// <summary>Physical result source.</summary>
    public ElasticRelationQueryResultSourceKind SourceKind { get; }

    /// <summary>Expected physical JSON encoding.</summary>
    public ElasticRelationQueryResultValueEncoding Encoding { get; }

    /// <summary>Physical source selector or composite-key name, or <see langword="null"/>.</summary>
    public string? PhysicalName { get; }

    /// <summary>Retained canonical constant, or <see langword="null"/>.</summary>
    public ObservationValue? Constant { get; }

    /// <summary>Producing canonical assignment, or <see langword="null"/>.</summary>
    public QueryAssignmentId? Assignment { get; }

    static ElasticRelationQueryResultValueEncoding ExpectedValueEncoding(ExprValueContract contract) =>
        contract.GetEffectiveType() switch
        {
            ScalarTypeRef { Kind: ScalarTypeKind.Bool } =>
                ElasticRelationQueryResultValueEncoding.JsonBoolean,
            ScalarTypeRef { Kind: ScalarTypeKind.Int32 or ScalarTypeKind.Int64 } =>
                ElasticRelationQueryResultValueEncoding.JsonInt64,
            ScalarTypeRef { Kind: ScalarTypeKind.String or ScalarTypeKind.Guid } =>
                ElasticRelationQueryResultValueEncoding.JsonString,
            ScalarTypeRef { Kind: ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant } =>
                ElasticRelationQueryResultValueEncoding.CanonicalTemporalString,
            _ => throw new ArgumentException(
                "The semantic value contract has no supported exact Elasticsearch result encoding.",
                nameof(contract))
        };
}

/// <summary>Binding from one request-template value to one canonical invocation parameter.</summary>
public sealed record ElasticRelationQueryParameterBinding
{
    /// <summary>Creates canonical parameter-binding metadata.</summary>
    /// <param name="definition">Canonical parameter declaration.</param>
    /// <param name="valueContract">Exact effective value contract after default application.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The contract does not match the declaration.</exception>
    public ElasticRelationQueryParameterBinding(
        QueryParameterDefinition definition,
        ExprValueContract valueContract)
    {
        Definition = Guard.RequireNotNull(definition);
        ValueContract = Guard.RequireNotNull(valueContract);
        if (ValueContract != Definition.EffectiveValueContract)
            throw new ArgumentException("The parameter value contract must match the canonical declaration.", nameof(valueContract));
    }

    /// <summary>Canonical parameter declaration.</summary>
    public QueryParameterDefinition Definition { get; }

    /// <summary>Exact effective value contract.</summary>
    public ExprValueContract ValueContract { get; }

    /// <summary>Stable canonical parameter identity.</summary>
    public QueryParameterId Parameter => Definition.Id;
}

/// <summary>Physical Elasticsearch pagination mechanism retained by an artifact.</summary>
public enum ElasticRelationQueryPagingKind
{
    /// <summary>
    /// The artifact uses one bounded <c>from</c>/<c>size</c> request without a cross-request continuation guarantee.
    /// </summary>
    Offset = 0,

    /// <summary>The artifact uses stable hit <c>search_after</c> paging.</summary>
    SearchAfter = 1,

    /// <summary>The artifact uses a composite-aggregation <c>after</c> key.</summary>
    CompositeAfter = 2
}

/// <summary>Exact pagination guarantees retained by a compiled Elasticsearch artifact.</summary>
public sealed record ElasticRelationQueryPagingContract
{
    /// <summary>Creates pagination metadata.</summary>
    /// <param name="kind">Physical Elasticsearch pagination mechanism.</param>
    /// <param name="offset">Number of ordered rows skipped by offset paging.</param>
    /// <param name="limit">Maximum hits or buckets returned.</param>
    /// <param name="sortFields">Ordered physical sort or composite fields.</param>
    /// <param name="stableUniqueFinalField">Final stable unique hit sort field, or <see langword="null"/> for composite paging.</param>
    /// <exception cref="ArgumentOutOfRangeException">An enum or numeric value is unsupported.</exception>
    /// <exception cref="ArgumentException">Fields or paging metadata are inconsistent.</exception>
    public ElasticRelationQueryPagingContract(
        ElasticRelationQueryPagingKind kind,
        int offset,
        int limit,
        ImmutableArray<string> sortFields,
        string? stableUniqueFinalField)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Elasticsearch paging kind.");
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "An Elasticsearch page offset cannot be negative.");
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "An Elasticsearch page limit must be positive.");
        var normalized = sortFields.IsDefault ? [] : sortFields;
        if (normalized.IsDefaultOrEmpty || normalized.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Elasticsearch paging requires non-empty physical sort fields.", nameof(sortFields));
        if (kind != ElasticRelationQueryPagingKind.Offset && offset != 0)
            throw new ArgumentException("Only offset paging may retain a non-zero offset.", nameof(offset));
        if (kind == ElasticRelationQueryPagingKind.CompositeAfter != (stableUniqueFinalField is null))
            throw new ArgumentException("Composite paging has no hit-level stable unique final field.", nameof(stableUniqueFinalField));
        if (stableUniqueFinalField is not null && string.IsNullOrWhiteSpace(stableUniqueFinalField))
            throw new ArgumentException("A stable unique final field cannot be empty.", nameof(stableUniqueFinalField));
        if (kind != ElasticRelationQueryPagingKind.CompositeAfter
            && !string.Equals(stableUniqueFinalField, normalized[^1], StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The stable unique hit field must be the final physical sort field.",
                nameof(stableUniqueFinalField));
        }

        Kind = kind;
        Offset = offset;
        Limit = limit;
        SortFields = normalized;
        StableUniqueFinalField = stableUniqueFinalField;
    }

    /// <summary>Physical pagination mechanism.</summary>
    public ElasticRelationQueryPagingKind Kind { get; }

    /// <summary>Number of ordered rows skipped by offset paging.</summary>
    public int Offset { get; }

    /// <summary>Maximum hits or buckets returned.</summary>
    public int Limit { get; }

    /// <summary>Ordered physical sort or composite fields.</summary>
    public ImmutableArray<string> SortFields { get; }

    /// <summary>Final stable unique hit sort field, or <see langword="null"/> for composite paging.</summary>
    public string? StableUniqueFinalField { get; }
}

/// <summary>One configurable physical-lowering decision attributed to a canonical expression site.</summary>
public sealed record ElasticRelationQueryLoweringDecision
{
    /// <summary>Creates site-attributed lowering metadata.</summary>
    /// <param name="siteId">Stable canonical expression-site identity.</param>
    /// <param name="decision">Complete policy preference and strategy decision.</param>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="siteId"/> is empty or contains a control character.</exception>
    public ElasticRelationQueryLoweringDecision(string siteId, ElasticQueryLoweringDecision decision)
    {
        SiteId = Guard.RequireNotNullOrWhiteSpace(siteId);
        if (SiteId.Any(char.IsControl))
            throw new ArgumentException("An Elasticsearch lowering site identity cannot contain control characters.", nameof(siteId));
        Decision = Guard.RequireNotNull(decision);
    }

    /// <summary>Stable canonical expression-site identity.</summary>
    public string SiteId { get; }

    /// <summary>Complete effective preference, attempts, and selected strategy.</summary>
    public ElasticQueryLoweringDecision Decision { get; }
}

/// <summary>One exact canonical branch compiled to a reusable Elasticsearch request template.</summary>
public sealed class ElasticRelationQueryCompiledArtifact
{
    internal ElasticRelationQueryCompiledArtifact(
        RelationQueryNativeResultBranch branch,
        ElasticSearchRequestTemplate requestTemplate,
        ElasticRelationQueryStorageBinding storageBinding,
        ImmutableArray<ElasticRelationQuerySelectedField> selectedFields,
        ImmutableArray<ElasticRelationQueryResultFieldBinding> resultFields,
        ImmutableArray<ElasticRelationQueryParameterBinding> parameters,
        ElasticRelationQueryPagingContract? paging,
        ImmutableArray<ElasticRelationQueryLoweringDecision> loweringDecisions,
        RelationQueryNativeCompilationProvenance provenance,
        ElasticRelationQueryArtifactFingerprint fingerprint)
    {
        Branch = Guard.RequireNotNull(branch);
        RequestTemplate = Guard.RequireNotNull(requestTemplate);
        StorageBinding = Guard.RequireNotNull(storageBinding);
        SelectedFields = NormalizeAndOrder(selectedFields, static field => field.Input.Value, nameof(selectedFields));
        ResultFields = NormalizePreservingOrder(
            resultFields,
            static field => $"{field.Field.Shape.GraphId.Value}/{field.Field.Shape.ShapeId.Value}/{field.Field.Path}",
            nameof(resultFields));
        Parameters = NormalizeAndOrder(parameters, static parameter => parameter.Parameter.Value, nameof(parameters));
        Paging = paging;
        LoweringDecisions = NormalizePreservingOrder(
            loweringDecisions,
            static decision => decision.SiteId,
            nameof(loweringDecisions));
        Provenance = Guard.RequireNotNull(provenance);
        Fingerprint = Guard.RequireNotNull(fingerprint);
        if (Branch.Id != Provenance.Branch)
            throw new ArgumentException("Artifact branch and provenance branch identities must match.", nameof(provenance));
    }

    /// <summary>Canonical terminal branch compiled by this artifact.</summary>
    public RelationQueryNativeResultBranch Branch { get; }

    /// <summary>Reusable low-level request template shared with direct builder usage.</summary>
    public ElasticSearchRequestTemplate RequestTemplate { get; }

    /// <summary>Exact Elasticsearch storage binding used by compilation.</summary>
    public ElasticRelationQueryStorageBinding StorageBinding { get; }

    /// <summary>Exact compiled semantic fields consumed by the request.</summary>
    public ImmutableArray<ElasticRelationQuerySelectedField> SelectedFields { get; }

    /// <summary>Bindings sufficient to reconstruct canonical result fields without rescanning IR.</summary>
    public ImmutableArray<ElasticRelationQueryResultFieldBinding> ResultFields { get; }

    /// <summary>Canonical invocation parameters in stable identity order.</summary>
    public ImmutableArray<ElasticRelationQueryParameterBinding> Parameters { get; }

    /// <summary>Exact paging guarantees, or <see langword="null"/> for an unpaged aggregate.</summary>
    public ElasticRelationQueryPagingContract? Paging { get; }

    /// <summary>Attributable configurable-lowering decisions in stable expression-site order.</summary>
    public ImmutableArray<ElasticRelationQueryLoweringDecision> LoweringDecisions { get; }

    /// <summary>Exact target-neutral compilation provenance.</summary>
    public RelationQueryNativeCompilationProvenance Provenance { get; }

    /// <summary>Deterministic identity of all semantic and physical artifact inputs.</summary>
    public ElasticRelationQueryArtifactFingerprint Fingerprint { get; }

    /// <summary>Binds one canonical invocation without recompiling the static plan or request template.</summary>
    /// <param name="parameters">
    /// Invocation values keyed by canonical identity. Undefined is treated as absent so a declared default may apply.
    /// </param>
    /// <returns>
    /// A fresh mutable Elasticsearch SDK request. Its initial state is covered by this artifact; caller mutation is
    /// an explicit escape hatch outside the artifact's provenance, fingerprint, and exactness guarantees.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An unknown parameter is supplied, a required parameter is absent, or an effective value violates its contract.
    /// </exception>
    public SearchRequest Bind(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var expected = Parameters.Select(static parameter => parameter.Parameter).ToHashSet();
        if (parameters.Keys.Any(parameter => !expected.Contains(parameter)))
            throw new ArgumentException("The invocation contains a parameter absent from this compiled artifact.", nameof(parameters));

        Dictionary<QueryParameterId, ObservationValue> effective = [];
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
            effective.Add(binding.Parameter, value);
        }
        return RequestTemplate.Bind(effective);
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

/// <summary>Structured result of compiling selected canonical branches to Elasticsearch.</summary>
public sealed class ElasticRelationQueryCompilationResult
{
    internal ElasticRelationQueryCompilationResult(
        RelationQueryNativeCompilationStatus status,
        ImmutableArray<ElasticRelationQueryCompiledArtifact> artifacts,
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
    public ImmutableArray<ElasticRelationQueryCompiledArtifact> Artifacts { get; }

    /// <summary>Structured diagnostics in deterministic attribution order.</summary>
    public ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics { get; }

    /// <summary>Whether every selected branch compiled exactly.</summary>
    public bool IsSuccessful => Status == RelationQueryNativeCompilationStatus.Exact;
}

/// <summary>Stable Elasticsearch-specific native-compilation diagnostic codes.</summary>
public static class ElasticRelationQueryCompilationDiagnosticCodes
{
    /// <summary>The storage binding conflicts with placement or target facts.</summary>
    public const string StorageBindingMismatch = "REL2230";

    /// <summary>The selected branch does not have a supported single-index topology.</summary>
    public const string UnsupportedBranchTopology = "REL2231";

    /// <summary>A logical operator is unsupported or appears in an inexact pipeline position.</summary>
    public const string UnsupportedLogicalOperator = "REL2232";

    /// <summary>A canonical expression cannot be lowered exactly to the supported Query DSL closure.</summary>
    public const string UnsupportedExpression = "REL2233";

    /// <summary>A compiled field input lacks the physical mapping facts required by its use.</summary>
    public const string FieldBindingMissing = "REL2234";

    /// <summary>Missing/null, value-domain, aggregation, or ordering semantics cannot be proven exact.</summary>
    public const string GuaranteeUnavailable = "REL2235";

    /// <summary>A canonical invocation parameter cannot be represented by the reusable template contract.</summary>
    public const string ParameterUnsupported = "REL2236";

    /// <summary>Aggregate semantics cannot be proven exact for the selected branch.</summary>
    public const string AggregateUnsupported = "REL2237";

    /// <summary>Paging stability or the configured Elasticsearch result-window boundary cannot be proven.</summary>
    public const string PagingUnstable = "REL2238";

    /// <summary>Artifact construction failed an internal consistency check.</summary>
    public const string ArtifactInvalid = "REL2239";

    /// <summary>The requested runtime result observability cannot be produced by Elasticsearch v1.</summary>
    public const string ResultObservabilityUnsupported = "REL2240";

    /// <summary>A relation terminal requires semantics absent from the v1 artifact contract.</summary>
    public const string RelationTerminalUnsupported = "REL2241";

    /// <summary>No configured exact lowering strategy can realize a canonical operation.</summary>
    public const string LoweringUnavailable = "REL2242";

    /// <summary>Configured lowering policy or extension registration is inconsistent.</summary>
    public const string LoweringConfigurationInvalid = "REL2243";
}
