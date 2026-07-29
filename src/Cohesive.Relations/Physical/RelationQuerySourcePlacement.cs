using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Physical;

/// <summary>How a source-placement declaration was selected.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQuerySourcePlacementOrigin
{
    /// <summary>The caller declared the placement explicitly.</summary>
    Explicit = 0,

    /// <summary>A named convention selected the placement.</summary>
    Convention = 1
}

/// <summary>Semantic kind of one plan-scoped placement binding.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQuerySourcePlacementBindingKind
{
    /// <summary>The binding places a compiled source-set input.</summary>
    SourceSet = 0,

    /// <summary>The binding places the result side of a relationship traversal.</summary>
    RelationshipTraversal = 1
}

/// <summary>Physical acquisition mode selected for one placement binding.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQuerySourceAcquisitionKind
{
    /// <summary>Runtime input is supplied without an external source read.</summary>
    Supplied = 0,

    /// <summary>The source is enumerated within an explicit bound.</summary>
    BoundedEnumeration = 1,

    /// <summary>The traversal target is acquired using bounded keyed lookup.</summary>
    BoundedLookup = 2
}

/// <summary>Explicit execution limits for one placed source instance.</summary>
public sealed record RelationQuerySourcePlacementLimits
{
    /// <summary>Creates source execution limits.</summary>
    /// <param name="maximumBatchSize">Maximum keys in one lookup request.</param>
    /// <param name="maximumBufferedRows">Maximum cumulative source rows retained during one plan execution.</param>
    /// <param name="maximumFanOut">Maximum related rows accepted for one source occurrence.</param>
    /// <param name="maximumConcurrency">Maximum concurrent requests to this source.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive or exceeds the portable integer range.</exception>
    [JsonConstructor]
    public RelationQuerySourcePlacementLimits(
        long maximumBatchSize,
        long maximumBufferedRows,
        long maximumFanOut,
        long maximumConcurrency)
    {
        MaximumBatchSize = RequireLimit(maximumBatchSize, nameof(maximumBatchSize));
        MaximumBufferedRows = RequireLimit(maximumBufferedRows, nameof(maximumBufferedRows));
        MaximumFanOut = RequireLimit(maximumFanOut, nameof(maximumFanOut));
        MaximumConcurrency = RequireLimit(maximumConcurrency, nameof(maximumConcurrency));
    }

    /// <summary>Maximum keys in one lookup request.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumBatchSize { get; }

    /// <summary>Maximum cumulative source rows retained during one plan execution.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumBufferedRows { get; }

    /// <summary>Maximum related rows accepted for one source occurrence.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumFanOut { get; }

    /// <summary>Maximum concurrent requests to this source.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumConcurrency { get; }

    internal static long RequireLimit(long value, string parameterName)
    {
        const long maximumPortableInteger = 9_007_199_254_740_991;
        if (value is <= 0 or > maximumPortableInteger)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "A physical limit must be positive and portable to JSON runtimes.");
        }

        return value;
    }
}

/// <summary>Physical selector that supplies stable observation identity.</summary>
public sealed record RelationQuerySourceIdentityBinding
{
    /// <summary>Creates an observation-identity binding.</summary>
    /// <param name="shape">Semantic shape whose identity is selected.</param>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="shape"/> is incomplete or the selector is empty.</exception>
    [JsonConstructor]
    public RelationQuerySourceIdentityBinding(QualifiedShapeId shape, string sourceSelector)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("An identity binding requires a graph-qualified shape.", nameof(shape));
        }

        Shape = shape;
        SourceSelector = Guard.RequireNotNullOrWhiteSpace(sourceSelector);
    }

    /// <summary>Semantic shape whose identity is selected.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Stable adapter-interpreted selector.</summary>
    public string SourceSelector { get; }
}

