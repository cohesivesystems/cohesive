using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Control;
using Cohesive.Execution;

namespace Cohesive.Storage.Materialization;

/// <summary>Explicit index-synchronization workload governed by a materialization Control loop.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationIndexSyncWorkloadKind
{
    /// <summary>Finite baseline and catch-up work that builds a candidate generation.</summary>
    Rebuild = 0,

    /// <summary>Freshness-sensitive synchronization work for an active generation.</summary>
    Realtime = 1
}

/// <summary>Binds one authored Control loop to an exact index-synchronization workload.</summary>
/// <remarks>
/// Workload is semantic input. Runtimes must not infer it from a generation lifecycle, target identity, loop
/// identity, or current routing state.
/// </remarks>
public sealed record MaterializationIndexSyncControlWorkloadBinding
{
    /// <summary>Creates an explicit workload binding.</summary>
    /// <param name="loopId">Authored Control loop identity.</param>
    /// <param name="workload">Exact rebuild or realtime workload.</param>
    /// <exception cref="ArgumentException"><paramref name="loopId"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workload"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationIndexSyncControlWorkloadBinding(
        ControlLoopId loopId,
        MaterializationIndexSyncWorkloadKind workload)
    {
        if (string.IsNullOrWhiteSpace(loopId.Value))
            throw new ArgumentException("A workload binding requires a Control loop identity.", nameof(loopId));
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");

        LoopId = loopId;
        Workload = workload;
    }

    /// <summary>Authored Control loop identity.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Exact governed workload.</summary>
    public MaterializationIndexSyncWorkloadKind Workload { get; }
}

/// <summary>Persisted compiler realization of one authored materialization Control loop.</summary>
public sealed record MaterializationIndexSyncControlRealization
{
    /// <summary>Creates a persisted Control realization.</summary>
    /// <param name="authoredDefinitionFingerprint">Exact authored loop-definition fingerprint.</param>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="effectiveDefinition">
    /// Effective definition retaining authored constraints and intersecting compiler, plan, deployment, and adapter
    /// evidence.
    /// </param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workload"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationIndexSyncControlRealization(
        ExecutionDefinitionFingerprint authoredDefinitionFingerprint,
        MaterializationIndexSyncWorkloadKind workload,
        ControlLoopDefinition effectiveDefinition)
    {
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");

        AuthoredDefinitionFingerprint = authoredDefinitionFingerprint
            ?? throw new ArgumentNullException(nameof(authoredDefinitionFingerprint));
        EffectiveDefinition = effectiveDefinition ?? throw new ArgumentNullException(nameof(effectiveDefinition));
        Workload = workload;
    }

    /// <summary>Exact fingerprint of the authored semantic loop.</summary>
    public ExecutionDefinitionFingerprint AuthoredDefinitionFingerprint { get; }

    /// <summary>Explicit governed workload.</summary>
    public MaterializationIndexSyncWorkloadKind Workload { get; }

    /// <summary>Effective evidence-backed definition executed by the materialization runtime.</summary>
    public ControlLoopDefinition EffectiveDefinition { get; }
}

/// <summary>Compiles authored materialization Control policy against an exact rebuild realization.</summary>
public static class MaterializationIndexSyncControlCompiler
{
    /// <summary>Stable materialization safe-point authority required by index-sync Control loops.</summary>
    public const string ApplicationAuthority = "cohesive.storage.materialization.index-sync/v1";

    /// <summary>Stable compiler evidence authority.</summary>
    public const string CompilerAuthority = "cohesive.storage.materialization.index-sync.compiler/v1";

    /// <summary>Compiles every explicitly bound loop into its effective persisted realization.</summary>
    /// <param name="definition">Canonical materialization definition.</param>
    /// <param name="sources">Selected source capability realizations.</param>
    /// <param name="target">Selected target capability realization.</param>
    /// <param name="limits">Persisted execution-plan bounds.</param>
    /// <returns>Effective realizations in canonical loop-identity order.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A binding is missing, an application authority is incompatible, a workload reservation cannot be realized,
    /// or capability and semantic constraints have no valid intersection.
    /// </exception>
    public static ImmutableArray<MaterializationIndexSyncControlRealization> Compile(
        MaterializationDefinition definition,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target,
        MaterializationRebuildLimits limits)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(limits);
        if (sources.IsDefault)
            throw new ArgumentException("Selected source realizations cannot be default.", nameof(sources));

