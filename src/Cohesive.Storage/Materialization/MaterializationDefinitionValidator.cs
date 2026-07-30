using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostics emitted while linking and validating canonical materialization definitions.</summary>
public static class MaterializationDefinitionDiagnosticCodes
{
    /// <summary>The retained Relations compilation request could not produce a complete plan.</summary>
    public const string RelationCompilationFailed = "materialization.relation.compilationFailed";

    /// <summary>The retained compiled-plan reference does not match its own fingerprint.</summary>
    public const string PlanFingerprintMismatch = "materialization.relation.planFingerprintMismatch";

    /// <summary>Recompiling the retained request produces a different compiled-plan reference.</summary>
    public const string PlanReferenceMismatch = "materialization.relation.planReferenceMismatch";

    /// <summary>The selected output is absent or differs from the exact compiled requirement graph.</summary>
    public const string OutputMismatch = "materialization.relation.outputMismatch";

    /// <summary>The selected output is not one complete rooted Relation output.</summary>
    public const string OutputUnsupported = "materialization.relation.outputUnsupported";

    /// <summary>A many-valued or set-valued output has no declared stable semantic key.</summary>
    public const string OutputKeyMissing = "materialization.relation.outputKeyMissing";

    /// <summary>A source requirement references an acquisition input absent from the exact Relations plan.</summary>
    public const string SourceInputMissing = "materialization.source.inputMissing";

    /// <summary>A compiled Relations acquisition input has no materialization source requirement.</summary>
    public const string SourceRequirementMissing = "materialization.source.requirementMissing";

    /// <summary>A capability requirement applies outside the definition's supported modes.</summary>
    public const string RequirementModeUnsupported = "materialization.capability.modeUnsupported";

    /// <summary>A required capability identity is duplicated across definition scopes.</summary>
    public const string RequirementIdentityDuplicate = "materialization.capability.requirementIdentityDuplicate";

    /// <summary>A synchronization mode omits a protocol capability required by canonical v1 semantics.</summary>
    public const string ProtocolCapabilityMissing = "materialization.capability.protocolMissing";

    /// <summary>A protocol capability omits a semantic guarantee required by canonical v1 semantics.</summary>
    public const string ProtocolGuaranteeMissing = "materialization.capability.guaranteeMissing";

    /// <summary>A protocol capability omits a positive operating bound required by canonical v1 semantics.</summary>
    public const string ProtocolLimitMissing = "materialization.capability.limitMissing";

    /// <summary>Per-item outcome coverage is narrower than an applicable bulk-write bound.</summary>
    public const string OutcomeLimitInsufficient = "materialization.capability.outcomeLimitInsufficient";

    /// <summary>A convergence protocol is incompatible with the definition's supported synchronization modes.</summary>
    public const string ConsistencyModeUnsupported = "materialization.consistency.modeUnsupported";
}

/// <summary>Validates exact Relations linkage and backend-independent materialization protocol invariants.</summary>
public static class MaterializationDefinitionValidator
{
    static readonly ImmutableArray<MaterializationCapabilityKind> IncrementalSourceCapabilities =
    [
        MaterializationCapabilityKind.SourceChangeDelivery,
        MaterializationCapabilityKind.SourceSettlement
    ];

    static readonly ImmutableArray<MaterializationCapabilityKind> RebuildTargetCapabilities =
    [
        MaterializationCapabilityKind.TargetGenerationIsolation,
        MaterializationCapabilityKind.TargetBulkUpsert,
        MaterializationCapabilityKind.TargetPerItemOutcomes,
        MaterializationCapabilityKind.TargetSeal,
        MaterializationCapabilityKind.TargetValidation,
        MaterializationCapabilityKind.TargetFencedPromotion,
        MaterializationCapabilityKind.TargetRetirement,
        MaterializationCapabilityKind.TargetCleanup
    ];

