using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;

namespace Cohesive.Storage.Materialization;

/// <summary>Exact portable reference to one canonical materialization definition.</summary>
public sealed record MaterializationDefinitionReference
{
    /// <summary>Current durable reference schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-definition-reference/v1";

    /// <summary>Creates or deserializes one exact materialization-definition reference.</summary>
    /// <param name="schemaVersion">Exact durable reference schema.</param>
    /// <param name="materialization">Stable logical materialization identity.</param>
    /// <param name="definitionFingerprint">Exact canonical definition content fence.</param>
    /// <exception cref="ArgumentNullException">A required reference component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema or materialization identity is invalid.</exception>
    [JsonConstructor]
    public MaterializationDefinitionReference(
        string schemaVersion,
        MaterializationId materialization,
        ExecutionDefinitionFingerprint definitionFingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Materialization-definition reference schema '{schemaVersion}' is unsupported.",
                nameof(schemaVersion));
        }

        MaterializationContract.RequireDefinedIdentity(materialization.Value, nameof(materialization));
        Materialization = materialization;
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
    }

    /// <summary>Exact durable reference schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable logical materialization identity.</summary>
    public MaterializationId Materialization { get; }

    /// <summary>Exact canonical materialization-definition content fence.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Creates an exact reference to a verified materialization document.</summary>
    /// <param name="document">Current canonical materialization document.</param>
    /// <returns>A reference containing its identity and definition fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The document is stale, semantically invalid, or uses an unsupported schema.</exception>
    /// <exception cref="System.Text.Json.JsonException">The definition cannot be serialized for fingerprint verification.</exception>
    /// <exception cref="NotSupportedException">The definition contains a runtime value unsupported by serialization.</exception>
    /// <exception cref="InvalidOperationException">The definition has no portable fingerprint representation.</exception>
    public static MaterializationDefinitionReference FromDocument(MaterializationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.SchemaVersion, MaterializationDocument.CurrentSchemaVersion, StringComparison.Ordinal)
            || document.DefinitionFingerprint != MaterializationDefinitionFingerprinter.Compute(document.Definition))
        {
            throw new ArgumentException("A definition reference requires an exact current materialization document.", nameof(document));
        }

        var validation = MaterializationDefinitionValidator.Validate(document.Definition);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "A definition reference requires a semantically valid materialization: "
                + string.Join(" ", validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(document));
        }

        return new(CurrentSchemaVersion, document.Definition.Id, document.DefinitionFingerprint);
    }
}

/// <summary>Provider- and domain-neutral identity independently placed by a rebuild request.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationPlacementSubjectId
{
    /// <summary>Creates a placement-subject identity.</summary>
    /// <param name="value">Stable identity whose meaning is supplied by the request selector.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationPlacementSubjectId(string value) =>
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Stable provider-neutral subject identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable subject identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Portable closed selection of placement subjects for a rebuild request.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$selection")]
[JsonDerivedType(typeof(MaterializationExplicitPlacementSubjectSelection), "explicit")]
[JsonDerivedType(typeof(MaterializationRelationsPlacementSubjectSelection), "relations")]
public abstract record MaterializationPlacementSubjectSelection;

/// <summary>Explicit finite placement-subject selection.</summary>
public sealed record MaterializationExplicitPlacementSubjectSelection : MaterializationPlacementSubjectSelection
{
    /// <summary>Creates a normalized explicit finite selection.</summary>
    /// <param name="subjects">Exact selected subjects; an empty selection is a valid no-op request.</param>
    /// <exception cref="ArgumentException">A subject identity is default or duplicated.</exception>
    [JsonConstructor]
    public MaterializationExplicitPlacementSubjectSelection(
        ImmutableArray<MaterializationPlacementSubjectId> subjects) =>
        Subjects = MaterializationRebuildPlanningContract.NormalizeSubjects(
            subjects.IsDefault ? [] : subjects,
            nameof(subjects),
            allowEmpty: true);

    /// <summary>Exact selected subjects in canonical ordinal identity order.</summary>
    public ImmutableArray<MaterializationPlacementSubjectId> Subjects { get; }

    /// <summary>Compares explicit selections structurally in canonical subject order.</summary>
    /// <param name="other">Selection to compare.</param>
    /// <returns><see langword="true"/> when both select the same exact subjects.</returns>
    public bool Equals(MaterializationExplicitPlacementSubjectSelection? other) =>
        ReferenceEquals(this, other) || other is not null && Subjects.SequenceEqual(other.Subjects);

    /// <summary>Returns a structural hash code for all selected subjects.</summary>
    /// <returns>A hash code consistent with structural equality.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var subject in Subjects)
            hash.Add(subject);
        return hash.ToHashCode();
    }
}