        if (definition.ControlLoops.IsEmpty)
            return [];

        var bindings = definition.ControlWorkloads.ToDictionary(static binding => binding.LoopId);
        var builder = ImmutableArray.CreateBuilder<MaterializationIndexSyncControlRealization>(
            definition.ControlLoops.Length);
        foreach (var authored in definition.ControlLoops)
        {
            if (!bindings.TryGetValue(authored.Id, out var binding))
                throw new ArgumentException($"Control loop '{authored.Id.Value}' has no explicit workload binding.", nameof(definition));
            if (!string.Equals(authored.ApplicationAuthority, ApplicationAuthority, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Control loop '{authored.Id.Value}' must authorize '{ApplicationAuthority}' safe points.",
                    nameof(definition));
            }
            foreach (var value in authored.InitialOperatingPoint.Values)
            {
                if (value.Actuator is not (
                    ControlActuatorKind.Concurrency
                    or ControlActuatorKind.BatchItems
                    or ControlActuatorKind.BatchBytes))
                {
                    throw new ArgumentException(
                        $"Materialization index sync cannot realize actuator '{value.Actuator}' in loop "
                        + $"'{authored.Id.Value}'; supported actuators are Concurrency, BatchItems, and BatchBytes.",
                        nameof(definition));
                }
            }
            var constraints = ImmutableArray.CreateBuilder<ControlHardLimit>(
                authored.HardLimits.Constraints.Length
                + (authored.InitialOperatingPoint.Values.Length * (sources.Length + 1)));
            constraints.AddRange(authored.HardLimits.Constraints);
            foreach (var value in authored.InitialOperatingPoint.Values)
            {
                AddPlanConstraint(constraints, authored.Stage, value.Actuator, limits);
                AddAdapterConstraints(
                    constraints,
                    authored.Stage,
                    value.Actuator,
                    binding.Workload,
                    sources,
                    target);
            }

            var hardLimits = new ControlHardLimits(constraints.ToImmutable());
            foreach (var budget in authored.Budgets)
            {
                if (binding.Workload == MaterializationIndexSyncWorkloadKind.Realtime
                    && budget.Reserved.Value != 0)
                {
                    throw new ArgumentException(
                        $"Control loop '{authored.Id.Value}' declares a realtime '{budget.Actuator}' reservation, "
                        + "but index-sync admission realizes only realtime capacity reserved by the rebuild workload.",
                        nameof(definition));
                }
                var physicalMaximum = GetPhysicalActuatorMaximum(
                    authored.Stage,
                    budget.Actuator,
                    binding.Workload,
                    sources,
                    target,
                    limits);
                if (budget.Capacity.Value > physicalMaximum)
                {
                    throw new ArgumentException(
                        $"Control loop '{authored.Id.Value}' attributes '{budget.Actuator}' budget capacity "
                        + $"{budget.Capacity.Value}, which exceeds exact physical capacity {physicalMaximum}.",
                        nameof(definition));
                }
                if (binding.Workload == MaterializationIndexSyncWorkloadKind.Rebuild
                    && budget.Actuator == ControlActuatorKind.Concurrency
                    && budget.Capacity.Value != physicalMaximum)
                {
                    throw new ArgumentException(
                        $"Control loop '{authored.Id.Value}' attributes concurrency budget capacity "
                        + $"{budget.Capacity.Value}, but shared index-sync admission requires the exact physical "
                        + $"capacity {physicalMaximum} so its realtime reservation is realized without inventing capacity.",
                        nameof(definition));
                }
            }
            var effectiveBudgets = authored.Budgets;
            var pointBuilder = ImmutableArray.CreateBuilder<ControlActuatorValue>(
                authored.InitialOperatingPoint.Values.Length);
            foreach (var initial in authored.InitialOperatingPoint.Values)
            {
                var effectiveRange = hardLimits.GetEffectiveRange(initial.Actuator);
                var maximum = effectiveRange.Maximum.Value;
                foreach (var budget in effectiveBudgets)
                {
                    if (budget.Actuator == initial.Actuator)
                    {
                        maximum = Math.Min(maximum, budget.Available.Value);
                        break;
                    }
                }
                if (maximum < effectiveRange.Minimum.Value)
                {
                    throw new ArgumentException(
                        $"Control loop '{authored.Id.Value}' workload reservation leaves no valid "
                        + $"'{initial.Actuator}' operating point after physical capability intersection.",
                        nameof(definition));
                }
                var clamped = Math.Clamp(
                    initial.Quantity.Value,
                    effectiveRange.Minimum.Value,
                    maximum);
                pointBuilder.Add(new(
                    initial.Actuator,
                    new ControlQuantity(clamped, effectiveRange.Minimum.Unit)));
            }
            var effectivePoint = new ControlOperatingPoint(pointBuilder.MoveToImmutable());

            var effective = new ControlLoopDefinition(
                schemaVersion: authored.SchemaVersion,
                id: authored.Id,
                target: authored.Target,
                applicationAuthority: authored.ApplicationAuthority,
                stage: authored.Stage,
                hardLimits: hardLimits,
                initialOperatingPoint: effectivePoint,
                objectives: authored.Objectives,
                policy: authored.Policy,
                budgets: effectiveBudgets,
                provenance: authored.Provenance);
            builder.Add(new(authored.Fingerprint, binding.Workload, effective));
        }

