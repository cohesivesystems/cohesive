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

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Compiles exact demand-scoped canonical relation/query branches to Cosmos SQL through the standalone builder.
/// </summary>
public sealed class CosmosRelationQueryCompiler
{
    /// <summary>Configuration-evidence setting for the exact compiler implementation profile.</summary>
    public const string CompilerProfileSetting = "compilerProfile";

    /// <summary>Configuration-evidence setting for the compiler convention set.</summary>
    public const string CompilerConventionSetting = "compilerConventionSet";

    readonly CosmosRelationQueryCompilerOptions options;
    readonly RelationQueryConfigurationValueOrigin optionsOrigin;

    /// <summary>Creates a canonical Cosmos SQL compiler.</summary>
    /// <param name="options">
    /// Options participating in compilation provenance and artifact identity, or <see langword="null"/> to use the
    /// current compiler and convention profiles.
    /// </param>
    public CosmosRelationQueryCompiler(CosmosRelationQueryCompilerOptions? options = null)
    {
        this.options = options ?? new();
        optionsOrigin = options is null
            ? RelationQueryConfigurationValueOrigin.AdapterConvention
            : RelationQueryConfigurationValueOrigin.Explicit;
    }

    /// <summary>Predicts exact Cosmos realizability from placement and immutable storage-binding evidence.</summary>
    /// <param name="request">Plan, profile feasibility, placement, and selected result branches.</param>
    /// <param name="storageBinding">Versioned Cosmos container and document-shape binding.</param>
    /// <returns>A deterministic exact bound-realization report.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="storageBinding"/> is <see langword="null"/>.
    /// </exception>
    public RelationQueryBoundRealizationReport Realize(
        RelationQueryBoundRealizationRequest request,
        CosmosRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        return RelationQueryCompilerTelemetry.Observe(
            CosmosRelationQueryTelemetry.Emitter,
            RelationQueryTelemetry.RealizationActivityName,
            (Compiler: this, Request: request, StorageBinding: storageBinding),
            static state => state.Compiler.RealizeCore(state.Request, state.StorageBinding),
            static report => RelationQueryTelemetry.GetStatusTagValue(report.Status),
            static (activity, state, report) => RelationQueryCompilerTelemetry.ProjectRealizationActivity(
                activity,
                state.Request,
                state.StorageBinding.Fingerprint.Value,
                report));
    }

    RelationQueryBoundRealizationReport RealizeCore(
        RelationQueryBoundRealizationRequest request,
        CosmosRelationQueryStorageBinding storageBinding)
    {
        var bindingDiagnostics = ValidateBinding(
            request.Plan,
            request.PlanReference,
            request.ProfileFeasibility,
            request.Placement,
            request.Selection,
            storageBinding);
        var projection = new RelationQueryContextualEvidenceProjection(
            CreateBindingReference(storageBinding),
            request.ProfileFeasibility.IsRealizable
                ? CreateContextualAssessments(request, storageBinding, bindingDiagnostics)
                : []);
        return RelationQueryBoundRealizationCompiler.Compile(request, projection);
    }