/// <summary>Dynamic placement-subject selection expressed by one exact canonical Relations evaluation.</summary>
/// <remarks>
/// The evaluation must explicitly demand one single string identity field. Evaluation results are not retained here:
/// an authoritative evaluator freezes them into <see cref="MaterializationRebuildMembershipEvidence"/>.
/// </remarks>
public sealed record MaterializationRelationsPlacementSubjectSelection : MaterializationPlacementSubjectSelection
{
    /// <summary>Creates and verifies a Relations-backed placement selection.</summary>
    /// <param name="evaluation">Exact target-neutral Relations evaluation selecting one identity field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="evaluation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The evaluation is not plan-bound, has supplied roots, does not compile exactly, or does not select exactly one
    /// single String identity field.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">The evaluation or compiled-plan reference cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">The evaluation contains a runtime value unsupported by serialization.</exception>
    /// <exception cref="InvalidOperationException">The evaluation has no portable fingerprint representation.</exception>
    [JsonConstructor]
    public MaterializationRelationsPlacementSubjectSelection(RelationQueryEvaluation evaluation)
    {
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        if (evaluation.SuppliedRoots is not null)
        {
            throw new ArgumentException(
                "A Relations placement selector cannot carry directly supplied roots; use an explicit selection.",
                nameof(evaluation));
        }

        var compilation = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
        if (!compilation.IsSuccessful || compilation.Plan is not { } plan)
        {
            throw new ArgumentException(
                "A Relations placement selector requires a successfully compiled exact evaluation.",
                nameof(evaluation));
        }

        if (evaluation.PlanReference is null
            || RelationQueryCompiledPlanReferenceFingerprinter.Compute(evaluation.PlanReference)
            != RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(plan)))
        {
            throw new ArgumentException(
                "A Relations placement selector requires exact compiled-plan attribution.",
                nameof(evaluation));
        }

        var fieldOutputs = plan.RequirementGraph.Outputs.Where(static output => output.Field is not null).ToArray();
        var isExplicitSingleFieldDemand = evaluation.DemandOrigin == RelationQueryCompilationDemandOrigin.Explicit
            && (evaluation.Demand.Kind == RelationQueryCompilationDemandKind.RelationFields
                && evaluation.Demand.RelationFields.Length == 1
                || evaluation.Demand.Kind == RelationQueryCompilationDemandKind.QueryResults
                && evaluation.Demand.QueryResults is [var queryResult]
                && queryResult.Selection == RelationQueryFieldSelectionKind.SelectedFields
                && queryResult.Fields.Length == 1);
        if (!isExplicitSingleFieldDemand
            || fieldOutputs is not [var output]
            || output.Field is not { } field
            || !RelationQueryFieldSemantics.IsSingleStringIdentityField(plan, field))
        {
            throw new ArgumentException(
                "A Relations placement selector must explicitly demand exactly one single String identity field.",
                nameof(evaluation));
        }
    }

    /// <summary>Exact target-neutral Relations evaluation, including parameter evidence and demand.</summary>
    public RelationQueryEvaluation Evaluation { get; }

    /// <summary>Compares Relations selections by their canonical evaluation fingerprints.</summary>
    /// <param name="other">Selection to compare.</param>
    /// <returns><see langword="true"/> when both retain the same exact evaluation authority.</returns>
    public bool Equals(MaterializationRelationsPlacementSubjectSelection? other) =>
        ReferenceEquals(this, other)
        || other is not null && Evaluation.Fingerprint == other.Evaluation.Fingerprint;

    /// <summary>Returns a hash code derived from the canonical evaluation fingerprint.</summary>
    /// <returns>A hash code consistent with semantic equality.</returns>
    public override int GetHashCode() => Evaluation.Fingerprint.GetHashCode();
}

/// <summary>Request policy pinning the exact backend pool in which every subject receives one target.</summary>
public sealed record MaterializationRebuildPlacementPolicy
{
    /// <summary>Creates an exactly-one-target-per-subject placement policy.</summary>
    /// <param name="pool">Exact canonical backend-pool definition reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public MaterializationRebuildPlacementPolicy(MaterializationBackendPoolReference pool) =>
        Pool = pool ?? throw new ArgumentNullException(nameof(pool));

    /// <summary>Exact pool within which every selected subject must receive one target.</summary>
    public MaterializationBackendPoolReference Pool { get; }
}

