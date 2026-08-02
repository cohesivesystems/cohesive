using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.Elastic;

/// <summary>
/// Compiles exact demand-scoped canonical relation/query branches to Elasticsearch request templates.
/// </summary>
public sealed class ElasticRelationQueryCompiler
{
    /// <summary>Configuration-evidence setting for the exact compiler implementation profile.</summary>
    public const string CompilerProfileSetting = "compilerProfile";

    /// <summary>Configuration-evidence setting for the compiler convention set.</summary>
    public const string CompilerConventionSetting = "compilerConventionSet";

    /// <summary>Configuration-evidence setting for the normalized lowering policy.</summary>
    public const string LoweringPolicySetting = "loweringPolicy";

    readonly ElasticRelationQueryCompilerOptions options;
    readonly ElasticQueryLoweringPolicy loweringPolicy;
    readonly EffectiveConfigurationOrigin optionsOrigin;
    readonly EffectiveConfigurationOrigin loweringPolicyOrigin;

    /// <summary>Creates a canonical Elasticsearch compiler.</summary>
    /// <param name="options">Artifact-identity options, or <see langword="null"/> for current defaults.</param>
    /// <param name="loweringPolicy">
    /// Configurable exact physical-lowering policy, or <see langword="null"/> for the adapter defaults.
    /// </param>
    public ElasticRelationQueryCompiler(
        ElasticRelationQueryCompilerOptions? options = null,
        ElasticQueryLoweringPolicy? loweringPolicy = null
        )
    {
        this.options = options ?? new();
        this.loweringPolicy = loweringPolicy ?? ElasticQueryLoweringPolicy.Default;
        optionsOrigin = options is null
            ? EffectiveConfigurationOrigin.AdapterConvention
            : EffectiveConfigurationOrigin.Explicit;
        loweringPolicyOrigin = loweringPolicy is null
            ? EffectiveConfigurationOrigin.AdapterConvention
            : EffectiveConfigurationOrigin.Explicit;
    }

    /// <summary>
    /// Qualifies profile-level Elasticsearch feasibility with the exact placement and storage-binding evidence.
    /// </summary>
    /// <param name="request">Exact plan, profile feasibility, placement, and selected branches to qualify.</param>
    /// <param name="storageBinding">Versioned concrete-index and field-mapping binding to examine.</param>
    /// <returns>
    /// A deterministic bound-realization report predicting whether native Elasticsearch compilation can preserve
    /// every selected branch.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="storageBinding"/> is <see langword="null"/>.
    /// </exception>
    public RelationQueryBoundRealizationReport Realize(
        RelationQueryBoundRealizationRequest request,
        ElasticRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        return ObserveContextualRealization(request, storageBinding).Report;
    }

    (RelationQueryBoundRealizationReport Report,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics) ObserveContextualRealization(
        RelationQueryBoundRealizationRequest request,
        ElasticRelationQueryStorageBinding storageBinding) =>
        RelationQueryCompilerTelemetry.Observe(
            ElasticRelationQueryTelemetry.Emitter,
            RelationQueryTelemetry.RealizationActivityName,
            (Compiler: this, Request: request, StorageBinding: storageBinding),
            static state => state.Compiler.RealizeCore(state.Request, state.StorageBinding),
            static realization => RelationQueryTelemetry.GetStatusTagValue(realization.Report.Status),
            static (activity, state, realization) => RelationQueryCompilerTelemetry.ProjectRealizationActivity(
                activity,
                state.Request,
                state.StorageBinding.Fingerprint.Value,
                realization.Report));

    (RelationQueryBoundRealizationReport Report,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics) RealizeCore(
        RelationQueryBoundRealizationRequest request,
        ElasticRelationQueryStorageBinding storageBinding)
    {
        var projection = ProjectContextualEvidence(request, storageBinding);
        return (
            RelationQueryBoundRealizationCompiler.Compile(request, projection.Evidence),
            projection.Diagnostics);
    }

    /// <summary>
    /// Contextually realizes and then compiles every selected branch using the same exact Elasticsearch binding.
    /// </summary>
    /// <param name="request">Exact plan, profile feasibility, placement, and selected branches to compile.</param>
    /// <param name="storageBinding">Versioned concrete-index and field-mapping binding.</param>
    /// <returns>Exact immutable artifacts or structured invalid/unsupported diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="storageBinding"/> is <see langword="null"/>.
    /// </exception>
    public ElasticRelationQueryCompilationResult Compile(
        RelationQueryBoundRealizationRequest request,
        ElasticRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        var outcome = RelationQueryCompilerTelemetry.Observe(
            ElasticRelationQueryTelemetry.Emitter,
            RelationQueryTelemetry.NativeCompilationActivityName,
            (Compiler: this, Request: request, StorageBinding: storageBinding),
            static state => state.Compiler.CompileBoundCore(state.Request, state.StorageBinding),
            static observed => RelationQueryTelemetry.GetStatusTagValue(observed.Compilation.Status),
            static (activity, state, observed) => ProjectCompilationActivity(
                activity,
                state.Request.PlanReference,
                state.Request.Placement,
                state.StorageBinding,
                observed.BoundRealization.ProfileFeasibility.TargetProfile.Target.Value,
                observed.BoundRealization.Fingerprint.Value,
                observed.Compilation),
            RelationQueryTelemetry.BoundRequestKind);
        return outcome.Compilation;
    }

    (ElasticRelationQueryCompilationResult Compilation, RelationQueryBoundRealizationReport BoundRealization)
        CompileBoundCore(
        RelationQueryBoundRealizationRequest request,
        ElasticRelationQueryStorageBinding storageBinding)
    {

        var realization = ObserveContextualRealization(request, storageBinding);
        var boundRealization = realization.Report;
        if (!boundRealization.IsRealizable)
        {
            ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics =
            [
                .. realization.Diagnostics,
                .. RelationQueryNativeCompilationDiagnostic.FromBoundRealizationFailure(boundRealization)
            ];
            ElasticRelationQueryCompilationResult compilation = new(
                boundRealization.Status == RelationQueryRealizationStatus.Invalid
                    ? RelationQueryNativeCompilationStatus.Invalid
                    : RelationQueryNativeCompilationStatus.Unsupported,
                [],
                diagnostics);
            return new(compilation, boundRealization);
        }

        RelationQueryNativeCompilationRequest nativeRequest = new(
            request.Plan,
            boundRealization,
            request.Placement);
        return new(CompileCore(nativeRequest, storageBinding), boundRealization);
    }

    /// <summary>Compiles every selected request branch independently and fails closed on semantic uncertainty.</summary>
    /// <param name="request">Exact static-plan, realization, placement, and branch-selection context.</param>
    /// <param name="storageBinding">Versioned concrete-index and field-mapping binding.</param>
    /// <returns>Exact immutable artifacts or structured invalid/unsupported diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="storageBinding"/> is <see langword="null"/>.
    /// </exception>
    public ElasticRelationQueryCompilationResult Compile(
        RelationQueryNativeCompilationRequest request,
        ElasticRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        return RelationQueryCompilerTelemetry.Observe(
            ElasticRelationQueryTelemetry.Emitter,
            RelationQueryTelemetry.NativeCompilationActivityName,
            (Compiler: this, Request: request, StorageBinding: storageBinding),
            static state => state.Compiler.CompileCore(state.Request, state.StorageBinding),
            static result => RelationQueryTelemetry.GetStatusTagValue(result.Status),
            static (activity, state, result) => ProjectCompilationActivity(
                activity,
                state.Request.PlanReference,
                state.Request.Placement,
                state.StorageBinding,
                state.Request.BoundRealization.ProfileFeasibility.TargetProfile.Target.Value,
                state.Request.BoundRealization.Fingerprint.Value,
                result),
            RelationQueryTelemetry.NativeRequestKind);
    }

    ElasticRelationQueryCompilationResult CompileCore(
        RelationQueryNativeCompilationRequest request,
        ElasticRelationQueryStorageBinding storageBinding)
    {

        var diagnostics = ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        var inputDiagnostics = request.ValidateInputs();
        var bindingDiagnostics = ValidateBinding(
            request.PlanReference,
            request.ProfileFeasibility,
            request.Placement,
            request.Selection,
            storageBinding);
        var exactBindingDiagnostics = ValidateExactBinding(request, storageBinding);
        diagnostics.AddRange(inputDiagnostics);
        diagnostics.AddRange(bindingDiagnostics);
        diagnostics.AddRange(exactBindingDiagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            var invalid = !bindingDiagnostics.IsDefaultOrEmpty
                || !exactBindingDiagnostics.IsDefaultOrEmpty
                || request.BoundRealization.Status == RelationQueryRealizationStatus.Invalid
                || inputDiagnostics.Any(static diagnostic =>
                    diagnostic.Code != RelationQueryNativeCompilationDiagnosticCodes.RealizationUnavailable
                    && diagnostic.Code != RelationQueryNativeCompilationDiagnosticCodes.BoundRealizationUnavailable);
            return new(
                invalid
                    ? RelationQueryNativeCompilationStatus.Invalid
                    : RelationQueryNativeCompilationStatus.Unsupported,
                [],
                diagnostics.ToImmutable());
        }

        var artifacts = ImmutableArray.CreateBuilder<ElasticRelationQueryCompiledArtifact>();
        foreach (var branch in request.Branches)
        {
            try
            {
                artifacts.Add(new BranchCompiler(
                    request,
                    storageBinding,
                    options,
                    loweringPolicy,
                    branch).Compile());
            }
            catch (BranchCompilationException exception)
            {
                diagnostics.Add(new(
                    exception.Code,
                    DiagnosticSeverity.Error,
                    exception.Message,
                    branch.Id,
                    exception.Node,
                    exception.Input,
                    adapterDecisionCode: new(exception.Code)));
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or KeyNotFoundException
                                              or NotSupportedException)
            {
                diagnostics.Add(new(
                    ElasticRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
                    DiagnosticSeverity.Error,
                    $"Elasticsearch artifact construction failed closed: {exception.Message}",
                    branch.Id,
                    adapterDecisionCode: new(ElasticRelationQueryCompilationDiagnosticCodes.ArtifactInvalid)));
            }
        }

        var normalizedDiagnostics = diagnostics.ToImmutable();
        return new(
            normalizedDiagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                ? RelationQueryNativeCompilationStatus.Unsupported
                : RelationQueryNativeCompilationStatus.Exact,
            artifacts.ToImmutable(),
            normalizedDiagnostics);
    }

    static void ProjectCompilationActivity(
        Activity activity,
        RelationQueryCompiledPlanReference plan,
        RelationQuerySourcePlacement placement,
        ElasticRelationQueryStorageBinding storageBinding,
        string target,
        string boundRealizationFingerprint,
        ElasticRelationQueryCompilationResult result)
    {
        RelationQueryCompilerTelemetry.ProjectNativeCompilationActivity(
            activity,
            plan,
            placement,
            target,
            storageBinding.Fingerprint.Value,
            boundRealizationFingerprint,
            result.Artifacts.Length,
            result.Diagnostics.Length,
            result.Artifacts.Length == 1 ? result.Artifacts[0].Fingerprint.Value : null);
        foreach (var diagnostic in result.Diagnostics)
        {
            RelationQueryTelemetry.AddDiagnosticEvent(
                activity,
                diagnostic.Code,
                diagnostic.Severity);
        }
    }

    static ImmutableArray<RelationQueryNativeCompilationDiagnostic> ValidateBinding(
        RelationQueryCompiledPlanReference planReference,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement,
        RelationQueryCompilationSelection selection,
        ElasticRelationQueryStorageBinding storageBinding)
    {
        var diagnostics = ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        var reportProfile = realization.TargetProfile;
        var expectedPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(planReference);
        if (storageBinding.CompiledPlanFingerprint is not { } compiledPlanFingerprint)
        {
            diagnostics.Add(BindingDiagnostic(
                "Exact Elasticsearch realization requires compiled-plan affinity on the storage binding."));
        }
        else if (!Equals(compiledPlanFingerprint, expectedPlanFingerprint))
        {
            diagnostics.Add(BindingDiagnostic(
                "The Elasticsearch storage binding's exact compiled-plan affinity does not match the native-compilation request."));
        }

        if (storageBinding.PlacementFingerprint is not { } placementFingerprint)
        {
            diagnostics.Add(BindingDiagnostic(
                "Exact Elasticsearch realization requires source-placement affinity on the storage binding."));
        }
        else if (!Equals(placementFingerprint, placement.Fingerprint))
        {
            diagnostics.Add(BindingDiagnostic(
                "The Elasticsearch storage binding's exact source-placement affinity does not match the native-compilation request."));
        }

        if (storageBinding.Target != reportProfile.Target
            || storageBinding.TargetProfile != reportProfile.Id
            || storageBinding.Target != ElasticRelationQueryTargetProfile.Target
            || storageBinding.TargetProfile != ElasticRelationQueryTargetProfile.ProfileId
            || !reportProfile.HasSameSemantics(ElasticRelationQueryTargetProfile.Default))
        {
            diagnostics.Add(BindingDiagnostic(
                "The Elasticsearch binding, realization report, and canonical target profile do not identify the same exact target snapshot."));
        }

        var sources = placement.SourceInstances
            .Where(source => source.Id == storageBinding.Source)
            .ToArray();
        if (sources.Length != 1)
        {
            diagnostics.Add(BindingDiagnostic(
                $"Elasticsearch source instance '{storageBinding.Source.Value}' is not declared exactly once by the source placement."));
        }
        else if (sources[0].TargetProfile.Target != storageBinding.Target
                 || sources[0].TargetProfile.Id != storageBinding.TargetProfile
                 || !sources[0].TargetProfile.HasSameSemantics(reportProfile))
        {
            diagnostics.Add(BindingDiagnostic(
                "The placed source capability snapshot does not match the realization report and Elasticsearch binding."));
        }

        var placements = placement.Bindings
            .Where(binding => binding.Id == storageBinding.PlacementBinding)
            .ToArray();
        if (placements.Length != 1)
        {
            diagnostics.Add(BindingDiagnostic(
                $"Placement binding '{storageBinding.PlacementBinding.Value}' is not declared exactly once."));
        }
        else
        {
            var placementBinding = placements[0];
            if (placementBinding.Source != storageBinding.Source
                || placementBinding.Kind != RelationQuerySourcePlacementBindingKind.SourceSet)
            {
                diagnostics.Add(BindingDiagnostic(
                    "The Elasticsearch storage binding must identify one source-set placement on its declared source instance."));
            }
            if (selection.Sources.Length != 1
                || selection.Traversals.Length != 0
                || selection.PlacementBindings.Length != 1
                || selection.SourceInstances.Length != 1
                || selection.Sources[0].Input.Id != placementBinding.Input
                || selection.Sources[0].Node != placementBinding.Node
                || selection.Sources[0].Binding != placementBinding.Binding
                || selection.PlacementBindings[0].Id != placementBinding.Id
                || selection.SourceInstances[0].Id != placementBinding.Source)
            {
                diagnostics.Add(BindingDiagnostic(
                    "Canonical Elasticsearch v2 requires the selected branches to use exactly one placed source contract and no relationship traversal contracts."));
            }
        }

        return diagnostics.ToImmutable();

        static RelationQueryNativeCompilationDiagnostic BindingDiagnostic(string message) => new(
            ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch,
            DiagnosticSeverity.Error,
            message,
            adapterDecisionCode: new(ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch));
    }

