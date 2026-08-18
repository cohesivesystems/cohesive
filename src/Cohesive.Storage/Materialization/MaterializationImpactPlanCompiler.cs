using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostic codes emitted by materialization impact compilation.</summary>
public static class MaterializationImpactDiagnosticCodes
{
    /// <summary>The materialization definition or exact Relations plan is invalid or stale.</summary>
    public const string DefinitionInvalid = "materialization.impact.definitionInvalid";

    /// <summary>No unique canonical relation-root acquisition input can be identified.</summary>
    public const string RootInputUnavailable = "materialization.impact.rootInputUnavailable";

    /// <summary>A dependency cannot be connected to relation roots through canonical relationship inputs.</summary>
    public const string RelationshipPathUnavailable = "materialization.impact.relationshipPathUnavailable";

    /// <summary>A strategy's required capability or operating bound is absent from the definition.</summary>
    public const string CapabilityUnavailable = "materialization.impact.capabilityUnavailable";

    /// <summary>No exact strategy or explicitly admitted bounded conservative fallback can cover a change input.</summary>
    public const string StrategyUnavailable = "materialization.impact.strategyUnavailable";
}

/// <summary>Deterministic result of compiling one exact materialization dependency manifest into impact routes.</summary>
public sealed record MaterializationImpactPlanCompilationResult
{
    /// <summary>Creates an impact-plan compilation result.</summary>
    /// <param name="plan">Complete plan when compilation succeeds; otherwise <see langword="null"/>.</param>
    /// <param name="diagnostics">Attributable diagnostics in deterministic order.</param>
    /// <exception cref="ArgumentException">
    /// Diagnostics contain null or incomplete entries, a plan is paired with an error diagnostic, or failure has
    /// no error diagnostic.
    /// </exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public MaterializationImpactPlanCompilationResult(
        MaterializationImpactPlan? plan,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        var normalized = MaterializationContract.NormalizeDiagnostics(
            diagnostics.IsDefault ? [] : diagnostics,
            nameof(diagnostics));
        if (plan is not null && normalized.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            throw new ArgumentException("A successful impact plan cannot retain error diagnostics.", nameof(diagnostics));
        }

        if (plan is null && normalized.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error))
        {
            throw new ArgumentException("Failed impact compilation requires an error diagnostic.", nameof(diagnostics));
        }

        Plan = plan;
        Diagnostics = normalized;
    }

    /// <summary>Complete impact plan, or <see langword="null"/> when compilation failed.</summary>
    public MaterializationImpactPlan? Plan { get; }

    /// <summary>Attributable diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether compilation produced a plan without error diagnostics.</summary>
    public bool IsSuccessful => Plan is not null
        && Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}

/// <summary>
/// Compiles the canonical Cohesive.Relations dependency manifest into bounded materialization root-impact routes.
/// </summary>
public static class MaterializationImpactPlanCompiler
{
    const string CompilationStage = "materialization-impact-compilation";

    /// <summary>Compiles one exact materialization definition into an executable impact plan.</summary>
    /// <param name="document">Validated materialization definition and content fence.</param>
    /// <param name="policy">Explicit strategy preference and hard work bounds.</param>
    /// <returns>A complete fingerprinted plan or fail-closed structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical plan content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical plan content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Canonical plan content cannot be fingerprinted.</exception>
    public static MaterializationImpactPlanCompilationResult Compile(
        MaterializationDocument document,
        MaterializationImpactPlanningPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(policy);
        return new CompilationContext(document, policy).Compile();
    }

    sealed class CompilationContext
    {
        readonly MaterializationDocument document;
        readonly MaterializationDefinition definition;
        readonly MaterializationImpactPlanningPolicy policy;
        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        readonly List<MaterializationImpactRoute> routes = [];
        CompiledRelationQueryPlan? plan;
        RelationDefinition? relation;
        RelationQuerySourceInputContract? root;