        return builder.MoveToImmutable();
    }

    /// <summary>Validates persisted realizations against deterministic recompilation.</summary>
    /// <param name="definition">Canonical materialization definition.</param>
    /// <param name="sources">Selected source capability realizations.</param>
    /// <param name="target">Selected target capability realization.</param>
    /// <param name="limits">Persisted execution-plan bounds.</param>
    /// <param name="persisted">Persisted realizations to validate.</param>
    /// <returns>Canonical validated realizations.</returns>
    /// <exception cref="ArgumentException">Persisted content differs from deterministic compilation.</exception>
    public static ImmutableArray<MaterializationIndexSyncControlRealization> Link(
        MaterializationDefinition definition,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target,
        MaterializationRebuildLimits limits,
        ImmutableArray<MaterializationIndexSyncControlRealization> persisted)
    {
        var expected = Compile(definition, sources, target, limits);
        var normalized = persisted.IsDefault ? [] : persisted;
        if (!expected.SequenceEqual(normalized.OrderBy(static item => item.EffectiveDefinition.Id.Value, StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "Persisted materialization Control realizations differ from deterministic compilation.",
                nameof(persisted));
        }

        return expected;
    }

    static void AddPlanConstraint(
        ImmutableArray<ControlHardLimit>.Builder constraints,
        ControlStageKind stage,
        ControlActuatorKind actuator,
        MaterializationRebuildLimits limits)
    {
        var maximum = actuator switch
        {
            ControlActuatorKind.BatchItems when stage == ControlStageKind.Target => limits.MaximumBulkItems,
            ControlActuatorKind.BatchBytes when stage == ControlStageKind.Target => limits.MaximumBulkBytes,
            ControlActuatorKind.BatchItems => limits.MaximumPageItems,
            ControlActuatorKind.BatchBytes => limits.MaximumPageBytes,
            ControlActuatorKind.Concurrency => limits.MaximumParallelism,
            _ => (long?)null
        };
        if (maximum is null)
            return;

        constraints.Add(new(
            new ControlRange(
                actuator,
                new ControlQuantity(1, ControlUnitCatalog.ForActuator(actuator)),
                new ControlQuantity(maximum.Value, ControlUnitCatalog.ForActuator(actuator))),
            ControlHardLimitOrigin.Compiler,
            CompilerAuthority));
    }

    /// <summary>Gets the exact plan-and-adapter shared capacity for a stage concurrency pool.</summary>
    internal static int GetPhysicalConcurrencyMaximum(
        ControlStageKind stage,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target,
        MaterializationRebuildLimits limits)
    {
        checked
        {
            return (int)GetPhysicalActuatorMaximum(
                stage,
                ControlActuatorKind.Concurrency,
                MaterializationIndexSyncWorkloadKind.Rebuild,
                sources,
                target,
                limits);
        }
    }

    static long GetPhysicalActuatorMaximum(
        ControlStageKind stage,
        ControlActuatorKind actuator,
        MaterializationIndexSyncWorkloadKind workload,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target,
        MaterializationRebuildLimits limits)
    {
        var maximum = actuator switch
        {
            ControlActuatorKind.BatchItems when stage == ControlStageKind.Target => limits.MaximumBulkItems,
            ControlActuatorKind.BatchBytes when stage == ControlStageKind.Target => limits.MaximumBulkBytes,
            ControlActuatorKind.BatchItems => limits.MaximumPageItems,
            ControlActuatorKind.BatchBytes => limits.MaximumPageBytes,
            ControlActuatorKind.Concurrency => limits.MaximumParallelism,
            _ => throw new ArgumentOutOfRangeException(nameof(actuator), actuator, "Unsupported index-sync actuator.")
        };
        var limitKinds = ToAdapterLimits(stage, actuator, workload);
        if (limitKinds.IsEmpty || stage == ControlStageKind.Transform)
            return maximum;
        if (stage == ControlStageKind.Source)
        {
            foreach (var source in sources)
                IntersectOperatingLimits(source.Profile, limitKinds, ref maximum);
        }
        else if (stage == ControlStageKind.Target)
        {
            IntersectOperatingLimits(target.Capabilities, limitKinds, ref maximum);
        }
        return maximum;
    }

    static void IntersectOperatingLimits(
        MaterializationCapabilityProfile profile,
        ImmutableArray<MaterializationLimitKind> limitKinds,
        ref long maximum)
    {
        foreach (var evidence in profile.Evidence)
        {
            foreach (var limit in evidence.OperatingLimits)
            {
                foreach (var limitKind in limitKinds)
                {
                    if (limit.Kind == limitKind)
                        maximum = Math.Min(maximum, limit.Maximum);
                }
            }
        }
    }

    static void AddAdapterConstraints(
        ImmutableArray<ControlHardLimit>.Builder constraints,
        ControlStageKind stage,
        ControlActuatorKind actuator,
        MaterializationIndexSyncWorkloadKind workload,
        ImmutableArray<MaterializationRebuildSourcePlan> sources,
        MaterializationTargetDescriptor target)
    {
        var limitKinds = ToAdapterLimits(stage, actuator, workload);
        if (limitKinds.IsEmpty || stage == ControlStageKind.Transform)
            return;

        if (stage == ControlStageKind.Target)
        {
            foreach (var limitKind in limitKinds)
                AddProfileConstraint(constraints, actuator, target.Capabilities, limitKind);
            return;
        }

        foreach (var source in sources)
        {
            foreach (var limitKind in limitKinds)
                AddProfileConstraint(constraints, actuator, source.Profile, limitKind);
        }
    }

    static ImmutableArray<MaterializationLimitKind> ToAdapterLimits(
        ControlStageKind stage,
        ControlActuatorKind actuator,
        MaterializationIndexSyncWorkloadKind workload) =>
        (stage, actuator, workload) switch
        {
            (ControlStageKind.Source, ControlActuatorKind.BatchItems, MaterializationIndexSyncWorkloadKind.Rebuild) =>
                [MaterializationLimitKind.ReadItems, MaterializationLimitKind.ChangeItems],
            (ControlStageKind.Source, ControlActuatorKind.BatchItems, MaterializationIndexSyncWorkloadKind.Realtime) =>
                [MaterializationLimitKind.ChangeItems],
            (ControlStageKind.Source, ControlActuatorKind.BatchBytes, _) => [MaterializationLimitKind.ReadBytes],
            (ControlStageKind.Source, ControlActuatorKind.Concurrency, _) => [MaterializationLimitKind.Parallelism],
            (ControlStageKind.Target, ControlActuatorKind.BatchItems, _) => [MaterializationLimitKind.WriteItems],
            (ControlStageKind.Target, ControlActuatorKind.BatchBytes, _) => [MaterializationLimitKind.WriteBytes],
            (ControlStageKind.Target, ControlActuatorKind.Concurrency, _) => [MaterializationLimitKind.Parallelism],
            _ => []
        };

    static void AddProfileConstraint(
        ImmutableArray<ControlHardLimit>.Builder constraints,
        ControlActuatorKind actuator,
        MaterializationCapabilityProfile profile,
        MaterializationLimitKind limitKind)
    {
        long? maximum = null;
        foreach (var evidence in profile.Evidence)
        {
            foreach (var limit in evidence.OperatingLimits)
            {
                if (limit.Kind == limitKind)
                    maximum = maximum is null ? limit.Maximum : Math.Min(maximum.Value, limit.Maximum);
            }
        }

        if (maximum is null)
            return;

        var unit = ControlUnitCatalog.ForActuator(actuator);
        constraints.Add(new(
            new ControlRange(
                actuator,
                new ControlQuantity(1, unit),
                new ControlQuantity(maximum.Value, unit)),
            ControlHardLimitOrigin.Adapter,
            $"{profile.Id.Value}#{limitKind}"));
    }
}
