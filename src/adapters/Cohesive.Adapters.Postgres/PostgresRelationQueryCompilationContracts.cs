using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Deterministic identity of one normalized canonical PostgreSQL artifact.</summary>
public sealed record PostgresRelationQueryArtifactFingerprint
{
    /// <summary>Creates an artifact fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public PostgresRelationQueryArtifactFingerprint(string algorithm, string canonicalization, string value)
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

/// <summary>Physical PostgreSQL value representation retained for a canonical scalar.</summary>
public enum PostgresRelationQueryValueEncoding
{
    /// <summary>PostgreSQL <c>boolean</c>.</summary>
    Boolean = 0,

    /// <summary>PostgreSQL <c>integer</c>.</summary>
    Int32 = 1,

    /// <summary>PostgreSQL <c>bigint</c>.</summary>
    Int64 = 2,

    /// <summary>PostgreSQL exact <c>numeric</c>.</summary>
    Numeric = 3,

    /// <summary>PostgreSQL <c>text</c> without adapter-side normalization.</summary>
    Text = 4,

    /// <summary>PostgreSQL <c>uuid</c>.</summary>
    Uuid = 5,

    /// <summary>PostgreSQL <c>date</c>.</summary>
    Date = 6,

    /// <summary>PostgreSQL <c>timestamp without time zone</c>.</summary>
    Timestamp = 7,

    /// <summary>PostgreSQL <c>timestamp with time zone</c>, normalized to a canonical instant.</summary>
    TimestampWithTimeZone = 8,

    /// <summary>PostgreSQL <c>bytea</c>.</summary>
    Bytea = 9
}

static class PostgresRelationQueryValueEncodingContracts
{
    public static void RequireCompatible(
        ValueContract contract,
        PostgresRelationQueryValueEncoding encoding,
        string parameterName)
    {
        if (contract.Cardinality != FieldCardinality.Single
            || !PostgresRelationQueryScalarCatalog.TryFromSemanticType(contract.Type, out var scalarType))
        {
            throw new ArgumentException(
                $"Semantic type '{contract.Type}' has no exact single-valued PostgreSQL scalar encoding.",
                parameterName);
        }
        var expected = PostgresRelationQueryScalarCatalog.ToValueEncoding(scalarType);
        if (encoding != expected)
        {
            throw new ArgumentException(
                $"PostgreSQL encoding '{encoding}' does not match semantic type '{contract.Type}'.",
                parameterName);
        }
    }
}

/// <summary>One exact compiled semantic field read from a PostgreSQL column.</summary>
public sealed record PostgresRelationQuerySelectedField
{
    /// <summary>Creates selected-field metadata.</summary>
    /// <param name="input">Exact compiled field-input identity.</param>
    /// <param name="field">Canonical graph-qualified semantic field.</param>
    /// <param name="placementBinding">Plan-scoped table placement supplying the field.</param>
    /// <param name="columnName">Quoted-at-render-time physical column name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, semantic field, or column name is invalid.</exception>
    public PostgresRelationQuerySelectedField(
        RelationQueryInputId input,
        RelationQueryFieldReference field,
        RelationQuerySourcePlacementBindingId placementBinding,
        string columnName)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A selected PostgreSQL field requires a compiled input identity.", nameof(input));
        if (string.IsNullOrWhiteSpace(field.Shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(field.Shape.ShapeId.Value)
            || field.Path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A selected PostgreSQL field requires a graph-qualified semantic field.",
                nameof(field));
        }
        if (string.IsNullOrWhiteSpace(placementBinding.Value))
            throw new ArgumentException("A selected PostgreSQL field requires placement attribution.", nameof(placementBinding));

        Input = input;
        Field = field;
        PlacementBinding = placementBinding;
        ColumnName = new PostgresSqlIdentifier(columnName).Value;
    }

    /// <summary>Exact compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Canonical graph-qualified semantic field.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Plan-scoped table placement supplying the field.</summary>
    public RelationQuerySourcePlacementBindingId PlacementBinding { get; }

    /// <summary>Physical PostgreSQL column name.</summary>
    public string ColumnName { get; }
}