    /// <summary>Realizes and compiles every selected branch, failing before artifact construction on contextual gaps.</summary>
    /// <param name="request">Plan, profile feasibility, placement, and selected result branches.</param>
    /// <param name="storageBinding">Versioned Cosmos container and document-shape binding.</param>
    /// <returns>Exact artifacts or structured invalid/unsupported diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="storageBinding"/> is <see langword="null"/>.
    /// </exception>
    public CosmosRelationQueryCompilationResult Compile(
        RelationQueryBoundRealizationRequest request,
        CosmosRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        var outcome = RelationQueryCompilerTelemetry.Observe(
            CosmosRelationQueryTelemetry.Emitter,
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

    (CosmosRelationQueryCompilationResult Compilation, RelationQueryBoundRealizationReport BoundRealization)
        CompileBoundCore(
        RelationQueryBoundRealizationRequest request,
        CosmosRelationQueryStorageBinding storageBinding)
    {
        var bound = Realize(request, storageBinding);
        if (!bound.IsRealizable)
        {
            CosmosRelationQueryCompilationResult compilation = new(
                bound.Status == RelationQueryRealizationStatus.Invalid
                    ? RelationQueryNativeCompilationStatus.Invalid
                    : RelationQueryNativeCompilationStatus.Unsupported,
                [],
                RelationQueryNativeCompilationDiagnostic.FromBoundRealizationFailure(bound));
            return new(compilation, bound);
        }
        return new(CompileCore(
            new RelationQueryNativeCompilationRequest(request.Plan, bound, request.Placement),
            storageBinding), bound);
    }

    /// <summary>Compiles every selected request branch independently and fails closed on semantic uncertainty.</summary>
    /// <param name="request">Exact static-plan, realization, placement, and branch-selection context.</param>
    /// <param name="storageBinding">Versioned Cosmos container and document-shape binding.</param>
    /// <returns>Exact artifacts or structured invalid/unsupported diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="storageBinding"/> is <see langword="null"/>.
    /// </exception>
    public CosmosRelationQueryCompilationResult Compile(
        RelationQueryNativeCompilationRequest request,
        CosmosRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        return RelationQueryCompilerTelemetry.Observe(
            CosmosRelationQueryTelemetry.Emitter,
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

    CosmosRelationQueryCompilationResult CompileCore(
        RelationQueryNativeCompilationRequest request,
        CosmosRelationQueryStorageBinding storageBinding)
    {

        ImmutableArray<RelationQueryNativeCompilationDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        var inputDiagnostics = request.ValidateInputs();
        var bindingDiagnostics = ValidateBinding(
            request.Plan,
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
                || inputDiagnostics.Any(static diagnostic =>
                    diagnostic.Code != RelationQueryNativeCompilationDiagnosticCodes.RealizationUnavailable);
            return new(
                invalid
                    ? RelationQueryNativeCompilationStatus.Invalid
                    : RelationQueryNativeCompilationStatus.Unsupported,
                [],
                diagnostics.ToImmutable());
        }

        ImmutableArray<CosmosRelationQueryCompiledArtifact>.Builder artifacts =
            ImmutableArray.CreateBuilder<CosmosRelationQueryCompiledArtifact>();
        foreach (var branch in request.Branches)
        {
            try
            {
                artifacts.Add(new BranchCompiler(request, storageBinding, options, branch).Compile());
            }
            catch (BranchCompilationException exception)
            {
                diagnostics.Add(new(
                    exception.Code,
                    DiagnosticSeverity.Error,
                    exception.Message,
                    branch.Id,
                    exception.Node,
                    exception.Input));
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or KeyNotFoundException
                                              or NotSupportedException)
            {
                diagnostics.Add(new(
                    CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
                    DiagnosticSeverity.Error,
                    $"Cosmos artifact construction failed closed: {exception.Message}",
                    branch.Id));
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
        CosmosRelationQueryStorageBinding storageBinding,
        string target,
        string boundRealizationFingerprint,
        CosmosRelationQueryCompilationResult result)
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
        CompiledRelationQueryPlan plan,
        RelationQueryCompiledPlanReference planReference,
        RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement sourcePlacement,
        RelationQueryCompilationSelection selection,
        CosmosRelationQueryStorageBinding storageBinding)
    {
        ImmutableArray<RelationQueryNativeCompilationDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        var reportProfile = realization.TargetProfile;
        if (storageBinding.CompiledPlanFingerprint is { } compiledPlanFingerprint
            && !Equals(
                compiledPlanFingerprint,
                RelationQueryCompiledPlanReferenceFingerprinter.Compute(planReference)))
        {
            diagnostics.Add(BindingDiagnostic(
                "The Cosmos storage binding's exact compiled-plan affinity does not match the native-compilation request."));
        }

        if (storageBinding.PlacementFingerprint is { } placementFingerprint
            && !Equals(placementFingerprint, sourcePlacement.Fingerprint))
        {
            diagnostics.Add(BindingDiagnostic(
                "The Cosmos storage binding's exact source-placement affinity does not match the native-compilation request."));
        }

        if (storageBinding.Target != reportProfile.Target
            || storageBinding.TargetProfile != reportProfile.Id
            || storageBinding.Target != CosmosRelationQueryTargetProfile.Target
            || storageBinding.TargetProfile != CosmosRelationQueryTargetProfile.ProfileId
            || !reportProfile.HasSameSemantics(CosmosRelationQueryTargetProfile.Default))
        {
            diagnostics.Add(BindingDiagnostic(
                "The Cosmos binding, realization report, and canonical Cosmos target profile do not identify the same exact target snapshot."));
        }

        var sources = sourcePlacement.SourceInstances
            .Where(source => source.Id == storageBinding.Source)
            .ToArray();
        if (sources.Length != 1)
        {
            diagnostics.Add(BindingDiagnostic(
                $"Cosmos source instance '{storageBinding.Source.Value}' is not declared exactly once by the source placement."));
        }
        else if (sources[0].TargetProfile.Target != storageBinding.Target
                 || sources[0].TargetProfile.Id != storageBinding.TargetProfile
                 || !sources[0].TargetProfile.HasSameSemantics(reportProfile))
        {
            diagnostics.Add(BindingDiagnostic(
                "The placed source capability snapshot does not match the realization report and Cosmos binding."));
        }

        var placements = sourcePlacement.Bindings
            .Where(binding => binding.Id == storageBinding.PlacementBinding)
            .ToArray();
        if (placements.Length != 1)
        {
            diagnostics.Add(BindingDiagnostic(
                $"Placement binding '{storageBinding.PlacementBinding.Value}' is not declared exactly once."));
        }
        else
        {
            var placement = placements[0];
            if (placement.Source != storageBinding.Source
                || placement.Kind != RelationQuerySourcePlacementBindingKind.SourceSet)
            {
                diagnostics.Add(BindingDiagnostic(
                    "The Cosmos storage binding must identify one source-set placement on its declared source instance."));
            }
            if (selection.Sources.Length != 1
                || selection.PlacementBindings.Length != 1
                || selection.SourceInstances.Length != 1
                || selection.Sources[0].Input.Id != placement.Input
                || selection.Sources[0].Node != placement.Node
                || selection.Sources[0].Binding != placement.Binding
                || selection.PlacementBindings[0].Id != placement.Id
                || selection.SourceInstances[0].Id != placement.Source)
            {
                diagnostics.Add(BindingDiagnostic(
                    "The selected Cosmos branches must reach exactly the one source contract identified by the storage placement."));
            }
        }

        var planFields = plan.InputContract.Sources
            .SelectMany(static source => source.Fields)
            .Select(static field => field.Input.Id)
            .ToHashSet();
        var boundFields = storageBinding.Fields.Select(static field => field.Input).ToHashSet();
        var unknownBindings = boundFields.Where(input => !planFields.Contains(input)).ToArray();
        if (unknownBindings.Length != 0)
        {
            diagnostics.Add(BindingDiagnostic(
                "The Cosmos storage binding contains field inputs absent from the exact compiled plan."));
        }

        return diagnostics.ToImmutable();

        static RelationQueryNativeCompilationDiagnostic BindingDiagnostic(string message) => new(
            CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch,
            DiagnosticSeverity.Error,
            message);
    }

    ImmutableArray<RelationQueryNativeCompilationDiagnostic> ValidateExactBinding(
        RelationQueryNativeCompilationRequest request,
        CosmosRelationQueryStorageBinding storageBinding)
    {
        var expected = request.BoundRealization.Evidence.Binding;
        var actual = CreateBindingReference(storageBinding);
        var expectedAuthority = ContextAuthority(storageBinding);
        var expectedOrigin = storageBinding.Origin == CosmosRelationQueryBindingOrigin.Convention
            ? RelationQueryConfigurationValueOrigin.AdapterConvention
            : RelationQueryConfigurationValueOrigin.Explicit;
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
        {
            return [];
        }

        return
        [
            new(
                CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch,
                DiagnosticSeverity.Error,
                "The Cosmos storage-binding fingerprint or compiler-policy evidence does not match the exact context qualified by the bound-realization report.")
        ];
    }

    string ContextAuthority(CosmosRelationQueryStorageBinding storageBinding)
    {
        var bindingAuthority = storageBinding.Origin == CosmosRelationQueryBindingOrigin.Convention
            ? storageBinding.ConventionSetVersion!
            : storageBinding.Id.Value;
        return bindingAuthority;
    }

    RelationQueryAdapterBindingReference CreateBindingReference(
        CosmosRelationQueryStorageBinding storageBinding)
    {
        var configuration = ImmutableArray.CreateBuilder<RelationQueryConfigurationDecision>(
            storageBinding.ConfigurationDecisions.Length + 2);
        configuration.AddRange(storageBinding.ConfigurationDecisions);
        configuration.Add(new(
            CompilerProfileSetting,
            optionsOrigin,
            options.CompilerProfile));
        configuration.Add(new(
            CompilerConventionSetting,
            optionsOrigin,
            options.ConventionSetVersion));
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

    ImmutableArray<RelationQueryBoundRequirementAssessment> CreateContextualAssessments(
        RelationQueryBoundRealizationRequest request,
        CosmosRelationQueryStorageBinding storageBinding,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> bindingDiagnostics) =>
        RelationQueryContextualAssessmentProjector.Project(
            request,
            "cosmos/context",
            branch => bindingDiagnostics.IsDefaultOrEmpty
                ? AnalyzeBranch(request, branch, storageBinding)
                : CreateBindingFailure(bindingDiagnostics[0]),
            (branch, requirement, failure) => ResolveAttribution(
                request,
                storageBinding,
                branch,
                requirement,
                failure));

    static RelationQueryContextualBranchFailure CreateBindingFailure(
        RelationQueryNativeCompilationDiagnostic diagnostic) => new(
        RelationQueryBoundAssessmentStatus.Invalid,
        RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
        new(diagnostic.Code),
        diagnostic.Message,
        "Re-author the Cosmos binding from the exact plan-bound placed input.",
        diagnostic.Node,
        diagnostic.Input);

    static FieldPath? ResolveAssessmentField(
        RelationQueryBoundRealizationRequest request,
        RelationQueryInputId? input)
    {
        if (input is null)
            return null;
        return request.Plan.InputContract.Requirements.Inputs
            .OfType<RelationQueryFieldInput>()
            .SingleOrDefault(candidate => candidate.Id == input.Value)?
            .Field.Path;
    }

    static RelationQuerySourcePlacementBindingId? ResolveAssessmentPlacement(
        RelationQueryBoundRealizationRequest request,
        CosmosRelationQueryStorageBinding storageBinding,
        RelationQueryInputId? input)
    {
        if (input is null
            || storageBinding.Fields.Any(field => field.Input == input.Value))
        {
            return storageBinding.PlacementBinding;
        }

        var placement = request.Placement.Bindings.Single(binding =>
            binding.Id == storageBinding.PlacementBinding);
        return placement.Input == input.Value ? placement.Id : null;
    }

    RelationQueryContextualBranchFailure? AnalyzeBranch(
        RelationQueryBoundRealizationRequest request,
        RelationQueryNativeResultBranch branch,
        CosmosRelationQueryStorageBinding storageBinding)
    {
        try
        {
            new BranchCompiler(request, storageBinding, options, branch).Validate();
            return null;
        }
        catch (BranchCompilationException exception)
        {
            var invalid = exception.Code is CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid
                or CosmosRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch;
            return CreateBranchFailure(
                request,
                branch,
                invalid ? RelationQueryBoundAssessmentStatus.Invalid : RelationQueryBoundAssessmentStatus.Unavailable,
                invalid
                    ? RelationQueryUnavailableReason.CapabilityEvidenceInvalid
                    : RelationQueryUnavailableReason.OperatingBoundaryInvalid,
                exception.Code,
                exception.Message,
                ResolutionFor(exception.Code),
                exception.Node,
                exception.Input);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or KeyNotFoundException
                                          or NotSupportedException)
        {
            return CreateBranchFailure(
                request,
                branch,
                RelationQueryBoundAssessmentStatus.Invalid,
                RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
                CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
                $"Cosmos contextual branch analysis failed closed: {exception.Message}",
                "Recompile the canonical plan and re-author the exact Cosmos binding before retrying realization.",
                branch.Node);
        }
    }

    static RelationQueryContextualBranchFailure CreateBranchFailure(
        RelationQueryBoundRealizationRequest request,
        RelationQueryNativeResultBranch branch,
        RelationQueryBoundAssessmentStatus status,
        RelationQueryUnavailableReason reason,
        string diagnosticCode,
        string message,
        string resolution,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null) => new(
        status,
        reason,
        new(diagnosticCode),
        message,
        resolution,
        node,
        input,
        failedOperatingBoundary: FailedOperatingBoundary(request, branch, diagnosticCode, message),
        failedConfigurationSetting: FailedConfigurationSetting(request, diagnosticCode, message, input));

    RelationQueryContextualAssessmentAttribution ResolveAttribution(
        RelationQueryBoundRealizationRequest request,
        CosmosRelationQueryStorageBinding storageBinding,
        RelationQueryNativeResultBranch branch,
        RelationQueryRealizationRequirement requirement,
        RelationQueryContextualBranchFailure? failure)
    {
        var node = failure?.Node ?? requirement.Origin?.Node ?? branch.Node;
        var input = failure?.Input ?? requirement.Origin?.Input;
        if (IsCompilerPolicyDecision(failure?.AdapterDecisionCode.Value))
        {
            return new(
                optionsOrigin,
                options.CompilerProfile,
                node,
                input,
                ResolveAssessmentField(request, input),
                ResolveAssessmentPlacement(request, storageBinding, input),
                CompilerProfileSetting);
        }
        if (TryResolveBindingDecision(storageBinding, requirement, failure, out var decision))
        {
            return new(
                decision.Origin,
                decision.Authority,
                node,
                input,
                ResolveAssessmentField(request, input),
                ResolveAssessmentPlacement(request, storageBinding, input),
                decision.Setting);
        }
        return new(
            storageBinding.Origin == CosmosRelationQueryBindingOrigin.Convention
                ? RelationQueryConfigurationValueOrigin.AdapterConvention
                : RelationQueryConfigurationValueOrigin.Explicit,
            ContextAuthority(storageBinding),
            node,
            input,
            ResolveAssessmentField(request, input),
            ResolveAssessmentPlacement(request, storageBinding, input));
    }

    static bool TryResolveBindingDecision(
        CosmosRelationQueryStorageBinding storageBinding,
        RelationQueryRealizationRequirement requirement,
        RelationQueryContextualBranchFailure? failure,
        out RelationQueryConfigurationDecision decision)
    {
        var input = failure?.Input ?? requirement.Origin?.Input;
        if (input is { } inputId)
        {
            var prefix = $"field/{inputId.Value}";
            decision = storageBinding.ConfigurationDecisions.FirstOrDefault(candidate =>
                string.Equals(candidate.Setting, prefix, StringComparison.Ordinal))
                ?? storageBinding.ConfigurationDecisions.FirstOrDefault(candidate =>
                    candidate.Setting.StartsWith(prefix + "/", StringComparison.Ordinal))!;
            if (decision is not null)
                return true;
        }

        var preferredSetting = failure?.AdapterDecisionCode.Value switch
        {
            CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported
                when ContainsAny(failure.Message, "maximumInputRows", "exact integer", "COUNT") =>
                "maximumInputRows",
            CosmosRelationQueryCompilationDiagnosticCodes.PagingUnstable
                when !failure.Message.Contains("Page size", StringComparison.OrdinalIgnoreCase) =>
                "exactOrderingPath/",
            CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
                when ContainsAny(failure.Message, "order", "ordering", "stable", "first-seen") =>
                "exactOrderingPath/",
            _ => null
        };
        if (preferredSetting is not null)
        {
            decision = storageBinding.ConfigurationDecisions.FirstOrDefault(candidate =>
                candidate.Setting.StartsWith(preferredSetting, StringComparison.Ordinal))!;
            if (decision is not null)
                return true;
        }

        decision = null!;
        return false;
    }

    static bool IsCompilerPolicyDecision(string? diagnosticCode) => diagnosticCode is
        CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology
        or CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator
        or CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression
        or CosmosRelationQueryCompilationDiagnosticCodes.ParameterUnsupported
        or CosmosRelationQueryCompilationDiagnosticCodes.ArtifactInvalid
        or CosmosRelationQueryCompilationDiagnosticCodes.ResultObservabilityUnsupported
        or CosmosRelationQueryCompilationDiagnosticCodes.RelationTerminalUnsupported;

    static RelationQueryOperatingBoundaryId? FailedOperatingBoundary(
        RelationQueryBoundRealizationRequest request,
        RelationQueryNativeResultBranch branch,
        string diagnosticCode,
        string message)
    {
        RelationQueryOperatingBoundaryId? candidate = diagnosticCode switch
        {
            CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology
                when ContainsAny(message, "single-source", "one demand-scoped placed source") =>
                CosmosRelationQueryTargetProfile.SingleSourceBoundary,
            CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
                when ContainsAny(message, "missing", "null", "non-null") =>
                CosmosRelationQueryTargetProfile.NonNullOperandsBoundary,
            CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
                when ContainsAny(message, "scalar", "value domain") =>
                CosmosRelationQueryTargetProfile.ScalarOperandsBoundary,
            CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
                when ContainsAny(message, "order", "ordering", "stable", "first-seen") =>
                CosmosRelationQueryTargetProfile.StableOrderingBoundary,
            CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported
                when ContainsAny(message, "maximumInputRows", "exact integer", "COUNT") =>
                CosmosRelationQueryTargetProfile.ExactCountInputRowsBoundary,
            CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported
                when ContainsAny(message, "order", "ordering", "deterministic") =>
                CosmosRelationQueryTargetProfile.StableOrderingBoundary,
            CosmosRelationQueryCompilationDiagnosticCodes.PagingUnstable
                when message.Contains("Page size", StringComparison.OrdinalIgnoreCase) =>
                CosmosRelationQueryTargetProfile.PageSizeBoundary,
            CosmosRelationQueryCompilationDiagnosticCodes.PagingUnstable =>
                CosmosRelationQueryTargetProfile.StableOrderingBoundary,
            CosmosRelationQueryCompilationDiagnosticCodes.CollectionElementEvidenceUnavailable
                when ContainsAny(message, "missing", "null", "non-null") =>
                CosmosRelationQueryTargetProfile.NonNullOperandsBoundary,
            _ => null
        };
        if (candidate is not { } boundary)
            return null;

        var decisions = request.ProfileFeasibility.Decisions.ToDictionary(static decision => decision.Requirement);
        return request.Selection.GetBranch(branch.Id).Requirements.Any(requirement =>
            decisions.TryGetValue(requirement.Id, out var decision)
            && decision.GetBoundaryValidations().Any(validation => validation.Boundary == boundary))
            ? boundary
            : null;
    }

    static string? FailedConfigurationSetting(
        RelationQueryBoundRealizationRequest request,
        string diagnosticCode,
        string message,
        RelationQueryInputId? input)
    {
        if (diagnosticCode == CosmosRelationQueryCompilationDiagnosticCodes.FieldBindingMissing
            && input is { } fieldInput)
        {
            return $"field/{fieldInput.Value}";
        }
        if (diagnosticCode == CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported
            && ContainsAny(message, "maximumInputRows", "exact integer", "COUNT"))
        {
            return "maximumInputRows";
        }
        if (diagnosticCode == CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable
            && ContainsAny(message, "exact physical ordering", "exact ordering")
            && ResolveAssessmentField(request, input) is { } exactOrderingPath)
        {
            return "exactOrderingPath/" + CosmosRelationQueryStorageBinding.FieldPathKey(exactOrderingPath);
        }
        if (diagnosticCode == CosmosRelationQueryCompilationDiagnosticCodes.CollectionElementEvidenceUnavailable
            && input is { } collectionInput)
        {
            return $"field/{collectionInput.Value}/collectionScope";
        }
        return null;
    }

    static bool ContainsAny(string value, params ReadOnlySpan<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static string ResolutionFor(string code) => code switch
    {
        CosmosRelationQueryCompilationDiagnosticCodes.FieldBindingMissing =>
            "Map every demanded input used by the selected branch in the Cosmos binding.",
        CosmosRelationQueryCompilationDiagnosticCodes.CollectionElementEvidenceUnavailable =>
            "Author exact structured-collection scope and child-field evidence for the referenced input.",
        CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable =>
            "Supply exact missing/null, ordering, value-domain, or visibility evidence for this operation.",
        CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported =>
            "Supply the required aggregate boundary evidence or select an exact target-independent aggregate strategy.",
        CosmosRelationQueryCompilationDiagnosticCodes.PagingUnstable =>
            "Supply an exact stable unique ordering strategy within the Cosmos paging boundary.",
        CosmosRelationQueryCompilationDiagnosticCodes.ResultObservabilityUnsupported =>
            "Request value-only observability or select an interpretation preserving contributor provenance.",
        CosmosRelationQueryCompilationDiagnosticCodes.RelationTerminalUnsupported =>
            "Select a query result branch or an adapter with exact relation-terminal support.",
        _ => "Select a supported canonical construct or provide an attributable exact lowering strategy."
    };

    static CollectionScopeGap? GetCollectionScopeGap(
        CosmosRelationQueryCollectionScopeEvidence scope)
    {
        if (scope.ElementScope != CosmosRelationQueryCollectionElementScope.JsonArrayElement)
        {
            return new(
                "The Cosmos collection binding does not attest JSON-array element scope.",
                "Attest JsonArrayElement scope for the structured collection.");
        }
        if (scope.CorrelationGuarantee
            != CosmosRelationQueryCollectionCorrelationGuarantee.SameArrayElement)
        {
            return new(
                "The Cosmos collection binding does not attest same-array-element correlation.",
                "Attest SameArrayElement correlation for the structured collection.");
        }
        if (scope.CollectionMissingValueBehavior
                != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion
            || scope.CollectionNullValueBehavior
                != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion)
        {
            return new(
                "The Cosmos collection binding must attest that ingestion prohibits missing and null collections; treating them as empty would weaken canonical any semantics.",
                "Prohibit missing and explicit-null collection values during ingestion.");
        }
        if (scope.NullElementBehavior
            != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion)
        {
            return new(
                "The Cosmos collection binding must attest that ingestion prohibits explicit-null collection elements.",
                "Prohibit explicit-null elements during ingestion.");
        }
        if (scope.EmptyCollectionBehavior != CosmosRelationQueryEmptyCollectionBehavior.NoElements)
        {
            return new(
                "The Cosmos collection binding does not prove that an empty JSON array contributes no existential subquery rows.",
                "Attest NoElements behavior for an empty JSON array.");
        }
        return null;
    }

    sealed record CollectionScopeGap(string Message, string Resolution);

    sealed class BranchCompiler
    {
        readonly CompiledRelationQueryPlan plan;
        readonly RelationQueryRealizationReport realization;
        readonly RelationQueryNativeCompilationRequest? nativeRequest;
        readonly CosmosRelationQueryStorageBinding storageBinding;
        readonly CosmosRelationQueryCompilerOptions options;
        readonly RelationQueryNativeResultBranch branch;
        readonly IReadOnlyDictionary<QueryNodeId, RelationQueryExecutionNode> nodes;
        readonly IReadOnlyDictionary<(ValueBindingId Binding, FieldPath Path), RelationQueryFieldInputContract> sourceFields;
        readonly IReadOnlyDictionary<QueryParameterId, RelationQueryParameterInputContract> parameters;
        readonly Dictionary<(ValueBindingId Binding, FieldPath Path), RelationQueryProjectionExecutionAssignment> projections = [];
        readonly Dictionary<(ValueBindingId Binding, FieldPath Path), RelationQueryAggregateGroupingExecution> groupings = [];
        readonly Dictionary<(ValueBindingId Binding, FieldPath Path), AggregateBinding> aggregates = [];
        readonly Dictionary<ValueBindingId, string> collectionAliases = [];
        readonly HashSet<RelationQueryInputId> selectedInputIds;
        readonly CosmosSqlBuilder builder;
        readonly ImmutableArray<RelationQueryExecutionNode> pipeline;
        AggregateQueryNode? aggregateNode;
        CosmosRelationQueryPagingContract? paging;

        public BranchCompiler(
            RelationQueryNativeCompilationRequest request,
            CosmosRelationQueryStorageBinding storageBinding,
            CosmosRelationQueryCompilerOptions options,
            RelationQueryNativeResultBranch branch)
            : this(
                request.Plan,
                request.ProfileFeasibility,
                request,
                storageBinding,
                options,
                branch)
        {
        }

        public BranchCompiler(
            RelationQueryBoundRealizationRequest request,
            CosmosRelationQueryStorageBinding storageBinding,
            CosmosRelationQueryCompilerOptions options,
            RelationQueryNativeResultBranch branch)
            : this(
                request.Plan,
                request.ProfileFeasibility,
                nativeRequest: null,
                storageBinding,
                options,
                branch)
        {
        }

        BranchCompiler(
            CompiledRelationQueryPlan plan,
            RelationQueryRealizationReport realization,
            RelationQueryNativeCompilationRequest? nativeRequest,
            CosmosRelationQueryStorageBinding storageBinding,
            CosmosRelationQueryCompilerOptions options,
            RelationQueryNativeResultBranch branch)
        {
            this.plan = plan;
            this.realization = realization;
            this.nativeRequest = nativeRequest;
            this.storageBinding = storageBinding;
            this.options = options;
            this.branch = branch;
            nodes = plan.ExecutionSlice.Nodes.ToDictionary(static node => node.Id);
            sourceFields = plan.InputContract.Sources
                .SelectMany(static source => source.Fields)
                .ToDictionary(static field => (field.Input.Binding, field.Input.Field.Path));
            parameters = plan.InputContract.Parameters.ToDictionary(static parameter => parameter.Definition.Id);
            selectedInputIds = SelectBranchFields().Select(static field => field.Input.Id).ToHashSet();
            builder = new(storageBinding.RootAlias);
            pipeline = CreatePipeline();
        }

        public CosmosRelationQueryCompiledArtifact Compile()
        {
            var request = nativeRequest ?? throw new InvalidOperationException(
                "A native-compilation request is required to construct a Cosmos artifact.");
            var prepared = Prepare();
            var provenance = CreateProvenance(request, prepared.SelectedFields);
            var fingerprint = CosmosRelationQueryArtifactFingerprinter.Compute(
                branch,
                prepared.Statement,
                storageBinding,
                prepared.SelectedFields,
                prepared.ResultFields,
                prepared.ResultIdentity,
                prepared.AuxiliaryResultAliases,
                prepared.ParameterBindings,
                paging,
                provenance);
            return new(
                branch,
                prepared.Statement,
                storageBinding,
                prepared.SelectedFields,
                prepared.ResultFields,
                prepared.ResultIdentity,
                prepared.AuxiliaryResultAliases,
                prepared.ParameterBindings,
                paging,
                provenance,
                fingerprint);
        }

        /// <summary>Runs the same complete branch analysis used by native compilation without constructing an artifact.</summary>
        public void Validate() => _ = Prepare();

        PreparedBranch Prepare()
        {
            if (branch.Kind == RelationQueryNativeResultKind.RelationRows)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.RelationTerminalUnsupported,
                    "Cosmos SQL v2 does not lower relation terminals until root correlation, cardinality, key, and invariant evidence are represented by the native artifact contract.",
                    branch.Node);
            }
            if (realization.Observability.OccurrenceProvenance
                != RelationQueryOccurrenceProvenanceMode.NotRequested)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.ResultObservabilityUnsupported,
                    "Cosmos SQL v2 compiles value results only and cannot provide exact contributor-occurrence lineage.",
                    branch.Node);
            }
            ValidatePipeline();
            ConfigureSourcePipeline();
            var (resultFields, resultIdentity, auxiliaryResultAliases) = ConfigureProjection();
            ConfigureOrderingAndPaging();
            var statement = builder.BuildTemplate();
            var parameterBindings = CreateParameterBindings(statement);
            var selectedFields = CreateSelectedFields();
            return new(
                statement,
                selectedFields,
                resultFields,
                resultIdentity,
                auxiliaryResultAliases,
                parameterBindings);
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
                    throw Fail(CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                        "The selected native branch contains a cycle.", current);
                }

                if (!nodes.TryGetValue(current, out var execution))
                {
                    throw Fail(CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                        $"Branch node '{current.Value}' is absent from the demand-scoped execution slice.", current);
                }

                reverse.Add(execution);
                if (execution.CanonicalNode is SourceQueryNode)
                {
                    break;
                }

                if (execution.LogicalPlan.EffectiveInputs.Length != 1)
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                        "Canonical Cosmos SQL v2 supports only a linear single-source branch.",
                        execution.Id);
                }
                if (execution.LogicalPlan.Inputs.Any(static input => !input.Bypasses.IsDefaultOrEmpty))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                        "A native Cosmos branch cannot rely on target-independent traversal bypasses.",
                        execution.Id);
                }
                current = execution.LogicalPlan.EffectiveInputs[0];
            }
            reverse.Reverse();
            return [.. reverse];
        }

        void ValidatePipeline()
        {
            if (pipeline[0].CanonicalNode is not SourceQueryNode source
                || !plan.InputContract.Sources.Any(contract =>
                    contract.Node == pipeline[0].Id && contract.Binding == source.Binding))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "A native Cosmos branch must begin at its one demand-scoped placed source binding.",
                    pipeline[0].Id);
            }

            var stage = PipelineStage.Source;
            var sawProjection = false;
            var sawAggregation = false;
            var sawOrder = false;
            var sawPage = false;
            foreach (var execution in pipeline.Skip(1))
            {
                switch (execution.CanonicalNode)
                {
                    case FilterQueryNode when stage <= PipelineStage.Row:
                        stage = PipelineStage.Row;
                        break;
                    case ExpandCollectionQueryNode when stage <= PipelineStage.Row:
                        stage = PipelineStage.Row;
                        break;
                    case ProjectQueryNode when !sawProjection && !sawAggregation && stage <= PipelineStage.Row:
                        sawProjection = true;
                        stage = PipelineStage.Shape;
                        break;
                    case AggregateQueryNode aggregate when !sawProjection && !sawAggregation && stage <= PipelineStage.Row:
                        sawAggregation = true;
                        aggregateNode = aggregate;
                        stage = PipelineStage.Shape;
                        break;
                    case DistinctQueryNode distinct when sawProjection && !sawAggregation && stage <= PipelineStage.Distinct:
                        if (!distinct.Keys.IsDefaultOrEmpty)
                        {
                            throw Fail(
                                CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                                "Cosmos SELECT DISTINCT is exact only for canonical whole-row distinctness; explicit distinct keys are unsupported.",
                                execution.Id);
                        }
                        stage = PipelineStage.Distinct;
                        break;
                    case OrderQueryNode when !sawOrder && !sawPage && stage <= PipelineStage.Order:
                        sawOrder = true;
                        stage = PipelineStage.Order;
                        break;
                    case PageQueryNode page when sawOrder && !sawPage && stage <= PipelineStage.Page:
                        if (page.Page is not OffsetPageDefinition)
                        {
                            throw Fail(
                                CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                                "Canonical Cosmos SQL v2 supports offset paging only.",
                                execution.Id);
                        }
                        sawPage = true;
                        stage = PipelineStage.Page;
                        break;
                    default:
                        throw Fail(
                            CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                            $"Logical node '{execution.CanonicalNode.GetType().Name}' is unsupported or appears in an inexact Cosmos pipeline position.",
                            execution.Id);
                }
            }

            if (branch.Kind == RelationQueryNativeResultKind.QueryAggregation != sawAggregation)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "The named result kind does not match the branch's aggregate topology.",
                    branch.Node);
            }
            if (branch.Kind != RelationQueryNativeResultKind.QueryAggregation && sawAggregation)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "A row terminal cannot consume an aggregate branch.",
                    branch.Node);
            }
            if (sawAggregation && (sawOrder || sawPage))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                    "Cosmos SQL v2 does not claim ordered or paged aggregate-result equivalence; grouped queries cannot combine GROUP BY and ORDER BY.",
                    branch.Node);
            }
        }

        void ConfigureSourcePipeline()
        {
            var joinIndex = 0;
            foreach (var execution in pipeline)
            {
                switch (execution.CanonicalNode)
                {
                    case SourceQueryNode:
                        break;
                    case FilterQueryNode filter:
                        builder.Where(CompileExpression(
                            filter.Predicate,
                            RequiredSite(execution, RelationQueryExpressionSiteKind.FilterPredicate),
                            requireNonNullInputs: true));
                        break;
                    case ExpandCollectionQueryNode expansion:
                        {
                            var alias = $"j{joinIndex.ToString(CultureInfo.InvariantCulture)}";
                            joinIndex++;
                            var collection = CompileExpression(
                                expansion.Collection,
                                RequiredSite(execution, RelationQueryExpressionSiteKind.ExpandCollection),
                                requireNonNullInputs: true);
                            builder.JoinCollection(alias, collection);
                            collectionAliases.Add(expansion.ItemBinding, alias);
                            break;
                        }
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
                            RequireNonNullResult(grouping.KeySite, execution.Id, "grouping");
                            RequireCosmosScalarResult(grouping.KeySite, execution.Id, "grouping");
                            builder.GroupBy(CompileExpression(
                                grouping.Definition.Key,
                                grouping.KeySite,
                                requireNonNullInputs: true));
                        }
                        foreach (var assignment in execution.AggregateAssignments)
                        {
                            aggregates.Add(
                                (aggregate.ResultBinding, assignment.Definition.Target),
                                new(assignment, aggregate.Groupings.Length != 0));
                        }
                        break;
                    case DistinctQueryNode distinct:
                        ValidateDistinct(distinct);
                        builder.Distinct();
                        break;
                }
            }
        }

        void ValidateDistinct(DistinctQueryNode distinct)
        {
            if (!distinct.Keys.IsDefaultOrEmpty)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                    "Keyed DISTINCT retains arbitrary source rows and is not equivalent to Cosmos whole-projection DISTINCT in v2.",
                    distinct.Id);
            }

            var projectionExecution = pipeline.LastOrDefault(execution =>
                execution.CanonicalNode is ProjectQueryNode project && project.Id == distinct.Input);
            if (projectionExecution?.CanonicalNode is not ProjectQueryNode projection
                || projectionExecution.ProjectionAssignments.Length != projection.Assignments.Length)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Whole-row DISTINCT requires one complete projected binding in Cosmos SQL v2.",
                    distinct.Id);
            }

            foreach (var assignment in projectionExecution.ProjectionAssignments)
            {
                if (!IsRequiredNonNull(assignment.ValueSite.Analysis.KnownResult)
                    || !IsCosmosEqualityScalar(
                        assignment.ValueSite.Analysis.KnownResult?.GetEffectiveType()))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        $"DISTINCT assignment '{assignment.Definition.Id.Value}' does not have a required, non-null exact scalar equality domain.",
                        distinct.Id);
                }
            }
        }

        (ImmutableArray<CosmosRelationQueryResultFieldBinding> Fields,
            CosmosRelationQueryResultIdentityBinding? Identity,
            ImmutableArray<string> AuxiliaryAliases) ConfigureProjection()
        {
            ImmutableArray<CosmosRelationQueryResultFieldBinding>.Builder resultFields =
                ImmutableArray.CreateBuilder<CosmosRelationQueryResultFieldBinding>(branch.Fields.Length);
            ImmutableArray<string>.Builder auxiliaryAliases = ImmutableArray.CreateBuilder<string>();
            CosmosRelationQueryResultIdentityBinding? identity = null;
            if (branch.Kind == RelationQueryNativeResultKind.RelationRows
                && plan.ExecutionSlice.RelationOutput is { } relation
                && relation.KeySite is { } keySite)
            {
                RequireNonNullResult(
                    keySite,
                    keySite.Node ?? branch.Node,
                    "relation identity");
                var identityContract = RequireKnownResultContract(
                    keySite,
                    keySite.Node ?? branch.Node,
                    "relation identity");
                var identityEncoding = ResolveResultEncoding(
                    identityContract,
                    keySite.Node ?? branch.Node);
                builder.Select(
                    CompileExpression(
                        relation.Definition.Key!,
                        keySite,
                        requireNonNullInputs: true),
                    "__identity");
                identity = new(
                    "__identity",
                    valueContract: identityContract,
                    encoding: identityEncoding);
            }

            for (var index = 0; index < branch.Fields.Length; index++)
            {
                var field = branch.Fields[index];
                var alias = $"f{index.ToString(CultureInfo.InvariantCulture)}";
                var resolved = ResolveOutput(field.Path);
                builder.Select(resolved.Expression, alias);
                resultFields.Add(new(
                    alias,
                    field,
                    resolved.ValueContract,
                    resolved.Encoding,
                    resolved.Assignment));
            }

            if (pipeline.Any(static execution => execution.CanonicalNode is DistinctQueryNode))
            {
                var visiblePaths = branch.Fields.Select(static field => field.Path).ToHashSet();
                var hiddenIndex = 0;
                foreach (var projection in projections
                             .Where(item => item.Key.Binding == branch.Binding
                                            && !visiblePaths.Contains(item.Key.Path))
                             .OrderBy(item => CosmosRelationQueryStorageBinding.FieldPathKey(item.Key.Path), StringComparer.Ordinal))
                {
                    var alias = $"__distinct{hiddenIndex.ToString(CultureInfo.InvariantCulture)}";
                    builder.Select(
                        CompileExpression(
                            projection.Value.Definition.Value,
                            projection.Value.ValueSite,
                            requireNonNullInputs: false),
                        alias);
                    auxiliaryAliases.Add(alias);
                    hiddenIndex++;
                }
            }
            return (resultFields.MoveToImmutable(), identity, auxiliaryAliases.ToImmutable());
        }

        ResolvedOutput ResolveOutput(FieldPath path)
        {
            if (projections.TryGetValue((branch.Binding, path), out var projection))
            {
                var valueContract = RequireKnownResultContract(
                    projection.ValueSite,
                    projection.ValueSite.Node ?? branch.Node,
                    "projection result");
                return new(
                    CompileExpression(
                        projection.Definition.Value,
                        projection.ValueSite,
                        requireNonNullInputs: false),
                    valueContract,
                    ResolveResultEncoding(valueContract, projection.ValueSite.Node ?? branch.Node),
                    projection.Definition.Id);
            }
            if (groupings.TryGetValue((branch.Binding, path), out var grouping))
            {
                var valueContract = RequireKnownResultContract(
                    grouping.KeySite,
                    grouping.KeySite.Node ?? branch.Node,
                    "grouping result");
                return new(
                    CompileExpression(
                        grouping.Definition.Key,
                        grouping.KeySite,
                        requireNonNullInputs: true),
                    valueContract,
                    ResolveResultEncoding(valueContract, grouping.KeySite.Node ?? branch.Node),
                    grouping.Definition.Id);
            }
            if (aggregates.TryGetValue((branch.Binding, path), out var aggregate))
            {
                return ResolveAggregateOutput(aggregate);
            }
            if (sourceFields.TryGetValue((branch.Binding, path), out var sourceField))
            {
                var valueContract = sourceField.Input.ValueContract
                    ?? throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        $"Source result field '{path}' has no resolved semantic value contract.",
                        branch.Node,
                        sourceField.Input.Id);
                return new(
                    CompileSourceField(sourceField, requireNonNull: false),
                    valueContract,
                    ResolveResultEncoding(valueContract, branch.Node),
                    Assignment: null);
            }
            throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                $"Demanded output field '{path}' has no demand-scoped producing assignment or source binding.",
                branch.Node);
        }

        ResolvedOutput ResolveAggregateOutput(AggregateBinding aggregate)
        {
            var definition = aggregate.Execution.Definition;
            if (definition.Operation == AggregateOperator.Count && definition.Value is null)
            {
                return new(
                    CompileAggregate(aggregate),
                    new ExprValueContract(new ScalarTypeRef(ScalarTypeKind.Int64)),
                    CosmosRelationQueryResultValueEncoding.ExactCountInteger,
                    definition.Id);
            }

            var valueSite = aggregate.Execution.ValueSite;
            if (valueSite is null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    $"Aggregate '{definition.Operation}' has no value contract for result decoding.",
                    branch.Node);
            }
            var valueContract = RequireKnownResultContract(
                valueSite,
                valueSite.Node ?? branch.Node,
                "aggregate result");
            return new(
                CompileAggregate(aggregate),
                valueContract,
                ResolveResultEncoding(valueContract, valueSite.Node ?? branch.Node),
                definition.Id);
        }

        CosmosSqlExpression CompileAggregate(AggregateBinding aggregate)
        {
            var definition = aggregate.Execution.Definition;
            if (definition.Filter is not null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Per-aggregate filters are not compiled until empty-input and undefined semantics can be proven exactly.",
                    branch.Node);
            }
            return definition.Operation switch
            {
                AggregateOperator.Count when definition.Value is null =>
                    CompileRowCount(),
                AggregateOperator.Count => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Value-count semantics are not equivalent to Cosmos COUNT for missing and null values in v2.",
                    branch.Node),
                AggregateOperator.Sum => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Canonical decimal SUM accumulation is not equivalent to Cosmos binary-number aggregation in v2.",
                    branch.Node),
                AggregateOperator.Min or AggregateOperator.Max when !aggregate.Grouped => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    $"Ungrouped '{definition.Operation}' is unsupported because Cosmos empty-input semantics are not exact.",
                    branch.Node),
                AggregateOperator.Min or AggregateOperator.Max => CompileNumericMinimumOrMaximum(aggregate),
                _ => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    $"Aggregate operation '{definition.Operation}' is not in the exact Cosmos SQL v2 closure.",
                    branch.Node)
            };
        }

        CosmosSqlExpression CompileRowCount()
        {
            if (storageBinding.MaximumInputRows is not { } maximum
                || maximum > CosmosRelationQueryTargetProfile.MaximumExactInteger)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Row COUNT requires a storage-binding maximumInputRows proof inside Cosmos's exact integer range.",
                    branch.Node);
            }
            return CosmosSqlExpression.Aggregate(CosmosSqlAggregateFunction.Count);
        }

        CosmosSqlExpression CompileNumericMinimumOrMaximum(AggregateBinding aggregate)
        {
            var valueSite = aggregate.Execution.ValueSite!;
            RequireNonNullResult(valueSite, valueSite.Node ?? branch.Node, "aggregate");
            if (!IsExactNumericScalar(valueSite.Analysis.KnownResult?.GetEffectiveType()))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    $"Cosmos SQL v2 requires a known Int32 value for exact '{aggregate.Execution.Definition.Operation}' semantics.",
                    valueSite.Node ?? branch.Node);
            }

            return CosmosSqlExpression.Aggregate(
                aggregate.Execution.Definition.Operation == AggregateOperator.Min
                    ? CosmosSqlAggregateFunction.Minimum
                    : CosmosSqlAggregateFunction.Maximum,
                CompileExpression(
                    aggregate.Execution.Definition.Value!,
                    valueSite,
                    requireNonNullInputs: true));
        }

        void ConfigureOrderingAndPaging()
        {
            var groupedAggregate = pipeline
                .Select(static execution => execution.CanonicalNode)
                .OfType<AggregateQueryNode>()
                .SingleOrDefault(static aggregate => !aggregate.Groupings.IsDefaultOrEmpty);
            if (groupedAggregate is not null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Grouped Cosmos results have no exact deterministic order until the artifact retains and executes an attributable grouping-order strategy.",
                    groupedAggregate.Id);
            }

            if (branch.Kind == RelationQueryNativeResultKind.QueryRows
                && pipeline.Any(static execution => execution.CanonicalNode is ExpandCollectionQueryNode))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Expanded Cosmos rows have no exact deterministic order until collection-element ordering evidence is represented.",
                    branch.Node);
            }

            RelationQueryExecutionNode? orderExecution = null;
            FieldPath? stableOrderingPath = null;
            foreach (var execution in pipeline)
            {
                if (execution.CanonicalNode is not OrderQueryNode order)
                {
                    continue;
                }

                orderExecution = execution;
                for (var index = 0; index < order.Orderings.Length; index++)
                {
                    var ordering = order.Orderings[index];
                    var site = execution.OrderKeys.Single(candidate => candidate.Ordinal == index);
                    RequireNonNullResult(site, execution.Id, "ordering");
                    RequireCosmosOrderableResult(
                        site,
                        execution.Id,
                        TryResolveStableSourcePath(ordering.Key, site));
                    builder.OrderBy(
                        CompileExpression(ordering.Key, site, requireNonNullInputs: true),
                        ordering.Direction == QuerySortDirection.Descending
                            ? CosmosSqlSortDirection.Descending
                            : CosmosSqlSortDirection.Ascending);
                }

                stableOrderingPath = TryResolveStableSourcePath(
                    order.Orderings[^1].Key,
                    execution.OrderKeys.Single(candidate => candidate.Ordinal == order.Orderings.Length - 1));
                if (stableOrderingPath is null
                    || stableOrderingPath.Value != storageBinding.IdentityPath
                    && !storageBinding.StableUniqueOrderingPaths.Contains(stableOrderingPath.Value))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        "Canonical ordering requires a final stable unique source path because input-order tie breaking is not available in Cosmos SQL.",
                        execution.Id);
                }
            }

            if (branch.Kind == RelationQueryNativeResultKind.QueryRows && orderExecution is null)
            {
                if (pipeline.Any(static execution => execution.CanonicalNode is DistinctQueryNode))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        "Unordered Cosmos DISTINCT cannot reproduce canonical first-seen row order.",
                        branch.Node);
                }
                if (!storageBinding.ExactOrderingPaths.Contains(storageBinding.IdentityPath))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        "Implicit deterministic Cosmos row order requires exact physical ordering evidence for the identity path.",
                        branch.Node);
                }

                builder.OrderBy(
                    CosmosSqlExpression.Property(
                        storageBinding.RootAlias,
                        FullDocumentPath(storageBinding.IdentityPath)),
                    CosmosSqlSortDirection.Ascending);
                stableOrderingPath = storageBinding.IdentityPath;
            }

            var pageExecution = pipeline.SingleOrDefault(static execution => execution.CanonicalNode is PageQueryNode);
            if (pageExecution is null)
            {
                return;
            }

            var page = (OffsetPageDefinition)((PageQueryNode)pageExecution.CanonicalNode).Page;
            if (page.Limit > CosmosRelationQueryTargetProfile.MaximumPageSize)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    $"Page size {page.Limit} exceeds the Cosmos v2 boundary of {CosmosRelationQueryTargetProfile.MaximumPageSize}.",
                    pageExecution.Id);
            }
            if (orderExecution?.CanonicalNode is not OrderQueryNode ordered)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Offset paging requires a preceding deterministic order node.",
                    pageExecution.Id);
            }
            if (stableOrderingPath is null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "The preceding order node has no stable unique path for offset paging.",
                    pageExecution.Id);
            }

            builder.OffsetLimit(page.Offset, page.Limit);
            paging = new(page.Offset, page.Limit, stableOrderingPath.Value);
        }

        FieldPath? TryResolveStableSourcePath(Expr expression, RelationQueryExpressionSiteAnalysis? site)
        {
            if (expression is not FieldExpr field)
            {
                return null;
            }

            var resolved = ResolveFieldRoot(field.Path, field.Binding, site);
            if (resolved.Root != FieldRoot.Binding)
            {
                return null;
            }

            if (projections.TryGetValue((resolved.Binding!.Value, resolved.Path), out var projection))
            {
                return TryResolveStableSourcePath(projection.Definition.Value, projection.ValueSite);
            }

            if (groupings.ContainsKey((resolved.Binding!.Value, resolved.Path))
                || aggregates.ContainsKey((resolved.Binding!.Value, resolved.Path)))
            {
                return null;
            }
            return sourceFields.TryGetValue((resolved.Binding!.Value, resolved.Path), out var input)
                ? storageBinding.ResolveField(input.Input.Id)
                : null;
        }

        CosmosSqlExpression CompileExpression(
            Expr expression,
            RelationQueryExpressionSiteAnalysis? site,
            bool requireNonNullInputs)
        {
            return expression switch
            {
                FieldExpr field => CompileField(
                    ResolveFieldRoot(field.Path, field.Binding, site),
                    site,
                    requireNonNullInputs,
                    field),
                FieldRefExpr field => CompileField(
                    ResolveFieldRoot(field.Path, explicitBinding: null, site),
                    site,
                    requireNonNullInputs,
                    field),
                CurrentItemExpr => CompileCurrentItem(site, requireNonNullInputs),
                ParameterExpr parameter => CompileParameter(parameter, requireNonNullInputs),
                ConstantExpr constant => CompileConstant(
                    constant.Value,
                    InferCanonicalConstantType(constant.Value),
                    requireNonNullInputs,
                    site?.Node),
                LiteralExpr literal => CompileConstant(
                    literal.Value,
                    literal.Type,
                    requireNonNullInputs,
                    site?.Node),
                UnaryExpr unary when unary.Operator == UnaryOperator.Not => CosmosSqlExpression.Unary(
                    CosmosSqlUnaryOperator.Not,
                    CompileExpression(unary.Operand, site, requireNonNullInputs: true)),
                BinaryExpr binary => CompileBinary(binary, site),
                ConditionalExpr conditional => CosmosSqlExpression.Conditional(
                    CompileExpression(conditional.Test, site, requireNonNullInputs: true),
                    CompileExpression(conditional.IfTrue, site, requireNonNullInputs),
                    CompileExpression(conditional.IfFalse, site, requireNonNullInputs)),
                CallExpr call when string.Equals(call.Function, ExprFunctionNames.Contains, StringComparison.Ordinal)
                                   && call.Arguments.Length == 2 => CompileContains(call, site),
                CallExpr call when string.Equals(call.Function, ExprFunctionNames.Any, StringComparison.Ordinal)
                                   && call.Arguments.Length == 2 => CompileCollectionAny(call, site),
                CallExpr call => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Function '{call.Function}' is not in the exact canonical Cosmos SQL v2 expression closure.",
                    site?.Node ?? branch.Node),
                AggregateExpr => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Embedded aggregate expressions are unsupported; use a canonical aggregate node.",
                    site?.Node ?? branch.Node),
                _ => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Expression node '{expression.GetType().Name}' is not in the exact canonical Cosmos SQL v2 closure.",
                    site?.Node ?? branch.Node)
            };
        }

        static CosmosSqlExpression CompileConstant(
            ObservationValue value,
            TypeRef? declaredType,
            bool requireNonNull,
            QueryNodeId? node)
        {
            if (requireNonNull && value.Kind == ObservationValueKind.Null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "A null constant is not valid where exact Cosmos scalar semantics require a value.",
                    node);
            }
            if (!IsCosmosConstantValue(value))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Constant value kind '{value.Kind}' has no exact Cosmos SQL v2 parameter encoding.",
                    node);
            }
            if (value.Kind != ObservationValueKind.Null
                && declaredType is not null
                && CosmosRelationQueryCanonicalValueCodec.SupportsRuntimeParameterType(declaredType)
                && !CosmosRelationQueryCanonicalValueCodec.TryEncodeRuntimeParameter(
                    new ExprValueContract(declaredType),
                    value,
                    out _))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "A typed constant must already use the exact canonical representation retained by Cosmos execution.",
                    node);
            }
            return CosmosSqlExpression.Parameter(value);
        }

        static TypeRef? InferCanonicalConstantType(ObservationValue value) => value.Kind switch
        {
            ObservationValueKind.DateOnly => new ScalarTypeRef(ScalarTypeKind.Date),
            ObservationValueKind.DateTimeOffset => new ScalarTypeRef(ScalarTypeKind.Instant),
            _ => null
        };

        static bool IsCosmosConstantValue(ObservationValue value) => value.Kind switch
        {
            ObservationValueKind.Null
                or ObservationValueKind.Bool
                or ObservationValueKind.String
                or ObservationValueKind.DateTimeOffset
                or ObservationValueKind.DateOnly
                or ObservationValueKind.TimeOnly
                or ObservationValueKind.TimeSpan => true,
            ObservationValueKind.Int64 => value.Int64 is >= -9_007_199_254_740_991L and <= 9_007_199_254_740_991L,
            ObservationValueKind.Double => double.IsFinite(value.Double),
            ObservationValueKind.Array => (value.Array.IsDefault ? [] : value.Array).All(IsCosmosConstantValue),
            ObservationValueKind.Object => (value.Fields?.Values ?? []).All(IsCosmosConstantValue),
            _ => false
        };

        CosmosSqlExpression CompileBinary(
            BinaryExpr binary,
            RelationQueryExpressionSiteAnalysis? site)
        {
            if (site is null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Binary operator '{binary.Operator}' has no analyzed canonical expression site.",
                    branch.Node);
            }

            var left = AnalyzeSubexpression(binary.Left, site, "left");
            var right = AnalyzeSubexpression(binary.Right, site, "right");
            var supported = binary.Operator switch
            {
                BinaryOperator.And or BinaryOperator.Or =>
                    IsBooleanScalar(left.GetEffectiveType()) && IsBooleanScalar(right.GetEffectiveType()),
                BinaryOperator.Eq or BinaryOperator.Ne =>
                    AreCosmosEqualityComparable(left.GetEffectiveType(), right.GetEffectiveType()),
                BinaryOperator.Gt or BinaryOperator.Ge or BinaryOperator.Lt or BinaryOperator.Le =>
                    AreCosmosOrderComparable(left.GetEffectiveType(), right.GetEffectiveType()),
                _ => false
            };
            if (!supported)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Binary operator '{binary.Operator}' does not have a proven exact Cosmos JSON value domain in v2.",
                    site.Node ?? branch.Node);
            }

            var leftExpression = CompileExpression(binary.Left, site, requireNonNullInputs: true);
            var rightExpression = CompileExpression(binary.Right, site, requireNonNullInputs: true);
            if (!IsRequiredNonNull(left) || !IsRequiredNonNull(right))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Binary operator '{binary.Operator}' has a missing or null operand whose Cosmos semantics are not proven exact.",
                    site.Node ?? branch.Node);
            }

            return CosmosSqlExpression.Binary(
                Convert(binary.Operator),
                leftExpression,
                rightExpression);
        }

        CosmosSqlExpression CompileContains(
            CallExpr call,
            RelationQueryExpressionSiteAnalysis? site)
        {
            if (site is null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "The contains function has no analyzed canonical expression site.",
                    branch.Node);
            }

            var collection = AnalyzeSubexpression(call.Arguments[0], site, "contains-source");
            var candidate = AnalyzeSubexpression(call.Arguments[1], site, "contains-candidate");
            if (collection.GetEffectiveType() is not ArrayTypeRef array
                || !AreCosmosEqualityComparable(array.ElementType, candidate.GetEffectiveType()))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "The contains function requires an array and candidate with one proven exact scalar equality domain.",
                    site.Node ?? branch.Node);
            }

            var sourceExpression = CompileExpression(
                call.Arguments[0],
                site,
                requireNonNullInputs: true);
            var candidateExpression = CompileExpression(
                call.Arguments[1],
                site,
                requireNonNullInputs: true);
            if (!IsRequiredNonNull(collection) || !IsRequiredNonNull(candidate))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "The contains function has a missing or null operand whose Cosmos semantics are not proven exact.",
                    site.Node ?? branch.Node);
            }

            return CosmosSqlExpression.Function(
                CosmosSqlFunction.ArrayContains,
                sourceExpression,
                candidateExpression);
        }

        CosmosSqlExpression CompileCollectionAny(
            CallExpr call,
            RelationQueryExpressionSiteAnalysis? site)
        {
            if (site is null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "The any function has no analyzed canonical expression site.",
                    branch.Node);
            }

            var node = site.Node ?? branch.Node;
            var collectionContract = AnalyzeSubexpression(
                call.Arguments[0],
                site,
                "collection-any-collection");
            if (!IsRequiredNonNull(collectionContract))
            {
                throw CollectionEvidenceFailure(
                    "Canonical any requires a required, non-null collection; Cosmos SQL cannot silently treat a missing or null collection as empty.",
                    node);
            }

            var elementContract = GetCollectionElementContract(collectionContract);
            if (elementContract is null
                || elementContract.GetEffectiveType() is not ObjectTypeRef
                    && elementContract.ShapeDefinition is null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Canonical any requires a structured object collection in the Cosmos SQL v2 closure.",
                    node);
            }

            if (!TryResolveSourceField(call.Arguments[0], site, out var collectionField))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Canonical any requires one direct physical structured collection field in Cosmos SQL v2.",
                    node);
            }

            CosmosRelationQueryFieldBinding physical;
            try
            {
                physical = storageBinding.ResolveFieldBinding(collectionField.Input.Id);
            }
            catch (KeyNotFoundException)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Compiled collection input '{collectionField.Input.Id.Value}' has no Cosmos document selector.",
                    node,
                    collectionField.Input.Id);
            }

            var evidence = physical.CollectionScope;
            if (evidence is null)
            {
                throw CollectionEvidenceFailure(
                    "The structured collection binding does not provide explicit JSON-array element scope, child-field, absence, and correlation evidence.",
                    node,
                    collectionField.Input.Id);
            }
            RequireExactCollectionScope(evidence, collectionField, node);

            var predicateScope = site.Analysis.Site.Scope.WithCurrentItem(elementContract);
            var predicateAnalysis = ExprAnalyzer.Analyze(
                new ExprSite(
                    new($"{site.Analysis.Site.Id.Value}/cosmos/collection-any-predicate"),
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
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "The canonical any predicate does not have one valid, required, non-null Boolean contract in its collection-element scope.",
                    node,
                    collectionField.Input.Id);
            }

            var collection = CosmosSqlExpression.Property(
                storageBinding.RootAlias,
                FullDocumentPath(physical.DocumentPath));
            return CosmosSqlExpression.CollectionExists(
                collection,
                item => CompileCollectionPredicate(
                    call.Arguments[1],
                    site,
                    predicateScope,
                    collectionField,
                    evidence,
                    item));
        }

        CosmosSqlExpression CompileCollectionPredicate(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            ExprScope predicateScope,
            RelationQueryFieldInputContract collectionField,
            CosmosRelationQueryCollectionScopeEvidence evidence,
            CosmosSqlExpression item)
        {
            return expression switch
            {
                BinaryExpr { Operator: BinaryOperator.And or BinaryOperator.Or } binary =>
                    CosmosSqlExpression.Binary(
                        Convert(binary.Operator),
                        CompileCollectionPredicate(
                            binary.Left,
                            site,
                            predicateScope,
                            collectionField,
                            evidence,
                            item),
                        CompileCollectionPredicate(
                            binary.Right,
                            site,
                            predicateScope,
                            collectionField,
                            evidence,
                            item)),
                UnaryExpr { Operator: UnaryOperator.Not } unary => CosmosSqlExpression.Unary(
                    CosmosSqlUnaryOperator.Not,
                    CompileCollectionPredicate(
                        unary.Operand,
                        site,
                        predicateScope,
                        collectionField,
                        evidence,
                        item)),
                BinaryExpr { Operator: BinaryOperator.Eq or BinaryOperator.Ne } comparison =>
                    CompileCollectionComparison(
                        comparison,
                        site,
                        predicateScope,
                        collectionField,
                        evidence,
                        item),
                _ => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Expression node '{expression.GetType().Name}' is outside the direct-child Cosmos SQL v2 collection predicate closure.",
                    site.Node ?? branch.Node,
                    collectionField.Input.Id)
            };
        }

        CosmosSqlExpression CompileCollectionComparison(
            BinaryExpr binary,
            RelationQueryExpressionSiteAnalysis site,
            ExprScope predicateScope,
            RelationQueryFieldInputContract collectionField,
            CosmosRelationQueryCollectionScopeEvidence evidence,
            CosmosSqlExpression item)
        {
            var node = site.Node ?? branch.Node;
            FieldPath elementPath;
            Expr childExpression;
            Expr valueExpression;
            if (TryResolveCurrentItemChildPath(binary.Left, out var leftPath))
            {
                elementPath = leftPath;
                childExpression = binary.Left;
                valueExpression = binary.Right;
            }
            else if (TryResolveCurrentItemChildPath(binary.Right, out var rightPath))
            {
                elementPath = rightPath;
                childExpression = binary.Right;
                valueExpression = binary.Left;
            }
            else
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "A Cosmos collection-element comparison requires one direct current-element child field and one constant or parameter.",
                    node,
                    collectionField.Input.Id);
            }

            var childContract = AnalyzeSubexpression(
                childExpression,
                site,
                "collection-any-child",
                predicateScope);
            var valueContract = AnalyzeCollectionComparisonValue(
                valueExpression,
                site,
                predicateScope,
                childContract,
                node,
                collectionField.Input.Id);
            if (!IsRequiredNonNull(childContract) || !IsRequiredNonNull(valueContract))
            {
                throw CollectionEvidenceFailure(
                    "A collection-element comparison requires canonical child and value operands to be required and non-null.",
                    node,
                    collectionField.Input.Id);
            }

            var valueType = childContract.GetEffectiveType();
            if (valueType != valueContract.GetEffectiveType() || !IsCosmosEqualityScalar(valueType))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "A collection-element comparison requires operands in one exact Cosmos scalar equality domain.",
                    node,
                    collectionField.Input.Id);
            }

            CosmosRelationQueryCollectionElementFieldBinding child;
            try
            {
                child = evidence.ResolveChild(elementPath);
            }
            catch (KeyNotFoundException)
            {
                throw CollectionEvidenceFailure(
                    $"The collection binding has no direct child mapping for current-element path '{elementPath}'.",
                    node,
                    collectionField.Input.Id);
            }

            if (child.MissingValueBehavior
                    != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion
                || child.NullValueBehavior
                    != CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion)
            {
                throw CollectionEvidenceFailure(
                    $"Collection child path '{elementPath}' does not prove that ingestion prohibits missing and null values.",
                    node,
                    collectionField.Input.Id);
            }

            var requiredCapability = binary.Operator == BinaryOperator.Eq
                ? CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
                : CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality;
            if (!child.SemanticCapabilities.HasFlag(requiredCapability))
            {
                throw CollectionEvidenceFailure(
                    $"Collection child path '{elementPath}' does not attest '{requiredCapability}'.",
                    node,
                    collectionField.Input.Id);
            }
            RequireCollectionValueDomain(child, valueType, node, collectionField.Input.Id);

            return CosmosSqlExpression.Binary(
                Convert(binary.Operator),
                CosmosSqlExpression.Property(item, child.DocumentPath),
                CompileCollectionComparisonValue(valueExpression, valueType, node));
        }

        static ExprValueContract AnalyzeCollectionComparisonValue(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            ExprScope predicateScope,
            ExprValueContract childContract,
            QueryNodeId node,
            RelationQueryInputId input)
        {
            if (expression is ConstantExpr constant)
            {
                if (!childContract.IsSatisfiedByConstant(constant.Value))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                        "The collection-element constant does not satisfy the compared child's canonical value contract.",
                        node,
                        input);
                }

                return childContract;
            }

            if (expression is ParameterExpr or LiteralExpr)
            {
                return AnalyzeSubexpression(
                    expression,
                    site,
                    "collection-any-value",
                    predicateScope);
            }

            throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                "A Cosmos collection-element comparison value must be a constant or invocation parameter.",
                node,
                input);
        }

        CosmosSqlExpression CompileCollectionComparisonValue(
            Expr expression,
            TypeRef? expectedType,
            QueryNodeId node) => expression switch
        {
            ParameterExpr parameter => CompileParameter(parameter, requireNonNull: true),
            ConstantExpr constant => CompileConstant(
                constant.Value,
                expectedType,
                requireNonNull: true,
                node),
            LiteralExpr literal => CompileConstant(
                literal.Value,
                literal.Type,
                requireNonNull: true,
                node),
            _ => throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                "A Cosmos collection-element comparison value must be a constant or invocation parameter.",
                node)
        };

        static ExprValueContract AnalyzeSubexpression(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            string operand,
            ExprScope? scope = null)
        {
            var parent = site.Analysis.Site;
            var analysis = ExprAnalyzer.Analyze(
                new ExprSite(
                    new($"{parent.Id.Value}/cosmos/{operand}"),
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
                CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"The {operand} operand does not have one valid, known value contract for exact Cosmos lowering.",
                    site.Node);
        }

        bool TryResolveSourceField(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            out RelationQueryFieldInputContract field)
        {
            FieldPath path;
            ValueBindingId? explicitBinding;
            switch (expression)
            {
                case FieldExpr fieldExpression:
                    path = fieldExpression.Path;
                    explicitBinding = fieldExpression.Binding;
                    break;
                case FieldRefExpr fieldReference:
                    path = fieldReference.Path;
                    explicitBinding = null;
                    break;
                default:
                    field = default!;
                    return false;
            }

            var resolved = ResolveFieldRoot(path, explicitBinding, site);
            if (resolved.Root != FieldRoot.Binding
                || resolved.Binding is not { } binding
                || !sourceFields.TryGetValue((binding, resolved.Path), out field!))
            {
                field = default!;
                return false;
            }
            return true;
        }

        static void RequireExactCollectionScope(
            CosmosRelationQueryCollectionScopeEvidence evidence,
            RelationQueryFieldInputContract field,
            QueryNodeId node)
        {
            if (GetCollectionScopeGap(evidence) is { } gap)
                throw CollectionEvidenceFailure(gap.Message, node, field.Input.Id);
        }

        static void RequireCollectionValueDomain(
            CosmosRelationQueryCollectionElementFieldBinding child,
            TypeRef? type,
            QueryNodeId node,
            RelationQueryInputId input)
        {
            var expected = type switch
            {
                ScalarTypeRef { Kind: ScalarTypeKind.Bool } =>
                    CosmosRelationQueryCollectionElementValueDomain.Bool,
                ScalarTypeRef { Kind: ScalarTypeKind.Int32 } =>
                    CosmosRelationQueryCollectionElementValueDomain.Int32,
                ScalarTypeRef { Kind: ScalarTypeKind.String } =>
                    CosmosRelationQueryCollectionElementValueDomain.String,
                ScalarTypeRef { Kind: ScalarTypeKind.Guid } =>
                    CosmosRelationQueryCollectionElementValueDomain.Guid,
                ScalarTypeRef { Kind: ScalarTypeKind.Date } =>
                    CosmosRelationQueryCollectionElementValueDomain.Date,
                _ => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "The current collection child is outside the exact Cosmos SQL v2 scalar equality domains.",
                    node,
                    input)
            };
            if (child.ValueDomain != expected)
            {
                throw CollectionEvidenceFailure(
                    $"Collection child path '{child.ElementPath}' attests value domain '{child.ValueDomain}' rather than required canonical domain '{expected}'.",
                    node,
                    input);
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

        static ExprValueContract? GetCollectionElementContract(ExprValueContract collection)
        {
            if (collection.Cardinality == FieldCardinality.Many)
            {
                return new(
                    collection.Type,
                    collection.Shape,
                    shapeDefinition: collection.ShapeDefinition);
            }
            return collection.GetEffectiveType() is ArrayTypeRef array
                ? new(
                    array.ElementType,
                    collection.Shape,
                    shapeDefinition: collection.ShapeDefinition)
                : null;
        }

        CosmosSqlExpression CompileField(
            ResolvedFieldRoot resolved,
            RelationQueryExpressionSiteAnalysis? site,
            bool requireNonNullInputs,
            Expr sourceExpression)
        {
            if (resolved.Root == FieldRoot.CurrentItem)
            {
                if (requireNonNullInputs
                    && (site is null
                        || !IsRequiredNonNull(AnalyzeSubexpression(
                            sourceExpression,
                            site,
                            "current-item-field"))))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        $"Current-item field '{resolved.Path}' may be missing or null where exact Cosmos scalar semantics require a value.",
                        site?.Node ?? branch.Node);
                }
                if (resolved.Binding is not { } currentBinding
                    || !collectionAliases.TryGetValue(currentBinding, out var alias))
                {
                    if (collectionAliases.Count != 1)
                    {
                        throw Fail(
                            CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                            "Current-item field access is ambiguous outside one collection expansion.",
                            site?.Node ?? branch.Node);
                    }
                    alias = collectionAliases.Values.Single();
                }
                return resolved.Path.Segments.IsDefaultOrEmpty
                    ? CosmosSqlExpression.Alias(alias)
                    : CosmosSqlExpression.Property(alias, resolved.Path);
            }

            var binding = resolved.Binding!.Value;
            if (collectionAliases.TryGetValue(binding, out var collectionAlias))
            {
                if (requireNonNullInputs
                    && (site is null
                        || !IsRequiredNonNull(AnalyzeSubexpression(
                            sourceExpression,
                            site,
                            "expanded-item-field"))))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        $"Expanded-item field '{resolved.Path}' may be missing or null where exact Cosmos scalar semantics require a value.",
                        site?.Node ?? branch.Node);
                }
                return resolved.Path.Segments.IsDefaultOrEmpty
                    ? CosmosSqlExpression.Alias(collectionAlias)
                    : CosmosSqlExpression.Property(collectionAlias, resolved.Path);
            }
            if (projections.TryGetValue((binding, resolved.Path), out var projection))
            {
                return CompileExpression(
                    projection.Definition.Value,
                    projection.ValueSite,
                    requireNonNullInputs);
            }
            if (groupings.TryGetValue((binding, resolved.Path), out var grouping))
            {
                return CompileExpression(
                    grouping.Definition.Key,
                    grouping.KeySite,
                    requireNonNullInputs: true);
            }
            if (aggregates.TryGetValue((binding, resolved.Path), out var aggregate))
            {
                return CompileAggregate(aggregate);
            }

            if (!sourceFields.TryGetValue((binding, resolved.Path), out var sourceField))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Field '{binding.Value}:{resolved.Path}' has no exact compiled source-field contract.",
                    site?.Node ?? branch.Node);
            }
            return CompileSourceField(sourceField, requireNonNullInputs);
        }

        CosmosSqlExpression CompileSourceField(
            RelationQueryFieldInputContract sourceField,
            bool requireNonNull)
        {
            if (requireNonNull && !IsRequiredNonNull(sourceField.Input.ValueContract))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Field input '{sourceField.Input.Id.Value}' may be missing or null, so Cosmos expression semantics are not proven exact.",
                    sourceField.Input.Producer,
                    sourceField.Input.Id);
            }
            FieldPath documentPath;
            try
            {
                documentPath = storageBinding.ResolveField(sourceField.Input.Id);
            }
            catch (KeyNotFoundException)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Compiled field input '{sourceField.Input.Id.Value}' has no Cosmos document selector.",
                    sourceField.Input.Producer,
                    sourceField.Input.Id);
            }
            if (documentPath.Segments.Any(static segment => segment.Kind == SegmentKind.Element))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Field input '{sourceField.Input.Id.Value}' crosses a collection element and requires an explicit expansion alias.",
                    sourceField.Input.Producer,
                    sourceField.Input.Id);
            }
            return CosmosSqlExpression.Property(storageBinding.RootAlias, FullDocumentPath(documentPath));
        }

        CosmosSqlExpression CompileCurrentItem(
            RelationQueryExpressionSiteAnalysis? site,
            bool requireNonNull)
        {
            if (requireNonNull && !IsRequiredNonNull(site?.Analysis.Site.Scope.CurrentItem))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "The current collection item may be missing or null where exact Cosmos scalar semantics require a value.",
                    site?.Node ?? branch.Node);
            }
            var binding = ResolveCurrentItemBinding(site?.Node);
            if (binding is { } current && collectionAliases.TryGetValue(current, out var currentAlias))
            {
                return CosmosSqlExpression.Alias(currentAlias);
            }

            if (collectionAliases.Count != 1)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "A current-item expression requires exactly one visible collection expansion in Cosmos SQL v2.",
                    branch.Node);
            }
            return CosmosSqlExpression.Alias(collectionAliases.Values.Single());
        }

        CosmosSqlExpression CompileParameter(ParameterExpr parameter, bool requireNonNull)
        {
            QueryParameterId id = new(parameter.Parameter);
            if (!parameters.TryGetValue(id, out var contract))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Canonical parameter '{parameter.Parameter}' is absent from the demand-scoped input contract.",
                    branch.Node);
            }
            if (!CosmosRelationQueryCanonicalValueCodec.SupportsRuntimeParameterType(
                    contract.ValueContract.GetEffectiveType()))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Canonical parameter '{parameter.Parameter}' does not have a Cosmos SQL v2 parameter encoding with exact value semantics.",
                    branch.Node,
                    contract.Input.Id);
            }
            if (contract.Definition.Presence == FieldPresence.Optional
                && contract.Definition.DefaultKind == QueryParameterDefaultKind.None)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Optional parameter '{parameter.Parameter}' has no default; Cosmos SQL cannot bind semantic undefined.",
                    branch.Node,
                    contract.Input.Id);
            }
            if (contract.Definition.DefaultKind == QueryParameterDefaultKind.Value
                && !CosmosRelationQueryCanonicalValueCodec.TryEncodeRuntimeParameter(
                    contract.ValueContract,
                    contract.Definition.DefaultValue ?? ObservationValue.Null,
                    out _))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Canonical parameter '{parameter.Parameter}' has a default outside its exact Cosmos representation.",
                    branch.Node,
                    contract.Input.Id);
            }
            if (requireNonNull && !IsRequiredNonNull(contract.ValueContract))
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Parameter '{parameter.Parameter}' may be null or missing where exact Cosmos scalar semantics require a value.",
                    branch.Node,
                    contract.Input.Id);
            }
            return CosmosSqlExpression.RuntimeParameter(parameter.Parameter);
        }

        ResolvedFieldRoot ResolveFieldRoot(
            FieldPath path,
            ValueBindingId? explicitBinding,
            RelationQueryExpressionSiteAnalysis? site)
        {
            if (explicitBinding is { } binding)
            {
                return new(FieldRoot.Binding, binding, path);
            }

            if (site is null)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Unqualified field '{path}' has no analyzed expression site.",
                    branch.Node);
            }
            var candidates = site.Analysis.Requirements.Fields
                .Where(requirement => requirement.WasUnqualified
                                      && (requirement.Path == path
                                          || requirement.Root == ExprFieldRootKind.CurrentItem
                                          && RemoveCurrentItemRoot(requirement.Path) == path))
                .ToArray();
            if (candidates.Length != 1)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Unqualified field '{path}' does not resolve to exactly one analyzed binding root.",
                    site.Node ?? branch.Node);
            }
            var candidate = candidates[0];
            return candidate.Root switch
            {
                ExprFieldRootKind.Binding when candidate.Binding is { } named =>
                    new(FieldRoot.Binding, named, candidate.Path),
                ExprFieldRootKind.CurrentItem => new(
                    FieldRoot.CurrentItem,
                    ResolveCurrentItemBinding(site.Node),
                    RemoveCurrentItemRoot(candidate.Path)),
                _ => throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Field '{path}' has an unresolved semantic root.",
                    site.Node ?? branch.Node)
            };
        }

        ValueBindingId? ResolveCurrentItemBinding(QueryNodeId? node)
        {
            if (node is null)
            {
                return collectionAliases.Count == 1 ? collectionAliases.Keys.Single() : null;
            }

            var index = pipeline.FindIndex(execution => execution.Id == node.Value);
            if (index < 0)
            {
                return null;
            }

            return pipeline.Take(index + 1)
                .Select(static execution => execution.CanonicalNode)
                .OfType<ExpandCollectionQueryNode>()
                .Select(static expansion => (ValueBindingId?)expansion.ItemBinding)
                .LastOrDefault();
        }

        ImmutableArray<RelationQueryFieldInputContract> SelectBranchFields()
        {
            var outputs = branch.Outputs.Select(static output => output.Id).ToHashSet();
            return
            [
                .. plan.InputContract.Sources
                    .SelectMany(static source => source.Fields)
                    .Where(field => field.Uses.Any(use => outputs.Contains(use.Output.Id)))
                    .OrderBy(static field => field.Input.Id.Value, StringComparer.Ordinal)
            ];
        }

        ImmutableArray<CosmosRelationQuerySelectedField> CreateSelectedFields() =>
        [
            .. SelectBranchFields().Select(field => new CosmosRelationQuerySelectedField(
                field.Input.Id,
                field.Input.Field,
                ResolveBoundPath(field.Input.Id)))
        ];

        FieldPath ResolveBoundPath(RelationQueryInputId input)
        {
            try
            {
                return storageBinding.ResolveField(input);
            }
            catch (KeyNotFoundException)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Compiled field input '{input.Value}' has no Cosmos document selector.",
                    branch.Node,
                    input);
            }
        }

        ImmutableArray<CosmosRelationQueryParameterBinding> CreateParameterBindings(
            CosmosSqlCommandTemplate statement)
        {
            ImmutableArray<CosmosRelationQueryParameterBinding>.Builder result =
                ImmutableArray.CreateBuilder<CosmosRelationQueryParameterBinding>();
            foreach (var slot in statement.Parameters.Where(static slot =>
                         slot.Kind == CosmosSqlParameterBindingKind.Runtime))
            {
                QueryParameterId parameter = new(slot.Binding!);
                if (!parameters.TryGetValue(parameter, out var contract))
                {
                    throw Fail(
                        CosmosRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                        $"SQL runtime slot '{slot.Binding}' does not identify a canonical parameter.",
                        branch.Node);
                }
                result.Add(new(slot.Name, contract.Definition, contract.ValueContract));
            }
            return result.ToImmutable();
        }

        RelationQueryNativeCompilationProvenance CreateProvenance(
            RelationQueryNativeCompilationRequest request,
            ImmutableArray<CosmosRelationQuerySelectedField> selectedFields)
        {
            var assignments = pipeline.SelectMany(static execution =>
                    execution.ProjectionAssignments.Select(static assignment => assignment.Definition.Id)
                        .Concat(execution.AggregateGroupings.Select(static grouping => grouping.Definition.Id))
                        .Concat(execution.AggregateAssignments.Select(static assignment => assignment.Definition.Id)))
                .Distinct()
                .ToImmutableArray();
            return RelationQueryNativeCompilationProvenanceFactory.Create(
                request,
                branch.Id,
                options.CompilerProfile,
                options.ConventionSetVersion,
                [.. pipeline.Select(static execution => execution.Id)],
                assignments,
                [.. selectedFields.Select(static field => field.Input)]);
        }

        readonly record struct PreparedBranch(
            CosmosSqlCommandTemplate Statement,
            ImmutableArray<CosmosRelationQuerySelectedField> SelectedFields,
            ImmutableArray<CosmosRelationQueryResultFieldBinding> ResultFields,
            CosmosRelationQueryResultIdentityBinding? ResultIdentity,
            ImmutableArray<string> AuxiliaryResultAliases,
            ImmutableArray<CosmosRelationQueryParameterBinding> ParameterBindings);

        FieldPath FullDocumentPath(FieldPath relative) => storageBinding.DocumentRoot is { } root
            ? new([.. root.Segments, .. relative.Segments])
            : relative;

        static RelationQueryExpressionSiteAnalysis RequiredSite(
            RelationQueryExecutionNode execution,
            RelationQueryExpressionSiteKind kind) =>
            execution.ExpressionSites.Single(site => site.Kind == kind);

        static void RequireNonNullResult(
            RelationQueryExpressionSiteAnalysis site,
            QueryNodeId node,
            string operation)
        {
            if (IsRequiredNonNull(site.Analysis.KnownResult))
            {
                return;
            }

            throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Canonical {operation} semantics for missing or null values are not proven exact by Cosmos SQL v2.",
                node);
        }

        static ExprValueContract RequireKnownResultContract(
            RelationQueryExpressionSiteAnalysis site,
            QueryNodeId node,
            string operation)
        {
            if (site.Analysis.KnownResult is { } result)
            {
                return result;
            }

            throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Canonical {operation} does not have one known semantic value contract for result decoding.",
                node);
        }

        static CosmosRelationQueryResultValueEncoding ResolveResultEncoding(
            ExprValueContract contract,
            QueryNodeId node)
        {
            if (contract.Cardinality != FieldCardinality.Single)
            {
                throw Fail(
                    CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Cosmos SQL v2 result fields require a single-valued semantic contract.",
                    node);
            }

            if (CosmosRelationQueryCanonicalValueCodec.TryResolveResultEncoding(contract, out var encoding))
                return encoding;

            throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                "Cosmos SQL v2 cannot prove a canonical physical result encoding for this value contract.",
                node);
        }

        static void RequireCosmosScalarResult(
            RelationQueryExpressionSiteAnalysis site,
            QueryNodeId node,
            string operation)
        {
            if (IsCosmosEqualityScalar(site.Analysis.KnownResult?.GetEffectiveType()))
            {
                return;
            }

            throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Canonical {operation} requires one proven exact scalar equality domain in Cosmos SQL v2.",
                node);
        }

        void RequireCosmosOrderableResult(
            RelationQueryExpressionSiteAnalysis site,
            QueryNodeId node,
            FieldPath? sourcePath)
        {
            var type = site.Analysis.KnownResult?.GetEffectiveType();
            if (type is ScalarTypeRef { Kind: ScalarTypeKind.Int32 })
            {
                return;
            }

            if (type is ScalarTypeRef { Kind: ScalarTypeKind.String or ScalarTypeKind.Date }
                && sourcePath is { } path
                && storageBinding.ExactOrderingPaths.Contains(path))
            {
                return;
            }

            throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                "Canonical ordering requires Int32 values or an explicitly proven string/date source path; wider numeric and temporal ordering is not exact in Cosmos SQL v2.",
                node);
        }

        static bool IsRequiredNonNull(ExprValueContract? contract) => contract is
        {
            Presence: FieldPresence.Required,
            Nullability: FieldNullability.NonNullable
        };

        static bool AreCosmosEqualityComparable(TypeRef? left, TypeRef? right) =>
            IsExactNumericScalar(left) && IsExactNumericScalar(right)
            || left == right && IsCosmosEqualityScalar(left);

        static bool AreCosmosOrderComparable(TypeRef? left, TypeRef? right) =>
            IsExactNumericScalar(left) && IsExactNumericScalar(right);

        static bool IsBooleanScalar(TypeRef? type) =>
            type is ScalarTypeRef { Kind: ScalarTypeKind.Bool };

        static bool IsExactNumericScalar(TypeRef? type) =>
            type is ScalarTypeRef { Kind: ScalarTypeKind.Int32 };

        static bool IsCosmosEqualityScalar(TypeRef? type) => type is ScalarTypeRef
        {
            Kind: ScalarTypeKind.Bool
                or ScalarTypeKind.Int32
                or ScalarTypeKind.String
                or ScalarTypeKind.Guid
                or ScalarTypeKind.Date
        };

        static CosmosSqlBinaryOperator Convert(BinaryOperator @operator) => @operator switch
        {
            BinaryOperator.Eq => CosmosSqlBinaryOperator.Equal,
            BinaryOperator.Ne => CosmosSqlBinaryOperator.NotEqual,
            BinaryOperator.Gt => CosmosSqlBinaryOperator.GreaterThan,
            BinaryOperator.Ge => CosmosSqlBinaryOperator.GreaterThanOrEqual,
            BinaryOperator.Lt => CosmosSqlBinaryOperator.LessThan,
            BinaryOperator.Le => CosmosSqlBinaryOperator.LessThanOrEqual,
            BinaryOperator.And => CosmosSqlBinaryOperator.And,
            BinaryOperator.Or => CosmosSqlBinaryOperator.Or,
            _ => throw Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"Binary operator '{@operator}' is not in the Cosmos SQL v2 closure.")
        };

        static FieldPath RemoveCurrentItemRoot(FieldPath path)
        {
            var segments = path.Segments;
            if (segments.IsDefaultOrEmpty
                || segments[0] is not { Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem })
            {
                return path;
            }
            return segments.Length == 1 ? default : new([.. segments.Skip(1)]);
        }

        static BranchCompilationException Fail(
            string code,
            string message,
            QueryNodeId? node = null,
            RelationQueryInputId? input = null) =>
            new(code, message, node, input);

        static BranchCompilationException CollectionEvidenceFailure(
            string message,
            QueryNodeId? node = null,
            RelationQueryInputId? input = null) =>
            Fail(
                CosmosRelationQueryCompilationDiagnosticCodes.CollectionElementEvidenceUnavailable,
                message,
                node,
                input);

        enum PipelineStage
        {
            Source = 0,
            Row = 1,
            Shape = 2,
            Distinct = 3,
            Order = 4,
            Page = 5
        }

        enum FieldRoot
        {
            Binding = 0,
            CurrentItem = 1
        }

        readonly record struct ResolvedFieldRoot(
            FieldRoot Root,
            ValueBindingId? Binding,
            FieldPath Path);

        readonly record struct ResolvedOutput(
            CosmosSqlExpression Expression,
            ExprValueContract ValueContract,
            CosmosRelationQueryResultValueEncoding Encoding,
            QueryAssignmentId? Assignment);

        readonly record struct AggregateBinding(
            RelationQueryAggregateAssignmentExecution Execution,
            bool Grouped);
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