/// <summary>Requested bounded scheduling policy for the outer rebuild plan set.</summary>
public sealed record MaterializationRebuildSchedulingPolicy
{
    /// <summary>Creates requested plan-set scheduling bounds.</summary>
    /// <param name="maximumStartsPerActivation">Maximum leaf starts admitted by one scheduler activation.</param>
    /// <param name="maximumParallelism">Maximum concurrently active leaf rebuilds.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    [JsonConstructor]
    public MaterializationRebuildSchedulingPolicy(int maximumStartsPerActivation, int maximumParallelism)
    {
        if (maximumStartsPerActivation <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumStartsPerActivation), maximumStartsPerActivation, "A start bound must be positive.");
        if (maximumParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumParallelism), maximumParallelism, "A parallelism bound must be positive.");
        MaximumStartsPerActivation = maximumStartsPerActivation;
        MaximumParallelism = maximumParallelism;
    }

    /// <summary>Maximum leaf starts admitted by one scheduler activation.</summary>
    public int MaximumStartsPerActivation { get; }

    /// <summary>Maximum concurrently active leaf rebuilds.</summary>
    public int MaximumParallelism { get; }
}

/// <summary>Required paired read/write routing visibility coordination across placement slices.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationRebuildPromotionMode
{
    /// <summary>Each ready target may switch its paired read and incremental-write routes independently.</summary>
    Independent = 0,

    /// <summary>
    /// Every leaf must validate before the first visibility change; paired read/write routes then change
    /// progressively under an explicit partial-failure policy.
    /// </summary>
    AllReadyProgressive = 1,

    /// <summary>All paired read/write routes require one authoritative all-or-none visibility transition.</summary>
    AtomicVisibility = 2
}

/// <summary>Required response when progressive visibility fails after one or more slices were promoted.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationProgressivePromotionFailurePolicy
{
    /// <summary>Retain already-promoted slices and stop all remaining promotions.</summary>
    RetainPromotedAndStop = 0,

    /// <summary>Retain promoted slices and continue promoting other independently ready slices.</summary>
    RetainPromotedAndContinue = 1,

    /// <summary>Compensate already-promoted slices back to their preceding visible generations.</summary>
    CompensatePromoted = 2
}

/// <summary>Semantic promotion coordination required by a rebuild request.</summary>
public sealed record MaterializationRebuildPromotionPolicy
{
    /// <summary>Creates a required promotion-coordination policy.</summary>
    /// <param name="mode">Independent, all-ready progressive, or globally atomic paired-routing visibility.</param>
    /// <param name="progressiveFailurePolicy">Required partial-failure semantics for all-ready progressive mode only.</param>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is unsupported.</exception>
    /// <exception cref="ArgumentException">Progressive failure semantics are missing or supplied for another mode.</exception>
    [JsonConstructor]
    public MaterializationRebuildPromotionPolicy(
        MaterializationRebuildPromotionMode mode,
        MaterializationProgressivePromotionFailurePolicy? progressiveFailurePolicy = null)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported promotion coordination mode.");
        if (progressiveFailurePolicy is { } failure && !Enum.IsDefined(failure))
            throw new ArgumentOutOfRangeException(nameof(progressiveFailurePolicy), failure, "Unsupported progressive failure policy.");
        if ((mode == MaterializationRebuildPromotionMode.AllReadyProgressive) != progressiveFailurePolicy.HasValue)
        {
            throw new ArgumentException(
                "Exactly all-ready progressive promotion requires an explicit progressive partial-failure policy.",
                nameof(progressiveFailurePolicy));
        }

        Mode = mode;
        ProgressiveFailurePolicy = progressiveFailurePolicy;
    }

    /// <summary>Independent, all-ready progressive, or globally atomic paired-routing visibility semantics.</summary>
    public MaterializationRebuildPromotionMode Mode { get; }

    /// <summary>Partial-failure semantics for all-ready progressive mode; otherwise <see langword="null"/>.</summary>
    public MaterializationProgressivePromotionFailurePolicy? ProgressiveFailurePolicy { get; }
}

/// <summary>Canonical, provider-neutral intent to rebuild one materialization over a selected subject set.</summary>
public sealed class MaterializationRebuildRequestDocument : IEquatable<MaterializationRebuildRequestDocument>
{
    /// <summary>Current portable request schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-request/v1";