/// <summary>Binding from one safe result alias to one canonical output field.</summary>
public sealed record PostgresRelationQueryResultFieldBinding
{
    /// <summary>Creates a result-field binding.</summary>
    /// <param name="alias">Safe result alias.</param>
    /// <param name="field">Canonical output field reconstructed from the alias.</param>
    /// <param name="valueContract">Exact semantic value contract used for decoding.</param>
    /// <param name="encoding">Expected physical PostgreSQL scalar encoding.</param>
    /// <param name="assignment">Producing canonical assignment, or <see langword="null"/> for a direct field.</param>
    /// <param name="presenceDependencies">
    /// Outer-joined bindings whose absence makes this result field semantically undefined.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="alias"/> or <paramref name="valueContract"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An alias, field, assignment, or value contract is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="encoding"/> is unsupported.</exception>
    public PostgresRelationQueryResultFieldBinding(
        string alias,
        RelationQueryFieldReference field,
        ValueContract valueContract,
        PostgresRelationQueryValueEncoding encoding,
        QueryAssignmentId? assignment = null,
        ImmutableArray<ValueBindingId> presenceDependencies = default)
    {
        Alias = new PostgresSqlIdentifier(alias).Value;
        if (string.IsNullOrWhiteSpace(field.Shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(field.Shape.ShapeId.Value)
            || field.Path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A PostgreSQL result binding requires a graph-qualified canonical field.",
                nameof(field));
        }
        if (!Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unsupported PostgreSQL result encoding.");
        if (assignment is { } assignmentId && string.IsNullOrWhiteSpace(assignmentId.Value))
            throw new ArgumentException("A result assignment identity cannot be empty.", nameof(assignment));

        Field = field;
        ValueContract = Guard.RequireNotNull(valueContract);
        if (ValueContract.Cardinality != FieldCardinality.Single)
            throw new ArgumentException("A PostgreSQL result field must be single-valued.", nameof(valueContract));
        PostgresRelationQueryValueEncodingContracts.RequireCompatible(ValueContract, encoding, nameof(encoding));
        Encoding = encoding;
        Assignment = assignment;
        var normalizedDependencies = presenceDependencies.IsDefault ? [] : presenceDependencies;
        if (normalizedDependencies.Any(static binding => string.IsNullOrWhiteSpace(binding.Value)))
            throw new ArgumentException("A presence dependency cannot be empty.", nameof(presenceDependencies));
        if (normalizedDependencies.Distinct().Count() != normalizedDependencies.Length)
            throw new ArgumentException("Presence dependencies cannot repeat.", nameof(presenceDependencies));
        PresenceDependencies =
        [
            .. normalizedDependencies.OrderBy(static binding => binding.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Safe SQL result alias.</summary>
    public string Alias { get; }

    /// <summary>Canonical output field reconstructed from the alias.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Exact semantic value contract used to decode the physical result.</summary>
    public ValueContract ValueContract { get; }

    /// <summary>Expected PostgreSQL scalar encoding.</summary>
    public PostgresRelationQueryValueEncoding Encoding { get; }

    /// <summary>Producing canonical assignment, or <see langword="null"/> for a direct field.</summary>
    public QueryAssignmentId? Assignment { get; }

    /// <summary>Outer-joined bindings whose absence makes this result field semantically undefined.</summary>
    public ImmutableArray<ValueBindingId> PresenceDependencies { get; }
}

/// <summary>Hidden result column that distinguishes an absent outer-joined binding from a present row.</summary>
public sealed record PostgresRelationQueryPresenceBinding
{
    /// <summary>Creates binding-presence metadata.</summary>
    /// <param name="binding">Canonical value binding whose presence is represented.</param>
    /// <param name="alias">Safe hidden result alias containing <see langword="true"/> for present rows.</param>
    /// <param name="placementBinding">Physical table placement attributed to the marker.</param>
    /// <exception cref="ArgumentNullException"><paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or alias is empty.</exception>
    public PostgresRelationQueryPresenceBinding(
        ValueBindingId binding,
        string alias,
        RelationQuerySourcePlacementBindingId placementBinding)
    {
        if (string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("Presence metadata requires a canonical binding.", nameof(binding));
        if (string.IsNullOrWhiteSpace(placementBinding.Value))
            throw new ArgumentException("Presence metadata requires placement attribution.", nameof(placementBinding));
        Binding = binding;
        Alias = new PostgresSqlIdentifier(alias).Value;
        PlacementBinding = placementBinding;
    }

    /// <summary>Canonical value binding whose presence is represented.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Hidden SQL result alias.</summary>
    public string Alias { get; }

    /// <summary>Physical table placement attributed to the marker.</summary>
    public RelationQuerySourcePlacementBindingId PlacementBinding { get; }
}

/// <summary>Binding from one runtime SQL slot to one demanded field of a supplied relation root.</summary>
public sealed record PostgresRelationQuerySuppliedFieldBinding
{
    /// <summary>Creates supplied-field binding metadata.</summary>
    /// <param name="position">One-based PostgreSQL positional parameter number.</param>
    /// <param name="input">Exact compiled field-input identity.</param>
    /// <param name="field">Canonical supplied semantic field.</param>
    /// <param name="valueContract">Exact semantic value contract expected at invocation time.</param>
    /// <param name="encoding">Expected PostgreSQL scalar encoding.</param>
    /// <param name="orderingDomain">
    /// Runtime text-domain restriction required by an ordinal ordering comparison, or <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="position"/> is not positive or <paramref name="encoding"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">An input or field identity is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="valueContract"/> is <see langword="null"/>.</exception>
    public PostgresRelationQuerySuppliedFieldBinding(
        int position,
        RelationQueryInputId input,
        RelationQueryFieldReference field,
        ValueContract valueContract,
        PostgresRelationQueryValueEncoding encoding,
        PostgresRelationQueryTextOrderingDomainEvidence? orderingDomain = null)
    {
        if (position <= 0)
            throw new ArgumentOutOfRangeException(nameof(position), position, "A PostgreSQL parameter position must be positive.");
        if (string.IsNullOrWhiteSpace(input.Value))
            throw new ArgumentException("A supplied PostgreSQL field requires a compiled input identity.", nameof(input));
        if (string.IsNullOrWhiteSpace(field.Shape.GraphId.Value)
            || string.IsNullOrWhiteSpace(field.Shape.ShapeId.Value)
            || field.Path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A supplied PostgreSQL field requires a graph-qualified field.", nameof(field));
        }
        if (!Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unsupported PostgreSQL value encoding.");

        Position = position;
        Input = input;
        Field = field;
        ValueContract = Guard.RequireNotNull(valueContract);
        if (ValueContract.Cardinality != FieldCardinality.Single)
            throw new ArgumentException("A supplied PostgreSQL field must be single-valued.", nameof(valueContract));
        PostgresRelationQueryValueEncodingContracts.RequireCompatible(ValueContract, encoding, nameof(encoding));
        if (orderingDomain is not null && encoding != PostgresRelationQueryValueEncoding.Text)
        {
            throw new ArgumentException(
                "Text ordering-domain evidence applies only to PostgreSQL text runtime values.",
                nameof(orderingDomain));
        }
        Encoding = encoding;
        OrderingDomain = orderingDomain;
    }

    /// <summary>One-based PostgreSQL positional parameter number.</summary>
    public int Position { get; }

    /// <summary>Exact compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Canonical supplied semantic field.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Exact invocation-time semantic value contract.</summary>
    public ValueContract ValueContract { get; }

    /// <summary>Expected PostgreSQL scalar encoding.</summary>
    public PostgresRelationQueryValueEncoding Encoding { get; }

    /// <summary>Required runtime text ordering domain, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTextOrderingDomainEvidence? OrderingDomain { get; }

    /// <summary>Stable SQL runtime binding identity.</summary>
    public string RuntimeBinding => $"supplied:{Input.Value}";
}

/// <summary>Hidden Boolean result used to validate one canonical relation invariant after SQL execution.</summary>
public sealed record PostgresRelationQueryInvariantBinding
{
    /// <summary>Creates invariant-validation metadata.</summary>
    /// <param name="name">Canonical invariant name.</param>
    /// <param name="alias">Safe hidden SQL result alias containing the invariant predicate result.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public PostgresRelationQueryInvariantBinding(string name, string alias)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Alias = new PostgresSqlIdentifier(alias).Value;
    }

    /// <summary>Canonical invariant name.</summary>
    public string Name { get; }

    /// <summary>Hidden Boolean SQL result alias.</summary>
    public string Alias { get; }
}

/// <summary>Hidden stable-key result emitted for a canonical relation terminal.</summary>
public sealed record PostgresRelationQueryRelationKeyBinding
{
    /// <summary>Creates relation-key metadata.</summary>
    /// <param name="alias">Safe hidden SQL result alias.</param>
    /// <param name="valueContract">Exact canonical key value contract.</param>
    /// <param name="encoding">Expected PostgreSQL scalar encoding.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="alias"/> or <paramref name="valueContract"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is empty or the key is not single-valued.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="encoding"/> is unsupported.</exception>
    public PostgresRelationQueryRelationKeyBinding(
        string alias,
        ValueContract valueContract,
        PostgresRelationQueryValueEncoding encoding)
    {
        Alias = new PostgresSqlIdentifier(alias).Value;
        ValueContract = Guard.RequireNotNull(valueContract);
        if (ValueContract.Cardinality != FieldCardinality.Single)
            throw new ArgumentException("A PostgreSQL relation key must be single-valued.", nameof(valueContract));
        if (!Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unsupported PostgreSQL key encoding.");
        PostgresRelationQueryValueEncodingContracts.RequireCompatible(ValueContract, encoding, nameof(encoding));
        Encoding = encoding;
    }

    /// <summary>Hidden SQL result alias.</summary>
    public string Alias { get; }

    /// <summary>Exact canonical key value contract.</summary>
    public ValueContract ValueContract { get; }

    /// <summary>Expected PostgreSQL scalar encoding.</summary>
    public PostgresRelationQueryValueEncoding Encoding { get; }
}

/// <summary>Binding from one positional SQL slot to one canonical invocation parameter.</summary>
public sealed record PostgresRelationQueryParameterBinding
{
    /// <summary>Creates canonical parameter-binding metadata.</summary>
    /// <param name="position">One-based PostgreSQL positional parameter number.</param>
    /// <param name="definition">Canonical parameter declaration.</param>
    /// <param name="valueContract">Exact effective value contract after default application.</param>
    /// <param name="encoding">Expected PostgreSQL scalar encoding.</param>
    /// <param name="orderingDomain">
    /// Runtime text-domain restriction required by an ordinal ordering comparison, or <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="valueContract"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="position"/> is not positive or <paramref name="encoding"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="valueContract"/> does not match the declaration.</exception>
    public PostgresRelationQueryParameterBinding(
        int position,
        QueryParameterDefinition definition,
        ValueContract valueContract,
        PostgresRelationQueryValueEncoding encoding,
        PostgresRelationQueryTextOrderingDomainEvidence? orderingDomain = null)
    {
        if (position <= 0)
            throw new ArgumentOutOfRangeException(nameof(position), position, "A PostgreSQL parameter position must be positive.");
        if (!Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "Unsupported PostgreSQL parameter encoding.");
        Position = position;
        Definition = Guard.RequireNotNull(definition);
        ValueContract = Guard.RequireNotNull(valueContract);
        if (ValueContract != Definition.EffectiveValueContract)
            throw new ArgumentException("The parameter value contract must match its canonical declaration.", nameof(valueContract));
        PostgresRelationQueryValueEncodingContracts.RequireCompatible(ValueContract, encoding, nameof(encoding));
        if (orderingDomain is not null && encoding != PostgresRelationQueryValueEncoding.Text)
        {
            throw new ArgumentException(
                "Text ordering-domain evidence applies only to PostgreSQL text runtime values.",
                nameof(orderingDomain));
        }
        Encoding = encoding;
        OrderingDomain = orderingDomain;
    }

    /// <summary>One-based PostgreSQL positional parameter number.</summary>
    public int Position { get; }

    /// <summary>Canonical parameter declaration.</summary>
    public QueryParameterDefinition Definition { get; }

    /// <summary>Exact effective compiled value contract.</summary>
    public ValueContract ValueContract { get; }

    /// <summary>Expected PostgreSQL scalar encoding.</summary>
    public PostgresRelationQueryValueEncoding Encoding { get; }

    /// <summary>Required runtime text ordering domain, or <see langword="null"/>.</summary>
    public PostgresRelationQueryTextOrderingDomainEvidence? OrderingDomain { get; }

    /// <summary>Stable canonical parameter identity.</summary>
    public QueryParameterId Parameter => Definition.Id;

    /// <summary>Namespaced SQL runtime binding identity.</summary>
    public string RuntimeBinding => $"parameter:{Parameter.Value}";
}

/// <summary>Pagination strategy retained by a PostgreSQL artifact.</summary>
public enum PostgresRelationQueryPagingKind
{
    /// <summary>Rows are selected after skipping a fixed offset.</summary>
    Offset = 0,

    /// <summary>Rows are selected after a lexicographic keyset continuation.</summary>
    Keyset = 1
}

/// <summary>Exact stable-pagination contract retained by a PostgreSQL artifact.</summary>
public sealed record PostgresRelationQueryPagingContract
{
    /// <summary>Creates paging metadata.</summary>
    /// <param name="kind">Offset or keyset strategy.</param>
    /// <param name="limit">Maximum returned row count.</param>
    /// <param name="offset">Skipped row count for offset paging; zero for keyset paging.</param>
    /// <param name="stableOrderingInputs">Final ordered field inputs proving stable page membership.</param>
    /// <exception cref="ArgumentOutOfRangeException">An enum or numeric value is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Stable ordering evidence is absent or repeated, or offset conflicts with the selected strategy.
    /// </exception>
    public PostgresRelationQueryPagingContract(
        PostgresRelationQueryPagingKind kind,
        int limit,
        int offset,
        ImmutableArray<RelationQueryInputId> stableOrderingInputs)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported PostgreSQL paging kind.");
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A PostgreSQL page limit must be positive.");
        if (offset < 0 || (kind == PostgresRelationQueryPagingKind.Keyset && offset != 0))
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The paging offset conflicts with the selected strategy.");
        var normalized = stableOrderingInputs.IsDefault ? [] : stableOrderingInputs;
        if (normalized.IsDefaultOrEmpty || normalized.Any(static input => string.IsNullOrWhiteSpace(input.Value)))
            throw new ArgumentException("Stable paging requires one or more ordering inputs.", nameof(stableOrderingInputs));
        if (normalized.Distinct().Count() != normalized.Length)
            throw new ArgumentException("Stable ordering inputs cannot be repeated.", nameof(stableOrderingInputs));

        Kind = kind;
        Limit = limit;
        Offset = offset;
        StableOrderingInputs = normalized;
    }

    /// <summary>Offset or keyset strategy.</summary>
    public PostgresRelationQueryPagingKind Kind { get; }

    /// <summary>Maximum returned row count.</summary>
    public int Limit { get; }

    /// <summary>Skipped row count for offset paging; zero for keyset paging.</summary>
    public int Offset { get; }

    /// <summary>Ordered field inputs proving stable page membership.</summary>
    public ImmutableArray<RelationQueryInputId> StableOrderingInputs { get; }
}

/// <summary>Kind of one attributable PostgreSQL lowering decision.</summary>
public enum PostgresRelationQueryLoweringDecisionKind
{
    /// <summary>A canonical source became a physical table scan.</summary>
    SourceTable = 0,

    /// <summary>A canonical filter became a SQL predicate.</summary>
    Filter = 1,

    /// <summary>A semantic relationship traversal became a native SQL join.</summary>
    RelationshipTraversalJoin = 2,

    /// <summary>An explicit rowset join became a native SQL join.</summary>
    ExplicitJoin = 3,

    /// <summary>A canonical valid-time join became a native SQL join predicate.</summary>
    TemporalJoin = 4,

    /// <summary>A canonical projection became a SQL select list.</summary>
    Projection = 5,

    /// <summary>Canonical duplicate elimination became SQL <c>DISTINCT</c>.</summary>
    Distinct = 6,

    /// <summary>A canonical aggregation became SQL grouping and aggregate expressions.</summary>
    Aggregation = 7,

    /// <summary>Canonical ordering became an explicit SQL ordering.</summary>
    Ordering = 8,

    /// <summary>Canonical offset paging became SQL <c>OFFSET</c> and <c>LIMIT</c>.</summary>
    OffsetPaging = 9,

    /// <summary>Canonical keyset paging became a lexicographic seek predicate and <c>LIMIT</c>.</summary>
    KeysetPaging = 10,

    /// <summary>A canonical expression site became a closed PostgreSQL expression.</summary>
    Expression = 11,

    /// <summary>A rooted relation rowset is correlated to its one supplied invocation root.</summary>
    RelationRootCorrelation = 12
}

/// <summary>One inspectable PostgreSQL lowering choice with semantic and physical attribution.</summary>
public sealed record PostgresRelationQueryLoweringDecision
{
    /// <summary>Creates lowering-decision provenance.</summary>
    /// <param name="kind">Kind of lowering performed.</param>
    /// <param name="strategy">Stable versioned lowering strategy identity.</param>
    /// <param name="node">Canonical logical node attributed to the decision.</param>
    /// <param name="expressionSite">Expression-site identity, or <see langword="null"/>.</param>
    /// <param name="assignment">Canonical assignment identity, or <see langword="null"/>.</param>
    /// <param name="relationship">Canonical relationship identity, or <see langword="null"/>.</param>
    /// <param name="placementBindings">Physical placement bindings used by the decision.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">An identity is empty or repeated.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="strategy"/> is <see langword="null"/>.</exception>
    public PostgresRelationQueryLoweringDecision(
        PostgresRelationQueryLoweringDecisionKind kind,
        string strategy,
        QueryNodeId node,
        ExprSiteId? expressionSite = null,
        QueryAssignmentId? assignment = null,
        RelationshipId? relationship = null,
        ImmutableArray<RelationQuerySourcePlacementBindingId> placementBindings = default)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported PostgreSQL lowering kind.");
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A lowering decision requires a canonical node.", nameof(node));
        if (expressionSite is { } site && string.IsNullOrWhiteSpace(site.Value))
            throw new ArgumentException("A lowering expression-site identity cannot be empty.", nameof(expressionSite));
        if (assignment is { } assignmentId && string.IsNullOrWhiteSpace(assignmentId.Value))
            throw new ArgumentException("A lowering assignment identity cannot be empty.", nameof(assignment));
        if (relationship is { } relationshipId && string.IsNullOrWhiteSpace(relationshipId.Value))
            throw new ArgumentException("A lowering relationship identity cannot be empty.", nameof(relationship));
        var normalized = placementBindings.IsDefault ? [] : placementBindings;
        if (normalized.Any(static binding => string.IsNullOrWhiteSpace(binding.Value))
            || normalized.Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException("Lowering placement identities must be nonempty and unique.", nameof(placementBindings));
        }

        Kind = kind;
        Strategy = Guard.RequireNotNullOrWhiteSpace(strategy);
        Node = node;
        ExpressionSite = expressionSite;
        Assignment = assignment;
        Relationship = relationship;
        PlacementBindings = [.. normalized.OrderBy(static binding => binding.Value, StringComparer.Ordinal)];
    }

    /// <summary>Kind of lowering performed.</summary>
    public PostgresRelationQueryLoweringDecisionKind Kind { get; }

    /// <summary>Stable versioned lowering strategy identity.</summary>
    public string Strategy { get; }

    /// <summary>Canonical logical node attributed to the decision.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Expression-site identity, or <see langword="null"/>.</summary>
    public ExprSiteId? ExpressionSite { get; }

    /// <summary>Canonical assignment identity, or <see langword="null"/>.</summary>
    public QueryAssignmentId? Assignment { get; }

    /// <summary>Canonical relationship identity, or <see langword="null"/>.</summary>
    public RelationshipId? Relationship { get; }

    /// <summary>Physical placement bindings used by the decision.</summary>
    public ImmutableArray<RelationQuerySourcePlacementBindingId> PlacementBindings { get; }
}

/// <summary>One exact canonical result branch compiled to reusable parameterized PostgreSQL SQL.</summary>
public sealed class PostgresRelationQueryCompiledArtifact
{
    /// <summary>Current persisted PostgreSQL native-artifact schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive.relations.postgres-artifact/v3";

    /// <summary>Creates or rehydrates one validated PostgreSQL native artifact.</summary>
    /// <param name="schemaVersion">Persisted artifact schema version.</param>
    /// <param name="branch">Canonical terminal branch.</param>
    /// <param name="statement">Reusable PostgreSQL command template.</param>
    /// <param name="storageBinding">Exact persisted storage binding.</param>
    /// <param name="selectedFields">Physical semantic fields read by the artifact.</param>
    /// <param name="resultFields">Result reconstruction metadata.</param>
    /// <param name="presenceBindings">Outer-join presence markers.</param>
    /// <param name="suppliedFields">Runtime-supplied root fields.</param>
    /// <param name="parameters">Canonical query parameters.</param>
    /// <param name="paging">Paging contract, or <see langword="null"/>.</param>
    /// <param name="relationKey">Relation key metadata, or <see langword="null"/>.</param>
    /// <param name="invariants">Relation-invariant result metadata.</param>
    /// <param name="loweringDecisions">Attributable PostgreSQL lowering decisions.</param>
    /// <param name="provenance">Target-neutral compilation provenance.</param>
    /// <param name="fingerprint">Persisted fingerprint expected to match normalized content.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported; metadata is invalid or inconsistent; runtime slots do not align; or the persisted
    /// fingerprint does not match normalized content.
    /// </exception>
    [JsonConstructor]
    public PostgresRelationQueryCompiledArtifact(
        string schemaVersion,
        RelationQueryNativeResultBranch branch,
        PostgresSqlCommandTemplate statement,
        PostgresRelationQueryStorageBinding storageBinding,
        ImmutableArray<PostgresRelationQuerySelectedField> selectedFields,
        ImmutableArray<PostgresRelationQueryResultFieldBinding> resultFields,
        ImmutableArray<PostgresRelationQueryPresenceBinding> presenceBindings,
        ImmutableArray<PostgresRelationQuerySuppliedFieldBinding> suppliedFields,
        ImmutableArray<PostgresRelationQueryParameterBinding> parameters,
        PostgresRelationQueryPagingContract? paging,
        PostgresRelationQueryRelationKeyBinding? relationKey,
        ImmutableArray<PostgresRelationQueryInvariantBinding> invariants,
        ImmutableArray<PostgresRelationQueryLoweringDecision> loweringDecisions,
        RelationQueryNativeCompilationProvenance provenance,
        PostgresRelationQueryArtifactFingerprint fingerprint)
    {
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported PostgreSQL artifact schema '{schemaVersion}'.", nameof(schemaVersion));
        SchemaVersion = CurrentSchemaVersion;
        Branch = Guard.RequireNotNull(branch);
        Statement = Guard.RequireNotNull(statement);
        StorageBinding = Guard.RequireNotNull(storageBinding);
        SelectedFields = Normalize(selectedFields, static field => field.Input.Value, nameof(selectedFields));
        ResultFields = NormalizePreservingOrder(resultFields, static field => field.Alias, nameof(resultFields));
        PresenceBindings = Normalize(presenceBindings, static binding => binding.Binding.Value, nameof(presenceBindings));
        SuppliedFields = Normalize(suppliedFields, static field => field.Input.Value, nameof(suppliedFields));
        Parameters = NormalizePreservingOrder(parameters, static parameter => parameter.Position.ToString(), nameof(parameters));
        Paging = paging;
        RelationKey = relationKey;
        Invariants = Normalize(invariants, static invariant => invariant.Name, nameof(invariants));
        LoweringDecisions = NormalizePreservingOrder(
            loweringDecisions,
            static decision => string.Join(
                '/',
                decision.Node.Value,
                ((int)decision.Kind).ToString(),
                decision.ExpressionSite?.Value ?? string.Empty,
                decision.Assignment?.Value ?? string.Empty),
            nameof(loweringDecisions));
        Provenance = Guard.RequireNotNull(provenance);
        Fingerprint = Guard.RequireNotNull(fingerprint);
        if (Branch.Id != Provenance.Branch)
            throw new ArgumentException("Artifact branch and provenance identities must match.", nameof(provenance));
        ValidateCrossMetadata(
            Branch,
            StorageBinding,
            SelectedFields,
            ResultFields,
            PresenceBindings,
            SuppliedFields,
            RelationKey,
            Invariants,
            Provenance);
        ValidateRuntimeBindings(Statement, SuppliedFields, Parameters);
        var computed = PostgresRelationQueryArtifactFingerprinter.Compute(
            SchemaVersion,
            Branch,
            Statement,
            StorageBinding,
            SelectedFields,
            ResultFields,
            PresenceBindings,
            SuppliedFields,
            Parameters,
            Paging,
            RelationKey,
            Invariants,
            LoweringDecisions,
            Provenance);
        if (!Equals(Fingerprint, computed))
            throw new ArgumentException("Persisted PostgreSQL artifact fingerprint does not match normalized content.", nameof(fingerprint));
    }

    /// <summary>Persisted PostgreSQL native-artifact schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Canonical terminal branch compiled by this artifact.</summary>
    public RelationQueryNativeResultBranch Branch { get; }

    /// <summary>Reusable low-level PostgreSQL command template.</summary>
    public PostgresSqlCommandTemplate Statement { get; }

    /// <summary>Exact multi-table PostgreSQL storage binding used by compilation.</summary>
    public PostgresRelationQueryStorageBinding StorageBinding { get; }

    /// <summary>Exact compiled semantic fields read from physical columns.</summary>
    public ImmutableArray<PostgresRelationQuerySelectedField> SelectedFields { get; }

    /// <summary>Bindings sufficient to reconstruct demanded canonical result fields.</summary>
    public ImmutableArray<PostgresRelationQueryResultFieldBinding> ResultFields { get; }

    /// <summary>Hidden Boolean markers used to reconstruct outer-join binding presence.</summary>
    public ImmutableArray<PostgresRelationQueryPresenceBinding> PresenceBindings { get; }

    /// <summary>Supplied relation-root fields in stable compiled-input order.</summary>
    public ImmutableArray<PostgresRelationQuerySuppliedFieldBinding> SuppliedFields { get; }

    /// <summary>Canonical invocation parameters in SQL position order.</summary>
    public ImmutableArray<PostgresRelationQueryParameterBinding> Parameters { get; }

    /// <summary>Stable paging contract, or <see langword="null"/> for an unpaged branch.</summary>
    public PostgresRelationQueryPagingContract? Paging { get; }

    /// <summary>Hidden stable relation key, or <see langword="null"/> for query branches or unkeyed relations.</summary>
    public PostgresRelationQueryRelationKeyBinding? RelationKey { get; }

    /// <summary>Hidden Boolean relation-invariant outputs in canonical name order.</summary>
    public ImmutableArray<PostgresRelationQueryInvariantBinding> Invariants { get; }

    /// <summary>Node- and expression-attributed target lowering decisions.</summary>
    public ImmutableArray<PostgresRelationQueryLoweringDecision> LoweringDecisions { get; }

    /// <summary>Exact target-neutral native-compilation provenance.</summary>
    public RelationQueryNativeCompilationProvenance Provenance { get; }

    /// <summary>Deterministic identity of every semantic and physical artifact input.</summary>
    public PostgresRelationQueryArtifactFingerprint Fingerprint { get; }

    /// <summary>Binds one canonical invocation without recompiling the static plan or SQL template.</summary>
    /// <param name="parameters">
    /// Invocation values keyed by canonical parameter identity. Undefined values are treated as absent so a declared
    /// default may be applied.
    /// </param>
    /// <returns>A provider-neutral PostgreSQL statement with ordered values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An unknown parameter is supplied, a required parameter is absent, or a value violates its compiled contract.
    /// </exception>
    public PostgresSqlStatement Bind(IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!SuppliedFields.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "This artifact requires supplied relation-root fields; use the supplied-fields Bind overload.",
                nameof(parameters));
        }

        return Bind(EmptySuppliedFields.Instance, parameters);
    }

    /// <summary>Binds supplied relation-root fields and canonical query parameters without recompiling SQL.</summary>
    /// <param name="suppliedFields">
    /// Supplied values keyed by compiled field-input identity. Required fields must be present. An absent or explicit
    /// Undefined value is accepted only by an optional, non-nullable contract, for which SQL <c>NULL</c>
    /// unambiguously represents semantic missing.
    /// </param>
    /// <param name="parameters">
    /// Invocation values keyed by canonical parameter identity. Undefined values are treated as absent so a declared
    /// default may be applied.
    /// </param>
    /// <returns>A provider-neutral PostgreSQL statement with normalized CLR scalar values in positional order.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An unknown or required value is absent, a value violates its compiled semantic contract, or a value has no
    /// exact CLR representation for its PostgreSQL encoding.
    /// </exception>
    public PostgresSqlStatement Bind(
        IReadOnlyDictionary<RelationQueryInputId, ObservationValue> suppliedFields,
        IReadOnlyDictionary<QueryParameterId, ObservationValue> parameters)
    {
        ArgumentNullException.ThrowIfNull(suppliedFields);
        ArgumentNullException.ThrowIfNull(parameters);
        var expectedSupplied = SuppliedFields.Select(static field => field.Input).ToHashSet();
        if (suppliedFields.Keys.Any(input => !expectedSupplied.Contains(input)))
            throw new ArgumentException("The invocation contains a supplied field absent from this artifact.", nameof(suppliedFields));
        if (SuppliedFields.Any(binding =>
                binding.ValueContract.Presence == FieldPresence.Required
                && !suppliedFields.ContainsKey(binding.Input)))
        {
            throw new ArgumentException("The invocation is missing a required supplied field.", nameof(suppliedFields));
        }

        var expected = Parameters.Select(static parameter => parameter.Parameter).ToHashSet();
        if (parameters.Keys.Any(parameter => !expected.Contains(parameter)))
            throw new ArgumentException("The invocation contains a parameter absent from this artifact.", nameof(parameters));

        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        foreach (var binding in SuppliedFields)
        {
            var value = suppliedFields.TryGetValue(binding.Input, out var suppliedValue)
                ? suppliedValue
                : ObservationValue.Undefined;
            if (!binding.ValueContract.IsSatisfiedByConstant(value))
            {
                throw new ArgumentException(
                    $"Supplied field '{binding.Input.Value}' does not satisfy its compiled value contract.",
                    nameof(suppliedFields));
            }
            ValidateRuntimeOrderingDomain(
                value,
                binding.OrderingDomain,
                binding.Input.Value,
                nameof(suppliedFields));
            values.Add(
                binding.RuntimeBinding,
                value.Kind == ObservationValueKind.Undefined
                    && binding.ValueContract is
                    {
                        Presence: FieldPresence.Optional,
                        Nullability: FieldNullability.NonNullable
                    }
                        ? null
                        : PostgresRelationQueryValueConverter.Convert(value, binding.Encoding, binding.Input.Value));
        }

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
                    $"Canonical query parameter '{binding.Parameter.Value}' does not satisfy its compiled value contract.",
                    nameof(parameters));
            }
            ValidateRuntimeOrderingDomain(
                value,
                binding.OrderingDomain,
                binding.Parameter.Value,
                nameof(parameters));
            values.Add(
                binding.RuntimeBinding,
                PostgresRelationQueryValueConverter.Convert(value, binding.Encoding, binding.Parameter.Value));
        }