    static readonly ImmutableArray<MaterializationCapabilityKind> BaselineCatchUpSourceCapabilities =
    [
        MaterializationCapabilityKind.SourceChangeDelivery,
        MaterializationCapabilityKind.SourceSettlement
    ];

    static readonly ImmutableArray<MaterializationCapabilityKind> IncrementalTargetCapabilities =
    [
        MaterializationCapabilityKind.TargetBulkUpsert,
        MaterializationCapabilityKind.TargetBulkDelete,
        MaterializationCapabilityKind.TargetPerItemOutcomes
    ];

    /// <summary>Validates a canonical materialization definition without selecting concrete endpoints.</summary>
    /// <param name="definition">Definition to validate.</param>
    /// <returns>Deterministically ordered structured diagnostics; an empty result authorizes persistence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A Relations semantic snapshot cannot be represented canonically.</exception>
    /// <exception cref="NotSupportedException">A Relations semantic snapshot contains an unsupported serialization type.</exception>
    public static DocumentValidationResult Validate(MaterializationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var relation = definition.Relation;
        var compilation = relation.Compile();
        CompiledRelationQueryPlan? compiledPlan = null;
        if (!compilation.IsSuccessful || compilation.Plan is not { } plan)
        {
            if (compilation.Diagnostics.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error(
                    definition,
                    MaterializationDefinitionDiagnosticCodes.RelationCompilationFailed,
                    "The retained Relations request did not produce a complete plan.",
                    "/relation/compilationRequest"));
            }
            else
            {
                foreach (var diagnostic in compilation.Diagnostics)
                {
                    diagnostics.Add(diagnostic with
                    {
                        Location = Prefix("/relation/compilationRequest", diagnostic.Location),
                        Evidence = MergeEvidence(
                            diagnostic.Evidence,
                            "materialization-relation-linking",
                            definition.Id.Value,
                            definition.Provenance.Source.Reference,
                            diagnostic.Message)
                    });
                }
            }
        }
        else
        {
            compiledPlan = plan;
            ValidatePlanReference(definition, plan, diagnostics);
            ValidateOutput(definition, plan, diagnostics);
            ValidateSources(definition, plan, diagnostics);
        }

