using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Physical;

/// <summary>Computes deterministic content fingerprints for portable relation/query source placements.</summary>
public static class RelationQuerySourcePlacementFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identifier.</summary>
    public const string Canonicalization = "relation-query-source-placement/v3-c14n/v1";

    /// <summary>Computes a deterministic source-placement fingerprint.</summary>
    /// <param name="placement">Normalized source placement to fingerprint.</param>
    /// <returns>Versioned SHA-256 fingerprint of placement-affecting content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placement"/> is <see langword="null"/>.</exception>
    public static RelationQuerySourcePlacementFingerprint Compute(RelationQuerySourcePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        PhysicalFingerprintWriter writer = new(Canonicalization);
        writer.Append(placement.SchemaVersion);
        writer.AppendPlan(placement.Plan);
        writer.Append(placement.ConventionSetVersion);
        writer.Append(placement.SourceInstances.Length);
        foreach (var source in placement.SourceInstances.OrderBy(static source => source.Id.Value, StringComparer.Ordinal))
        {
            writer.Append(source.Id.Value);
            writer.Append(source.ExecutionDomain.Value);
            writer.AppendProfile(source.TargetProfile);
            writer.Append(source.Limits.MaximumBatchSize);
            writer.Append(source.Limits.MaximumBufferedRows);
            writer.Append(source.Limits.MaximumFanOut);
            writer.Append(source.Limits.MaximumConcurrency);
        }

        writer.Append(placement.Bindings.Length);
        foreach (var binding in placement.Bindings.OrderBy(static binding => binding.Id.Value, StringComparer.Ordinal))
        {
            writer.Append(binding.Id.Value);
            writer.Append(binding.Input.Value);
            writer.Append(binding.Node.Value);
            writer.Append(binding.Binding.Value);
            writer.AppendShape(binding.Shape);
            writer.Append(binding.Source.Value);
            writer.Append((int)binding.Kind);
            writer.Append((int)binding.Acquisition);
            writer.Append((int)binding.Origin);
            writer.Append(binding.Identity is not null);
            if (binding.Identity is { } identity)
            {
                writer.AppendShape(identity.Shape);
                writer.Append(identity.SourceSelector);
                writer.Append(identity.SemanticPath is not null);
                if (identity.SemanticPath is { } semanticPath)
                {
                    writer.AppendPath(semanticPath);
                }
            }
            writer.Append(binding.Fields.Length);
            foreach (var field in binding.Fields.OrderBy(static field => field.Input.Value, StringComparer.Ordinal))
            {
                writer.Append(field.Input.Value);
                writer.AppendPath(field.SemanticPath);
                writer.Append(field.SourceSelector);
            }
            writer.Append(binding.RelationshipKeys.Length);
            foreach (var key in binding.RelationshipKeys.OrderBy(static key => key.Input.Value, StringComparer.Ordinal))
            {
                writer.Append(key.Input.Value);
                writer.AppendPath(key.SemanticPath);
                writer.Append(key.SourceSelector);
            }
            writer.AppendOptional(binding.Partition?.SourceSelector);
        }

        writer.Append(placement.ConfigurationDecisions.Length);
        foreach (var decision in placement.ConfigurationDecisions
                     .OrderBy(static decision => decision.Setting, StringComparer.Ordinal))
        {
            writer.Append(decision.Setting);
            writer.Append((int)decision.Origin);
            writer.Append(decision.Authority);
        }

        return new(Algorithm, Canonicalization, writer.Hash());
    }
}