    ContextualProjectionResult ProjectContextualEvidence(
        RelationQueryBoundRealizationRequest request,
        ElasticRelationQueryStorageBinding storageBinding)
    {
        var diagnostics = ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        var inputDiagnostics = request.ValidateInputs();
        var bindingDiagnostics = ValidateBinding(
            request.PlanReference,
            request.ProfileFeasibility,
            request.Placement,
            request.Selection,
            storageBinding);
        diagnostics.AddRange(inputDiagnostics);
        diagnostics.AddRange(bindingDiagnostics);

        var bindingReference = CreateBindingReference(storageBinding);
        if (!request.ProfileFeasibility.IsRealizable)
        {
            return new(
                new(bindingReference, []),
                diagnostics.ToImmutable());
        }

        RelationQueryContextualBranchFailure? globalFailure = null;
        if (!inputDiagnostics.IsDefaultOrEmpty)
        {
            globalFailure = new(
                RelationQueryBoundAssessmentStatus.Invalid,
                RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
                new(ElasticRelationQueryCompilationDiagnosticCodes.ArtifactInvalid),
                string.Join("; ", inputDiagnostics.Select(static diagnostic => diagnostic.Message)),
                "Recompile the canonical plan, feasibility report, and placement from the same definition snapshot.",
                failedConfigurationSetting: CompilerProfileSetting);
        }
        else if (!bindingDiagnostics.IsDefaultOrEmpty)
        {
            globalFailure = new(
                RelationQueryBoundAssessmentStatus.Invalid,
                RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
                new(ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch),
                string.Join("; ", bindingDiagnostics.Select(static diagnostic => diagnostic.Message)),
                "Re-author the Elasticsearch binding from the selected plan-bound placement input.",
                failedConfigurationSetting: BindingFailureSetting(bindingDiagnostics));
        }

        Dictionary<RelationQueryNativeResultBranchId, RelationQueryContextualBranchFailure?> failures = [];
        foreach (var branch in request.Branches)
        {
            if (globalFailure is { } failure)
            {
                failures.Add(branch.Id, AttributeFailure(request, branch, failure));
                continue;
            }

            try
            {
                new BranchCompiler(
                    request,
                    storageBinding,
                    options,
                    loweringPolicy,
                    branch).Validate();
                failures.Add(branch.Id, null);
            }
            catch (BranchCompilationException exception)
            {
                diagnostics.Add(new(
                    exception.Code,
                    DiagnosticSeverity.Error,
                    exception.Message,
                    branch.Id,
                    exception.Node,
                    exception.Input,
                    adapterDecisionCode: new(exception.Code)));
                failures.Add(branch.Id, CreateBranchFailure(request, branch, exception));
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or KeyNotFoundException
                                              or NotSupportedException)
            {
                var message = $"Elasticsearch contextual validation failed closed: {exception.Message}";
                diagnostics.Add(new(
                    ElasticRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
                    DiagnosticSeverity.Error,
                    message,
                    branch.Id,
                    adapterDecisionCode: new(ElasticRelationQueryCompilationDiagnosticCodes.ArtifactInvalid)));
                failures.Add(branch.Id, AttributeFailure(request, branch, new(
                    RelationQueryBoundAssessmentStatus.Invalid,
                    RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
                    new(ElasticRelationQueryCompilationDiagnosticCodes.ArtifactInvalid),
                    message,
                    "Correct the exact Elasticsearch binding or canonical plan before retrying contextual realization.",
                    failedConfigurationSetting: CompilerProfileSetting)));
            }
        }

        var assessments = RelationQueryContextualAssessmentProjector.Project(
            request,
            "elastic/context",
            branch => failures[branch.Id],
            (branch, requirement, failure) => ResolveAssessmentAttribution(
                request,
                storageBinding,
                bindingReference,
                branch,
                requirement,
                failure));

        return new(
            new(bindingReference, assessments),
            diagnostics.ToImmutable());
    }

    static RelationQueryContextualBranchFailure CreateBranchFailure(
        RelationQueryBoundRealizationRequest request,
        RelationQueryNativeResultBranch branch,
        BranchCompilationException exception)
    {
        var (status, reason) = exception.Code switch
        {
            ElasticRelationQueryCompilationDiagnosticCodes.ArtifactInvalid
                or ElasticRelationQueryCompilationDiagnosticCodes.LoweringConfigurationInvalid =>
                (RelationQueryBoundAssessmentStatus.Invalid,
                    RelationQueryUnavailableReason.CapabilityEvidenceInvalid),
            ElasticRelationQueryCompilationDiagnosticCodes.FieldBindingMissing
                or ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
                or ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable
                or ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable =>
                (RelationQueryBoundAssessmentStatus.Unavailable,
                    RelationQueryUnavailableReason.OperatingBoundaryInvalid),
            _ =>
                (RelationQueryBoundAssessmentStatus.Unavailable,
                    RelationQueryUnavailableReason.PolicyRejected)
        };
        var failure = new RelationQueryContextualBranchFailure(
            status,
            reason,
            new(exception.Code),
            exception.Message,
            "Update the Elasticsearch binding or compiler policy so the canonical compiler can preserve this branch.",
            exception.Node,
            exception.Input,
            failedOperatingBoundary: FailureBoundary(exception),
            failedConfigurationSetting: FailureConfigurationSetting(exception));
        return AttributeFailure(request, branch, failure);
    }

    static RelationQueryContextualBranchFailure AttributeFailure(
        RelationQueryBoundRealizationRequest request,
        RelationQueryNativeResultBranch branch,
        RelationQueryContextualBranchFailure failure)
    {
        var selection = request.Selection.GetBranch(branch.Id);
        var decisions = request.ProfileFeasibility.Decisions.ToDictionary(static decision => decision.Requirement);
        RelationQueryOperatingBoundaryId? failedBoundary = failure.FailedOperatingBoundary is { } boundary
                                                              && selection.Requirements.Any(requirement =>
                                                                  decisions[requirement.Id].GetBoundaryValidations()
                                                                      .Any(validation => validation.Boundary == boundary))
            ? boundary
            : null;
        var missingEvidence = failure.MissingCapabilityEvidence
            .Where(evidence => selection.Requirements.Any(requirement =>
                decisions[requirement.Id].GetCapabilityEvidence().Contains(evidence)))
            .ToImmutableArray();
        return new(
            failure.Status,
            failure.Reason,
            failure.AdapterDecisionCode,
            failure.Message,
            failure.Resolution,
            failure.Node,
            failure.Input,
            null,
            missingEvidence,
            failedBoundary,
            failure.FailedConfigurationSetting);
    }

    static RelationQueryOperatingBoundaryId? FailureBoundary(BranchCompilationException exception)
    {
        if (exception.Code == ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology)
            return ElasticRelationQueryTargetProfile.SingleIndexBoundary;
        if (exception.Code is ElasticRelationQueryCompilationDiagnosticCodes.LoweringUnavailable
            or ElasticRelationQueryCompilationDiagnosticCodes.LoweringConfigurationInvalid)
        {
            return ElasticRelationQueryTargetProfile.DeterministicProviderBoundary;
        }
        if (exception.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable)
            return ElasticRelationQueryTargetProfile.NonNullOperandsBoundary;
        if (exception.Code == ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable)
        {
            if (Contains(exception.Message, "page size") || Contains(exception.Message, "result window"))
                return ElasticRelationQueryTargetProfile.PageSizeBoundary;
            if (Contains(exception.Message, "stable unique")
                || Contains(exception.Message, "StableUniqueOrdering")
                || Contains(exception.Message, "ordering"))
                return ElasticRelationQueryTargetProfile.StableOrderingBoundary;
            return null;
        }
        if (exception.Code != ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable)
            return null;
        if (Contains(exception.Message, "stable unique")
            || Contains(exception.Message, "StableUniqueOrdering")
            || Contains(exception.Message, "ordering"))
            return ElasticRelationQueryTargetProfile.StableOrderingBoundary;
        if (Contains(exception.Message, "non-null")
            || Contains(exception.Message, "missing")
            || Contains(exception.Message, "null"))
        {
            return ElasticRelationQueryTargetProfile.NonNullOperandsBoundary;
        }
        return Contains(exception.Message, "scalar")
            ? ElasticRelationQueryTargetProfile.ScalarOperandsBoundary
            : null;
    }

    static string? FailureConfigurationSetting(BranchCompilationException exception)
    {
        if (exception.Code is ElasticRelationQueryCompilationDiagnosticCodes.LoweringUnavailable
            or ElasticRelationQueryCompilationDiagnosticCodes.LoweringConfigurationInvalid)
        {
            return LoweringPolicySetting;
        }
        if (exception.Code == ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable)
        {
            if (Contains(exception.Message, "result window"))
                return ElasticRelationQueryStorageBindingBuilder.MaximumResultWindowSetting;
            if (Contains(exception.Message, "page size"))
                return ElasticRelationQueryStorageBindingBuilder.MaximumPageSizeSetting;
            if (Contains(exception.Message, "search-visible view")
                || Contains(exception.Message, "pagination consistency"))
            {
                return ElasticRelationQueryStorageBindingBuilder.PaginationConsistencySetting;
            }
        }
        if (exception.Input is { } input
            && (exception.Code is ElasticRelationQueryCompilationDiagnosticCodes.FieldBindingMissing
                or ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
                or ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable))
        {
            var leaf = exception.Code == ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable
                ? "nestedScope"
                : FailureFieldSettingLeaf(exception.Message);
            return $"{ElasticRelationQueryStorageBindingBuilder.FieldSetting(input)}/{leaf}";
        }
        return CompilerProfileSetting;
    }

    static string FailureFieldSettingLeaf(string message)
    {
        if (Contains(message, "retrieval") || Contains(message, "encoding"))
            return "retrievalEncoding";
        if (Contains(message, "document scope") || Contains(message, "nested"))
            return "documentScope";
        if (Contains(message, "query field"))
            return "queryField";
        if (Contains(message, "source field"))
            return "sourceField";
        if (Contains(message, "mapping"))
            return "mappingKind";
        if (Contains(message, "missing"))
            return "missingValueBehavior";
        if (Contains(message, "null"))
            return "nullValueBehavior";
        return "semanticCapabilities";
    }

    static string? BindingFailureSetting(
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics)
    {
        var message = string.Join(' ', diagnostics.Select(static diagnostic => diagnostic.Message));
        if (Contains(message, "compiled-plan"))
            return "compiledPlanFingerprint";
        if (Contains(message, "source-placement"))
            return "placementFingerprint";
        if (Contains(message, "target profile") || Contains(message, "target snapshot"))
            return ElasticRelationQueryStorageBindingBuilder.TargetProfileSetting;
        if (Contains(message, "source instance"))
            return "source";
        if (Contains(message, "placement binding"))
            return "placementBinding";
        return CompilerProfileSetting;
    }

    static string? ResolveConfigurationSetting(
        RelationQueryAdapterBindingReference bindingReference,
        string? failedSetting)
    {
        if (failedSetting is null)
            return null;
        return bindingReference.ConfigurationDecisions.Any(decision =>
            string.Equals(decision.Setting, failedSetting, StringComparison.Ordinal))
            ? failedSetting
            : null;
    }