        ValidateRequirementClosure(definition, compiledPlan, diagnostics);
        var normalized = MaterializationContract.NormalizeDiagnostics(
            [.. diagnostics.Distinct()],
            nameof(diagnostics));
        return normalized.IsDefaultOrEmpty
            ? DocumentValidationResult.Valid
            : new DocumentValidationResult(normalized);
    }

    static void ValidatePlanReference(
        MaterializationDefinition definition,
        CompiledRelationQueryPlan plan,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var relation = definition.Relation;
        var retainedFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(relation.CompiledPlan);
        if (!Equals(retainedFingerprint, relation.CompiledPlanFingerprint))
        {
            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.PlanFingerprintMismatch,
                "The retained Relations compiled-plan reference does not match its declared fingerprint.",
                "/relation/compiledPlanFingerprint",
                expected: retainedFingerprint.Value,
                observed: relation.CompiledPlanFingerprint.Value));
        }

        var actualReference = RelationQueryCompiledPlanReference.From(plan);
        var actualFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(actualReference);
        if (!Equals(actualFingerprint, relation.CompiledPlanFingerprint))
        {
            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.PlanReferenceMismatch,
                "Recompiling the retained Relations request produces a different exact compiled-plan reference.",
                "/relation/compiledPlan",
                expected: relation.CompiledPlanFingerprint.Value,
                observed: actualFingerprint.Value));
        }
    }

    static void ValidateOutput(
        MaterializationDefinition definition,
        CompiledRelationQueryPlan plan,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var selected = definition.Relation.Output;
        var compiled = plan.RequirementGraph.Outputs.FirstOrDefault(candidate => candidate.Id == selected.Id);
        if (compiled is null || !Equals(compiled, selected))
        {
            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.OutputMismatch,
                $"Selected output '{selected.Id.Value}' is absent or differs from the exact compiled requirement graph.",
                "/relation/output",
                expected: string.Join(",", plan.RequirementGraph.Outputs.Select(static output => output.Id.Value)),
                observed: selected.Id.Value));
            return;
        }

        if (selected.Kind != RelationQueryOutputReferenceKind.Relation
            || selected.Field is not null
            || plan.Definition is not RelationDefinition relationDefinition
            || selected.Relation != relationDefinition.Id
            || selected.Node != relationDefinition.Output.Node
            || selected.Shape != relationDefinition.Output.Shape)
        {
            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.OutputUnsupported,
                "Canonical materialization v1 requires one complete rooted Relation output.",
                "/relation/output",
                expected: "complete rooted relation output",
                observed: selected.Kind.ToString()));
            return;
        }

        if (relationDefinition.Output.Mode is RelationOutputMode.ManyPerRoot or RelationOutputMode.Set
            && relationDefinition.Output.Key is null)
        {
            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.OutputKeyMissing,
                "A many-valued or set-valued materialization output requires a stable Relations output key.",
                "/relation/compilationRequest/definitionDocument/definition/output/key",
                expected: "stable output-key expression",
                observed: "missing"));
        }
    }

    static void ValidateSources(
        MaterializationDefinition definition,
        CompiledRelationQueryPlan plan,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var inputs = GetAcquisitionInputs(plan).ToHashSet();
        var declared = definition.Sources.Select(static source => source.Input).ToHashSet();
        foreach (var source in definition.Sources)
        {
            if (inputs.Contains(source.Input))
            {
                continue;
            }

            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.SourceInputMissing,
                $"Source requirement input '{source.Input.Value}' is absent from the exact Relations acquisition contract.",
                $"/sources/{Encode(source.Input.Value)}",
                expected: "compiled Relations acquisition input",
                observed: source.Input.Value));
        }

        foreach (var input in inputs.OrderBy(static input => input.Value, StringComparer.Ordinal))
        {
            if (declared.Contains(input))
            {
                continue;
            }

            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.SourceRequirementMissing,
                $"Compiled Relations acquisition input '{input.Value}' has no materialization source requirement.",
                $"/sources/{Encode(input.Value)}",
                expected: "one source requirement for every compiled Relations acquisition input",
                observed: "missing"));
        }
    }

    static void ValidateRequirementClosure(
        MaterializationDefinition definition,
        CompiledRelationQueryPlan? plan,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var sourceRequirements = definition.Sources.SelectMany(static source => source.Capabilities).ToArray();
        var all = sourceRequirements.Concat(definition.TargetCapabilities).ToArray();
        foreach (var duplicate in all.GroupBy(static requirement => requirement.Id).Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.RequirementIdentityDuplicate,
                $"Capability requirement identity '{duplicate.Key.Value}' is repeated across definition scopes.",
                $"/capabilities/{Encode(duplicate.Key.Value)}"));
        }

        foreach (var requirement in all)
        {
            if ((requirement.Modes & ~definition.UpdatePolicy.SupportedModes) == 0)
            {
                continue;
            }

            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.RequirementModeUnsupported,
                $"Capability requirement '{requirement.Id.Value}' applies to a synchronization mode the definition does not support.",
                $"/capabilities/{Encode(requirement.Id.Value)}/modes",
                expected: definition.UpdatePolicy.SupportedModes.ToString(),
                observed: requirement.Modes.ToString()));
        }

        if (definition.UpdatePolicy.Consistency == MaterializationConsistencyKind.BaselinePlusCatchUp
            && (definition.UpdatePolicy.SupportedModes & MaterializationSynchronizationMode.Rebuild) == 0)
        {
            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.ConsistencyModeUnsupported,
                "Baseline-plus-catch-up consistency requires rebuild synchronization to establish the baseline.",
                "/updatePolicy/consistency",
                expected: MaterializationSynchronizationMode.Rebuild.ToString(),
                observed: definition.UpdatePolicy.SupportedModes.ToString()));
        }

        if ((definition.UpdatePolicy.SupportedModes & MaterializationSynchronizationMode.Rebuild) != 0)
        {
            ImmutableArray<MaterializationCapabilityKind> requiredTarget =
                definition.UpdatePolicy.Consistency == MaterializationConsistencyKind.BaselinePlusCatchUp
                ? [.. RebuildTargetCapabilities, MaterializationCapabilityKind.TargetBulkDelete]
                : RebuildTargetCapabilities;
            foreach (var source in definition.Sources)
            {
                var requiredSource = GetRebuildSourceCapabilities(plan, source.Input);
                if (definition.UpdatePolicy.Consistency == MaterializationConsistencyKind.BaselinePlusCatchUp)
                {
                    requiredSource = [.. requiredSource, .. BaselineCatchUpSourceCapabilities];
                }

                RequireCapabilities(
                    definition,
                    MaterializationSynchronizationMode.Rebuild,
                    source.Capabilities,
                    requiredSource,
                    $"/sources/{Encode(source.Input.Value)}/capabilities",
                    diagnostics);
            }
            RequireCapabilities(
                definition,
                MaterializationSynchronizationMode.Rebuild,
                definition.TargetCapabilities,
                requiredTarget,
                "/targetCapabilities",
                diagnostics);
            ValidateOutcomeBounds(
                definition,
                MaterializationSynchronizationMode.Rebuild,
                definition.TargetCapabilities,
                diagnostics);
        }

        if ((definition.UpdatePolicy.SupportedModes & MaterializationSynchronizationMode.Incremental) != 0)
        {
            foreach (var source in definition.Sources)
            {
                RequireCapabilities(
                    definition,
                    MaterializationSynchronizationMode.Incremental,
                    source.Capabilities,
                    IncrementalSourceCapabilities,
                    $"/sources/{Encode(source.Input.Value)}/capabilities",
                    diagnostics);
            }
            RequireCapabilities(
                definition,
                MaterializationSynchronizationMode.Incremental,
                definition.TargetCapabilities,
                IncrementalTargetCapabilities,
                "/targetCapabilities",
                diagnostics);
            ValidateOutcomeBounds(
                definition,
                MaterializationSynchronizationMode.Incremental,
                definition.TargetCapabilities,
                diagnostics);
        }
    }

    static void RequireCapabilities(
        MaterializationDefinition definition,
        MaterializationSynchronizationMode mode,
        IEnumerable<MaterializationCapabilityRequirement> declared,
        ImmutableArray<MaterializationCapabilityKind> required,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var applicable = declared
            .Where(requirement => (requirement.Modes & mode) != 0)
            .ToArray();
        foreach (var capability in required)
        {
            var requirement = applicable.FirstOrDefault(candidate => candidate.Capability == capability);
            if (requirement is not null)
            {
                RequireGuarantees(definition, mode, requirement, location, diagnostics);
                continue;
            }
            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.ProtocolCapabilityMissing,
                $"{mode} synchronization requires capability '{capability}' at this endpoint.",
                location,
                expected: capability.ToString(),
                observed: "missing"));
        }
    }

    static void RequireGuarantees(
        MaterializationDefinition definition,
        MaterializationSynchronizationMode mode,
        MaterializationCapabilityRequirement requirement,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var guarantee in RequiredGuarantees(definition, mode, requirement.Capability))
        {
            if (requirement.Guarantees.Contains(guarantee))
            {
                continue;
            }

            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.ProtocolGuaranteeMissing,
                $"Capability '{requirement.Capability}' must require guarantee '{guarantee}' for {mode} synchronization.",
                $"{location}/{Encode(requirement.Id.Value)}/guarantees",
                expected: guarantee.ToString(),
                observed: requirement.Guarantees.IsDefaultOrEmpty
                    ? "none"
                    : string.Join(",", requirement.Guarantees)));
        }
        foreach (var limitKind in MaterializationCapabilityCatalog.RequiredHardLimits(requirement.Capability))
        {
            if (requirement.OperatingLimits.Any(limit => limit.Kind == limitKind))
            {
                continue;
            }

            diagnostics.Add(Error(
                definition,
                MaterializationDefinitionDiagnosticCodes.ProtocolLimitMissing,
                $"Capability '{requirement.Capability}' must declare a positive '{limitKind}' bound for {mode} synchronization.",
                $"{location}/{Encode(requirement.Id.Value)}/operatingLimits",
                expected: limitKind.ToString(),
                observed: "missing"));
        }
    }

    static void ValidateOutcomeBounds(
        MaterializationDefinition definition,
        MaterializationSynchronizationMode mode,
        ImmutableArray<MaterializationCapabilityRequirement> requirements,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var applicable = requirements.Where(requirement => (requirement.Modes & mode) != 0).ToArray();
        var outcomes = applicable.FirstOrDefault(static requirement =>
            requirement.Capability == MaterializationCapabilityKind.TargetPerItemOutcomes);
        if (outcomes is null)
        {
            return;
        }

        foreach (var bulk in applicable.Where(static requirement => requirement.Capability is
                     MaterializationCapabilityKind.TargetBulkUpsert or MaterializationCapabilityKind.TargetBulkDelete))
        {
            foreach (var kind in new[] { MaterializationLimitKind.WriteItems, MaterializationLimitKind.WriteBytes })
            {
                var bulkLimit = bulk.OperatingLimits.FirstOrDefault(limit => limit.Kind == kind).Maximum;
                var outcomeLimit = outcomes.OperatingLimits.FirstOrDefault(limit => limit.Kind == kind).Maximum;
                if (bulkLimit == 0 || outcomeLimit >= bulkLimit)
                {
                    continue;
                }

                diagnostics.Add(Error(
                    definition,
                    MaterializationDefinitionDiagnosticCodes.OutcomeLimitInsufficient,
                    $"Per-item outcome capability '{outcomes.Id.Value}' is narrower than bulk capability '{bulk.Id.Value}'.",
                    $"/targetCapabilities/{Encode(outcomes.Id.Value)}/operatingLimits",
                    expected: $"{kind}>={bulkLimit}",
                    observed: outcomeLimit == 0 ? "missing" : outcomeLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
    }

    static IEnumerable<MaterializationGuaranteeKind> RequiredGuarantees(
        MaterializationDefinition definition,
        MaterializationSynchronizationMode mode,
        MaterializationCapabilityKind capability)
    {
        if (capability is MaterializationCapabilityKind.SourceBatchedPointRead
            or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
            or MaterializationCapabilityKind.SourceBoundedEnumeration)
        {
            yield return MaterializationGuaranteeKind.StableOrdering;
            yield return MaterializationGuaranteeKind.RequestLocalCompleteness;
            if (definition.UpdatePolicy.Consistency == MaterializationConsistencyKind.CoordinatedSnapshot)
            {
                yield return MaterializationGuaranteeKind.CoordinatedSnapshot;
            }
            else if (definition.UpdatePolicy.Consistency == MaterializationConsistencyKind.Reconciliation)
            {
                yield return MaterializationGuaranteeKind.Reconciliation;
            }
        }
        else if (capability == MaterializationCapabilityKind.SourceChangeDelivery)
        {
            yield return MaterializationGuaranteeKind.StableOrdering;
            yield return MaterializationGuaranteeKind.AtLeastOnceDelivery;
            if (definition.UpdatePolicy.Consistency == MaterializationConsistencyKind.BaselinePlusCatchUp)
            {
                yield return MaterializationGuaranteeKind.BaselinePlusCatchUp;
            }
        }
        else if (capability == MaterializationCapabilityKind.SourceSettlement)
        {
            yield return MaterializationGuaranteeKind.ExplicitSettlement;
        }
        else if (capability == MaterializationCapabilityKind.TargetGenerationIsolation)
        {
            yield return MaterializationGuaranteeKind.GenerationIsolation;
            yield return MaterializationGuaranteeKind.FencedMutation;
        }
        else if (capability is MaterializationCapabilityKind.TargetBulkUpsert
                 or MaterializationCapabilityKind.TargetBulkDelete)
        {
            yield return MaterializationGuaranteeKind.IdempotentWrite;
            yield return MaterializationGuaranteeKind.FencedMutation;
            if (definition.UpdatePolicy.Idempotency == MaterializationIdempotencyKind.StableOutputIdentityAndVersion)
            {
                yield return MaterializationGuaranteeKind.VersionConditionalWrite;
            }
        }
        else if (capability == MaterializationCapabilityKind.TargetPerItemOutcomes)
        {
            yield return MaterializationGuaranteeKind.ExactPerItemOutcome;
        }
        else if (capability == MaterializationCapabilityKind.TargetFencedPromotion)
        {
            yield return MaterializationGuaranteeKind.AtomicPromotion;
            yield return MaterializationGuaranteeKind.FencedPromotion;
        }
        else if (capability is MaterializationCapabilityKind.TargetSeal
                 or MaterializationCapabilityKind.TargetValidation
                 or MaterializationCapabilityKind.TargetRetirement
                 or MaterializationCapabilityKind.TargetCleanup)
        {
            yield return MaterializationGuaranteeKind.FencedMutation;
        }
    }

    static IEnumerable<RelationQueryInputId> GetAcquisitionInputs(CompiledRelationQueryPlan plan)
    {
        foreach (var source in plan.InputContract.Sources)
        {
            yield return source.Input.Id;
        }

        foreach (var traversal in plan.InputContract.Traversals)
        {
            yield return traversal.Input.Id;
        }
    }

    static ImmutableArray<MaterializationCapabilityKind> GetRebuildSourceCapabilities(
        CompiledRelationQueryPlan? plan,
        RelationQueryInputId input)
    {
        if (plan is null)
        {
            return [];
        }

        if (!MaterializationSourceAcquisitionCatalog.TryGetReadCapability(plan, input, out var readCapability))
        {
            return [];
        }

        return
        [
            readCapability,
            MaterializationCapabilityKind.SourceContinuation
        ];
    }

    static DocumentValidationDiagnostic Error(
        MaterializationDefinition definition,
        string code,
        string message,
        string location,
        string? expected = null,
        string? observed = null) =>
        MaterializationContract.CreateDiagnostic(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            "materialization-definition-validation",
            definition.Id.Value,
            [definition.Provenance.Source.Reference],
            expected ?? "canonical materialization definition invariant satisfied",
            observed ?? message);

    static DocumentDiagnosticEvidence MergeEvidence(
        DocumentDiagnosticEvidence? evidence,
        string stage,
        string subject,
        string sourceReference,
        string observedFallback)
    {
        var sourceReferences = evidence?.SourceReferences ?? [];
        if (!sourceReferences.Contains(sourceReference, StringComparer.Ordinal))
        {
            sourceReferences = [.. sourceReferences, sourceReference];
        }

        return new(
            stage,
            subject,
            evidence?.RelatedLocations ?? [],
            sourceReferences,
            evidence?.ResolutionOptions ?? [],
            evidence?.Expected ?? "successful canonical Relations compilation",
            evidence?.Observed ?? observedFallback);
    }

    static string Prefix(string prefix, string? location) => location switch
    {
        null or "" or "$" => prefix,
        _ when location.StartsWith("/", StringComparison.Ordinal) => prefix + location,
        _ => prefix + "/" + location
    };

    static string Encode(string value) => Uri.EscapeDataString(value);
}