static class CosmosRelationQueryArtifactFingerprinter
{
    const string Algorithm = "sha256";
    const string Canonicalization = "cohesive.relations.cosmos-artifact/v1-c14n/v4";

    public static CosmosRelationQueryArtifactFingerprint Compute(
        RelationQueryNativeResultBranch branch,
        CosmosSqlCommandTemplate statement,
        CosmosRelationQueryStorageBinding storageBinding,
        ImmutableArray<CosmosRelationQuerySelectedField> selectedFields,
        ImmutableArray<CosmosRelationQueryResultFieldBinding> resultFields,
        CosmosRelationQueryResultIdentityBinding? resultIdentity,
        ImmutableArray<string> auxiliaryResultAliases,
        ImmutableArray<CosmosRelationQueryParameterBinding> parameters,
        CosmosRelationQueryPagingContract? paging,
        RelationQueryNativeCompilationProvenance provenance)
    {
        StringBuilder canonical = new();
        var jsonOptions = RelationQueryJsonSerializer.CreateOptions();
        Append(canonical, Canonicalization);
        Append(canonical, JsonSerializer.Serialize(branch, jsonOptions));
        Append(canonical, statement.Text);
        Append(canonical, statement.Parameters.Length);
        foreach (var parameter in statement.Parameters)
        {
            Append(canonical, parameter.Name);
            Append(canonical, (int)parameter.Kind);
            Append(canonical, parameter.Binding);
            Append(canonical, JsonSerializer.Serialize(parameter.ConstantValue));
        }
        Append(canonical, storageBinding.Id.Value);
        Append(canonical, storageBinding.Fingerprint.Value);
        Append(canonical, selectedFields.Length);
        foreach (var field in selectedFields)
        {
            Append(canonical, field.Input.Value);
            Append(canonical, field.Field);
            Append(canonical, CosmosRelationQueryStorageBinding.FieldPathKey(field.DocumentPath));
        }
        Append(canonical, resultFields.Length);
        foreach (var field in resultFields)
        {
            Append(canonical, field.Alias);
            Append(canonical, field.Field);
            Append(canonical, JsonSerializer.Serialize(field.ValueContract, jsonOptions));
            Append(canonical, (int)field.Encoding);
            Append(canonical, field.Assignment?.Value);
        }
        Append(canonical, resultIdentity?.Alias);
        Append(canonical, resultIdentity is null
            ? null
            : JsonSerializer.Serialize(resultIdentity.ValueContract, jsonOptions));
        Append(canonical, resultIdentity is null ? -1 : (int)resultIdentity.Encoding);
        Append(canonical, auxiliaryResultAliases.Length);
        foreach (var alias in auxiliaryResultAliases)
        {
            Append(canonical, alias);
        }
        Append(canonical, parameters.Length);
        foreach (var parameter in parameters)
        {
            Append(canonical, parameter.SqlName);
            Append(canonical, parameter.Parameter.Value);
            Append(canonical, JsonSerializer.Serialize(parameter.Definition, jsonOptions));
            Append(canonical, JsonSerializer.Serialize(parameter.ValueContract, jsonOptions));
        }
        Append(canonical, paging?.Offset ?? -1);
        Append(canonical, paging?.Limit ?? -1);
        Append(canonical, paging is null
            ? null
            : CosmosRelationQueryStorageBinding.FieldPathKey(paging.StableUniquePath));
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

    static void Append(StringBuilder builder, RelationQueryFieldReference field)
    {
        Append(builder, field.Shape.GraphId.Value);
        Append(builder, field.Shape.ShapeId.Value);
        Append(builder, field.Path.Segments.Length);
        foreach (var segment in field.Path.Segments)
        {
            Append(builder, (int)segment.Kind);
            Append(builder, segment.Segment);
        }
    }
}

static class RelationQueryExecutionNodeListExtensions
{
    public static int FindIndex(
        this ImmutableArray<RelationQueryExecutionNode> nodes,
        Func<RelationQueryExecutionNode, bool> predicate)
    {
        for (var index = 0; index < nodes.Length; index++)
        {
            if (predicate(nodes[index]))
            {
                return index;
            }
        }
        return -1;
    }
}