    static bool Contains(string value, string fragment) =>
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    RelationQueryAdapterBindingReference CreateBindingReference(
        ElasticRelationQueryStorageBinding storageBinding)
    {
        var configuration = ImmutableArray.CreateBuilder<EffectiveConfigurationDecision>(
            storageBinding.ConfigurationDecisions.Length + 3);
        configuration.AddRange(storageBinding.ConfigurationDecisions);
        configuration.Add(new(CompilerProfileSetting, optionsOrigin, options.CompilerProfile));
        configuration.Add(new(CompilerConventionSetting, optionsOrigin, options.ConventionSetVersion));
        configuration.Add(new(
            LoweringPolicySetting,
            loweringPolicyOrigin,
            string.Join(
                "|",
                loweringPolicy.Fingerprint.Algorithm,
                loweringPolicy.Fingerprint.Canonicalization,
                loweringPolicy.Fingerprint.Value)));
        return new(
            storageBinding.SchemaVersion,
            storageBinding.Id.Value,
            storageBinding.Target,
            storageBinding.TargetProfile,
            new(
                storageBinding.Fingerprint.Algorithm,
                storageBinding.Fingerprint.Canonicalization,
                storageBinding.Fingerprint.Value),
            storageBinding.CompiledPlanFingerprint,
            storageBinding.PlacementFingerprint,
            [storageBinding.Source],
            [storageBinding.PlacementBinding],
            configuration.MoveToImmutable());
    }

    RelationQueryContextualAssessmentAttribution ResolveAssessmentAttribution(
        RelationQueryBoundRealizationRequest request,
        ElasticRelationQueryStorageBinding storageBinding,
        RelationQueryAdapterBindingReference bindingReference,
        RelationQueryNativeResultBranch branch,
        RelationQueryRealizationRequirement requirement,
        RelationQueryContextualBranchFailure? failure)
    {
        var selection = request.Selection.GetBranch(branch.Id);
        var input = failure?.Input is { } failedInput
                    && selection.IsInputRelevant(failedInput, failure.Node, requirement)
            ? failedInput
            : requirement.Origin?.Input;
        var field = input is { } inputId
            ? request.Plan.InputContract.Requirements.Inputs
                .OfType<RelationQueryFieldInput>()
                .SingleOrDefault(candidate => candidate.Id == inputId)
                ?.Field.Path
            : null;
        var placement = input is { } placedInput
            ? request.Placement.Bindings.SingleOrDefault(candidate =>
                candidate.Id == storageBinding.PlacementBinding
                && (candidate.Input == placedInput
                    || candidate.Fields.Any(fieldBinding => fieldBinding.Input == placedInput)))?.Id
            : null;
        var configurationSetting = failure is null
            ? null
            : ResolveConfigurationSetting(bindingReference, failure.FailedConfigurationSetting);
        if (configurationSetting is not null)
        {
            var decision = bindingReference.ConfigurationDecisions.Single(candidate =>
                string.Equals(candidate.Setting, configurationSetting, StringComparison.Ordinal));
            return new(
                decision.Origin,
                decision.Authority,
                failure?.Node ?? requirement.Origin?.Node,
                input,
                field,
                placement,
                configurationSetting);
        }

        return new(
            storageBinding.Origin == ElasticRelationQueryBindingOrigin.Convention
                ? EffectiveConfigurationOrigin.AdapterConvention
                : EffectiveConfigurationOrigin.Explicit,
            ContextAuthority(storageBinding),
            failure?.Node ?? requirement.Origin?.Node,
            input,
            field,
            placement);
    }

    ImmutableArray<RelationQueryNativeCompilationDiagnostic> ValidateExactBinding(
        RelationQueryNativeCompilationRequest request,
        ElasticRelationQueryStorageBinding storageBinding)
    {
        var expected = request.BoundRealization.Evidence.Binding;
        var actual = CreateBindingReference(storageBinding);
        var expectedAuthority = ContextAuthority(storageBinding);
        var expectedOrigin = storageBinding.Origin == ElasticRelationQueryBindingOrigin.Convention
            ? EffectiveConfigurationOrigin.AdapterConvention
            : EffectiveConfigurationOrigin.Explicit;
        var configuration = actual.ConfigurationDecisions.ToDictionary(
            static decision => decision.Setting,
            StringComparer.Ordinal);
        var attributionMatches = request.BoundRealization.Evidence.Assessments.All(assessment =>
            assessment.ConfigurationSetting is { } setting
                ? configuration.TryGetValue(setting, out var decision)
                  && decision.Origin == assessment.Origin
                  && string.Equals(decision.Authority, assessment.Authority, StringComparison.Ordinal)
                : assessment.Origin == expectedOrigin
                  && string.Equals(assessment.Authority, expectedAuthority, StringComparison.Ordinal));
        if (expected.HasSameSemantics(actual) && attributionMatches)
            return [];

        return
        [
            new(
                ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch,
                DiagnosticSeverity.Error,
                "The Elasticsearch storage-binding fingerprint or compiler-policy evidence does not match the exact context qualified by the bound-realization report.",
                adapterDecisionCode: new(ElasticRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch))
        ];
    }

    string ContextAuthority(ElasticRelationQueryStorageBinding storageBinding)
    {
        var bindingAuthority = storageBinding.Origin == ElasticRelationQueryBindingOrigin.Convention
            ? storageBinding.ConventionSetVersion!
            : storageBinding.Id.Value;
        return bindingAuthority;
    }

    readonly record struct ContextualProjectionResult(
        RelationQueryContextualEvidenceProjection Evidence,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics);

    sealed class BranchCompiler
    {
        readonly CompiledRelationQueryPlan plan;
        readonly RelationQueryRealizationReport realization;
        readonly RelationQueryNativeCompilationRequest? nativeRequest;
        readonly ElasticRelationQueryStorageBinding storageBinding;
        readonly ElasticRelationQueryCompilerOptions options;
        readonly ElasticQueryLoweringPolicy loweringPolicy;
        readonly RelationQueryNativeResultBranch branch;
        readonly IReadOnlyDictionary<QueryNodeId, RelationQueryExecutionNode> nodes;
        readonly IReadOnlyDictionary<(ValueBindingId Binding, FieldPath Path), RelationQueryFieldInputContract> sourceFields;
        readonly IReadOnlyDictionary<QueryParameterId, RelationQueryParameterInputContract> parameters;
        readonly Dictionary<(ValueBindingId Binding, FieldPath Path), RelationQueryProjectionExecutionAssignment> projections = [];
        readonly Dictionary<(ValueBindingId Binding, FieldPath Path), RelationQueryAggregateGroupingExecution> groupings = [];
        readonly Dictionary<(ValueBindingId Binding, FieldPath Path), RelationQueryAggregateAssignmentExecution> aggregates = [];
        readonly HashSet<QueryParameterId> usedParameters = [];
        readonly HashSet<RelationQueryInputId> sourceRetrievedInputs = [];
        readonly Dictionary<RelationQueryInputId, HashSet<FieldPath>> queriedFields = [];
        readonly List<ElasticRelationQueryLoweringDecision> loweringDecisions = [];
        readonly Dictionary<ExprSiteId, int> loweringOrdinals = [];
        readonly ImmutableArray<RelationQueryExecutionNode> pipeline;
        RelationQueryExecutionNode? aggregateExecution;

        public BranchCompiler(
            RelationQueryNativeCompilationRequest request,
            ElasticRelationQueryStorageBinding storageBinding,
            ElasticRelationQueryCompilerOptions options,
            ElasticQueryLoweringPolicy loweringPolicy,
            RelationQueryNativeResultBranch branch
            )
            : this(
                request.Plan,
                request.ProfileFeasibility,
                request,
                storageBinding,
                options,
                loweringPolicy,
                branch)
        {
        }

        public BranchCompiler(
            RelationQueryBoundRealizationRequest request,
            ElasticRelationQueryStorageBinding storageBinding,
            ElasticRelationQueryCompilerOptions options,
            ElasticQueryLoweringPolicy loweringPolicy,
            RelationQueryNativeResultBranch branch)
            : this(
                request.Plan,
                request.ProfileFeasibility,
                nativeRequest: null,
                storageBinding,
                options,
                loweringPolicy,
                branch)
        {
        }

        BranchCompiler(
            CompiledRelationQueryPlan plan,
            RelationQueryRealizationReport realization,
            RelationQueryNativeCompilationRequest? nativeRequest,
            ElasticRelationQueryStorageBinding storageBinding,
            ElasticRelationQueryCompilerOptions options,
            ElasticQueryLoweringPolicy loweringPolicy,
            RelationQueryNativeResultBranch branch)
        {
            this.plan = plan;
            this.realization = realization;
            this.nativeRequest = nativeRequest;
            this.storageBinding = storageBinding;
            this.options = options;
            this.loweringPolicy = loweringPolicy;
            this.branch = branch;
            nodes = plan.ExecutionSlice.Nodes.ToDictionary(static node => node.Id);
            sourceFields = plan.InputContract.Sources
                .SelectMany(static source => source.Fields)
                .ToDictionary(static field => (field.Input.Binding, field.Input.Field.Path));
            parameters = plan.InputContract.Parameters.ToDictionary(static parameter => parameter.Definition.Id);
            pipeline = CreatePipeline();
        }

        public void Validate() => _ = Analyze();

        public ElasticRelationQueryCompiledArtifact Compile()
        {
            var analysis = Analyze();
            var provenance = CreateProvenance(analysis.SelectedFields);
            var fingerprint = ElasticRelationQueryArtifactFingerprinter.Compute(
                branch,
                analysis.Body.Request,
                storageBinding,
                analysis.SelectedFields,
                analysis.Body.ResultFields,
                analysis.ParameterBindings,
                analysis.Body.Paging,
                [.. loweringDecisions],
                loweringPolicy.Fingerprint,
                provenance);
            return new(
                branch,
                analysis.Body.Request,
                storageBinding,
                analysis.SelectedFields,
                analysis.Body.ResultFields,
                analysis.ParameterBindings,
                analysis.Body.Paging,
                loweringPolicy.Fingerprint,
                [.. loweringDecisions],
                provenance,
                fingerprint);
        }