/// <summary>Physical selector for one exact compiled field input.</summary>
public sealed record RelationQuerySourceFieldBinding
{
    /// <summary>Creates a source-field binding.</summary>
    /// <param name="input">Compiled field-input identity.</param>
    /// <param name="semanticPath">Semantic path represented by the selector.</param>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or path is default, or the selector is empty.</exception>
    [JsonConstructor]
    public RelationQuerySourceFieldBinding(
        RelationQueryInputId input,
        FieldPath semanticPath,
        string sourceSelector)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("A field binding requires a compiled input identity.", nameof(input));
        }

        if (semanticPath.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A field binding requires a semantic path.", nameof(semanticPath));
        }

        Input = input;
        SemanticPath = semanticPath;
        SourceSelector = Guard.RequireNotNullOrWhiteSpace(sourceSelector);
    }

    /// <summary>Compiled field-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Semantic path represented by the selector.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Stable adapter-interpreted selector.</summary>
    public string SourceSelector { get; }
}

/// <summary>Physical selector for a relationship reference used by keyed acquisition.</summary>
public sealed record RelationQueryRelationshipKeyBinding
{
    /// <summary>Creates a relationship-key binding.</summary>
    /// <param name="input">Compiled relationship-input identity.</param>
    /// <param name="semanticPath">Canonical relationship-reference path.</param>
    /// <param name="sourceSelector">Stable adapter-interpreted selector.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or path is default, or the selector is empty.</exception>
    [JsonConstructor]
    public RelationQueryRelationshipKeyBinding(
        RelationQueryInputId input,
        FieldPath semanticPath,
        string sourceSelector)
    {
        if (string.IsNullOrWhiteSpace(input.Value))
        {
            throw new ArgumentException("A relationship-key binding requires a compiled input identity.", nameof(input));
        }

        if (semanticPath.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A relationship-key binding requires a semantic path.", nameof(semanticPath));
        }

        Input = input;
        SemanticPath = semanticPath;
        SourceSelector = Guard.RequireNotNullOrWhiteSpace(sourceSelector);
    }

    /// <summary>Compiled relationship-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Canonical relationship-reference path.</summary>
    public FieldPath SemanticPath { get; }

    /// <summary>Stable adapter-interpreted selector.</summary>
    public string SourceSelector { get; }
}

/// <summary>Optional physical selector used to scope source requests to a partition.</summary>
public sealed record RelationQueryPartitionBinding
{
    /// <summary>Creates a source partition binding.</summary>
    /// <param name="sourceSelector">Stable adapter-interpreted partition selector.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sourceSelector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceSelector"/> is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryPartitionBinding(string sourceSelector) =>
        SourceSelector = Guard.RequireNotNullOrWhiteSpace(sourceSelector);

    /// <summary>Stable adapter-interpreted partition selector.</summary>
    public string SourceSelector { get; }
}

/// <summary>One concrete source instance and the exact capability snapshot under which it may execute.</summary>
public sealed record RelationQuerySourceInstance
{
    /// <summary>Creates a placed source instance.</summary>
    /// <param name="id">Stable source-instance identity.</param>
    /// <param name="executionDomain">Execution or consistency domain containing the source.</param>
    /// <param name="targetProfile">Exact target capability profile snapshot.</param>
    /// <param name="limits">Explicit source execution limits.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="targetProfile"/> or <paramref name="limits"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RelationQuerySourceInstance(
        RelationQuerySourceInstanceId id,
        RelationQueryExecutionDomainId executionDomain,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQuerySourcePlacementLimits limits)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A source instance requires an identity.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(executionDomain.Value))
        {
            throw new ArgumentException("A source instance requires an execution domain.", nameof(executionDomain));
        }

        Id = id;
        ExecutionDomain = executionDomain;
        TargetProfile = Guard.RequireNotNull(targetProfile);
        Limits = Guard.RequireNotNull(limits);
    }

    /// <summary>Stable source-instance identity.</summary>
    public RelationQuerySourceInstanceId Id { get; }

    /// <summary>Execution or consistency domain containing the source.</summary>
    public RelationQueryExecutionDomainId ExecutionDomain { get; }

    /// <summary>Exact target capability profile snapshot.</summary>
    public RelationQueryTargetCapabilityProfile TargetProfile { get; }

    /// <summary>Explicit source execution limits.</summary>
    public RelationQuerySourcePlacementLimits Limits { get; }
}