    /// <summary>Creates and verifies a canonical rebuild request.</summary>
    /// <param name="schemaVersion">Exact portable request schema.</param>
    /// <param name="materialization">Exact current materialization definition.</param>
    /// <param name="selection">Explicit or Relations-backed placement-subject selection.</param>
    /// <param name="placement">Exactly-one-target placement policy and pinned pool.</param>
    /// <param name="scheduling">Requested outer scheduling bounds.</param>
    /// <param name="promotion">Required cross-target promotion coordination.</param>
    /// <param name="provenance">Producer and source attribution for explicit request decisions.</param>
    /// <param name="fingerprint">Persisted request fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required artifact is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, materialization, pool affinity, or fingerprint is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical request content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical request content contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Canonical request content has no portable representation.</exception>
    [JsonConstructor]
    public MaterializationRebuildRequestDocument(
        string schemaVersion,
        MaterializationDocument materialization,
        MaterializationPlacementSubjectSelection selection,
        MaterializationRebuildPlacementPolicy placement,
        MaterializationRebuildSchedulingPolicy scheduling,
        MaterializationRebuildPromotionPolicy promotion,
        ExecutionProvenance provenance,
        MaterializationRebuildRequestFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Rebuild-request schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));

        Materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
        MaterializationReference = MaterializationDefinitionReference.FromDocument(materialization);
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        Placement = placement ?? throw new ArgumentNullException(nameof(placement));
        Scheduling = scheduling ?? throw new ArgumentNullException(nameof(scheduling));
        Promotion = promotion ?? throw new ArgumentNullException(nameof(promotion));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (placement.Pool.Materialization != MaterializationReference)
            throw new ArgumentException("The requested backend pool must serve the exact materialization definition.", nameof(placement));

        var computed = MaterializationRebuildPlanningFingerprinters.ComputeRequest(this);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The rebuild-request fingerprint does not match canonical content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact portable request schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact current materialization definition.</summary>
    public MaterializationDocument Materialization { get; }

    /// <summary>Exact materialization identity and definition fence derived from <see cref="Materialization"/>.</summary>
    [JsonIgnore]
    public MaterializationDefinitionReference MaterializationReference { get; }

    /// <summary>Explicit or Relations-backed placement-subject selection.</summary>
    public MaterializationPlacementSubjectSelection Selection { get; }

    /// <summary>Exactly-one-target placement policy and pinned pool.</summary>
    public MaterializationRebuildPlacementPolicy Placement { get; }

    /// <summary>Requested outer scheduling bounds.</summary>
    public MaterializationRebuildSchedulingPolicy Scheduling { get; }

    /// <summary>Required cross-target promotion coordination.</summary>
    public MaterializationRebuildPromotionPolicy Promotion { get; }

    /// <summary>Producer and source attribution for explicit request decisions.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Deterministic fingerprint of every request-semantic field.</summary>
    public MaterializationRebuildRequestFingerprint Fingerprint { get; }

    /// <summary>Compares requests by their constructor-verified canonical fingerprint.</summary>
    /// <param name="other">Request to compare.</param>
    /// <returns><see langword="true"/> when both requests have identical canonical content.</returns>
    public bool Equals(MaterializationRebuildRequestDocument? other) =>
        ReferenceEquals(this, other) || other is not null && Fingerprint == other.Fingerprint;

    /// <summary>Compares an object with this request by canonical fingerprint.</summary>
    /// <param name="obj">Object to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is a canonically equal request.</returns>
    public override bool Equals(object? obj) =>
        obj is MaterializationRebuildRequestDocument other && Equals(other);

    /// <summary>Returns a hash code derived from the canonical request fingerprint.</summary>
    /// <returns>A stable hash for canonically equal requests.</returns>
    public override int GetHashCode() => Fingerprint.GetHashCode();
}

/// <summary>Exact portable reference to one persisted rebuild request.</summary>
public sealed record MaterializationRebuildRequestReference
{
    /// <summary>Current durable request-reference schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-request-reference/v1";

    /// <summary>Creates or deserializes one exact request reference.</summary>
    /// <param name="schemaVersion">Exact durable reference schema.</param>
    /// <param name="materialization">Exact materialization identity and definition fence.</param>
    /// <param name="request">Exact persisted request fingerprint.</param>
    /// <exception cref="ArgumentNullException">A required component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema is unsupported.</exception>
    [JsonConstructor]
    public MaterializationRebuildRequestReference(
        string schemaVersion,
        MaterializationDefinitionReference materialization,
        MaterializationRebuildRequestFingerprint request)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Rebuild-request reference schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        Materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
        Request = request ?? throw new ArgumentNullException(nameof(request));
    }

    /// <summary>Exact durable request-reference schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact materialization identity and definition fence.</summary>
    public MaterializationDefinitionReference Materialization { get; }

    /// <summary>Exact persisted request fingerprint.</summary>
    public MaterializationRebuildRequestFingerprint Request { get; }

    /// <summary>Creates a reference to a verified request document.</summary>
    /// <param name="document">Canonical request document.</param>
    /// <returns>An exact request reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static MaterializationRebuildRequestReference FromDocument(MaterializationRebuildRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(CurrentSchemaVersion, document.MaterializationReference, document.Fingerprint);
    }
}