/// <summary>Computes deterministic content fingerprints for compiled relation/query physical plans.</summary>
public static class RelationQueryPhysicalPlanFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identifier.</summary>
    public const string Canonicalization = "relation-query-physical-plan/v1-c14n/v1";

    /// <summary>Computes a deterministic compiled physical-plan fingerprint.</summary>
    /// <param name="plan">Normalized compiled physical plan to fingerprint.</param>
    /// <returns>Versioned SHA-256 fingerprint of execution-affecting plan content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static RelationQueryPhysicalPlanFingerprint Compute(CompiledRelationQueryPhysicalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PhysicalFingerprintWriter writer = new(Canonicalization);
        writer.Append(plan.SchemaVersion);
        writer.AppendPlan(plan.Plan);
        writer.Append(plan.Realization.Algorithm);
        writer.Append(plan.Realization.Canonicalization);
        writer.Append(plan.Realization.Value);
        writer.Append(plan.Placement.Fingerprint.Algorithm);
        writer.Append(plan.Placement.Fingerprint.Canonicalization);
        writer.Append(plan.Placement.Fingerprint.Value);
        AppendPolicy(writer, plan.Policy);
        writer.Append(plan.Stages.Length);
        foreach (var stage in plan.Stages.OrderBy(static stage => stage.Id.Value, StringComparer.Ordinal))
        {
            AppendStage(writer, stage);
        }

        writer.Append(plan.Terminal.Value);
        writer.Append(plan.Diagnostics.Length);
        foreach (var diagnostic in plan.Diagnostics)
        {
            writer.Append(diagnostic.Code);
            writer.Append((int)diagnostic.Severity);
            writer.AppendOptional(diagnostic.Input?.Value);
            writer.AppendOptional(diagnostic.Stage?.Value);
            writer.AppendOptional(diagnostic.PlacementBinding?.Value);
            writer.AppendOptional(diagnostic.Requirement?.Value);
        }
        return new(Algorithm, Canonicalization, writer.Hash());
    }

    static void AppendPolicy(PhysicalFingerprintWriter writer, RelationQueryPhysicalPlanningPolicy policy)
    {
        writer.Append(policy.Id.Value);
        writer.Append(policy.ConventionSetVersion);
        writer.Append(policy.MaximumBatchSize);
        writer.Append(policy.MaximumBufferedRows);
        writer.Append(policy.MaximumLocalRows);
        writer.Append(policy.MaximumFanOut);
        writer.Append(policy.MaximumReferenceKeysPerObservation);
        writer.Append(policy.MaximumConcurrency);
        writer.Append(policy.LoweringSelections.Length);
        foreach (var selection in policy.LoweringSelections)
        {
            writer.Append(selection.CompositionRule.Value);
            writer.Append(selection.PhysicalLowering.Value);
        }
    }

    static void AppendStage(PhysicalFingerprintWriter writer, RelationQueryPhysicalStage stage)
    {
        writer.Append(stage.Id.Value);
        writer.Append((int)stage.Kind);
        writer.AppendIds(stage.Dependencies.Select(static dependency => dependency.Value));
        writer.AppendOptional(stage.PlacementBinding?.Value);
        writer.AppendIds(stage.SemanticInputs.Select(static input => input.Value));
        writer.AppendIds(stage.RequestedFields.Select(static input => input.Value));
        writer.AppendNullable(stage.BatchSize);
        var provenance = stage.Provenance;
        writer.AppendIds(provenance.Nodes.Select(static node => node.Value));
        writer.AppendIds(provenance.Inputs.Select(static input => input.Value));
        writer.AppendIds(provenance.Requirements.Select(static requirement => requirement.Value));
        writer.Append(provenance.CapabilityEvidence.Length);
        foreach (var evidence in provenance.CapabilityEvidence)
        {
            writer.Append(evidence.Source.Value);
            writer.Append(evidence.Target.Value);
            writer.Append(evidence.Profile.Value);
            writer.Append(evidence.Evidence.Value);
        }
        writer.AppendIds(provenance.CompositionRules.Select(static rule => rule.Value));
        writer.AppendIds(provenance.OperatingBoundaries.Select(static boundary => boundary.Value));
        writer.AppendIds(provenance.PlacementBindings.Select(static binding => binding.Value));
        writer.AppendOptional(provenance.LoweringRule?.Value);
        writer.AppendIds(provenance.PolicyDecisions.Select(static decision => decision.Value));
    }
}

sealed class PhysicalFingerprintWriter
{
    readonly ArrayBufferWriter<byte> buffer = new();

    public PhysicalFingerprintWriter(string canonicalization) => Append(canonicalization);

    public void Append(string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Append(length);
        var destination = buffer.GetSpan(length);
        Encoding.UTF8.GetBytes(value.AsSpan(), destination);
        buffer.Advance(length);
    }

    public void Append(int value)
    {
        var destination = buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        buffer.Advance(sizeof(int));
    }

    public void Append(long value)
    {
        var destination = buffer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64BigEndian(destination, value);
        buffer.Advance(sizeof(long));
    }

    public void Append(bool value)
    {
        var destination = buffer.GetSpan(1);
        destination[0] = value ? (byte)1 : (byte)0;
        buffer.Advance(1);
    }

    public void AppendOptional(string? value)
    {
        if (value is null)
        {
            Append(-1);
            return;
        }
        Append(value);
    }

    public void AppendNullable(long? value)
    {
        Append(value.HasValue);
        if (value is { } concrete)
        {
            Append(concrete);
        }
    }

    public void AppendIds(IEnumerable<string> values)
    {
        var normalized = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Append(normalized.Length);
        foreach (var value in normalized)
        {
            Append(value);
        }
    }

    public void AppendShape(QualifiedShapeId shape)
    {
        Append(shape.GraphId.Value);
        Append(shape.ShapeId.Value);
    }