/// <summary>Plan-scoped binding from one compiled source or traversal input to a physical source.</summary>
public sealed record RelationQuerySourcePlacementBinding
{
    /// <summary>Creates one source-placement binding.</summary>
    /// <param name="id">Stable placement-binding identity.</param>
    /// <param name="input">Compiled source-set or relationship-input identity.</param>
    /// <param name="node">Canonical source or traversal node.</param>
    /// <param name="binding">Canonical value binding supplied by the placed source.</param>
    /// <param name="shape">Semantic shape supplied by the placed source.</param>
    /// <param name="source">Physical source instance.</param>
    /// <param name="kind">Source-set or relationship-traversal placement kind.</param>
    /// <param name="acquisition">Physical acquisition mode.</param>
    /// <param name="origin">Whether the placement was explicit or convention-derived.</param>
    /// <param name="identity">Observation-identity selector, or <see langword="null"/> when identity is not required.</param>
    /// <param name="fields">Physical selectors for compiled fields.</param>
    /// <param name="relationshipKeys">Relationship-reference selectors needed by acquisition.</param>
    /// <param name="partition">Optional partition selector.</param>
    /// <exception cref="ArgumentException">An identity is default, a collection conflicts, or kind and acquisition are incompatible.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/>, <paramref name="acquisition"/>, or <paramref name="origin"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQuerySourcePlacementBinding(
        RelationQuerySourcePlacementBindingId id,
        RelationQueryInputId input,
        QueryNodeId node,
        ValueBindingId binding,
        QualifiedShapeId shape,
        RelationQuerySourceInstanceId source,
        RelationQuerySourcePlacementBindingKind kind,
        RelationQuerySourceAcquisitionKind acquisition,
        RelationQuerySourcePlacementOrigin origin,
        RelationQuerySourceIdentityBinding? identity = null,
        ImmutableArray<RelationQuerySourceFieldBinding> fields = default,
        ImmutableArray<RelationQueryRelationshipKeyBinding> relationshipKeys = default,
        RelationQueryPartitionBinding? partition = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value) || string.IsNullOrWhiteSpace(input.Value)
            || string.IsNullOrWhiteSpace(node.Value) || string.IsNullOrWhiteSpace(binding.Value))
        {
            throw new ArgumentException("A placement binding requires non-default identities.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("A placement binding requires a graph-qualified shape.", nameof(shape));
        }

        if (string.IsNullOrWhiteSpace(source.Value))
        {
            throw new ArgumentException("A placement binding requires a source instance.", nameof(source));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported placement-binding kind.");
        }

        if (!Enum.IsDefined(acquisition))
        {
            throw new ArgumentOutOfRangeException(nameof(acquisition), acquisition, "Unsupported source-acquisition kind.");
        }

        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported placement origin.");
        }

        if (kind == RelationQuerySourcePlacementBindingKind.RelationshipTraversal
            && acquisition != RelationQuerySourceAcquisitionKind.BoundedLookup)
        {
            throw new ArgumentException("A traversal placement requires bounded lookup acquisition.", nameof(acquisition));
        }

        if (kind == RelationQuerySourcePlacementBindingKind.SourceSet
            && acquisition == RelationQuerySourceAcquisitionKind.BoundedLookup)
        {
            throw new ArgumentException("A source-set placement cannot use relationship lookup acquisition.", nameof(acquisition));
        }

        var normalizedFields = Normalize(fields, static field => field.Input.Value, nameof(fields));
        var normalizedKeys = Normalize(relationshipKeys, static key => key.Input.Value, nameof(relationshipKeys));
        if (normalizedFields.GroupBy(static field => field.Input).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A placement cannot bind one compiled field more than once.", nameof(fields));
        }

        if (normalizedKeys.GroupBy(static key => key.Input).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A placement cannot bind one relationship key more than once.", nameof(relationshipKeys));
        }

        if (identity is not null && identity.Shape != shape)
        {
            throw new ArgumentException("An identity selector must describe the placed shape.", nameof(identity));
        }

        Id = id;
        Input = input;
        Node = node;
        Binding = binding;
        Shape = shape;
        Source = source;
        Kind = kind;
        Acquisition = acquisition;
        Origin = origin;
        Identity = identity;
        Fields = normalizedFields;
        RelationshipKeys = normalizedKeys;
        Partition = partition;
    }

    /// <summary>Stable placement-binding identity.</summary>
    public RelationQuerySourcePlacementBindingId Id { get; }

    /// <summary>Compiled source-set or relationship-input identity.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Canonical source or traversal node.</summary>
    public QueryNodeId Node { get; }

    /// <summary>Canonical value binding supplied by the placed source.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Semantic shape supplied by the placed source.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Physical source instance.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>Source-set or relationship-traversal placement kind.</summary>
    public RelationQuerySourcePlacementBindingKind Kind { get; }

    /// <summary>Physical acquisition mode.</summary>
    public RelationQuerySourceAcquisitionKind Acquisition { get; }

    /// <summary>Whether the placement was explicit or convention-derived.</summary>
    public RelationQuerySourcePlacementOrigin Origin { get; }

    /// <summary>Observation-identity selector, or <see langword="null"/>.</summary>
    public RelationQuerySourceIdentityBinding? Identity { get; }

    /// <summary>Physical selectors for compiled fields.</summary>
    public ImmutableArray<RelationQuerySourceFieldBinding> Fields { get; }

    /// <summary>Relationship-reference selectors needed by acquisition.</summary>
    public ImmutableArray<RelationQueryRelationshipKeyBinding> RelationshipKeys { get; }

    /// <summary>Optional partition selector.</summary>
    public RelationQueryPartitionBinding? Partition { get; }

    static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null))
        {
            throw new ArgumentException("Placement metadata cannot contain null entries.", parameterName);
        }

        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }
}