        public CompilationContext(
            MaterializationDocument document,
            MaterializationImpactPlanningPolicy policy)
        {
            this.document = document;
            definition = document.Definition;
            this.policy = policy;
        }

        public MaterializationImpactPlanCompilationResult Compile()
        {
            if (!ValidateInputs())
            {
                return Failure();
            }

            CompileRoutes();
            if (HasErrors)
            {
                return Failure();
            }

            var impactPlan = new MaterializationImpactPlan(
                schemaVersion: MaterializationImpactPlan.CurrentSchemaVersion,
                materialization: definition.Id,
                definitionFingerprint: document.DefinitionFingerprint,
                relationPlan: definition.Relation.CompiledPlan,
                output: definition.Relation.Output,
                policy,
                routes: [.. routes]);
            return new(
                plan: impactPlan,
                diagnostics: [.. diagnostics]);
        }

        bool ValidateInputs()
        {
            if (!string.Equals(
                    document.SchemaVersion,
                    MaterializationDocument.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                Error(
                    MaterializationImpactDiagnosticCodes.DefinitionInvalid,
                    "Impact compilation requires a current-version materialization document.",
                    "/schemaVersion",
                    expected: MaterializationDocument.CurrentSchemaVersion,
                    observed: document.SchemaVersion);
            }

            var definitionValidation = MaterializationDefinitionValidator.Validate(definition);
            if (!definitionValidation.IsValid)
            {
                diagnostics.AddRange(definitionValidation.Diagnostics);
            }

            var actualDefinitionFingerprint = MaterializationDefinitionFingerprinter.Compute(definition);
            if (!Equals(actualDefinitionFingerprint, document.DefinitionFingerprint))
            {
                Error(
                    MaterializationImpactDiagnosticCodes.DefinitionInvalid,
                    "The materialization document fingerprint is stale.",
                    "/definitionFingerprint",
                    expected: actualDefinitionFingerprint.Value,
                    observed: document.DefinitionFingerprint.Value);
            }

            var compilation = definition.Relation.Compile();
            if (!compilation.IsSuccessful || compilation.Plan is not { } compiled)
            {
                Error(
                    MaterializationImpactDiagnosticCodes.DefinitionInvalid,
                    "The retained Relations compilation request cannot produce an impact-planning input.",
                    "/definition/relation/compilationRequest",
                    expected: "successful exact Relations compilation",
                    observed: string.Join(
                        ",",
                        compilation.Diagnostics.Select(static diagnostic => diagnostic.Code)));
                return false;
            }

            plan = compiled;
            relation = compiled.Definition as RelationDefinition;
            if (relation is null)
            {
                Error(
                    MaterializationImpactDiagnosticCodes.DefinitionInvalid,
                    "Materialization impact compilation requires a canonical rooted Relation definition.",
                    "/definition/relation",
                    expected: nameof(RelationDefinition),
                    observed: compiled.Definition.GetType().Name);
                return false;
            }

            var roots = compiled.InputContract.Sources
                .Where(source => source.Role == RelationQuerySourceInputRole.RelationRoot
                                 && source.Binding == relation.RootBinding)
                .ToArray();
            if (roots.Length != 1)
            {
                Error(
                    MaterializationImpactDiagnosticCodes.RootInputUnavailable,
                    "Impact compilation requires exactly one canonical source for the Relation root binding.",
                    "/definition/relation/compilationRequest/definitionDocument/definition/rootBinding",
                    expected: "one compiled relation-root source",
                    observed: roots.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return false;
            }

            root = roots[0];
            return !HasErrors;
        }

        void CompileRoutes()
        {
            foreach (var source in plan!.InputContract.Sources)
            {
                CompileSource(source);
            }

            foreach (var traversal in plan.InputContract.Traversals)
            {
                CompileTraversal(traversal);
            }
        }

        void CompileSource(RelationQuerySourceInputContract source)
        {
            var dependencies = GetDependencyInputs(
                source.Input.Id,
                source.Fields,
                source.Input.Source,
                source.Binding,
                source.Shape);
            if (dependencies.IsDefaultOrEmpty)
            {
                return;
            }

            if (relation!.Output.Mode == RelationOutputMode.Set)
            {
                CompileGlobalOnly(
                    changeInput: source.Input.Id,
                    shape: source.Shape,
                    dependencies,
                    diagnosticCode: MaterializationImpactDiagnosticCodes.StrategyUnavailable,
                    message: "Set-valued materialization impact requires complete bounded root enumeration.");
                return;
            }

            if (source.Input.Id == root!.Input.Id)
            {
                if (!TryFindChangeDelivery(source.Input.Id, requireBeforeImage: false, out var changeDelivery))
                {
                    Error(
                        MaterializationImpactDiagnosticCodes.CapabilityUnavailable,
                        $"Direct-root change input '{source.Input.Id.Value}' lacks complete-mutation bounded incremental delivery.",
                        $"/definition/sources/{Encode(source.Input.Id.Value)}",
                        expected: nameof(MaterializationGuaranteeKind.CompleteMutationDelivery),
                        observed: "missing complete incremental change-delivery requirement");
                    return;
                }

                routes.Add(new(
                    changeInput: source.Input.Id,
                    changeShape: source.Shape,
                    dependencyInputs: dependencies,
                    strategy: new MaterializationDirectRootImpactStrategy(root.Input.Id),
                    precision: MaterializationImpactPrecision.Exact,
                    capabilities: [changeDelivery!],
                    maximumAffectedRoots: 1,
                    maximumReadBytes: policy.MaximumReadBytes));
                return;
            }

            CompileGlobalOnly(
                changeInput: source.Input.Id,
                shape: source.Shape,
                dependencies: dependencies,
                diagnosticCode: MaterializationImpactDiagnosticCodes.RelationshipPathUnavailable,
                message: $"Change input '{source.Input.Id.Value}' has no canonical relationship path to relation roots.");
        }

        void CompileTraversal(RelationQueryTraversalInputContract traversal)
        {
            var dependencies = GetDependencyInputs(
                traversal.Input.Id,
                traversal.Fields,
                traversal.Input.Traversal,
                traversal.Result,
                traversal.ResultShape);
            if (dependencies.IsDefaultOrEmpty)
            {
                return;
            }

            if (relation!.Output.Mode == RelationOutputMode.Set)
            {
                CompileGlobalOnly(
                    changeInput: traversal.Input.Id,
                    shape: traversal.ResultShape,
                    dependencies: dependencies,
                    diagnosticCode: MaterializationImpactDiagnosticCodes.StrategyUnavailable,
                    message: "Set-valued materialization impact requires complete bounded root enumeration.");
                return;
            }

            var path = BuildInversePath(traversal);
            List<string> unavailable = [];
            if (path is null)
            {
                unavailable.Add("canonical relationship path to the relation root is unavailable or ambiguous");
            }

            foreach (var strategy in policy.StrategyPreference)
            {
                MaterializationImpactRoute? route = strategy switch
                {
                    MaterializationImpactStrategyKind.InverseTraversal =>
                        path is { } inversePath
                            ? TryCompileInverseTraversal(traversal, inversePath, dependencies, unavailable)
                            : null,
                    MaterializationImpactStrategyKind.ContributorLedger =>
                        path is { } ledgerPath
                            ? TryCompileContributorLedger(traversal, ledgerPath, dependencies, unavailable)
                            : null,
                    MaterializationImpactStrategyKind.BoundedGlobalInvalidation =>
                        TryCompileGlobal(
                            traversal.Input.Id,
                            traversal.ResultShape,
                            dependencies,
                            unavailable),
                    _ => null
                };
                if (route is not null)
                {
                    routes.Add(route);
                    return;
                }
            }

            Error(
                path is null
                    ? MaterializationImpactDiagnosticCodes.RelationshipPathUnavailable
                    : MaterializationImpactDiagnosticCodes.StrategyUnavailable,
                $"No exact or explicitly bounded impact strategy covers change input '{traversal.Input.Id.Value}'.",
                $"/definition/sources/{Encode(traversal.Input.Id.Value)}",
                expected: string.Join(",", policy.StrategyPreference),
                observed: unavailable.Count == 0
                    ? "no non-direct strategy is permitted"
                    : string.Join("; ", unavailable.Distinct(StringComparer.Ordinal)),
                resolutionOptions:
                [
                    "Declare a complete bounded inverse-read capability.",
                    "Declare a target contributor ledger with complete bounded read/write limits.",
                    "Explicitly permit bounded global invalidation."
                ]);
        }

        void CompileGlobalOnly(
            RelationQueryInputId changeInput,
            QualifiedShapeId shape,
            ImmutableArray<RelationQueryInputId> dependencies,
            string diagnosticCode,
            string message)
        {
            List<string> unavailable = [];
            if (policy.StrategyPreference.Contains(MaterializationImpactStrategyKind.BoundedGlobalInvalidation)
                && TryCompileGlobal(changeInput, shape, dependencies, unavailable) is { } route)
            {
                routes.Add(route);
                return;
            }

            Error(
                diagnosticCode,
                message,
                $"/definition/sources/{Encode(changeInput.Value)}",
                expected: "explicit bounded global invalidation with complete source capability",
                observed: unavailable.Count == 0
                    ? "bounded global invalidation is not permitted by policy"
                    : string.Join("; ", unavailable.Distinct(StringComparer.Ordinal)),
                resolutionOptions:
                [
                    "Explicitly permit bounded global invalidation."
                ]);
        }

        MaterializationImpactRoute? TryCompileInverseTraversal(
            RelationQueryTraversalInputContract changed,
            ImmutableArray<RelationQueryTraversalInputContract> path,
            ImmutableArray<RelationQueryInputId> dependencies,
            ICollection<string> unavailable)
        {
            if (BuildSteps(path, ReferenceHistoryRequirement.PriorAndCurrent) is not { } steps)
            {
                unavailable.Add(
                    $"{MaterializationImpactStrategyKind.InverseTraversal}: relationship path requires an "
                    + "intermediate observation read not expressible by the v1 step language");
                return null;
            }

            var requiresBeforeImage = steps[0].Operation
                == MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction;
            if (!TryFindChangeDelivery(changed.Input.Id, requiresBeforeImage, out var changeDelivery))
            {
                unavailable.Add(
                    $"{MaterializationImpactStrategyKind.InverseTraversal}: input '{changed.Input.Id.Value}' lacks "
                    + $"complete bounded change delivery{(requiresBeforeImage ? " with BeforeImage" : string.Empty)} "
                    + $"and ReadBytes>={policy.MaximumReadBytes}");
                return null;
            }

            List<MaterializationImpactCapabilityReference> capabilityReferences = [changeDelivery!];
            foreach (var step in steps)
            {
                if (step.Operation == MaterializationInverseImpactOperationKind.PredicateLookup)
                {
                    if (!TryFindSourceCapability(
                            step.ReferenceSourceInput,
                            MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                            RequiredGuarantees(
                                MaterializationCapabilityKind.SourceParameterizedPredicateQuery),
                            [
                                new(
                                    kind: MaterializationLimitKind.ReadItems,
                                    maximum: policy.MaximumAffectedRoots),
                                new(
                                    kind: MaterializationLimitKind.ReadBytes,
                                    maximum: policy.MaximumReadBytes)
                            ],
                            out var predicate))
                    {
                        unavailable.Add(
                            $"{MaterializationImpactStrategyKind.InverseTraversal}: reference source "
                            + $"'{step.ReferenceSourceInput.Value}' lacks complete "
                            + $"{MaterializationCapabilityKind.SourceParameterizedPredicateQuery} with "
                            + $"ReadItems>={policy.MaximumAffectedRoots} and ReadBytes>={policy.MaximumReadBytes}");
                        return null;
                    }

                    capabilityReferences.Add(predicate!);
                }
            }

            var lineage = steps[0].Operation
                == MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction
                ? MaterializationImpactLineageKind.BeforeAndAfterRelationshipReferences
                : MaterializationImpactLineageKind.ContributorIdentity;
            return new(
                changeInput: changed.Input.Id,
                changeShape: changed.ResultShape,
                dependencyInputs: dependencies,
                strategy: new MaterializationInverseTraversalImpactStrategy(
                    steps: steps,
                    lineage: lineage),
                precision: MaterializationImpactPrecision.Exact,
                capabilities: [.. capabilityReferences],
                maximumAffectedRoots: policy.MaximumAffectedRoots,
                maximumReadBytes: policy.MaximumReadBytes);
        }

        ImmutableArray<RelationQueryTraversalInputContract>? BuildInversePath(
            RelationQueryTraversalInputContract changed)
        {
            var traversals = plan!.InputContract.Traversals;
            List<RelationQueryTraversalInputContract> path = [];
            var current = changed;
            HashSet<RelationQueryInputId> visited = [];
            while (visited.Add(current.Input.Id))
            {
                path.Add(current);
                if (current.From == relation!.RootBinding)
                {
                    return [.. path];
                }

                var upstream = traversals.Where(candidate => candidate.Result == current.From).Take(2).ToArray();
                if (upstream.Length != 1)
                {
                    return null;
                }

                current = upstream[0];
            }

            return null;
        }

        ImmutableArray<MaterializationInverseImpactStep>? BuildSteps(
            ImmutableArray<RelationQueryTraversalInputContract> path,
            ReferenceHistoryRequirement referenceHistory)
        {
            List<MaterializationInverseImpactStep> steps = [];
            for (var index = 0; index < path.Length; index++)
            {
                var traversal = path[index];
                RelationQueryInputId referenceSource;
                MaterializationInverseImpactOperationKind operation;
                switch (traversal.Input.Direction)
                {
                    case RelationshipTraversalDirection.Forward:
                        if (FindAcquisitionInput(traversal.From) is not { } acquisition)
                        {
                            return null;
                        }

                        referenceSource = acquisition;
                        operation = MaterializationInverseImpactOperationKind.PredicateLookup;
                        break;
                    case RelationshipTraversalDirection.Inverse:
                        if (index > 0
                            && steps[^1].Operation != MaterializationInverseImpactOperationKind.PredicateLookup)
                        {
                            return null;
                        }

                        referenceSource = traversal.Input.Id;
                        operation = index switch
                        {
                            0 when referenceHistory == ReferenceHistoryRequirement.PriorAndCurrent =>
                                MaterializationInverseImpactOperationKind.BeforeAndAfterReferenceExtraction,
                            0 => MaterializationInverseImpactOperationKind.AfterRelationshipReferenceExtraction,
                            _ => MaterializationInverseImpactOperationKind.CurrentRelationshipReferenceExtraction
                        };
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(path),
                            traversal.Input.Direction,
                            "Unsupported relationship traversal direction.");
                }

                steps.Add(new(
                    relationshipInput: traversal.Input.Id,
                    referenceSourceInput: referenceSource,
                    operation: operation));
            }

            return [.. steps];
        }

        RelationQueryInputId? FindAcquisitionInput(ValueBindingId binding)
        {
            var candidates = plan!.InputContract.Sources
                .Where(source => source.Binding == binding)
                .Select(static source => source.Input.Id)
                .Concat(plan.InputContract.Traversals
                    .Where(traversal => traversal.Result == binding)
                    .Select(static traversal => traversal.Input.Id))
                .Take(2)
                .ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }

        MaterializationImpactRoute? TryCompileContributorLedger(
            RelationQueryTraversalInputContract changed,
            ImmutableArray<RelationQueryTraversalInputContract> path,
            ImmutableArray<RelationQueryInputId> dependencies,
            ICollection<string> unavailable)
        {
            if (BuildSteps(path, ReferenceHistoryRequirement.CurrentOnly) is not { } currentRootSteps)
            {
                unavailable.Add(
                    $"{MaterializationImpactStrategyKind.ContributorLedger}: current relationship path requires an "
                    + "intermediate observation read not expressible by the v1 step language");
                return null;
            }

            if (!TryFindChangeDelivery(changed.Input.Id, requireBeforeImage: false, out var changeDelivery))
            {
                unavailable.Add(
                    $"{MaterializationImpactStrategyKind.ContributorLedger}: input '{changed.Input.Id.Value}' lacks "
                    + $"complete bounded change delivery with ReadBytes>={policy.MaximumReadBytes}");
                return null;
            }

            if (!TryFindTargetCapability(
                    MaterializationCapabilityKind.TargetContributorLedger,
                    RequiredGuarantees(MaterializationCapabilityKind.TargetContributorLedger),
                    [
                        new(
                            kind: MaterializationLimitKind.ReadItems,
                            maximum: policy.MaximumAffectedRoots),
                        new(
                            kind: MaterializationLimitKind.ReadBytes,
                            maximum: policy.MaximumReadBytes),
                        new(
                            kind: MaterializationLimitKind.WriteItems,
                            maximum: 1),
                        new(
                            kind: MaterializationLimitKind.WriteBytes,
                            maximum: policy.MaximumLedgerWriteBytes!.Value)
                    ],
                    out var capability))
            {
                unavailable.Add(
                    $"{MaterializationImpactStrategyKind.ContributorLedger}: target lacks complete atomic fenced "
                    + $"ledger evidence with ReadItems>={policy.MaximumAffectedRoots}, "
                    + $"ReadBytes>={policy.MaximumReadBytes}, WriteItems>=1, and "
                    + $"WriteBytes>={policy.MaximumLedgerWriteBytes!.Value}");
                return null;
            }
            List<MaterializationImpactCapabilityReference> capabilityReferences = [changeDelivery!, capability!];
            foreach (var step in currentRootSteps)
            {
                if (step.Operation == MaterializationInverseImpactOperationKind.PredicateLookup)
                {
                    if (!TryFindSourceCapability(
                            step.ReferenceSourceInput,
                            MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                            RequiredGuarantees(
                                MaterializationCapabilityKind.SourceParameterizedPredicateQuery),
                            [
                                new(
                                    kind: MaterializationLimitKind.ReadItems,
                                    maximum: policy.MaximumAffectedRoots),
                                new(
                                    kind: MaterializationLimitKind.ReadBytes,
                                    maximum: policy.MaximumReadBytes)
                            ],
                            out var predicate))
                    {
                        unavailable.Add(
                            $"{MaterializationImpactStrategyKind.ContributorLedger}: current-root reference source "
                            + $"'{step.ReferenceSourceInput.Value}' lacks complete "
                            + $"{MaterializationCapabilityKind.SourceParameterizedPredicateQuery} with "
                            + $"ReadItems>={policy.MaximumAffectedRoots} and ReadBytes>={policy.MaximumReadBytes}");
                        return null;
                    }

                    capabilityReferences.Add(predicate!);
                }
            }

            return new(
                changeInput: changed.Input.Id,
                changeShape: changed.ResultShape,
                dependencyInputs: dependencies,
                strategy: new MaterializationContributorLedgerImpactStrategy(
                    contributorInput: changed.Input.Id,
                    currentRootSteps: currentRootSteps),
                precision: MaterializationImpactPrecision.Exact,
                capabilities: [.. capabilityReferences],
                maximumAffectedRoots: policy.MaximumAffectedRoots,
                maximumReadBytes: policy.MaximumReadBytes);
        }

        MaterializationImpactRoute? TryCompileGlobal(
            RelationQueryInputId changeInput,
            QualifiedShapeId shape,
            ImmutableArray<RelationQueryInputId> dependencies,
            ICollection<string> unavailable)
        {
            if (policy.MaximumGlobalRoots is not { } maximumGlobalRoots)
            {
                unavailable.Add(
                    $"{MaterializationImpactStrategyKind.BoundedGlobalInvalidation}: policy omits a complete root bound");
                return null;
            }

            if (!TryFindChangeDelivery(changeInput, requireBeforeImage: false, out var changeDelivery))
            {
                unavailable.Add(
                    $"{MaterializationImpactStrategyKind.BoundedGlobalInvalidation}: input '{changeInput.Value}' "
                    + $"lacks complete bounded change delivery with ReadBytes>={policy.MaximumReadBytes}");
                return null;
            }

            if (!TryFindSourceCapability(
                    root!.Input.Id,
                    MaterializationCapabilityKind.SourceBoundedEnumeration,
                    RequiredGuarantees(MaterializationCapabilityKind.SourceBoundedEnumeration),
                    [
                        new(
                            kind: MaterializationLimitKind.ReadItems,
                            maximum: maximumGlobalRoots),
                        new(
                            kind: MaterializationLimitKind.ReadBytes,
                            maximum: policy.MaximumReadBytes)
                    ],
                    out var capability))
            {
                unavailable.Add(
                    $"{MaterializationImpactStrategyKind.BoundedGlobalInvalidation}: root source "
                    + $"'{root.Input.Id.Value}' lacks complete {MaterializationCapabilityKind.SourceBoundedEnumeration} "
                    + $"with ReadItems>={maximumGlobalRoots} and ReadBytes>={policy.MaximumReadBytes}");
                return null;
            }

            return new(
                changeInput: changeInput,
                changeShape: shape,
                dependencyInputs: dependencies,
                strategy: new MaterializationBoundedGlobalImpactStrategy(root.Input.Id),
                precision: MaterializationImpactPrecision.Conservative,
                capabilities: [changeDelivery!, capability!],
                maximumAffectedRoots: maximumGlobalRoots,
                maximumReadBytes: policy.MaximumReadBytes);
        }

        ImmutableArray<RelationQueryInputId> GetDependencyInputs(
            RelationQueryInputId acquisitionInput,
            ImmutableArray<RelationQueryFieldInputContract> fields,
            QueryNodeId producer,
            ValueBindingId binding,
            QualifiedShapeId shape)
        {
            HashSet<RelationQueryInputId> candidates = [acquisitionInput];
            foreach (var field in fields)
            {
                candidates.Add(field.Input.Id);
            }

            foreach (var identity in plan!.InputContract.Identities)
            {
                if (identity.Input.Producer == producer
                    && identity.Input.Binding == binding
                    && identity.Input.Shape == shape)
                {
                    candidates.Add(identity.Input.Id);
                }
            }

            return
            [
                .. plan.DependencyManifest.Entries
                    .Where(entry => candidates.Contains(entry.Input.Id)
                                    && entry.Impacts.Any(impact =>
                                        definition.Relation.Output.Covers(impact.Output)))
                    .Select(static entry => entry.Input.Id)
                    .OrderBy(static input => input.Value, StringComparer.Ordinal)
            ];
        }

        bool TryFindSourceCapability(
            RelationQueryInputId sourceInput,
            MaterializationCapabilityKind capability,
            ImmutableArray<MaterializationGuaranteeKind> guarantees,
            ImmutableArray<MaterializationOperatingLimit> limits,
            out MaterializationImpactCapabilityReference? reference)
        {
            reference = null;
            var source = definition.Sources.SingleOrDefault(candidate => candidate.Input == sourceInput);
            var requirement = source?.Capabilities.FirstOrDefault(candidate =>
                candidate.Capability == capability
                && (candidate.Modes & MaterializationSynchronizationMode.Incremental) != 0
                && Satisfies(candidate, guarantees, limits));
            if (requirement is null)
            {
                return false;
            }

            reference = new(
                role: MaterializationEndpointRole.Source,
                requirement: requirement.Id,
                sourceInput);
            return true;
        }

        bool TryFindChangeDelivery(
            RelationQueryInputId sourceInput,
            bool requireBeforeImage,
            out MaterializationImpactCapabilityReference? reference)
        {
            var guarantees = RequiredGuarantees(MaterializationCapabilityKind.SourceChangeDelivery)
                .Add(MaterializationGuaranteeKind.CompleteMutationDelivery);
            if (requireBeforeImage)
            {
                guarantees = guarantees.Add(MaterializationGuaranteeKind.BeforeImage);
            }

            return TryFindSourceCapability(
                sourceInput,
                MaterializationCapabilityKind.SourceChangeDelivery,
                guarantees,
                [
                    new(
                        kind: MaterializationLimitKind.ChangeItems,
                        maximum: 1),
                    new(
                        kind: MaterializationLimitKind.ReadBytes,
                        maximum: policy.MaximumReadBytes)
                ],
                out reference);
        }

        bool TryFindTargetCapability(
            MaterializationCapabilityKind capability,
            ImmutableArray<MaterializationGuaranteeKind> guarantees,
            ImmutableArray<MaterializationOperatingLimit> limits,
            out MaterializationImpactCapabilityReference? reference)
        {
            reference = null;
            var requirement = definition.TargetCapabilities.FirstOrDefault(candidate =>
                candidate.Capability == capability
                && (candidate.Modes & MaterializationSynchronizationMode.Incremental) != 0
                && Satisfies(candidate, guarantees, limits));
            if (requirement is null)
            {
                return false;
            }

            reference = new(
                role: MaterializationEndpointRole.Target,
                requirement: requirement.Id);
            return true;
        }

        static bool Satisfies(
            MaterializationCapabilityRequirement requirement,
            ImmutableArray<MaterializationGuaranteeKind> guarantees,
            ImmutableArray<MaterializationOperatingLimit> limits)
        {
            if (guarantees.Any(guarantee => !requirement.Guarantees.Contains(guarantee)))
            {
                return false;
            }

            foreach (var limit in limits)
            {
                var declared = requirement.OperatingLimits.FirstOrDefault(candidate => candidate.Kind == limit.Kind);
                if (declared.Maximum < limit.Maximum)
                {
                    return false;
                }
            }

            return true;
        }

        ImmutableArray<MaterializationGuaranteeKind> RequiredGuarantees(
            MaterializationCapabilityKind capability) =>
            [
                .. MaterializationDefinitionValidator.GetRequiredGuarantees(
                    definition.UpdatePolicy,
                    capability)
            ];

        bool HasErrors => diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        MaterializationImpactPlanCompilationResult Failure() => new(
            plan: null,
            diagnostics: [.. diagnostics]);

        void Error(
            string code,
            string message,
            string location,
            string expected,
            string observed,
            ImmutableArray<string> resolutionOptions = default) =>
            diagnostics.Add(MaterializationContract.CreateDiagnostic(
                code: code,
                severity: DiagnosticSeverity.Error,
                message: message,
                location: location,
                stage: CompilationStage,
                subject: definition.Id.Value,
                sourceReferences: [definition.Provenance.Source.Reference, policy.Id.Value],
                expected: expected,
                observed: observed,
                resolutionOptions: resolutionOptions));

        static string Encode(string value) => Uri.EscapeDataString(value);

        enum ReferenceHistoryRequirement
        {
            CurrentOnly = 0,
            PriorAndCurrent = 1
        }

    }
}