        return Statement.Bind(values);
    }

    static void ValidateRuntimeOrderingDomain(
        ObservationValue value,
        PostgresRelationQueryTextOrderingDomainEvidence? orderingDomain,
        string semanticIdentity,
        string parameterName)
    {
        if (orderingDomain is null || value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            return;
        if (value.Kind != ObservationValueKind.String
            || !orderingDomain.IsSatisfiedBy(value.GetRequiredString()))
        {
            throw new ArgumentException(
                $"Runtime text value '{semanticIdentity}' violates ordering-domain strategy '{orderingDomain.Strategy}'.",
                parameterName);
        }
    }

    sealed class EmptySuppliedFields : Dictionary<RelationQueryInputId, ObservationValue>
    {
        public static EmptySuppliedFields Instance { get; } = new();
    }

    static ImmutableArray<T> Normalize<T>(
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

    static void ValidateCrossMetadata(
        RelationQueryNativeResultBranch branch,
        PostgresRelationQueryStorageBinding storageBinding,
        ImmutableArray<PostgresRelationQuerySelectedField> selectedFields,
        ImmutableArray<PostgresRelationQueryResultFieldBinding> resultFields,
        ImmutableArray<PostgresRelationQueryPresenceBinding> presenceBindings,
        ImmutableArray<PostgresRelationQuerySuppliedFieldBinding> suppliedFields,
        PostgresRelationQueryRelationKeyBinding? relationKey,
        ImmutableArray<PostgresRelationQueryInvariantBinding> invariants,
        RelationQueryNativeCompilationProvenance provenance)
    {
        if (!provenance.CoveredNodes.Contains(branch.Node))
        {
            throw new ArgumentException(
                "The artifact branch node must be included in provenance covered nodes.",
                nameof(provenance));
        }

        var tablesByPlacement = storageBinding.Tables.ToDictionary(static table => table.PlacementBinding);
        foreach (var selected in selectedFields)
        {
            if (!tablesByPlacement.TryGetValue(selected.PlacementBinding, out var table))
            {
                throw new ArgumentException(
                    "Every selected field must resolve to its exact persisted PostgreSQL table placement.",
                    nameof(selectedFields));
            }

            var physical = table.Fields.SingleOrDefault(field => field.Input == selected.Input);
            if (physical is null
                || table.Shape != selected.Field.Shape
                || physical.SemanticPath != selected.Field.Path
                || !string.Equals(physical.ColumnName, selected.ColumnName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Selected-field metadata must match the exact shape, path, and column in the persisted storage binding.",
                    nameof(selectedFields));
            }
        }

        var branchFields = branch.Fields
            .OrderBy(static field => field.Shape.GraphId.Value, StringComparer.Ordinal)
            .ThenBy(static field => field.Shape.ShapeId.Value, StringComparer.Ordinal)
            .ThenBy(static field => field.Path.ToString(), StringComparer.Ordinal);
        var reconstructedFields = resultFields.Select(static field => field.Field)
            .OrderBy(static field => field.Shape.GraphId.Value, StringComparer.Ordinal)
            .ThenBy(static field => field.Shape.ShapeId.Value, StringComparer.Ordinal)
            .ThenBy(static field => field.Path.ToString(), StringComparer.Ordinal);
        if (!branchFields.SequenceEqual(reconstructedFields))
        {
            throw new ArgumentException(
                "Artifact result fields must reconstruct the exact demanded branch fields.",
                nameof(resultFields));
        }

        var physicalInputs = selectedFields.Select(static field => field.Input).ToHashSet();
        var suppliedInputs = suppliedFields.Select(static field => field.Input).ToHashSet();
        if (physicalInputs.Overlaps(suppliedInputs))
        {
            throw new ArgumentException(
                "An artifact input cannot be both physically selected and supplied at invocation time.",
                nameof(suppliedFields));
        }

        var artifactInputs = physicalInputs.Concat(suppliedInputs)
            .OrderBy(static input => input.Value, StringComparer.Ordinal);
        if (!artifactInputs.SequenceEqual(provenance.InputFields))
        {
            throw new ArgumentException(
                "Artifact physical and supplied fields must match the exact provenance input-field set.",
                nameof(selectedFields));
        }

        var presence = presenceBindings.Select(static marker => marker.Binding).ToHashSet();
        if (presenceBindings.Any(marker => !tablesByPlacement.ContainsKey(marker.PlacementBinding)))
        {
            throw new ArgumentException(
                "Every outer-presence marker must resolve to its persisted PostgreSQL table placement.",
                nameof(presenceBindings));
        }
        if (resultFields.SelectMany(static field => field.PresenceDependencies)
            .Any(dependency => !presence.Contains(dependency)))
        {
            throw new ArgumentException(
                "Every result-field presence dependency must resolve to artifact presence metadata.",
                nameof(resultFields));
        }

        var aliases = resultFields.Select(static field => field.Alias)
            .Concat(presenceBindings.Select(static marker => marker.Alias))
            .Concat(relationKey is null ? [] : [relationKey.Alias])
            .Concat(invariants.Select(static invariant => invariant.Alias))
            .ToArray();
        if (aliases.Distinct(StringComparer.Ordinal).Count() != aliases.Length)
        {
            throw new ArgumentException(
                "Artifact result, presence, key, and invariant aliases must be globally unique.",
                nameof(resultFields));
        }
    }

    static void ValidateRuntimeBindings(
        PostgresSqlCommandTemplate statement,
        ImmutableArray<PostgresRelationQuerySuppliedFieldBinding> suppliedFields,
        ImmutableArray<PostgresRelationQueryParameterBinding> parameters)
    {
        var runtimeSlots = statement.Parameters
            .Where(static slot => slot.Kind == PostgresSqlParameterBindingKind.Runtime)
            .ToDictionary(static slot => slot.Position);
        var metadata = suppliedFields
            .Select(static field => (field.Position, Binding: field.RuntimeBinding))
            .Concat(parameters.Select(static parameter =>
                (parameter.Position, Binding: parameter.RuntimeBinding)))
            .ToArray();
        if (metadata.Select(static item => item.Position).Distinct().Count() != metadata.Length)
            throw new ArgumentException("Artifact runtime-binding positions cannot repeat.", nameof(parameters));
        if (metadata.Length != runtimeSlots.Count
            || metadata.Any(item => !runtimeSlots.TryGetValue(item.Position, out var slot)
                                    || !string.Equals(slot.Binding, item.Binding, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Artifact runtime-binding metadata must align exactly with every runtime SQL slot.",
                nameof(parameters));
        }
    }
}

/// <summary>
/// Strict JSON persistence and consistency validation for trusted, versioned PostgreSQL native relation/query
/// artifacts.
/// </summary>
public static class PostgresRelationQueryArtifactJsonSerializer
{
    static JsonSerializerOptions CreateOptions(bool indented = false) =>
        RelationQueryJsonSerializer.CreateOptions(indented);

    /// <summary>Serializes one validated PostgreSQL native artifact.</summary>
    /// <param name="artifact">Artifact to serialize.</param>
    /// <param name="indented">Whether serialized JSON is indented.</param>
    /// <returns>Portable versioned artifact JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">Artifact metadata contains an unsupported JSON type.</exception>
    public static string Serialize(
        PostgresRelationQueryCompiledArtifact artifact,
        bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return JsonSerializer.Serialize(artifact, CreateOptions(indented));
    }

    /// <summary>Deserializes and validates one trusted current-version PostgreSQL native artifact.</summary>
    /// <remarks>
    /// A native artifact contains executable PostgreSQL command text. The deterministic fingerprint detects stale or
    /// inconsistent content; it is not an authenticity or authorization mechanism. Callers must obtain
    /// <paramref name="json"/> from a trusted source or verify it with an application-owned cryptographic integrity
    /// mechanism before calling this method. Runtime semantic values remain positional parameters and are never read
    /// from SQL text.
    /// </remarks>
    /// <param name="json">Trusted persisted artifact JSON.</param>
    /// <returns>An artifact whose schema, fingerprint, and runtime slots have been validated.</returns>
    /// <exception cref="ArgumentException"><paramref name="json"/> is empty or white space.</exception>
    /// <exception cref="JsonException">JSON is malformed, unsupported, inconsistent, or has a stale fingerprint.</exception>
    public static PostgresRelationQueryCompiledArtifact DeserializeTrusted(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("PostgreSQL artifact JSON cannot be empty.", nameof(json));

        using (var document = JsonDocument.Parse(json))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("A PostgreSQL native artifact must be a JSON object.");
            if (TryFindDuplicateProperty(document.RootElement, path: string.Empty, out var duplicate))
                throw new JsonException($"PostgreSQL native artifact JSON contains duplicate property '{duplicate}'.");
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("A PostgreSQL native artifact must declare a string schemaVersion.");
            }
            if (!string.Equals(
                    schemaVersion.GetString(),
                    PostgresRelationQueryCompiledArtifact.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"Unsupported PostgreSQL native-artifact schema '{schemaVersion.GetString()}'.");
            }
        }

        try
        {
            return JsonSerializer.Deserialize<PostgresRelationQueryCompiledArtifact>(json, CreateOptions())
                   ?? throw new JsonException("PostgreSQL artifact JSON produced no artifact.");
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new JsonException("PostgreSQL native-artifact metadata failed validation.", exception);
        }
    }

    static bool TryFindDuplicateProperty(
        JsonElement element,
        string path,
        out string duplicateLocation)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = $"{path}/{property.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";
                    if (!names.Add(property.Name))
                    {
                        duplicateLocation = propertyPath;
                        return true;
                    }
                    if (TryFindDuplicateProperty(property.Value, propertyPath, out duplicateLocation))
                        return true;
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindDuplicateProperty(item, $"{path}/{index}", out duplicateLocation))
                        return true;
                    index++;
                }
                break;
        }

        duplicateLocation = string.Empty;
        return false;
    }
}