        AnalyzedBranch Analyze()
        {
            if (branch.Kind == RelationQueryNativeResultKind.RelationRows)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.RelationTerminalUnsupported,
                    "Elasticsearch v2 does not lower relation terminals until root correlation, cardinality, key, and invariant evidence are represented by the artifact contract.",
                    branch.Node);
            }
            if (realization.Observability.OccurrenceProvenance
                != RelationQueryOccurrenceProvenanceMode.NotRequested)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.ResultObservabilityUnsupported,
                    "Elasticsearch v2 compiles value results only and cannot provide exact contributor-occurrence lineage.",
                    branch.Node);
            }

            ValidatePipeline();
            IndexAssignments();
            var query = CompileFilters();
            CompiledBranchBody body = branch.Kind switch
            {
                RelationQueryNativeResultKind.QueryRows => CompileRows(query),
                RelationQueryNativeResultKind.QueryAggregation => CompileAggregation(query),
                _ => throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    $"Result kind '{branch.Kind}' is unsupported by canonical Elasticsearch v2.",
                    branch.Node)
            };
            var selectedFields = CreateSelectedFields();
            var parameterBindings = CreateParameterBindings();
            return new(body, selectedFields, parameterBindings);
        }

        ImmutableArray<RelationQueryExecutionNode> CreatePipeline()
        {
            List<RelationQueryExecutionNode> reverse = [];
            HashSet<QueryNodeId> visited = [];
            var current = branch.Node;
            while (true)
            {
                if (!visited.Add(current))
                {
                    throw Fail(ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                        "The selected native branch contains a cycle.", current);
                }

                if (!nodes.TryGetValue(current, out var execution))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                        $"Branch node '{current.Value}' is absent from the demand-scoped execution slice.",
                        current);
                }
                reverse.Add(execution);
                if (execution.CanonicalNode is SourceQueryNode)
                {
                    break;
                }

                if (execution.LogicalPlan.EffectiveInputs.Length != 1
                    || execution.LogicalPlan.Inputs.Any(static input => !input.Bypasses.IsDefaultOrEmpty))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                        "Canonical Elasticsearch v2 supports only a linear single-source branch without traversal bypasses.",
                        execution.Id);
                }
                current = execution.LogicalPlan.EffectiveInputs[0];
            }
            reverse.Reverse();
            return [.. reverse];
        }

        void ValidatePipeline()
        {
            if (pipeline[0].CanonicalNode is not SourceQueryNode source)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "A native Elasticsearch branch must begin at one placed source binding.",
                    pipeline[0].Id);
            }
            var sourceContracts = plan.InputContract.Sources
                .Where(contract => contract.Node == source.Id)
                .ToArray();
            if (sourceContracts.Length != 1 || source.Binding != sourceContracts[0].Binding)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "A native Elasticsearch branch must begin at its exact demand-scoped source binding.",
                    pipeline[0].Id);
            }

            var stage = PipelineStage.Source;
            var sawProjection = false;
            var sawAggregate = false;
            var sawOrder = false;
            var sawPage = false;
            foreach (var execution in pipeline.Skip(1))
            {
                switch (execution.CanonicalNode)
                {
                    case FilterQueryNode when stage <= PipelineStage.Row:
                        stage = PipelineStage.Row;
                        break;
                    case ProjectQueryNode when !sawProjection && !sawAggregate && stage <= PipelineStage.Row:
                        sawProjection = true;
                        stage = PipelineStage.Shape;
                        break;
                    case AggregateQueryNode when !sawProjection && !sawAggregate && stage <= PipelineStage.Row:
                        sawAggregate = true;
                        aggregateExecution = execution;
                        stage = PipelineStage.Shape;
                        break;
                    case OrderQueryNode when !sawOrder && !sawPage && stage <= PipelineStage.Order:
                        sawOrder = true;
                        stage = PipelineStage.Order;
                        break;
                    case PageQueryNode when sawOrder && !sawPage && stage <= PipelineStage.Page:
                        sawPage = true;
                        stage = PipelineStage.Page;
                        break;
                    default:
                        throw Fail(
                            ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                            $"Logical node '{execution.CanonicalNode.GetType().Name}' is unsupported or appears in an inexact Elasticsearch pipeline position.",
                            execution.Id);
                }
            }

            if (branch.Kind == RelationQueryNativeResultKind.QueryAggregation != sawAggregate)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "The named result kind does not match the branch's aggregate topology.",
                    branch.Node);
            }
            if (branch.Kind == RelationQueryNativeResultKind.QueryRows && (!sawOrder || !sawPage))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Canonical Elasticsearch row results require an explicit deterministic order and bounded page.",
                    branch.Node);
            }
        }

        void IndexAssignments()
        {
            foreach (var execution in pipeline)
            {
                switch (execution.CanonicalNode)
                {
                    case ProjectQueryNode projection:
                        foreach (var assignment in execution.ProjectionAssignments)
                        {
                            projections.Add((projection.ResultBinding, assignment.Definition.Target), assignment);
                        }

                        break;
                    case AggregateQueryNode aggregate:
                        foreach (var grouping in execution.AggregateGroupings)
                        {
                            groupings.Add((aggregate.ResultBinding, grouping.Definition.Target), grouping);
                        }

                        foreach (var assignment in execution.AggregateAssignments)
                        {
                            aggregates.Add((aggregate.ResultBinding, assignment.Definition.Target), assignment);
                        }

                        break;
                }
            }
        }

        ElasticQueryTemplate CompileFilters()
        {
            ImmutableArray<ElasticQueryTemplate>.Builder filters =
                ImmutableArray.CreateBuilder<ElasticQueryTemplate>();
            foreach (var execution in pipeline)
            {
                if (execution.CanonicalNode is not FilterQueryNode filter)
                {
                    continue;
                }

                filters.Add(CompilePredicate(
                    filter.Predicate,
                    RequiredSite(execution, RelationQueryExpressionSiteKind.FilterPredicate)));
            }
            return filters.Count switch
            {
                0 => ElasticQueryTemplate.MatchAll(),
                _ => ElasticQueryTemplate.Boolean(filter: filters.ToImmutable())
            };
        }

        ElasticQueryTemplate CompilePredicate(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site)
        {
            if (!IsBooleanScalar(site.Analysis.KnownResult?.GetEffectiveType())
                || !IsRequiredNonNull(site.Analysis.KnownResult))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "An Elasticsearch filter predicate requires one known, required, non-null Boolean result.",
                    site.Node ?? branch.Node);
            }

            return expression switch
            {
                BinaryExpr { Operator: BinaryOperator.And } conjunction => ElasticQueryTemplate.Boolean(
                    filter:
                    [
                        CompilePredicate(conjunction.Left, site),
                        CompilePredicate(conjunction.Right, site)
                    ]),
                BinaryExpr { Operator: BinaryOperator.Or } disjunction => ElasticQueryTemplate.Boolean(
                    should:
                    [
                        CompilePredicate(disjunction.Left, site),
                        CompilePredicate(disjunction.Right, site)
                    ]),
                UnaryExpr { Operator: UnaryOperator.Not } negation => ElasticQueryTemplate.Boolean(
                    mustNot: [CompilePredicate(negation.Operand, site)]),
                BinaryExpr binary when binary.Operator is BinaryOperator.Eq
                    or BinaryOperator.Ne
                    or BinaryOperator.Gt
                    or BinaryOperator.Ge
                    or BinaryOperator.Lt
                    or BinaryOperator.Le => CompileComparison(binary, site),
                CallExpr call when string.Equals(call.Function, ExprFunctionNames.Any, StringComparison.Ordinal)
                                        && call.Arguments.Length == 2 => CompileCollectionAny(call, site),
                CallExpr call when string.Equals(call.Function, ExprFunctionNames.Contains, StringComparison.Ordinal)
                                        && call.Arguments.Length == 2 => CompileCollectionMembership(call, site),
                CallExpr call when string.Equals(call.Function, ExprFunctionNames.EndsWith, StringComparison.Ordinal)
                                        && call.Arguments.Length == 2 => CompileSuffix(call, site),
                _ => throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Expression node '{expression.GetType().Name}' is not in the exact Elasticsearch v2 predicate closure.",
                    site.Node ?? branch.Node)
            };
        }

        ElasticQueryTemplate CompileComparison(
            BinaryExpr binary,
            RelationQueryExpressionSiteAnalysis site)
        {
            var left = AnalyzeSubexpression(binary.Left, site, "comparison-left");
            var right = AnalyzeSubexpression(binary.Right, site, "comparison-right");
            if (!IsRequiredNonNull(left) || !IsRequiredNonNull(right))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Binary operator '{binary.Operator}' has a missing or null operand whose Elasticsearch semantics are not proven exact.",
                    site.Node ?? branch.Node);
            }
            if (left.GetEffectiveType() != right.GetEffectiveType()
                || !IsSupportedScalar(left.GetEffectiveType()))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Binary operator '{binary.Operator}' requires operands in one supported canonical scalar domain.",
                    site.Node ?? branch.Node);
            }

            ResolvedSourceField field;
            Expr valueExpression;
            var physicalOperator = binary.Operator;
            if (TryResolveSourceField(binary.Left, site, out var leftField))
            {
                field = leftField;
                valueExpression = binary.Right;
            }
            else if (TryResolveSourceField(binary.Right, site, out var rightField))
            {
                field = rightField;
                valueExpression = binary.Left;
                physicalOperator = Reverse(binary.Operator);
            }
            else
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Elasticsearch v2 comparisons require one direct physical field and one constant or parameter.",
                    site.Node ?? branch.Node);
            }

            RequireNonNullField(field, site.Node ?? branch.Node, "comparison");
            RequireMappingCompatibility(field, left.GetEffectiveType(), site.Node ?? branch.Node);
            var value = CreateValueTemplate(valueExpression, site, requireNonNull: true);
            var queryField = RequireQueryField(field, site.Node ?? branch.Node);
            return physicalOperator switch
            {
                BinaryOperator.Eq => WithCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                    ElasticQueryTemplate.Term(queryField, value),
                    site.Node ?? branch.Node),
                BinaryOperator.Ne => WithCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactTerm,
                    ElasticQueryTemplate.Boolean(mustNot: [ElasticQueryTemplate.Term(queryField, value)]),
                    site.Node ?? branch.Node),
                BinaryOperator.Gt => WithCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactRange,
                    ElasticQueryTemplate.Range(
                        queryField,
                        lower: new(value, ElasticRangeBoundKind.Exclusive)),
                    site.Node ?? branch.Node),
                BinaryOperator.Ge => WithCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactRange,
                    ElasticQueryTemplate.Range(
                        queryField,
                        lower: new(value, ElasticRangeBoundKind.Inclusive)),
                    site.Node ?? branch.Node),
                BinaryOperator.Lt => WithCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactRange,
                    ElasticQueryTemplate.Range(
                        queryField,
                        upper: new(value, ElasticRangeBoundKind.Exclusive)),
                    site.Node ?? branch.Node),
                BinaryOperator.Le => WithCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactRange,
                    ElasticQueryTemplate.Range(
                        queryField,
                        upper: new(value, ElasticRangeBoundKind.Inclusive)),
                    site.Node ?? branch.Node),
                _ => throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Binary operator '{physicalOperator}' is not a physical Elasticsearch comparison.",
                    site.Node ?? branch.Node)
            };
        }

        ElasticQueryTemplate CompileCollectionMembership(
            CallExpr call,
            RelationQueryExpressionSiteAnalysis site)
        {
            var collectionContract = AnalyzeSubexpression(
                call.Arguments[0],
                site,
                "collection-membership-collection");
            var valueContract = AnalyzeSubexpression(
                call.Arguments[1],
                site,
                "collection-membership-value");
            if (!IsRequiredNonNull(collectionContract) || !IsRequiredNonNull(valueContract))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Canonical contains requires a required, non-null collection and membership value for exact Elasticsearch lowering.",
                    site.Node ?? branch.Node);
            }
            if (collectionContract.GetEffectiveType() is not ArrayTypeRef { ElementType: var elementType }
                || !IsSupportedScalar(elementType)
                || valueContract.GetEffectiveType() != elementType)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Canonical contains requires one collection of a supported scalar domain and one value in that same domain.",
                    site.Node ?? branch.Node);
            }
            if (!TryResolveSourceField(call.Arguments[0], site, out var field))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Canonical contains requires a direct physical collection field in Elasticsearch v2.",
                    site.Node ?? branch.Node);
            }

            RequireNonNullField(field, site.Node ?? branch.Node, "collection membership");
            RequireMappingCompatibility(field, elementType, site.Node ?? branch.Node);
            var value = CreateValueTemplate(call.Arguments[1], site, requireNonNull: true);
            var queryField = RequireQueryField(field, site.Node ?? branch.Node);
            return WithCapability(
                field,
                ElasticRelationQueryFieldSemanticCapabilities.ExactCollectionMembership,
                ElasticQueryTemplate.Term(queryField, value),
                site.Node ?? branch.Node);
        }

        ElasticQueryTemplate CompileCollectionAny(
            CallExpr call,
            RelationQueryExpressionSiteAnalysis site)
        {
            var node = site.Node ?? branch.Node;
            var collectionContract = AnalyzeSubexpression(
                call.Arguments[0],
                site,
                "collection-any-collection");
            if (!IsRequiredNonNull(collectionContract))
            {
                throw NestedFailure(
                    "Canonical any requires a required, non-null collection; Elasticsearch cannot silently treat a missing or null collection as empty.",
                    node);
            }
            var elementContract = GetCollectionElementContract(collectionContract);
            if (elementContract is null
                || elementContract.GetEffectiveType() is not ObjectTypeRef)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Canonical any requires a structured object collection in the Elasticsearch v2 closure.",
                    node);
            }
            if (!TryResolveSourceField(call.Arguments[0], site, out var collectionField))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Canonical any requires one direct physical structured collection field in Elasticsearch v2.",
                    node);
            }
            RequireNonNullField(collectionField, node, "structured collection existential");

            var physical = collectionField.Physical;
            if (physical.MappingKind == ElasticRelationQueryFieldMappingKind.Object
                || physical.DocumentScope == ElasticRelationQueryFieldDocumentScope.RootDocument)
            {
                throw NestedFailure(
                    "The structured collection is mapped as a flattened/root object array, which cannot preserve same-element correlation. Configure an Elasticsearch nested mapping, or denormalize an independently queried scalar to a root multivalued field and use canonical Contains.",
                    node,
                    collectionField.Contract.Input.Id);
            }
            if (physical.MappingKind != ElasticRelationQueryFieldMappingKind.Nested
                || physical.DocumentScope != ElasticRelationQueryFieldDocumentScope.NestedDocument
                || physical.NestedScope is not { } nested)
            {
                throw NestedFailure(
                    "The structured collection binding does not provide an exact nested path, element scope, child mappings, and correlation evidence. Configure those facts, or use a denormalized root scalar collection with canonical Contains when correlation is unnecessary.",
                    node,
                    collectionField.Contract.Input.Id);
            }
            RequireExactNestedScope(nested, collectionField, node);

            var predicateScope = site.Analysis.Site.Scope.WithCurrentItem(elementContract);
            var predicateAnalysis = ExprAnalyzer.Analyze(
                new ExprSite(
                    new($"{site.Analysis.Site.Id.Value}/elastic/collection-any-predicate"),
                    call.Arguments[1],
                    predicateScope,
                    ExprExpectation.Boolean,
                    site.Analysis.Site.CapabilityProfile,
                    site.Analysis.Site.DiagnosticLocation),
                site.Analysis.Semantics);
            if (!predicateAnalysis.IsValid
                || !IsBooleanScalar(predicateAnalysis.KnownResult?.GetEffectiveType())
                || !IsRequiredNonNull(predicateAnalysis.KnownResult))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "The canonical any predicate does not have one valid, required, non-null Boolean contract in its collection-element scope.",
                    node,
                    collectionField.Contract.Input.Id);
            }

            var inner = CompileNestedPredicate(
                call.Arguments[1],
                site,
                predicateScope,
                collectionField,
                nested);
            TrackQueryField(collectionField.Contract.Input.Id, nested.NestedPath);
            return ElasticQueryTemplate.Nested(
                ElasticRelationQuerySelectedField.PhysicalName(nested.NestedPath),
                inner);
        }

        ElasticQueryTemplate CompileNestedPredicate(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            ExprScope predicateScope,
            ResolvedSourceField collectionField,
            ElasticRelationQueryNestedScopeEvidence nested)
        {
            return expression switch
            {
                BinaryExpr { Operator: BinaryOperator.And } conjunction => ElasticQueryTemplate.Boolean(filter:
                [
                    CompileNestedPredicate(conjunction.Left, site, predicateScope, collectionField, nested),
                    CompileNestedPredicate(conjunction.Right, site, predicateScope, collectionField, nested)
                ]),
                BinaryExpr { Operator: BinaryOperator.Or } disjunction => ElasticQueryTemplate.Boolean(should:
                [
                    CompileNestedPredicate(disjunction.Left, site, predicateScope, collectionField, nested),
                    CompileNestedPredicate(disjunction.Right, site, predicateScope, collectionField, nested)
                ]),
                UnaryExpr { Operator: UnaryOperator.Not } negation => ElasticQueryTemplate.Boolean(mustNot:
                [
                    CompileNestedPredicate(negation.Operand, site, predicateScope, collectionField, nested)
                ]),
                BinaryExpr binary when binary.Operator is BinaryOperator.Eq or BinaryOperator.Ne =>
                    CompileNestedComparison(binary, site, predicateScope, collectionField, nested),
                _ => throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Expression node '{expression.GetType().Name}' is outside the direct-child Elasticsearch nested predicate closure.",
                    site.Node ?? branch.Node,
                    collectionField.Contract.Input.Id)
            };
        }

        ElasticQueryTemplate CompileNestedComparison(
            BinaryExpr binary,
            RelationQueryExpressionSiteAnalysis site,
            ExprScope predicateScope,
            ResolvedSourceField collectionField,
            ElasticRelationQueryNestedScopeEvidence nested)
        {
            var node = site.Node ?? branch.Node;
            FieldPath elementPath;
            Expr valueExpression;
            if (TryResolveCurrentItemChildPath(binary.Left, out var leftPath))
            {
                elementPath = leftPath;
                valueExpression = binary.Right;
            }
            else if (TryResolveCurrentItemChildPath(binary.Right, out var rightPath))
            {
                elementPath = rightPath;
                valueExpression = binary.Left;
            }
            else
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "An Elasticsearch nested comparison requires one direct current-element child field and one constant or parameter.",
                    node,
                    collectionField.Contract.Input.Id);
            }

            var fieldExpression = TryResolveCurrentItemChildPath(binary.Left, out _)
                ? binary.Left
                : binary.Right;
            var childContract = AnalyzeSubexpression(
                fieldExpression,
                site,
                "collection-any-child",
                predicateScope);
            var valueContract = AnalyzeSubexpression(
                valueExpression,
                site,
                "collection-any-value",
                predicateScope);
            if (!IsRequiredNonNull(childContract) || !IsRequiredNonNull(valueContract))
            {
                throw NestedFailure(
                    "A nested child comparison requires canonical child and value operands to be required and non-null.",
                    node,
                    collectionField.Contract.Input.Id);
            }
            var valueType = childContract.GetEffectiveType();
            if (valueType != valueContract.GetEffectiveType() || !IsSupportedScalar(valueType))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "A nested child comparison requires operands in one supported canonical scalar domain.",
                    node,
                    collectionField.Contract.Input.Id);
            }

            ElasticRelationQueryNestedChildFieldBinding child;
            try
            {
                child = nested.ResolveChild(elementPath);
            }
            catch (KeyNotFoundException)
            {
                throw NestedFailure(
                    $"The nested binding has no terminal child mapping for current-element path '{elementPath}'.",
                    node,
                    collectionField.Contract.Input.Id);
            }
            if (child.MissingValueBehavior != ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion
                || child.NullValueBehavior != ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion)
            {
                throw NestedFailure(
                    $"Nested child path '{elementPath}' does not prove that ingestion prohibits missing and null values; Elasticsearch would otherwise turn an invalid canonical operand into a non-match.",
                    node,
                    collectionField.Contract.Input.Id);
            }
            if (!child.SemanticCapabilities.HasFlag(ElasticRelationQueryFieldSemanticCapabilities.ExactTerm))
            {
                throw NestedFailure(
                    $"Nested child path '{elementPath}' does not attest '{ElasticRelationQueryFieldSemanticCapabilities.ExactTerm}'.",
                    node,
                    collectionField.Contract.Input.Id);
            }
            RequireNestedMappingCompatibility(child, valueType, node, collectionField.Contract.Input.Id);

            var value = CreateValueTemplate(valueExpression, site, requireNonNull: true);
            TrackQueryField(collectionField.Contract.Input.Id, child.QueryField);
            var term = ElasticQueryTemplate.Term(
                ElasticRelationQuerySelectedField.PhysicalName(child.QueryField),
                value);
            return binary.Operator == BinaryOperator.Eq
                ? term
                : ElasticQueryTemplate.Boolean(mustNot: [term]);
        }

        static void RequireExactNestedScope(
            ElasticRelationQueryNestedScopeEvidence nested,
            ResolvedSourceField field,
            QueryNodeId node)
        {
            if (nested.CorrelationGuarantee != ElasticRelationQueryNestedCorrelationGuarantee.SameNestedDocument)
            {
                throw NestedFailure(
                    "The Elasticsearch nested binding does not attest same-nested-document correlation.",
                    node,
                    field.Contract.Input.Id);
            }
            if (field.Physical.MissingValueBehavior != ElasticRelationQueryMissingValueBehavior.ProhibitedByIngestion
                || field.Physical.NullValueBehavior != ElasticRelationQueryNullValueBehavior.ProhibitedByIngestion)
            {
                throw NestedFailure(
                    "The Elasticsearch nested binding must attest that ingestion prohibits missing and null collections; treating them as empty would weaken canonical any semantics.",
                    node,
                    field.Contract.Input.Id);
            }
            if (nested.NullElementBehavior != ElasticRelationQueryNestedAbsenceBehavior.ProhibitedByIngestion)
            {
                throw NestedFailure(
                    "The Elasticsearch nested binding must attest that ingestion prohibits explicit-null collection elements; Elasticsearch would otherwise omit an element and change negative-predicate semantics.",
                    node,
                    field.Contract.Input.Id);
            }
            if (nested.EmptyCollectionBehavior != ElasticRelationQueryEmptyCollectionBehavior.NoNestedDocuments)
            {
                throw NestedFailure(
                    "The Elasticsearch nested binding does not prove that an empty collection contributes no matching nested documents.",
                    node,
                    field.Contract.Input.Id);
            }
        }

        static bool TryResolveCurrentItemChildPath(Expr expression, out FieldPath path)
        {
            if (expression is FieldExpr
                {
                    Binding: null,
                    Path.Segments: [{ Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem }, .. var remainder]
                }
                && remainder.Length == 1
                && remainder[0] is { Kind: SegmentKind.Field, Segment: not null })
            {
                path = new([remainder[0]]);
                return true;
            }

            path = default;
            return false;
        }

        static ValueContract? GetCollectionElementContract(ValueContract collection)
        {
            if (collection.Cardinality == FieldCardinality.Many)
            {
                return new(
                    collection.Type,
                    collection.Shape);
            }
            return collection.GetEffectiveType() is ArrayTypeRef array
                ? new(
                    array.ElementType,
                    collection.Shape)
                : null;
        }

        ElasticQueryTemplate CompileSuffix(
            CallExpr call,
            RelationQueryExpressionSiteAnalysis site)
        {
            var valueContract = AnalyzeSubexpression(call.Arguments[0], site, "suffix-value");
            var suffixContract = AnalyzeSubexpression(call.Arguments[1], site, "suffix-pattern");
            if (!IsRequiredNonNull(valueContract)
                || !IsRequiredNonNull(suffixContract)
                || valueContract.GetEffectiveType() is not ScalarTypeRef { Kind: ScalarTypeKind.String }
                || suffixContract.GetEffectiveType() is not ScalarTypeRef { Kind: ScalarTypeKind.String })
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Canonical endsWith requires required, non-null text operands for exact Elasticsearch lowering.",
                    site.Node ?? branch.Node);
            }
            if (!TryResolveSourceField(call.Arguments[0], site, out var field))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Canonical endsWith requires a direct physical field as its value operand in Elasticsearch v2.",
                    site.Node ?? branch.Node);
            }
            RequireNonNullField(field, site.Node ?? branch.Node, "endsWith");
            RequireMappingCompatibility(field, valueContract.GetEffectiveType(), site.Node ?? branch.Node);
            var value = CreateValueTemplate(call.Arguments[1], site, requireNonNull: true);
            var queryFieldPath = RequireRootQueryFieldPath(field, site.Node ?? branch.Node);
            var queryField = ElasticRelationQuerySelectedField.PhysicalName(queryFieldPath);
            ElasticQueryLoweringResolution resolution;
            try
            {
                resolution = loweringPolicy.Resolve(new(
                    ElasticQueryLoweringOperation.Suffix,
                    field.Physical,
                    value));
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException
                or StackOverflowException
                or AccessViolationException))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.LoweringConfigurationInvalid,
                    $"The configured Elasticsearch suffix-lowering policy failed: {exception.GetType().Name}: {exception.Message}",
                    site.Node ?? branch.Node,
                    field.Contract.Input.Id);
            }
            var parentSite = site.Analysis.Site.Id;
            loweringOrdinals.TryGetValue(parentSite, out var loweringOrdinal);
            loweringOrdinals[parentSite] = loweringOrdinal + 1;
            loweringDecisions.Add(new(
                $"{parentSite.Value}/lowering/{loweringOrdinal.ToString(CultureInfo.InvariantCulture)}",
                resolution.Decision));
            if (!resolution.IsSuccessful)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.LoweringUnavailable,
                    "No configured exact Elasticsearch suffix strategy was selected: "
                    + string.Join(
                        "; ",
                        resolution.Decision.Attempts.Select(static attempt =>
                            $"{attempt.Strategy.Value}: {attempt.Explanation}")),
                    site.Node ?? branch.Node,
                    field.Contract.Input.Id);
            }
            var resolvedQuery = resolution.Query!;
            var expectedParameters = value.Parameter is { } parameter
                ? ImmutableHashSet.Create(parameter)
                : ImmutableHashSet<QueryParameterId>.Empty;
            if (!resolvedQuery.ReferencedParameters().SetEquals(expectedParameters))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.LoweringConfigurationInvalid,
                    "The selected Elasticsearch suffix strategy referenced parameters outside its canonical suffix operand.",
                    site.Node ?? branch.Node,
                    field.Contract.Input.Id);
            }
            var allowedFields = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            allowedFields.Add(queryField);
            if (field.Physical.ReversedSuffixField is { } reversedSuffixField)
            {
                allowedFields.Add(ElasticRelationQuerySelectedField.PhysicalName(reversedSuffixField));
            }

            var referencedFields = resolvedQuery.ReferencedFields();
            if (referencedFields.IsEmpty || !referencedFields.IsSubsetOf(allowedFields))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.LoweringConfigurationInvalid,
                    "The selected Elasticsearch suffix strategy referenced a physical field outside its bound field evidence.",
                    site.Node ?? branch.Node,
                    field.Contract.Input.Id);
            }
            foreach (var referencedField in referencedFields)
            {
                if (string.Equals(referencedField, queryField, StringComparison.Ordinal))
                {
                    TrackQueryField(field.Contract.Input.Id, queryFieldPath);
                    continue;
                }
                if (field.Physical.ReversedSuffixField is { } reversed
                    && string.Equals(
                        referencedField,
                        ElasticRelationQuerySelectedField.PhysicalName(reversed),
                        StringComparison.Ordinal))
                {
                    TrackQueryField(field.Contract.Input.Id, reversed);
                }
            }
            return resolvedQuery;
        }

        static ElasticQueryTemplate WithCapability(
            ResolvedSourceField field,
            ElasticRelationQueryFieldSemanticCapabilities capability,
            ElasticQueryTemplate query,
            QueryNodeId node)
        {
            if (!field.Physical.SemanticCapabilities.HasFlag(capability))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Elasticsearch field input '{field.Contract.Input.Id.Value}' does not attest '{capability}'.",
                    node,
                    field.Contract.Input.Id);
            }
            return query;
        }

        ElasticQueryValueTemplate CreateValueTemplate(
            Expr expression,
            RelationQueryExpressionSiteAnalysis? site,
            bool requireNonNull)
        {
            ObservationValue? constant = expression switch
            {
                ConstantExpr value => value.Value,
                LiteralExpr value => value.Value,
                _ => null
            };
            if (constant is { } constantValue)
            {
                if (requireNonNull && constantValue.Kind == ObservationValueKind.Null)
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        "A null constant is not valid where exact Elasticsearch scalar semantics require a value.",
                        site?.Node ?? branch.Node);
                }
                if (!ElasticQueryValueTemplate.IsSupportedScalar(constantValue))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                        $"Constant value kind '{constantValue.Kind}' has no exact Elasticsearch request encoding.",
                        site?.Node ?? branch.Node);
                }
                return ElasticQueryValueTemplate.FromConstant(constantValue);
            }
            if (expression is not ParameterExpr parameterExpression)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "An Elasticsearch request value must be a canonical constant or invocation parameter.",
                    site?.Node ?? branch.Node);
            }

            QueryParameterId parameter = new(parameterExpression.Parameter);
            if (!parameters.TryGetValue(parameter, out var contract))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Canonical parameter '{parameter.Value}' is absent from the demand-scoped input contract.",
                    site?.Node ?? branch.Node);
            }
            if (!IsSupportedScalar(contract.ValueContract.GetEffectiveType()))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Canonical parameter '{parameter.Value}' does not have a supported Elasticsearch scalar encoding.",
                    site?.Node ?? branch.Node,
                    contract.Input.Id);
            }
            if (contract.Definition.Presence == FieldPresence.Optional
                && contract.Definition.DefaultKind == QueryParameterDefaultKind.None)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Optional parameter '{parameter.Value}' has no default; Elasticsearch cannot bind semantic undefined.",
                    site?.Node ?? branch.Node,
                    contract.Input.Id);
            }
            if (requireNonNull && !IsRequiredNonNull(contract.ValueContract))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Parameter '{parameter.Value}' may be null or missing where exact Elasticsearch scalar semantics require a value.",
                    site?.Node ?? branch.Node,
                    contract.Input.Id);
            }
            usedParameters.Add(parameter);
            return ElasticQueryValueTemplate.FromParameter(parameter);
        }

        CompiledBranchBody CompileRows(ElasticQueryTemplate query)
        {
            if (storageBinding.SourceMode != ElasticRelationQuerySourceMode.Enabled)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Elasticsearch row decoding v2 requires stored _source; synthetic and disabled source modes are not yet proven exact.",
                    branch.Node);
            }

            ImmutableArray<ElasticRelationQueryResultFieldBinding>.Builder resultFields =
                ImmutableArray.CreateBuilder<ElasticRelationQueryResultFieldBinding>(branch.Fields.Length);
            ImmutableArray<string>.Builder sourceIncludes = ImmutableArray.CreateBuilder<string>();
            foreach (var field in branch.Fields)
            {
                var resolved = ResolveRowOutput(field);
                resultFields.Add(resolved);
                if (resolved.SourceKind == ElasticRelationQueryResultSourceKind.SourceField)
                {
                    sourceIncludes.Add(resolved.PhysicalName!);
                }
            }

            var orderExecution = pipeline.Single(static execution => execution.CanonicalNode is OrderQueryNode);
            var order = (OrderQueryNode)orderExecution.CanonicalNode;
            ImmutableArray<ElasticSearchSort>.Builder sorts =
                ImmutableArray.CreateBuilder<ElasticSearchSort>(order.Orderings.Length);
            ImmutableArray<string>.Builder sortFields = ImmutableArray.CreateBuilder<string>(order.Orderings.Length);
            ImmutableArray<ValueContract>.Builder sortValueContracts =
                ImmutableArray.CreateBuilder<ValueContract>(order.Orderings.Length);
            string? stableFinalField = null;
            for (var index = 0; index < order.Orderings.Length; index++)
            {
                var ordering = order.Orderings[index];
                var site = orderExecution.OrderKeys.Single(candidate => candidate.Ordinal == index);
                if (!IsRequiredNonNull(site.Analysis.KnownResult))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        "Canonical Elasticsearch ordering v2 requires required, non-null keys.",
                        orderExecution.Id);
                }
                if (!TryResolveSourceField(ordering.Key, site, out var field))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                        "Canonical Elasticsearch ordering v2 requires direct physical source fields.",
                        orderExecution.Id);
                }
                RequireNonNullField(field, orderExecution.Id, "ordering");
                RequireMappingCompatibility(
                    field,
                    site.Analysis.KnownResult?.GetEffectiveType(),
                    orderExecution.Id);
                RequireCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering,
                    orderExecution.Id);
                var physicalName = RequireQueryField(field, orderExecution.Id);
                sorts.Add(new(physicalName, ordering.Direction, ordering.NullPlacement));
                sortFields.Add(physicalName);
                sortValueContracts.Add(site.Analysis.KnownResult!);
                if (index == order.Orderings.Length - 1)
                {
                    RequireCapability(
                        field,
                        ElasticRelationQueryFieldSemanticCapabilities.StableUniqueOrdering,
                        orderExecution.Id);
                    stableFinalField = physicalName;
                }
            }

            var pageExecution = pipeline.Single(static execution => execution.CanonicalNode is PageQueryNode);
            var page = ((PageQueryNode)pageExecution.CanonicalNode).Page;
            if (page.Limit > storageBinding.MaximumPageSize)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    $"Page size {page.Limit} exceeds the binding boundary of {storageBinding.MaximumPageSize}.",
                    pageExecution.Id);
            }

            ElasticSearchPageTemplate pageTemplate;
            ElasticRelationQueryPagingContract paging;
            switch (page)
            {
                case OffsetPageDefinition offset:
                    if ((long)offset.Offset + offset.Limit > storageBinding.MaximumResultWindow)
                    {
                        throw Fail(
                            ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                            $"Offset page end {((long)offset.Offset + offset.Limit).ToString(CultureInfo.InvariantCulture)} exceeds index.max_result_window {storageBinding.MaximumResultWindow}.",
                            pageExecution.Id);
                    }
                    pageTemplate = ElasticSearchPageTemplate.OffsetPage(offset.Offset, offset.Limit);
                    paging = new(
                        ElasticRelationQueryPagingKind.Offset,
                        offset.Offset,
                        offset.Limit,
                        sortFields.ToImmutable(),
                        sortValueContracts.ToImmutable(),
                        stableFinalField);
                    break;
                case KeysetPageDefinition keyset:
                    RequireImmutablePagination(pageExecution.Id, "search_after");
                    if (!keyset.After.IsDefaultOrEmpty && keyset.After.Length != order.Orderings.Length)
                    {
                        throw Fail(
                            ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                            "A keyset continuation must contain one value for every Elasticsearch sort key.",
                            pageExecution.Id);
                    }
                    ImmutableArray<ElasticQueryValueTemplate>.Builder after =
                        ImmutableArray.CreateBuilder<ElasticQueryValueTemplate>(keyset.After.Length);
                    for (var index = 0; index < keyset.After.Length; index++)
                    {
                        var site = pageExecution.KeysetBoundaries.Single(candidate => candidate.Ordinal == index);
                        if (!IsRequiredNonNull(site.Analysis.KnownResult)
                            || site.Analysis.KnownResult?.GetEffectiveType()
                            != orderExecution.OrderKeys.Single(candidate => candidate.Ordinal == index)
                                .Analysis.KnownResult?.GetEffectiveType())
                        {
                            throw Fail(
                                ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                                "A keyset continuation must be required, non-null, and type-aligned with its sort key.",
                                pageExecution.Id);
                        }
                        after.Add(CreateValueTemplate(keyset.After[index], site, requireNonNull: true));
                    }
                    pageTemplate = ElasticSearchPageTemplate.SearchAfterPage(keyset.Limit, after.ToImmutable());
                    paging = new(
                        ElasticRelationQueryPagingKind.SearchAfter,
                        offset: 0,
                        keyset.Limit,
                        sortFields.ToImmutable(),
                        sortValueContracts.ToImmutable(),
                        stableFinalField);
                    break;
                default:
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                    $"Page kind '{page.GetType().Name}' is unsupported by Elasticsearch v2.",
                        pageExecution.Id);
            }

            return new(
                new(
                    storageBinding.IndexName,
                    query,
                    sourceIncludes.ToImmutable(),
                    sorts.ToImmutable(),
                    pageTemplate,
                    ElasticAggregationTemplate.None),
                resultFields.ToImmutable(),
                paging);
        }

        ElasticRelationQueryResultFieldBinding ResolveRowOutput(RelationQueryFieldReference field)
        {
            if (projections.TryGetValue((branch.Binding, field.Path), out var projection))
            {
                var contract = RequireKnownResultContract(
                    projection.ValueSite,
                    projection.ValueSite.Node ?? branch.Node,
                    "projection result");
                var constant = projection.Definition.Value switch
                {
                    ConstantExpr constantExpression => (ObservationValue?)constantExpression.Value,
                    LiteralExpr literalExpression => literalExpression.Value,
                    _ => null
                };
                if (constant is { } value)
                {
                    if (!contract.IsSatisfiedByConstant(value)
                        || !ElasticQueryValueTemplate.IsSupportedScalar(value))
                    {
                        throw Fail(
                            ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                            $"Projection assignment '{projection.Definition.Id.Value}' does not retain one exact Elasticsearch scalar constant.",
                            projection.ValueSite.Node ?? branch.Node);
                    }
                    return new(
                        field,
                        contract,
                        ElasticRelationQueryResultSourceKind.Constant,
                        ResolveResultEncoding(contract, projection.ValueSite.Node ?? branch.Node),
                        constant: value,
                        assignment: projection.Definition.Id);
                }
                if (!TryResolveSourceField(projection.Definition.Value, projection.ValueSite, out var source))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                        $"Projection assignment '{projection.Definition.Id.Value}' is not a direct field or scalar constant.",
                        projection.ValueSite.Node ?? branch.Node);
                }
                return CreateSourceResult(field, contract, source, projection.Definition.Id);
            }
            if (!sourceFields.TryGetValue((branch.Binding, field.Path), out var direct))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Demanded output field '{field.Path}' has no demand-scoped projection or source binding.",
                    branch.Node);
            }
            var directContract = direct.Input.ValueContract
                ?? throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Source result field '{field.Path}' has no resolved semantic value contract.",
                    branch.Node,
                    direct.Input.Id);
            return CreateSourceResult(field, directContract, ResolveSourceField(direct), assignment: null);
        }

        ElasticRelationQueryResultFieldBinding CreateSourceResult(
            RelationQueryFieldReference output,
            ValueContract contract,
            ResolvedSourceField source,
            QueryAssignmentId? assignment)
        {
            if (source.Physical.RetrievalKind != ElasticRelationQueryFieldRetrievalKind.Source
                || source.Physical.SourceField is not { } sourcePath)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Result field input '{source.Contract.Input.Id.Value}' requires _source retrieval in Elasticsearch v2.",
                    branch.Node,
                    source.Contract.Input.Id);
            }
            if (source.Physical.DocumentScope != ElasticRelationQueryFieldDocumentScope.RootDocument)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Result field input '{source.Contract.Input.Id.Value}' is not proven to be a scalar root-document _source value; nested source extraction is deferred.",
                    branch.Node,
                    source.Contract.Input.Id);
            }
            if (source.Contract.Input.ValueContract != contract)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Projection result for input '{source.Contract.Input.Id.Value}' changes its value contract without a target transform.",
                    branch.Node,
                    source.Contract.Input.Id);
            }
            var semanticEncoding = ResolveResultEncoding(contract, branch.Node);
            var retrievalEncoding = source.Physical.RetrievalEncoding switch
            {
                ElasticRelationQueryFieldValueEncoding.JsonBoolean =>
                    ElasticRelationQueryResultValueEncoding.JsonBoolean,
                ElasticRelationQueryFieldValueEncoding.JsonInt64 =>
                    ElasticRelationQueryResultValueEncoding.JsonInt64,
                ElasticRelationQueryFieldValueEncoding.JsonString =>
                    ElasticRelationQueryResultValueEncoding.JsonString,
                ElasticRelationQueryFieldValueEncoding.CanonicalTemporalString =>
                    ElasticRelationQueryResultValueEncoding.CanonicalTemporalString,
                _ => throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Result field input '{source.Contract.Input.Id.Value}' has no exact physical retrieval encoding.",
                    branch.Node,
                    source.Contract.Input.Id)
            };
            if (retrievalEncoding != semanticEncoding)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Result field input '{source.Contract.Input.Id.Value}' retrieves '{retrievalEncoding}', which does not preserve its '{semanticEncoding}' semantic encoding.",
                    branch.Node,
                    source.Contract.Input.Id);
            }
            sourceRetrievedInputs.Add(source.Contract.Input.Id);
            return new(
                output,
                contract,
                ElasticRelationQueryResultSourceKind.SourceField,
                retrievalEncoding,
                ElasticRelationQuerySelectedField.PhysicalName(sourcePath),
                assignment: assignment);
        }

        CompiledBranchBody CompileAggregation(ElasticQueryTemplate query)
        {
            if (aggregateExecution?.CanonicalNode is not AggregateQueryNode aggregate)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "An Elasticsearch aggregation branch has no retained aggregate execution node.",
                    branch.Node);
            }
            if (aggregateExecution.AggregateAssignments.IsDefaultOrEmpty
                || aggregateExecution.AggregateAssignments.Any(static assignment =>
                    assignment.Definition.Operation != AggregateOperator.Count
                    || assignment.Definition.Value is not null
                    || assignment.Definition.Filter is not null))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Canonical Elasticsearch v2 supports demanded row-count assignments only; value count, filters, and numeric metrics are deferred.",
                    aggregateExecution.Id);
            }

            return aggregate.Groupings.IsDefaultOrEmpty
                ? CompileGlobalCount(query, aggregate)
                : CompileGroupedCount(query, aggregate);
        }

        CompiledBranchBody CompileGlobalCount(
            ElasticQueryTemplate query,
            AggregateQueryNode aggregate)
        {
            if (pipeline.Any(static execution => execution.CanonicalNode is OrderQueryNode or PageQueryNode))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                    "A global Elasticsearch row count cannot carry row ordering or paging nodes.",
                    aggregate.Id);
            }
            var countAssignments = aggregateExecution!.AggregateAssignments
                .ToDictionary(static assignment => assignment.Definition.Target);
            ImmutableArray<ElasticRelationQueryResultFieldBinding>.Builder resultFields =
                ImmutableArray.CreateBuilder<ElasticRelationQueryResultFieldBinding>(branch.Fields.Length);
            foreach (var field in branch.Fields)
            {
                if (!countAssignments.TryGetValue(field.Path, out var assignment))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                        $"Global count output '{field.Path}' is not produced by a demanded row-count assignment.",
                        aggregate.Id);
                }
                resultFields.Add(new(
                    field,
                    new ValueContract(new ScalarTypeRef(ScalarTypeKind.Int64)),
                    ElasticRelationQueryResultSourceKind.ExactTotalHits,
                    ElasticRelationQueryResultValueEncoding.ExactCountInt64,
                    assignment: assignment.Definition.Id));
            }

            return new(
                new(
                    storageBinding.IndexName,
                    query,
                    sourceIncludes: [],
                    sorts: [],
                    ElasticSearchPageTemplate.Unpaged,
                    ElasticAggregationTemplate.CountRows()),
                resultFields.ToImmutable(),
                Paging: null);
        }

        CompiledBranchBody CompileGroupedCount(
            ElasticQueryTemplate query,
            AggregateQueryNode aggregate)
        {
            if (aggregateExecution!.AggregateGroupings.Length != aggregate.Groupings.Length)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Exact composite grouping requires analyzed execution material for every canonical grouping key.",
                    aggregate.Id);
            }
            var orderExecution = pipeline.SingleOrDefault(static execution => execution.CanonicalNode is OrderQueryNode)
                ?? throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Composite grouped counts require an explicit ordering over every grouping key.",
                    aggregate.Id);
            var pageExecution = pipeline.SingleOrDefault(static execution => execution.CanonicalNode is PageQueryNode)
                ?? throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Composite grouped counts require bounded keyset pagination.",
                    aggregate.Id);
            var order = (OrderQueryNode)orderExecution.CanonicalNode;
            if (((PageQueryNode)pageExecution.CanonicalNode).Page is not KeysetPageDefinition page)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Composite grouped counts require keyset pagination; offset and terms-bucket approximation are not exact.",
                    pageExecution.Id);
            }
            RequireImmutablePagination(pageExecution.Id, "composite after-key");
            if (page.Limit > storageBinding.MaximumPageSize)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    $"Composite bucket page size {page.Limit} exceeds the binding boundary of {storageBinding.MaximumPageSize}.",
                    pageExecution.Id);
            }
            if (order.Orderings.Length != aggregateExecution.AggregateGroupings.Length)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Composite ordering must contain every demanded grouping key exactly once.",
                    orderExecution.Id);
            }

            ImmutableArray<ElasticCompositeAggregationSource>.Builder sources =
                ImmutableArray.CreateBuilder<ElasticCompositeAggregationSource>(order.Orderings.Length);
            ImmutableArray<string>.Builder physicalFields = ImmutableArray.CreateBuilder<string>(order.Orderings.Length);
            ImmutableArray<ValueContract>.Builder physicalValueContracts =
                ImmutableArray.CreateBuilder<ValueContract>(order.Orderings.Length);
            Dictionary<FieldPath, (string Name, RelationQueryAggregateGroupingExecution Grouping)> groupResultSources = [];
            List<RelationQueryAggregateGroupingExecution> orderedGroupings = [];
            HashSet<QueryAssignmentId> seenGroupings = [];
            for (var index = 0; index < order.Orderings.Length; index++)
            {
                var ordering = order.Orderings[index];
                var orderSite = orderExecution.OrderKeys.Single(candidate => candidate.Ordinal == index);
                var grouping = ResolveGroupingOrdering(ordering.Key, orderSite, aggregate);
                if (!seenGroupings.Add(grouping.Definition.Id))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                        $"Grouping '{grouping.Definition.Id.Value}' appears more than once in composite ordering.",
                        orderExecution.Id);
                }
                if (!IsRequiredNonNull(grouping.KeySite.Analysis.KnownResult))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        $"Grouping '{grouping.Definition.Id.Value}' may be missing or null; composite missing-bucket semantics are not canonical v2 semantics.",
                        aggregate.Id);
                }
                if (!IsExactCompositeGroupingType(grouping.KeySite.Analysis.KnownResult?.GetEffectiveType()))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                        $"Grouping '{grouping.Definition.Id.Value}' is outside the exact Elasticsearch v2 composite-key domain; only text, GUID, and integer keys are supported.",
                        aggregate.Id);
                }
                if (!TryResolveSourceField(grouping.Definition.Key, grouping.KeySite, out var field))
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                        $"Grouping '{grouping.Definition.Id.Value}' is not a direct physical field.",
                        aggregate.Id);
                }
                RequireNonNullField(field, aggregate.Id, "grouping");
                RequireMappingCompatibility(
                    field,
                    grouping.KeySite.Analysis.KnownResult?.GetEffectiveType(),
                    aggregate.Id);
                RequireCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactAggregation,
                    aggregate.Id);
                RequireCapability(
                    field,
                    ElasticRelationQueryFieldSemanticCapabilities.ExactOrdering,
                    aggregate.Id);
                var physicalField = RequireQueryField(field, aggregate.Id);
                var name = $"g{index.ToString(CultureInfo.InvariantCulture)}";
                sources.Add(new(name, physicalField, ordering.Direction));
                physicalFields.Add(physicalField);
                physicalValueContracts.Add(grouping.KeySite.Analysis.KnownResult!);
                orderedGroupings.Add(grouping);
                groupResultSources.Add(grouping.Definition.Target, (name, grouping));
            }
            if (seenGroupings.Count != aggregateExecution.AggregateGroupings.Length)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Composite ordering does not cover every demanded grouping key.",
                    orderExecution.Id);
            }

            if (!page.After.IsDefaultOrEmpty && page.After.Length != sources.Count)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "A composite continuation must contain one value for every grouping source.",
                    pageExecution.Id);
            }
            ImmutableArray<ElasticQueryValueTemplate>.Builder after =
                ImmutableArray.CreateBuilder<ElasticQueryValueTemplate>(page.After.Length);
            for (var index = 0; index < page.After.Length; index++)
            {
                var boundarySite = pageExecution.KeysetBoundaries.Single(candidate => candidate.Ordinal == index);
                var groupContract = orderedGroupings[index].KeySite.Analysis.KnownResult;
                if (!IsRequiredNonNull(boundarySite.Analysis.KnownResult)
                    || boundarySite.Analysis.KnownResult?.GetEffectiveType() != groupContract?.GetEffectiveType())
                {
                    throw Fail(
                        ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                        "A composite continuation must be required, non-null, and type-aligned with its grouping key.",
                        pageExecution.Id);
                }
                after.Add(CreateValueTemplate(page.After[index], boundarySite, requireNonNull: true));
            }

            var countAssignments = aggregateExecution.AggregateAssignments
                .ToDictionary(static assignment => assignment.Definition.Target);
            ImmutableArray<ElasticRelationQueryResultFieldBinding>.Builder resultFields =
                ImmutableArray.CreateBuilder<ElasticRelationQueryResultFieldBinding>(branch.Fields.Length);
            foreach (var field in branch.Fields)
            {
                if (groupResultSources.TryGetValue(field.Path, out var grouping))
                {
                    var contract = RequireKnownResultContract(
                        grouping.Grouping.KeySite,
                        aggregate.Id,
                        "grouping result");
                    resultFields.Add(new(
                        field,
                        contract,
                        ElasticRelationQueryResultSourceKind.CompositeKey,
                        ResolveResultEncoding(contract, aggregate.Id),
                        physicalName: grouping.Name,
                        assignment: grouping.Grouping.Definition.Id));
                    continue;
                }
                if (countAssignments.TryGetValue(field.Path, out var count))
                {
                    resultFields.Add(new(
                        field,
                        new ValueContract(new ScalarTypeRef(ScalarTypeKind.Int64)),
                        ElasticRelationQueryResultSourceKind.CompositeDocumentCount,
                        ElasticRelationQueryResultValueEncoding.ExactCountInt64,
                        assignment: count.Definition.Id));
                    continue;
                }
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    $"Grouped count output '{field.Path}' is not produced by a demanded grouping or row-count assignment.",
                    aggregate.Id);
            }

            return new(
                new(
                    storageBinding.IndexName,
                    query,
                    sourceIncludes: [],
                    sorts: [],
                    ElasticSearchPageTemplate.Unpaged,
                    ElasticAggregationTemplate.CompositeCount(
                        "groups",
                        page.Limit,
                        sources.ToImmutable(),
                        after.ToImmutable())),
                resultFields.ToImmutable(),
                new(
                    ElasticRelationQueryPagingKind.CompositeAfter,
                    offset: 0,
                    page.Limit,
                    physicalFields.ToImmutable(),
                    physicalValueContracts.ToImmutable(),
                    stableUniqueFinalField: null));
        }

        RelationQueryAggregateGroupingExecution ResolveGroupingOrdering(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            AggregateQueryNode aggregate)
        {
            if (!TryResolveFieldRoot(expression, site, out var resolved)
                || resolved.Binding != aggregate.ResultBinding
                || !groupings.TryGetValue((aggregate.ResultBinding, resolved.Path), out var grouping))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Composite ordering must reference one aggregate grouping output field directly.",
                    site.Node ?? aggregate.Id);
            }
            return grouping;
        }

        bool TryResolveSourceField(
            Expr expression,
            RelationQueryExpressionSiteAnalysis? site,
            out ResolvedSourceField field)
        {
            if (!TryResolveFieldRoot(expression, site, out var root))
            {
                field = default;
                return false;
            }
            if (projections.TryGetValue((root.Binding, root.Path), out var projection))
            {
                return TryResolveSourceField(projection.Definition.Value, projection.ValueSite, out field);
            }

            if (groupings.TryGetValue((root.Binding, root.Path), out var grouping))
            {
                return TryResolveSourceField(grouping.Definition.Key, grouping.KeySite, out field);
            }

            if (!sourceFields.TryGetValue((root.Binding, root.Path), out var contract))
            {
                field = default;
                return false;
            }
            field = ResolveSourceField(contract);
            return true;
        }

        bool TryResolveFieldRoot(
            Expr expression,
            RelationQueryExpressionSiteAnalysis? site,
            out ResolvedFieldRoot resolved)
        {
            FieldPath path;
            ValueBindingId? explicitBinding;
            switch (expression)
            {
                case FieldExpr field:
                    path = field.Path;
                    explicitBinding = field.Binding;
                    break;
                case FieldRefExpr field:
                    path = field.Path;
                    explicitBinding = null;
                    break;
                default:
                    resolved = default;
                    return false;
            }
            if (explicitBinding is { } binding)
            {
                resolved = new(binding, path);
                return true;
            }
            if (site is null)
            {
                resolved = default;
                return false;
            }
            var candidates = site.Analysis.Requirements.Fields
                .Where(requirement => requirement.WasUnqualified
                                      && requirement.Root == ExprFieldRootKind.Binding
                                      && requirement.Path == path
                                      && requirement.Binding is not null)
                .ToArray();
            if (candidates.Length != 1)
            {
                resolved = default;
                return false;
            }
            resolved = new(candidates[0].Binding!.Value, candidates[0].Path);
            return true;
        }

        ResolvedSourceField ResolveSourceField(RelationQueryFieldInputContract contract)
        {
            try
            {
                return new(contract, storageBinding.ResolveField(contract.Input.Id));
            }
            catch (KeyNotFoundException)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Compiled field input '{contract.Input.Id.Value}' has no Elasticsearch field binding.",
                    contract.Input.Producer,
                    contract.Input.Id);
            }
        }

        static void RequireNonNullField(
            ResolvedSourceField field,
            QueryNodeId node,
            string operation)
        {
            if (IsRequiredNonNull(field.Contract.Input.ValueContract))
            {
                return;
            }

            throw Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Field input '{field.Contract.Input.Id.Value}' may be missing or null where exact {operation} semantics require a value.",
                node,
                field.Contract.Input.Id);
        }

        static void RequireCapability(
            ResolvedSourceField field,
            ElasticRelationQueryFieldSemanticCapabilities capability,
            QueryNodeId node)
        {
            if (field.Physical.SemanticCapabilities.HasFlag(capability))
            {
                return;
            }

            throw Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Elasticsearch field input '{field.Contract.Input.Id.Value}' does not attest '{capability}'.",
                node,
                field.Contract.Input.Id);
        }

        string RequireQueryField(ResolvedSourceField field, QueryNodeId node)
        {
            var path = RequireRootQueryFieldPath(field, node);
            TrackQueryField(field.Contract.Input.Id, path);
            return ElasticRelationQuerySelectedField.PhysicalName(path);
        }

        static FieldPath RequireRootQueryFieldPath(ResolvedSourceField field, QueryNodeId node)
        {
            if (field.Physical.QueryField is not { } path)
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Field input '{field.Contract.Input.Id.Value}' has no indexed Elasticsearch query field.",
                    node,
                    field.Contract.Input.Id);
            }
            if (field.Physical.DocumentScope == ElasticRelationQueryFieldDocumentScope.RootDocument)
            {
                return path;
            }

            throw Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Field input '{field.Contract.Input.Id.Value}' is not proven queryable in the root Elasticsearch document; nested-query lowering is deferred.",
                node,
                field.Contract.Input.Id);
        }

        void TrackQueryField(RelationQueryInputId input, FieldPath path)
        {
            if (!queriedFields.TryGetValue(input, out var fields))
            {
                fields = [];
                queriedFields.Add(input, fields);
            }
            fields.Add(path);
        }

        static void RequireMappingCompatibility(
            ResolvedSourceField field,
            TypeRef? type,
            QueryNodeId node)
        {
            if (type is ScalarTypeRef
                {
                    Kind: ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant
                })
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Temporal field input '{field.Contract.Input.Id.Value}' is outside the exact Elasticsearch v2 query domain because canonical equality retains representation while Elasticsearch dates normalize instants and precision.",
                    node,
                    field.Contract.Input.Id);
            }

            var compatible = (type, field.Physical.MappingKind) switch
            {
                (ScalarTypeRef { Kind: ScalarTypeKind.Bool }, ElasticRelationQueryFieldMappingKind.Boolean) => true,
                (ScalarTypeRef { Kind: ScalarTypeKind.Int32 },
                    ElasticRelationQueryFieldMappingKind.Integer or ElasticRelationQueryFieldMappingKind.Long) => true,
                (ScalarTypeRef { Kind: ScalarTypeKind.Int64 }, ElasticRelationQueryFieldMappingKind.Long) => true,
                (ScalarTypeRef { Kind: ScalarTypeKind.String or ScalarTypeKind.Guid },
                    ElasticRelationQueryFieldMappingKind.Keyword or ElasticRelationQueryFieldMappingKind.Wildcard) => true,
                _ => false
            };
            if (compatible)
            {
                return;
            }

            throw Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Elasticsearch mapping '{field.Physical.MappingKind}' does not prove the canonical value domain for field input '{field.Contract.Input.Id.Value}'.",
                node,
                field.Contract.Input.Id);
        }

        static void RequireNestedMappingCompatibility(
            ElasticRelationQueryNestedChildFieldBinding child,
            TypeRef? type,
            QueryNodeId node,
            RelationQueryInputId input)
        {
            var compatible = (type, child.MappingKind) switch
            {
                (ScalarTypeRef { Kind: ScalarTypeKind.Bool }, ElasticRelationQueryFieldMappingKind.Boolean) => true,
                (ScalarTypeRef { Kind: ScalarTypeKind.Int32 },
                    ElasticRelationQueryFieldMappingKind.Integer or ElasticRelationQueryFieldMappingKind.Long) => true,
                (ScalarTypeRef { Kind: ScalarTypeKind.Int64 }, ElasticRelationQueryFieldMappingKind.Long) => true,
                (ScalarTypeRef { Kind: ScalarTypeKind.String or ScalarTypeKind.Guid },
                    ElasticRelationQueryFieldMappingKind.Keyword or ElasticRelationQueryFieldMappingKind.Wildcard) => true,
                _ => false
            };
            if (compatible)
            {
                return;
            }

            throw NestedFailure(
                $"Elasticsearch nested child mapping '{child.MappingKind}' does not prove the canonical value domain for element path '{child.ElementPath}'.",
                node,
                input);
        }

        ImmutableArray<ElasticRelationQuerySelectedField> CreateSelectedFields()
        {
            return
            [
                .. plan.InputContract.Sources
                    .SelectMany(static source => source.Fields)
                    .Where(field => sourceRetrievedInputs.Contains(field.Input.Id)
                                    || queriedFields.ContainsKey(field.Input.Id))
                    .OrderBy(static field => field.Input.Id.Value, StringComparer.Ordinal)
                    .Select(field =>
                    {
                        var physical = ResolveSourceField(field).Physical;
                        return new ElasticRelationQuerySelectedField(
                            field.Input.Id,
                            field.Input.Field,
                            sourceRetrievedInputs.Contains(field.Input.Id) ? physical.SourceField : null,
                            queriedFields.TryGetValue(field.Input.Id, out var queryPaths)
                                ? [.. queryPaths]
                                : []);
                    })
            ];
        }

        ImmutableArray<ElasticRelationQueryParameterBinding> CreateParameterBindings() =>
        [
            .. usedParameters
                .OrderBy(static parameter => parameter.Value, StringComparer.Ordinal)
                .Select(parameter =>
                {
                    var contract = parameters[parameter];
                    return new ElasticRelationQueryParameterBinding(contract.Definition, contract.ValueContract);
                })
        ];

        RelationQueryNativeCompilationProvenance CreateProvenance(
            ImmutableArray<ElasticRelationQuerySelectedField> selectedFields)
        {
            var assignments = pipeline.SelectMany(static execution =>
                    execution.ProjectionAssignments.Select(static assignment => assignment.Definition.Id)
                        .Concat(execution.AggregateGroupings.Select(static grouping => grouping.Definition.Id))
                        .Concat(execution.AggregateAssignments.Select(static assignment => assignment.Definition.Id)))
                .Distinct()
                .ToImmutableArray();
            return RelationQueryNativeCompilationProvenanceFactory.Create(
                nativeRequest ?? throw new InvalidOperationException(
                    "Elasticsearch artifact provenance requires an authorized native-compilation request."),
                branch.Id,
                options.CompilerProfile,
                options.ConventionSetVersion,
                [.. pipeline.Select(static execution => execution.Id)],
                assignments,
                [.. selectedFields.Select(static field => field.Input)]);
        }

        static RelationQueryExpressionSiteAnalysis RequiredSite(
            RelationQueryExecutionNode execution,
            RelationQueryExpressionSiteKind kind) =>
            execution.ExpressionSites.Single(site => site.Kind == kind);

        static ValueContract AnalyzeSubexpression(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            string operand,
            ExprScope? scope = null)
        {
            var parent = site.Analysis.Site;
            var analysis = ExprAnalyzer.Analyze(
                new ExprSite(
                    new($"{parent.Id.Value}/elastic/{operand}"),
                    expression,
                    scope ?? parent.Scope,
                    ExprExpectation.Any,
                    parent.CapabilityProfile,
                    parent.DiagnosticLocation),
                site.Analysis.Semantics);
            if (analysis.IsValid && analysis.KnownResult is { } result)
            {
                return result;
            }

            throw Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"The {operand} operand does not have one valid, known value contract for exact Elasticsearch lowering.",
                site.Node);
        }

        static ValueContract RequireKnownResultContract(
            RelationQueryExpressionSiteAnalysis site,
            QueryNodeId node,
            string operation)
        {
            if (site.Analysis.KnownResult is { } result)
            {
                return result;
            }

            throw Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Canonical {operation} does not have one known semantic value contract for result decoding.",
                node);
        }

        static ElasticRelationQueryResultValueEncoding ResolveResultEncoding(
            ValueContract contract,
            QueryNodeId node)
        {
            if (!ElasticRelationQueryResultValueEncodingSemantics.TryResolve(contract, out var encoding))
            {
                throw Fail(
                    ElasticRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Elasticsearch v2 cannot prove a canonical physical result encoding for this value contract.",
                    node);
            }
            return encoding;
        }

        static bool IsRequiredNonNull(ValueContract? contract) => contract is
        {
            Presence: FieldPresence.Required,
            Nullability: FieldNullability.NonNullable
        };

        static bool IsBooleanScalar(TypeRef? type) =>
            type is ScalarTypeRef { Kind: ScalarTypeKind.Bool };

        static bool IsSupportedScalar(TypeRef? type) => type is ScalarTypeRef
        {
            Kind: ScalarTypeKind.Bool
                or ScalarTypeKind.Int32
                or ScalarTypeKind.Int64
                or ScalarTypeKind.String
                or ScalarTypeKind.Guid
                or ScalarTypeKind.Date
                or ScalarTypeKind.DateTime
                or ScalarTypeKind.Instant
        };

        static bool IsExactCompositeGroupingType(TypeRef? type) => type is ScalarTypeRef
        {
            Kind: ScalarTypeKind.Int32
                or ScalarTypeKind.Int64
                or ScalarTypeKind.String
                or ScalarTypeKind.Guid
        };

        void RequireImmutablePagination(QueryNodeId node, string mechanism)
        {
            if (storageBinding.PaginationConsistency == ElasticRelationQueryPaginationConsistency.StableSearchView)
            {
                return;
            }

            throw Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                $"Exact Elasticsearch {mechanism} pagination requires a binding that attests one unchanged search-visible view for the complete logical page sequence; point-in-time execution is deferred.",
                node);
        }

        static BinaryOperator Reverse(BinaryOperator @operator) => @operator switch
        {
            BinaryOperator.Eq => BinaryOperator.Eq,
            BinaryOperator.Ne => BinaryOperator.Ne,
            BinaryOperator.Gt => BinaryOperator.Lt,
            BinaryOperator.Ge => BinaryOperator.Le,
            BinaryOperator.Lt => BinaryOperator.Gt,
            BinaryOperator.Le => BinaryOperator.Ge,
            _ => throw Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"Binary operator '{@operator}' cannot be reversed for Elasticsearch lowering.")
        };

        static BranchCompilationException Fail(
            string code,
            string message,
            QueryNodeId? node = null,
            RelationQueryInputId? input = null) =>
            new(code, message, node, input);

        static BranchCompilationException NestedFailure(
            string message,
            QueryNodeId? node = null,
            RelationQueryInputId? input = null) =>
            Fail(
                ElasticRelationQueryCompilationDiagnosticCodes.NestedCorrelationUnavailable,
                message,
                node,
                input);

        enum PipelineStage
        {
            Source = 0,
            Row = 1,
            Shape = 2,
            Order = 3,
            Page = 4
        }

        readonly record struct ResolvedFieldRoot(ValueBindingId Binding, FieldPath Path);

        readonly record struct ResolvedSourceField(
            RelationQueryFieldInputContract Contract,
            ElasticRelationQueryFieldBinding Physical);

        readonly record struct CompiledBranchBody(
            ElasticSearchRequestTemplate Request,
            ImmutableArray<ElasticRelationQueryResultFieldBinding> ResultFields,
            ElasticRelationQueryPagingContract? Paging);

        readonly record struct AnalyzedBranch(
            CompiledBranchBody Body,
            ImmutableArray<ElasticRelationQuerySelectedField> SelectedFields,
            ImmutableArray<ElasticRelationQueryParameterBinding> ParameterBindings);
    }

    sealed class BranchCompilationException(
        string code,
        string message,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null) : Exception(message)
    {
        public string Code { get; } = code;
        public QueryNodeId? Node { get; } = node;
        public RelationQueryInputId? Input { get; } = input;
    }
}