/// <summary>Portable, versioned source placement for one exact demand-scoped compiled plan.</summary>
public sealed class RelationQuerySourcePlacement
{
    /// <summary>Current portable source-placement schema version.</summary>
    public const string CurrentSchemaVersion = "relation-query-source-placement/v2";

    /// <summary>Creates a normalized source-placement artifact.</summary>
    /// <param name="schemaVersion">Portable placement schema version.</param>
    /// <param name="plan">Exact compiled-plan reference to which placement applies.</param>
    /// <param name="conventionSetVersion">Version of conventions used to derive convention placements.</param>
    /// <param name="sourceInstances">Concrete physical source instances.</param>
    /// <param name="bindings">Plan-scoped source and traversal bindings.</param>
    /// <param name="fingerprint">Persisted fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <param name="configurationDecisions">
    /// Optional, potentially partial per-setting attribution for effective explicit, profile, adapter, and framework
    /// values. Every setting must identify a fact present in this artifact, and a binding's source-selection decision
    /// must agree with its <see cref="RelationQuerySourcePlacementBinding.Origin"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="plan"/>, or <paramref name="conventionSetVersion"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A string is empty; a collection is empty, contains null, or conflicts; a source is unknown; a
    /// configuration setting is repeated, does not identify an actual placement fact, or conflicts with placement
    /// origin; or the fingerprint is stale.
    /// </exception>
    [JsonConstructor]
    public RelationQuerySourcePlacement(
        string schemaVersion,
        RelationQueryCompiledPlanReference plan,
        string conventionSetVersion,
        ImmutableArray<RelationQuerySourceInstance> sourceInstances,
        ImmutableArray<RelationQuerySourcePlacementBinding> bindings,
        RelationQuerySourcePlacementFingerprint? fingerprint = null,
        ImmutableArray<EffectiveConfigurationDecision> configurationDecisions = default)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported source-placement schema version '{SchemaVersion}'.", nameof(schemaVersion));
        }

        Plan = Guard.RequireNotNull(plan);
        ConventionSetVersion = Guard.RequireNotNullOrWhiteSpace(conventionSetVersion);
        SourceInstances = NormalizeRequired(sourceInstances, static source => source.Id.Value, nameof(sourceInstances));
        Bindings = NormalizeRequired(bindings, static binding => binding.Id.Value, nameof(bindings));
        if (SourceInstances.GroupBy(static source => source.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Source instances cannot repeat an identity.", nameof(sourceInstances));
        }

        if (Bindings.GroupBy(static binding => binding.Id).Any(static group => group.Count() > 1)
            || Bindings.GroupBy(static binding => binding.Input).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Placement bindings cannot repeat a binding or compiled input identity.", nameof(bindings));
        }

        var sourceIds = SourceInstances.Select(static source => source.Id).ToHashSet();
        if (Bindings.Any(binding => !sourceIds.Contains(binding.Source)))
        {
            throw new ArgumentException("Every placement binding must reference a declared source instance.", nameof(bindings));
        }

        if (Bindings.Any(binding => !Plan.Inputs.Contains(binding.Input)))
        {
            throw new ArgumentException("Every placement binding must reference an input in the compiled-plan reference.", nameof(bindings));
        }

        var fieldBindings = Bindings.SelectMany(static binding => binding.Fields).ToArray();
        if (fieldBindings.Any(field => !Plan.Inputs.Contains(field.Input)))
        {
            throw new ArgumentException("Every physical field binding must reference an input in the compiled-plan reference.", nameof(bindings));
        }

        if (fieldBindings.GroupBy(static field => field.Input).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A compiled field input cannot be placed more than once.", nameof(bindings));
        }

        var relationshipKeys = Bindings.SelectMany(static binding => binding.RelationshipKeys).ToArray();
        if (relationshipKeys.Any(key => !Plan.Inputs.Contains(key.Input)))
        {
            throw new ArgumentException("Every relationship-key binding must reference an input in the compiled-plan reference.", nameof(bindings));
        }

        if (relationshipKeys.GroupBy(static key => key.Input).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A compiled relationship key cannot be placed more than once.", nameof(bindings));
        }

        var normalizedDecisions = configurationDecisions.IsDefault ? [] : configurationDecisions;
        if (normalizedDecisions.Any(static decision => decision is null))
        {
            throw new ArgumentException("Configuration decisions cannot contain null entries.", nameof(configurationDecisions));
        }

        if (normalizedDecisions.GroupBy(static decision => decision.Setting, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Configuration decisions cannot repeat a setting identity.",
                nameof(configurationDecisions));
        }

        var knownConfigurationSettings = GetConfigurationSettings(SourceInstances, Bindings);
        var foreignConfigurationSettings = normalizedDecisions
            .Select(static decision => decision.Setting)
            .Where(setting => !knownConfigurationSettings.Contains(setting))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (foreignConfigurationSettings.Length > 0)
        {
            throw new ArgumentException(
                $"Configuration decision setting '{foreignConfigurationSettings[0]}' does not identify an actual "
                + "artifact, source, placement binding, or field fact.",
                nameof(configurationDecisions));
        }

        ValidateSourceSelectionDecisions(normalizedDecisions, Bindings, nameof(configurationDecisions));

        ConfigurationDecisions =
        [
            .. normalizedDecisions.OrderBy(static decision => decision.Setting, StringComparer.Ordinal)
        ];

        var computed = RelationQuerySourcePlacementFingerprinter.Compute(this);
        if (fingerprint is not null && !Equals(fingerprint, computed))
        {
            throw new ArgumentException("The source-placement fingerprint does not match normalized content.", nameof(fingerprint));
        }

        Fingerprint = computed;
    }

    /// <summary>Portable placement schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact compiled-plan reference to which placement applies.</summary>
    public RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>Version of conventions used to derive convention placements.</summary>
    public string ConventionSetVersion { get; }

    /// <summary>Concrete source instances in deterministic identity order.</summary>
    public ImmutableArray<RelationQuerySourceInstance> SourceInstances { get; }

    /// <summary>Plan-scoped bindings in deterministic identity order.</summary>
    public ImmutableArray<RelationQuerySourcePlacementBinding> Bindings { get; }

    /// <summary>Per-setting effective configuration attribution in deterministic setting order.</summary>
    public ImmutableArray<EffectiveConfigurationDecision> ConfigurationDecisions { get; }

    /// <summary>Deterministic identity of this placement artifact.</summary>
    public RelationQuerySourcePlacementFingerprint Fingerprint { get; }

    static ImmutableArray<T> NormalizeRequired<T>(ImmutableArray<T> values, Func<T, string> key, string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one placement entry is required.", parameterName);
        }

        if (normalized.Any(static value => value is null))
        {
            throw new ArgumentException("Placement collections cannot contain null entries.", parameterName);
        }

        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }

    static HashSet<string> GetConfigurationSettings(
        ImmutableArray<RelationQuerySourceInstance> sources,
        ImmutableArray<RelationQuerySourcePlacementBinding> bindings)
    {
        HashSet<string> settings = new(StringComparer.Ordinal)
        {
            "placement/convention-set-version"
        };
        foreach (var source in sources)
        {
            var prefix = $"source/{EncodeConfigurationSegment(source.Id.Value)}";
            settings.Add($"{prefix}/id");
            settings.Add($"{prefix}/execution-domain");
            settings.Add($"{prefix}/target-profile");
            settings.Add($"{prefix}/limits/maximum-batch-size");
            settings.Add($"{prefix}/limits/maximum-buffered-rows");
            settings.Add($"{prefix}/limits/maximum-fan-out");
            settings.Add($"{prefix}/limits/maximum-concurrency");
        }

        foreach (var binding in bindings)
        {
            var prefix = $"placement/{EncodeConfigurationSegment(binding.Id.Value)}";
            settings.Add($"{prefix}/id");
            settings.Add($"{prefix}/source");
            settings.Add($"{prefix}/acquisition");
            if (binding.Identity is not null)
            {
                settings.Add($"{prefix}/identity/source-selector");
            }

            if (!binding.RelationshipKeys.IsDefaultOrEmpty)
            {
                settings.Add($"{prefix}/relationship-key/source-selector");
            }

            if (binding.Partition is not null)
            {
                settings.Add($"{prefix}/partition/source-selector");
            }

            foreach (var field in binding.Fields)
            {
                settings.Add(
                    $"{prefix}/field/{EncodeConfigurationSegment(field.Input.Value)}/source-selector");
            }
        }

        return settings;
    }

    static string EncodeConfigurationSegment(string value) => Uri.EscapeDataString(value);

    static void ValidateSourceSelectionDecisions(
        ImmutableArray<EffectiveConfigurationDecision> decisions,
        ImmutableArray<RelationQuerySourcePlacementBinding> bindings,
        string parameterName)
    {
        var decisionsBySetting = decisions.ToDictionary(static decision => decision.Setting, StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            var setting = $"placement/{EncodeConfigurationSegment(binding.Id.Value)}/source";
            if (!decisionsBySetting.TryGetValue(setting, out var decision))
            {
                continue;
            }

            var isConsistent = binding.Origin switch
            {
                RelationQuerySourcePlacementOrigin.Explicit =>
                    decision.Origin == EffectiveConfigurationOrigin.Explicit,
                RelationQuerySourcePlacementOrigin.Convention =>
                    decision.Origin is EffectiveConfigurationOrigin.AdapterConvention
                        or EffectiveConfigurationOrigin.FrameworkDefault,
                _ => false
            };
            if (!isConsistent)
            {
                throw new ArgumentException(
                    $"Configuration decision setting '{setting}' has origin '{decision.Origin}', which conflicts "
                    + $"with placement binding origin '{binding.Origin}'.",
                    parameterName);
            }
        }
    }
}