internal static class PostgresRelationQueryValueConverter
{
    public static object? Convert(
        ObservationValue value,
        PostgresRelationQueryValueEncoding encoding,
        string semanticIdentity)
    {
        if (value.Kind == ObservationValueKind.Null)
            return null;
        if (value.Kind == ObservationValueKind.Undefined)
            throw Invalid(value, encoding, semanticIdentity);

        try
        {
            return encoding switch
            {
                PostgresRelationQueryValueEncoding.Boolean when value.Kind == ObservationValueKind.Bool => value.Bool,
                PostgresRelationQueryValueEncoding.Int32 when TryGetIntegralDecimal(value, out var int32) =>
                    checked((int)int32),
                PostgresRelationQueryValueEncoding.Int64 when TryGetIntegralDecimal(value, out var int64) =>
                    checked((long)int64),
                PostgresRelationQueryValueEncoding.Numeric when value.TryGetCanonicalNumericDecimal(out var numeric) =>
                    numeric,
                PostgresRelationQueryValueEncoding.Text when value.Kind == ObservationValueKind.String => value.String!,
                PostgresRelationQueryValueEncoding.Uuid when value.Kind == ObservationValueKind.String =>
                    Guid.Parse(value.GetRequiredString()),
                PostgresRelationQueryValueEncoding.Date when value.TryGetDateOnly(out var date) => date,
                PostgresRelationQueryValueEncoding.Timestamp when TryGetCivilDateTime(value, out var timestamp)
                    && timestamp.Ticks % 10 == 0 => timestamp,
                PostgresRelationQueryValueEncoding.TimestampWithTimeZone when value.TryGetInstant(out var instant)
                    && instant.Ticks % 10 == 0 =>
                    instant.ToUniversalTime(),
                PostgresRelationQueryValueEncoding.Bytea when value.Kind == ObservationValueKind.Bytes => value.Bytes.ToArray(),
                _ => throw Invalid(value, encoding, semanticIdentity)
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"Semantic value '{semanticIdentity}' cannot be represented as PostgreSQL {encoding}.",
                semanticIdentity,
                exception);
        }
    }