static class ElasticRelationQueryArtifactFingerprinter
{
    const string Algorithm = "sha256";
    const string Canonicalization = "cohesive.relations.elastic-artifact/v4-c14n/v1";

    public static ElasticRelationQueryArtifactFingerprint Compute(
        RelationQueryNativeResultBranch branch,
        ElasticSearchRequestTemplate request,
        ElasticRelationQueryStorageBinding storageBinding,
        ImmutableArray<ElasticRelationQuerySelectedField> selectedFields,
        ImmutableArray<ElasticRelationQueryResultFieldBinding> resultFields,
        ImmutableArray<ElasticRelationQueryParameterBinding> parameters,
        ElasticRelationQueryPagingContract? paging,
        ImmutableArray<ElasticRelationQueryLoweringDecision> loweringDecisions,
        ElasticQueryLoweringFingerprint loweringPolicyFingerprint,
        RelationQueryNativeCompilationProvenance provenance)
    {
        StringBuilder canonical = new();
        var jsonOptions = RelationQueryJsonSerializer.CreateOptions();
        Append(canonical, Canonicalization);
        Append(canonical, branch.Id.Value);
        Append(canonical, request.CanonicalText());
        Append(canonical, storageBinding.Fingerprint.Algorithm);
        Append(canonical, storageBinding.Fingerprint.Canonicalization);
        Append(canonical, storageBinding.Fingerprint.Value);
        Append(canonical, loweringPolicyFingerprint.Algorithm);
        Append(canonical, loweringPolicyFingerprint.Canonicalization);
        Append(canonical, loweringPolicyFingerprint.Value);
        Append(canonical, selectedFields.Length);
        foreach (var field in selectedFields)
        {
            Append(canonical, field.Input.Value);
            Append(canonical, field.Field.Shape.GraphId.Value);
            Append(canonical, field.Field.Shape.ShapeId.Value);
            Append(canonical, ElasticRelationQueryStorageBinding.FieldPathKey(field.Field.Path));
            Append(canonical, field.SourceField is { } sourceField
                ? ElasticRelationQueryStorageBinding.FieldPathKey(sourceField)
                : null);
            Append(canonical, field.QueryFields.Length);
            foreach (var queryField in field.QueryFields)
            {
                Append(canonical, ElasticRelationQueryStorageBinding.FieldPathKey(queryField));
            }
        }
        Append(canonical, resultFields.Length);
        foreach (var field in resultFields)
        {
            Append(canonical, field.Field.Shape.GraphId.Value);
            Append(canonical, field.Field.Shape.ShapeId.Value);
            Append(canonical, ElasticRelationQueryStorageBinding.FieldPathKey(field.Field.Path));
            Append(canonical, (int)field.SourceKind);
            Append(canonical, (int)field.Encoding);
            Append(canonical, field.PhysicalName);
            Append(canonical, JsonSerializer.Serialize(field.Constant));
            Append(canonical, field.Assignment?.Value);
            Append(canonical, JsonSerializer.Serialize(field.ValueContract, jsonOptions));
        }
        Append(canonical, parameters.Length);
        foreach (var parameter in parameters)
        {
            Append(canonical, parameter.Parameter.Value);
            Append(canonical, JsonSerializer.Serialize(parameter.ValueContract, jsonOptions));
            Append(canonical, (int)parameter.Definition.DefaultKind);
            Append(canonical, JsonSerializer.Serialize(parameter.Definition.DefaultValue));
        }
        Append(canonical, paging is null ? -1 : (int)paging.Kind);
        if (paging is not null)
        {
            Append(canonical, paging.Offset);
            Append(canonical, paging.Limit);
            Append(canonical, paging.StableUniqueFinalField);
            Append(canonical, paging.SortFields.Length);
            foreach (var field in paging.SortFields)
            {
                Append(canonical, field);
            }
            foreach (var contract in paging.SortValueContracts)
            {
                Append(canonical, JsonSerializer.Serialize(contract, jsonOptions));
            }
        }
        Append(canonical, loweringDecisions.Length);
        foreach (var lowering in loweringDecisions.OrderBy(static item => item.SiteId, StringComparer.Ordinal))
        {
            Append(canonical, lowering.SiteId);
            Append(canonical, lowering.Decision.Fingerprint.Algorithm);
            Append(canonical, lowering.Decision.Fingerprint.Canonicalization);
            Append(canonical, lowering.Decision.Fingerprint.Value);
        }
        Append(canonical, JsonSerializer.Serialize(provenance, jsonOptions));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new(Algorithm, Canonicalization, Convert.ToHexStringLower(bytes));
    }

    static void Append(StringBuilder builder, string? value)
    {
        builder
            .Append(value?.Length ?? -1)
            .Append(':')
            .Append(value)
            .Append(';');
    }

    static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));
}