    public void AppendPath(FieldPath path)
    {
        Append(path.Segments.Length);
        foreach (var segment in path.Segments)
        {
            Append((int)segment.Kind);
            AppendOptional(segment.Segment);
        }
    }

    public void AppendPlan(RelationQueryCompiledPlanReference plan)
    {
        Append(plan.CompilerProfile);
        Append(plan.DefinitionSchemaVersion);
        Append(plan.DefinitionFingerprint.Algorithm);
        Append(plan.DefinitionFingerprint.Canonicalization);
        Append(plan.DefinitionFingerprint.Value);
        Append(plan.ShapeSnapshotsFingerprint.Algorithm);
        Append(plan.ShapeSnapshotsFingerprint.Canonicalization);
        Append(plan.ShapeSnapshotsFingerprint.Value);
        Append(plan.RelationshipCatalogFingerprint is not null);
        if (plan.RelationshipCatalogFingerprint is { } catalog)
        {
            Append(catalog.Algorithm);
            Append(catalog.Canonicalization);
            Append(catalog.Value);
        }
        Append(plan.DemandFingerprint.Algorithm);
        Append(plan.DemandFingerprint.Canonicalization);
        Append(plan.DemandFingerprint.Value);
        AppendIds(plan.Inputs.Select(static input => input.Value));
    }

    public void AppendProfile(RelationQueryTargetCapabilityProfile profile)
    {
        Append(profile.Target.Value);
        Append(profile.Id.Value);
        AppendIds(profile.SupportedDefinitionSchemaVersions);
        AppendIds(profile.SupportedCompilerProfiles);
        Append(profile.Capabilities.Length);
        foreach (var evidence in profile.Capabilities
                     .OrderBy(static evidence => evidence.Id.Value, StringComparer.Ordinal)
                     .ThenBy(static evidence => CapabilityKey(evidence.Capability), StringComparer.Ordinal))
        {
            Append(evidence.Id.Value);
            AppendCapability(evidence.Capability);
            AppendIds(evidence.OperatingBoundaries.Select(static boundary => boundary.Value));
        }
        Append(profile.OperatingBoundaries.Length);
        foreach (var boundary in profile.OperatingBoundaries
                     .OrderBy(static boundary => boundary.Id.Value, StringComparer.Ordinal)
                     .ThenBy(static boundary => (int)boundary.Kind)
                     .ThenBy(static boundary => boundary.Limit))
        {
            Append(boundary.Id.Value);
            Append((int)boundary.Kind);
            AppendNullable(boundary.Limit);
        }
    }

    public string Hash() => Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();

    void AppendCapability(RelationQueryCapability capability)
    {
        switch (capability)
        {
            case LogicalRelationQueryCapability logical:
                Append(0); Append((int)logical.Kind); break;
            case ExpressionRelationQueryCapability expression:
                Append(1); Append((int)expression.RequirementKind); Append(expression.Capability.Value); break;
            case TemporalRelationQueryCapability temporal:
                Append(2); Append((int)temporal.Capability); break;
            case StructuralRelationQueryCapability structural:
                Append(3); Append((int)structural.Role); Append((int)structural.PathKind); break;
            case GuaranteeRelationQueryCapability guarantee:
                Append(4); Append((int)guarantee.Kind); break;
            case OperatingBoundaryValidationRelationQueryCapability boundary:
                Append(5); Append(boundary.Boundary.Value); break;
            case PrimitiveRelationQueryCapability primitive:
                Append(6); Append((int)primitive.Kind); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported capability variant in physical fingerprinting.");
        }
    }

    static string CapabilityKey(RelationQueryCapability capability) => capability switch
    {
        LogicalRelationQueryCapability logical => $"0/{EnumKey((int)logical.Kind)}",
        ExpressionRelationQueryCapability expression => $"1/{EnumKey((int)expression.RequirementKind)}/{expression.Capability.Value}",
        TemporalRelationQueryCapability temporal => $"2/{EnumKey((int)temporal.Capability)}",
        StructuralRelationQueryCapability structural => $"3/{EnumKey((int)structural.Role)}/{EnumKey((int)structural.PathKind)}",
        GuaranteeRelationQueryCapability guarantee => $"4/{EnumKey((int)guarantee.Kind)}",
        OperatingBoundaryValidationRelationQueryCapability boundary => $"5/{boundary.Boundary.Value}",
        PrimitiveRelationQueryCapability primitive => $"6/{EnumKey((int)primitive.Kind)}",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported capability variant.")
    };

    static string EnumKey(int value) => value.ToString("D4", CultureInfo.InvariantCulture);
}