    static bool TryGetCivilDateTime(ObservationValue value, out DateTime result)
    {
        var text = value.Kind switch
        {
            ObservationValueKind.String or ObservationValueKind.DateTimeOffset => value.String,
            _ => null
        };
        if (text is null
            || !DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out result)
            || result.Kind != DateTimeKind.Unspecified)
        {
            result = default;
            return false;
        }
        return true;
    }

    static bool TryGetIntegralDecimal(ObservationValue value, out decimal result)
    {
        if (value.TryGetCanonicalNumericDecimal(out result)
            && decimal.Truncate(result) == result)
        {
            return true;
        }

        result = default;
        return false;
    }

    static ArgumentException Invalid(
        ObservationValue value,
        PostgresRelationQueryValueEncoding encoding,
        string semanticIdentity) =>
        new(
            $"Semantic value '{semanticIdentity}' of kind '{value.Kind}' cannot be represented as PostgreSQL {encoding}.",
            semanticIdentity);
}

/// <summary>Structured result of compiling selected canonical branches to PostgreSQL SQL.</summary>
public sealed class PostgresRelationQueryCompilationResult
{
    internal PostgresRelationQueryCompilationResult(
        RelationQueryNativeCompilationStatus status,
        ImmutableArray<PostgresRelationQueryCompiledArtifact> artifacts,
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
        if (status == RelationQueryNativeCompilationStatus.Exact
            && (normalizedArtifacts.IsDefaultOrEmpty || hasErrors))
        {
            throw new ArgumentException("Exact compilation requires artifacts and no errors.", nameof(status));
        }
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
    public ImmutableArray<PostgresRelationQueryCompiledArtifact> Artifacts { get; }

    /// <summary>Structured diagnostics in deterministic attribution order.</summary>
    public ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics { get; }

    /// <summary>Whether every selected branch compiled exactly.</summary>
    public bool IsSuccessful => Status == RelationQueryNativeCompilationStatus.Exact;
}

/// <summary>Stable PostgreSQL-specific native-compilation diagnostic codes.</summary>
public static class PostgresRelationQueryCompilationDiagnosticCodes
{
    /// <summary>The PostgreSQL binding conflicts with placement, affinity, or target facts.</summary>
    public const string StorageBindingMismatch = "REL2250";

    /// <summary>The selected branch topology cannot be represented by the current native compiler.</summary>
    public const string UnsupportedBranchTopology = "REL2251";

    /// <summary>A logical operator or pipeline position is unsupported.</summary>
    public const string UnsupportedLogicalOperator = "REL2252";

    /// <summary>A canonical expression cannot be lowered exactly.</summary>
    public const string UnsupportedExpression = "REL2253";

    /// <summary>A demanded field input lacks one exact PostgreSQL column binding.</summary>
    public const string FieldBindingMissing = "REL2254";

    /// <summary>Null, missing, collation, ordering, or snapshot semantics cannot be proven exact.</summary>
    public const string GuaranteeUnavailable = "REL2255";

    /// <summary>A canonical invocation parameter cannot be represented by the portable PostgreSQL contract.</summary>
    public const string ParameterUnsupported = "REL2256";

    /// <summary>An aggregate operation or numeric/result contract cannot be preserved exactly.</summary>
    public const string AggregateUnsupported = "REL2257";

    /// <summary>Stable offset or keyset page membership cannot be proven.</summary>
    public const string PagingUnstable = "REL2258";

    /// <summary>A relationship traversal lacks exact physical endpoint evidence.</summary>
    public const string RelationshipEndpointMissing = "REL2259";

    /// <summary>A native statement would cross a PostgreSQL source or execution domain.</summary>
    public const string CrossSourceJoin = "REL2260";

    /// <summary>A canonical join kind or predicate cannot be preserved exactly.</summary>
    public const string JoinUnsupported = "REL2261";

    /// <summary>A canonical temporal domain, boundary, null rule, or interval guarantee is unavailable.</summary>
    public const string TemporalJoinUnsupported = "REL2262";

    /// <summary>Artifact construction failed an internal consistency check.</summary>
    public const string ArtifactInvalid = "REL2263";

    /// <summary>The requested runtime result observability cannot be produced by the current PostgreSQL compiler.</summary>
    public const string ResultObservabilityUnsupported = "REL2264";

    /// <summary>A relation terminal requires a root invocation or invariant contract unavailable to the current compiler.</summary>
    public const string RelationTerminalUnsupported = "REL2265";
}
