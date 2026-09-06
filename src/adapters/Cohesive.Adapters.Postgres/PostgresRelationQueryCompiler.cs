using Cohesive.Adapters.Sql;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>
/// Compiles exact demand-scoped canonical relation/query branches to parameterized PostgreSQL SQL.
/// </summary>
public sealed class PostgresRelationQueryCompiler
{
    const string CompilerProfileSetting = "compilerProfile";

    /// <summary>Versioned identity of this concrete compiler implementation and semantic profile.</summary>
    public const string CompilerProfile = "cohesive.adapters.postgres.sql/compiler-v2";

    /// <summary>Creates a canonical PostgreSQL relation/query compiler.</summary>
    public PostgresRelationQueryCompiler()
    {
    }

    /// <summary>
    /// Qualifies profile-level feasibility using the exact PostgreSQL placement and storage-binding evidence.
    /// </summary>
    /// <param name="request">Plan, profile feasibility, placement, and selected branches to qualify.</param>
    /// <param name="storageBinding">Exact PostgreSQL storage binding whose physical evidence is examined.</param>
    /// <returns>A deterministic contextual realization report predicting native PostgreSQL compilation.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public RelationQueryBoundRealizationReport Realize(
        RelationQueryBoundRealizationRequest request,
        PostgresRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        return ObserveContextualRealization(request, storageBinding).Report;
    }

    ContextualEvaluation ObserveContextualRealization(
        RelationQueryBoundRealizationRequest request,
        PostgresRelationQueryStorageBinding storageBinding) =>
        RelationQueryCompilerTelemetry.Observe(
            PostgresRelationQueryTelemetry.Emitter,
            RelationQueryTelemetry.RealizationActivityName,
            (Compiler: this, Request: request, StorageBinding: storageBinding),
            static state => EvaluateContext(state.Request, state.StorageBinding),
            static evaluation => RelationQueryTelemetry.GetStatusTagValue(evaluation.Report.Status),
            static (activity, state, evaluation) => RelationQueryCompilerTelemetry.ProjectRealizationActivity(
                activity,
                state.Request,
                state.StorageBinding.Fingerprint.Value,
                evaluation.Report));

    /// <summary>
    /// Qualifies the exact PostgreSQL context and compiles every selected branch when that context is realizable.
    /// </summary>
    /// <param name="request">Plan, profile feasibility, placement, and selected branches to qualify and compile.</param>
    /// <param name="storageBinding">Exact PostgreSQL storage binding to qualify and lower.</param>
    /// <returns>Exact compiled artifacts or structured contextual and native diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public PostgresRelationQueryCompilationResult Compile(
        RelationQueryBoundRealizationRequest request,
        PostgresRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        var outcome = RelationQueryCompilerTelemetry.Observe(
            PostgresRelationQueryTelemetry.Emitter,
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

    (PostgresRelationQueryCompilationResult Compilation, RelationQueryBoundRealizationReport BoundRealization)
        CompileBoundCore(
        RelationQueryBoundRealizationRequest request,
        PostgresRelationQueryStorageBinding storageBinding)
    {

        var evaluation = ObserveContextualRealization(request, storageBinding);
        if (!evaluation.Report.IsRealizable)
        {
            ImmutableArray<RelationQueryNativeCompilationDiagnostic> diagnostics =
            [
                .. evaluation.Diagnostics,
                .. RelationQueryNativeCompilationDiagnostic.FromBoundRealizationFailure(evaluation.Report)
            ];

            PostgresRelationQueryCompilationResult compilation = new(
                evaluation.Report.Status == RelationQueryRealizationStatus.Invalid
                    ? RelationQueryNativeCompilationStatus.Invalid
                    : RelationQueryNativeCompilationStatus.Unsupported,
                [],
                diagnostics);
            return new(compilation, evaluation.Report);
        }

        return new(CompileCore(
            new RelationQueryNativeCompilationRequest(request.Plan, evaluation.Report, request.Placement),
            storageBinding), evaluation.Report);
    }

    /// <summary>Compiles every selected canonical result branch independently and fails closed on uncertainty.</summary>
    /// <param name="request">Exact static plan, realization proof, placement, and branch selection.</param>
    /// <param name="storageBinding">Versioned PostgreSQL database, table, column, and semantic evidence.</param>
    /// <returns>Exact compiled artifacts or structured invalid/unsupported diagnostics.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public PostgresRelationQueryCompilationResult Compile(RelationQueryNativeCompilationRequest request, PostgresRelationQueryStorageBinding storageBinding)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storageBinding);
        return RelationQueryCompilerTelemetry.Observe(
            PostgresRelationQueryTelemetry.Emitter,
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

    PostgresRelationQueryCompilationResult CompileCore(
        RelationQueryNativeCompilationRequest request,
        PostgresRelationQueryStorageBinding storageBinding)
    {

        var context = new CompilationContext(request);
        var diagnostics = ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        var inputDiagnostics = request.ValidateInputs();
        var bindingDiagnostics = ValidateBinding(
            context,
            storageBinding,
            request.BoundRealization.Evidence.Binding);
        diagnostics.AddRange(inputDiagnostics);
        diagnostics.AddRange(bindingDiagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            var invalid = !bindingDiagnostics.IsDefaultOrEmpty
                          || inputDiagnostics.Any(static diagnostic =>
                              diagnostic.Code != RelationQueryNativeCompilationDiagnosticCodes.RealizationUnavailable);
            return new(
                invalid ? RelationQueryNativeCompilationStatus.Invalid : RelationQueryNativeCompilationStatus.Unsupported,
                [],
                diagnostics.ToImmutable());
        }

        var artifacts = ImmutableArray.CreateBuilder<PostgresRelationQueryCompiledArtifact>();
        foreach (var branch in request.Branches)
        {
            try
            {
                artifacts.Add(new BranchCompiler(context, storageBinding, branch).Compile(request));
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
                    PostgresRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
                    DiagnosticSeverity.Error,
                    $"PostgreSQL artifact construction failed closed: {exception.Message}",
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
        PostgresRelationQueryStorageBinding storageBinding,
        string target,
        string boundRealizationFingerprint,
        PostgresRelationQueryCompilationResult result)
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

    static ContextualEvaluation EvaluateContext(
        RelationQueryBoundRealizationRequest request,
        PostgresRelationQueryStorageBinding storageBinding)
    {
        var context = new CompilationContext(request);
        var diagnostics = ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();
        List<ContextualFailure> failures = [];

        var inputDiagnostics = request.ValidateInputs();
        diagnostics.AddRange(inputDiagnostics);
        foreach (var diagnostic in inputDiagnostics.Where(static item => item.Severity == DiagnosticSeverity.Error))
        {
            failures.Add(new(
                RelationQueryBoundAssessmentStatus.Invalid,
                PostgresRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
                diagnostic.Message,
                diagnostic.Branch,
                diagnostic.Node,
                diagnostic.Input));
        }

        if (failures.Count == 0)
        {
            var bindingDiagnostics = ValidateBinding(context, storageBinding);
            diagnostics.AddRange(bindingDiagnostics);
            foreach (var diagnostic in bindingDiagnostics.Where(static item => item.Severity == DiagnosticSeverity.Error))
            {
                failures.Add(new(
                    RelationQueryBoundAssessmentStatus.Invalid,
                    diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Branch,
                    diagnostic.Node,
                    diagnostic.Input));
            }
        }

        if (failures.Count == 0 && request.ProfileFeasibility.IsRealizable)
        {
            foreach (var branch in request.Branches)
            {
                try
                {
                    new BranchCompiler(context, storageBinding, branch).Validate();
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
                    failures.Add(new(
                        RelationQueryBoundAssessmentStatus.Unavailable,
                        exception.Code,
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
                    var message = $"PostgreSQL contextual validation failed closed: {exception.Message}";
                    diagnostics.Add(new(
                        PostgresRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
                        DiagnosticSeverity.Error,
                        message,
                        branch.Id));
                    failures.Add(new(
                        RelationQueryBoundAssessmentStatus.Invalid,
                        PostgresRelationQueryCompilationDiagnosticCodes.ArtifactInvalid,
                        message,
                        branch.Id));
                }
            }
        }

        var evidence = new RelationQueryContextualEvidenceProjection(
            CreateBindingReference(context, storageBinding),
            request.ProfileFeasibility.IsRealizable
                ? CreateContextualAssessments(request, context, storageBinding, failures)
                : []);
        var report = RelationQueryBoundRealizationCompiler.Compile(request, evidence);
        return new(report, diagnostics.ToImmutable());
    }

    static RelationQueryAdapterBindingReference CreateBindingReference(
        CompilationContext request,
        PostgresRelationQueryStorageBinding storageBinding)
    {
        var selectedInputs = request.SelectedInputs;
        var selectedTables = storageBinding.Tables
            .Where(table => selectedInputs.Contains(table.Input))
            .ToArray();
        var configuration = ImmutableArray.CreateBuilder<EffectiveConfigurationDecision>(
            storageBinding.ConfigurationDecisions.Length + 1);
        configuration.AddRange(storageBinding.ConfigurationDecisions);
        configuration.Add(new(
            CompilerProfileSetting,
            EffectiveConfigurationOrigin.AdapterConvention,
            CompilerProfile));
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
            [.. selectedTables.Select(static table => table.Source).Distinct()],
            [.. selectedTables.Select(static table => table.PlacementBinding)],
            configuration.MoveToImmutable());
    }

    static ImmutableArray<RelationQueryBoundRequirementAssessment> CreateContextualAssessments(
        RelationQueryBoundRealizationRequest boundRequest,
        CompilationContext request,
        PostgresRelationQueryStorageBinding storageBinding,
        IReadOnlyList<ContextualFailure> failures)
    {
        var projectedPlacements = storageBinding.Tables
            .Select(static table => table.PlacementBinding)
            .ToHashSet();
        return RelationQueryContextualAssessmentProjector.Project(
            boundRequest,
            "postgres/context",
            branch =>
            {
                var branchFailures = failures
                    .Where(failure => failure.Branch is null || failure.Branch == branch.Id)
                    .OrderByDescending(static failure => failure.Status == RelationQueryBoundAssessmentStatus.Invalid)
                    .ThenBy(static failure => failure.Code, StringComparer.Ordinal)
                    .ThenBy(static failure => failure.Message, StringComparer.Ordinal)
                    .ToArray();
                if (branchFailures.Length == 0)
                    return null;

                var primary = branchFailures[0];
                var failedBoundary = FailedBoundary(primary.Code);
                return new(
                    primary.Status,
                    FailureReason(primary.Status, primary.Code, failedBoundary),
                    new(primary.Code),
                    string.Join(
                        "; ",
                        branchFailures.Select(static failure => $"[{failure.Code}] {failure.Message}")),
                    "Correct the attributed PostgreSQL binding evidence or choose a supported placement and lowering strategy.",
                    primary.Node,
                    primary.Input,
                    failedOperatingBoundary: failedBoundary,
                    failedConfigurationSetting: FailedConfigurationSetting(request, storageBinding, primary));
            },
            (_, requirement, failure) => ResolveAssessmentAttribution(
                request,
                storageBinding,
                projectedPlacements,
                requirement,
                failure));
    }

    static (EffectiveConfigurationOrigin Origin, string Authority) AssessmentAuthority(
        PostgresRelationQueryStorageBinding storageBinding)
    {
        var (origin, bindingAuthority) = storageBinding.Origin switch
        {
            PostgresRelationQueryBindingOrigin.Explicit =>
                (EffectiveConfigurationOrigin.Explicit, storageBinding.Id.Value),
            PostgresRelationQueryBindingOrigin.Convention =>
                (EffectiveConfigurationOrigin.AdapterConvention, storageBinding.ConventionSetVersion!),
            _ => throw new ArgumentOutOfRangeException(
                nameof(storageBinding),
                storageBinding.Origin,
                "Unsupported PostgreSQL binding origin.")
        };
        return (origin, bindingAuthority);
    }

    static RelationQueryContextualAssessmentAttribution ResolveAssessmentAttribution(
        CompilationContext request,
        PostgresRelationQueryStorageBinding storageBinding,
        IReadOnlySet<RelationQuerySourcePlacementBindingId> projectedPlacements,
        RelationQueryRealizationRequirement requirement,
        RelationQueryContextualBranchFailure? failure)
    {
        var site = ResolveAssessmentSite(
            request,
            projectedPlacements,
            requirement,
            failure?.Node,
            failure?.Input);
        if (failure?.FailedConfigurationSetting is { } failedSetting)
        {
            if (string.Equals(failedSetting, CompilerProfileSetting, StringComparison.Ordinal))
            {
                return new(
                    EffectiveConfigurationOrigin.AdapterConvention,
                    CompilerProfile,
                    site.Node,
                    site.Input,
                    site.Field,
                    site.Placement,
                    CompilerProfileSetting);
            }

            var decision = storageBinding.ConfigurationDecisions.FirstOrDefault(candidate =>
                string.Equals(candidate.Setting, failedSetting, StringComparison.Ordinal));
            if (decision is not null)
            {
                return new(
                    decision.Origin,
                    decision.Authority,
                    site.Node,
                    site.Input,
                    site.Field,
                    site.Placement,
                    decision.Setting);
            }
        }

        var (origin, authority) = AssessmentAuthority(storageBinding);
        return new(origin, authority, site.Node, site.Input, site.Field, site.Placement);
    }

    static AssessmentSite ResolveAssessmentSite(
        CompilationContext request,
        IReadOnlySet<RelationQuerySourcePlacementBindingId> projectedPlacements,
        RelationQueryRealizationRequirement requirement,
        QueryNodeId? failedNode,
        RelationQueryInputId? failedInput)
    {
        var input = failedInput is { } candidateInput && requirement.Origin?.Input == candidateInput
            ? candidateInput
            : requirement.Origin?.Input;
        if (input is { } candidate && !request.PlanReference.Inputs.Contains(candidate))
            input = null;
        var field = input is { } inputId
            ? request.Plan.InputContract.Requirements.Inputs
                .OfType<RelationQueryFieldInput>()
                .SingleOrDefault(candidate => candidate.Id == inputId)
                ?.Field.Path
                ?? request.SelectedFields.SingleOrDefault(candidate => candidate.Input.Id == inputId)
                    ?.Input.Field.Path
                ?? request.SelectedPlacements.SelectMany(static placement => placement.Fields)
                    .SingleOrDefault(candidate => candidate.Input == inputId)
                    ?.SemanticPath
            : null;
        var placement = input is { } placedInput
            ? request.SelectedPlacements.SingleOrDefault(candidate =>
                projectedPlacements.Contains(candidate.Id)
                && (candidate.Input == placedInput
                    || candidate.Fields.Any(fieldBinding => fieldBinding.Input == placedInput)))?.Id
            : null;
        var node = input is not null && requirement.Origin?.Input == input
            ? requirement.Origin.Node
            : failedNode ?? requirement.Origin?.Node;
        return new(node, input, field, placement);
    }

    static RelationQueryUnavailableReason FailureReason(
        RelationQueryBoundAssessmentStatus status,
        string code,
        RelationQueryOperatingBoundaryId? failedBoundary) => status == RelationQueryBoundAssessmentStatus.Invalid
        ? RelationQueryUnavailableReason.CapabilityEvidenceInvalid
        : code is PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing
            or PostgresRelationQueryCompilationDiagnosticCodes.RelationshipEndpointMissing
            ? RelationQueryUnavailableReason.CapabilityEvidenceInvalid
        : failedBoundary is null
            ? RelationQueryUnavailableReason.PolicyRejected
            : RelationQueryUnavailableReason.OperatingBoundaryInvalid;

    static RelationQueryOperatingBoundaryId? FailedBoundary(string code) => code switch
    {
        PostgresRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch =>
            PostgresRelationQueryTargetProfile.CompleteInputEvidenceBoundary,
        PostgresRelationQueryCompilationDiagnosticCodes.CrossSourceJoin =>
            PostgresRelationQueryTargetProfile.SingleDatabaseBoundary,
        PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable =>
            PostgresRelationQueryTargetProfile.StableOrderingBoundary,
        PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported =>
            PostgresRelationQueryTargetProfile.ExactTemporalDomainBoundary,
        _ => null
    };

    static string? FailedConfigurationSetting(
        CompilationContext request,
        PostgresRelationQueryStorageBinding storageBinding,
        ContextualFailure failure)
    {
        if (failure.Input is { } input)
        {
            var placement = request.SelectedPlacements.SingleOrDefault(candidate =>
                candidate.Input == input
                || candidate.Fields.Any(field => field.Input == input));
            if (placement is not null)
            {
                var prefix = $"table/{Uri.EscapeDataString(placement.Id.Value)}/";
                var inputSegment = Uri.EscapeDataString(input.Value);
                if (failure.Code == PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing)
                    return $"{prefix}field/{inputSegment}/columnName";
                if (failure.Code == PostgresRelationQueryCompilationDiagnosticCodes.RelationshipEndpointMissing)
                    return $"{prefix}relationship/{inputSegment}/columnName";

                var inputMarker = $"/{inputSegment}/";
                var decision = storageBinding.ConfigurationDecisions.FirstOrDefault(candidate =>
                    candidate.Setting.StartsWith(prefix, StringComparison.Ordinal)
                    && candidate.Setting.Contains(inputMarker, StringComparison.Ordinal));
                if (decision is not null)
                    return decision.Setting;
            }
        }

        return failure.Code is PostgresRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch
            or PostgresRelationQueryCompilationDiagnosticCodes.CrossSourceJoin
            ? null
            : CompilerProfileSetting;
    }

    static ImmutableArray<RelationQueryNativeCompilationDiagnostic> ValidateBinding(
        CompilationContext request,
        PostgresRelationQueryStorageBinding storageBinding,
        RelationQueryAdapterBindingReference? expectedBinding = null)
    {
        var diagnostics = ImmutableArray.CreateBuilder<RelationQueryNativeCompilationDiagnostic>();

        void Error(string message) => diagnostics.Add(new(
            PostgresRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch,
            DiagnosticSeverity.Error,
            message));

        if (expectedBinding is not null
            && !ReferencesExactBinding(expectedBinding, request, storageBinding))
        {
            Error(
                "The exact PostgreSQL storage binding does not match the adapter-binding fingerprint retained by contextual realization.");
        }

        List<string> affinityMismatches = [];
        if (storageBinding.CompiledPlanFingerprint is null
            || storageBinding.PlacementFingerprint is null)
        {
            affinityMismatches.Add("compiled plan and source placement (missing)");
        }
        else if (storageBinding.CompiledPlanFingerprint is { } planFingerprint
            && !Equals(
                planFingerprint,
                RelationQueryCompiledPlanReferenceFingerprinter.Compute(request.PlanReference)))
        {
            affinityMismatches.Add("compiled plan");
        }

        if (storageBinding.PlacementFingerprint is { } placementFingerprint
            && !Equals(placementFingerprint, request.Placement.Fingerprint))
        {
            affinityMismatches.Add("source placement");
        }

        if (affinityMismatches.Count != 0)
        {
            Error($"The PostgreSQL storage binding's {string.Join(" and ", affinityMismatches)} affinity does not match the request.");
        }

        var profile = request.ProfileFeasibility.TargetProfile;
        if (storageBinding.Target != PostgresRelationQueryTargetProfile.Target
            || storageBinding.TargetProfile != PostgresRelationQueryTargetProfile.ProfileId
            || storageBinding.Target != profile.Target
            || storageBinding.TargetProfile != profile.Id
            || !profile.HasSameSemantics(PostgresRelationQueryTargetProfile.Default))
        {
            Error("The binding, realization report, and canonical PostgreSQL target profile are not the same snapshot.");
        }

        var placements = request.SelectedPlacements.ToDictionary(static binding => binding.Input);
        if (request.SelectedPlacements.Any(static placement => placement.Partition is not null))
        {
            Error(
                "The current PostgreSQL compiler does not lower source-partition selectors; issuing an unscoped scan would weaken placement semantics.");
        }
        var selectedTables = storageBinding.Tables
            .Where(table => request.SelectedInputs.Contains(table.Input))
            .ToArray();

        foreach (var placement in request.SelectedPlacements)
        {
            var tables = selectedTables.Where(table => table.Input == placement.Input).ToArray();
            if (placement.Acquisition == RelationQuerySourceAcquisitionKind.Supplied)
            {
                if (tables.Length != 0)
                {
                    Error($"Supplied input '{placement.Input.Value}' must not have a physical PostgreSQL table.");
                }

                continue;
            }
            if (tables.Length != 1)
            {
                Error($"Acquired input '{placement.Input.Value}' must have exactly one PostgreSQL table binding.");
                continue;
            }
            var table = tables[0];
            if (table.PlacementBinding != placement.Id
                || table.Source != placement.Source
                || table.Shape != placement.Shape)
            {
                Error($"Table binding for input '{placement.Input.Value}' conflicts with exact placement evidence.");
            }
        }

        foreach (var table in selectedTables)
        {
            if (!placements.TryGetValue(table.Input, out var placement)
                || placement.Acquisition == RelationQuerySourceAcquisitionKind.Supplied)
            {
                Error($"Table binding '{table.PlacementBinding.Value}' is foreign to the exact acquired placement.");
            }
        }

        ValidateTableSemantics(request, selectedTables, placements, Error);

        var tableSources = selectedTables.Select(static table => table.Source).Distinct().ToArray();
        var sourceInstances = request.Placement.SourceInstances
            .Where(source => tableSources.Contains(source.Id))
            .ToArray();
        if (sourceInstances.Length != tableSources.Length)
        {
            Error("Every PostgreSQL table must belong to one declared source instance.");
        }

        return diagnostics.ToImmutable();
    }

    static bool ReferencesExactBinding(
        RelationQueryAdapterBindingReference reference,
        CompilationContext request,
        PostgresRelationQueryStorageBinding storageBinding)
    {
        var actual = CreateBindingReference(request, storageBinding);
        if (!reference.HasSameSemantics(actual))
            return false;

        var configuration = actual.ConfigurationDecisions.ToDictionary(
            static decision => decision.Setting,
            StringComparer.Ordinal);
        var (bindingOrigin, bindingAuthority) = AssessmentAuthority(storageBinding);
        return request.BoundAssessments.All(assessment =>
        {
            if (assessment.ConfigurationSetting is not { } setting)
            {
                return assessment.Origin == bindingOrigin
                       && string.Equals(assessment.Authority, bindingAuthority, StringComparison.Ordinal);
            }

            return configuration.TryGetValue(setting, out var decision)
                   && assessment.Origin == decision.Origin
                   && string.Equals(assessment.Authority, decision.Authority, StringComparison.Ordinal);
        });
    }

    static void ValidateTableSemantics(
        CompilationContext request,
        IReadOnlyList<PostgresRelationQueryTableBinding> selectedTables,
        IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements,
        Action<string> error)
    {
        var requiredIdentityBindings = request.SelectedTraversals
            .Select(static traversal => traversal.Input.Direction == RelationshipTraversalDirection.Forward
                ? traversal.Result
                : traversal.From)
            .ToHashSet();
        foreach (var message in PostgresRelationQueryBindingSemanticValidator.ValidateCompilation(
                     request.Plan,
                     selectedTables,
                     placements,
                     request.SelectedFields,
                     request.SelectedTraversals,
                     requiredIdentityBindings))
        {
            error(message);
        }
    }

    sealed class CompilationContext
    {
        public CompilationContext(RelationQueryBoundRealizationRequest request)
        {
            Plan = request.Plan;
            PlanReference = request.PlanReference;
            ProfileFeasibility = request.ProfileFeasibility;
            Placement = request.Placement;
            Branches = request.Branches;
            Selection = request.Selection;
            BoundAssessments = [];
            SelectedSources = Selection.Sources;
            SelectedTraversals = Selection.Traversals;
            SelectedFields = Selection.Fields;
            SelectedPlacements = Selection.PlacementBindings;
            SelectedInputs = Selection.InputIds.ToHashSet();
        }

        public CompilationContext(RelationQueryNativeCompilationRequest request)
        {
            Plan = request.Plan;
            PlanReference = request.PlanReference;
            ProfileFeasibility = request.ProfileFeasibility;
            Placement = request.Placement;
            Branches = request.Branches;
            Selection = request.Selection;
            BoundAssessments = request.BoundRealization.Evidence.Assessments;
            SelectedSources = Selection.Sources;
            SelectedTraversals = Selection.Traversals;
            SelectedFields = Selection.Fields;
            SelectedPlacements = Selection.PlacementBindings;
            SelectedInputs = Selection.InputIds.ToHashSet();
        }

        public CompiledRelationQueryPlan Plan { get; }

        public RelationQueryCompiledPlanReference PlanReference { get; }

        public RelationQueryRealizationReport ProfileFeasibility { get; }

        public RelationQuerySourcePlacement Placement { get; }

        public ImmutableArray<RelationQueryNativeResultBranch> Branches { get; }

        public RelationQueryCompilationSelection Selection { get; }

        public ImmutableArray<RelationQueryBoundRequirementAssessment> BoundAssessments { get; }

        public ImmutableArray<RelationQuerySourceInputContract> SelectedSources { get; }

        public ImmutableArray<RelationQueryTraversalInputContract> SelectedTraversals { get; }

        public ImmutableArray<RelationQueryFieldInputContract> SelectedFields { get; }

        public ImmutableArray<RelationQuerySourcePlacementBinding> SelectedPlacements { get; }

        public IReadOnlySet<RelationQueryInputId> SelectedInputs { get; }
    }

    sealed class BranchCompiler
    {
        const string SuppliedPrefix = "supplied:";
        const string ParameterPrefix = "parameter:";

        readonly CompilationContext request;
        readonly PostgresRelationQueryStorageBinding storageBinding;
        readonly RelationQueryNativeResultBranch branch;
        readonly RelationQueryBranchSelection branchSelection;
        readonly IReadOnlyDictionary<QueryNodeId, RelationQueryExecutionNode> nodes;
        readonly IReadOnlyDictionary<QueryNodeId, RelationQuerySourceInputContract> sourcesByNode;
        readonly IReadOnlyDictionary<QueryNodeId, RelationQueryTraversalInputContract> traversalsByNode;
        readonly IReadOnlySet<QueryNodeId> branchNodes;
        readonly IReadOnlyDictionary<QueryParameterId, RelationQueryParameterInputContract> parameters;
        readonly IReadOnlyDictionary<RelationQueryInputId, RelationQuerySourcePlacementBinding> placements;
        readonly IReadOnlyDictionary<ValueBindingId, string> bindingAliases;
        readonly ImmutableArray<RelationQueryFieldInputContract> selectedFieldContracts;
        readonly IReadOnlySet<ValueBindingId> requiredIdentityBindings;
        readonly IReadOnlySet<RelationshipKey> requiredRelationshipReferences;
        readonly Dictionary<QueryNodeId, Scope> scopes = [];
        readonly List<PostgresRelationQueryLoweringDecision> decisions = [];
        readonly HashSet<ExprSiteId> expressionDecisions = [];
        readonly HashSet<QueryNodeId> coveredNodes = [];
        readonly HashSet<QueryAssignmentId> coveredAssignments = [];
        readonly Dictionary<string, PostgresRelationQueryTextOrderingDomainEvidence> runtimeTextOrderingDomains =
            new(StringComparer.Ordinal);
        readonly SqlAliasAllocator relationAliases = new(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);
        readonly SqlAliasAllocator valueAliases = new(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);
        readonly SqlAliasAllocator resultAliases = new(PostgresSqlDialect.StandardMaxUtf8ByteLength, StringComparer.Ordinal);
        PostgresRelationQueryPagingContract? paging;

        public BranchCompiler(
            CompilationContext request,
            PostgresRelationQueryStorageBinding storageBinding,
            RelationQueryNativeResultBranch branch)
        {
            this.request = request;
            this.storageBinding = storageBinding;
            this.branch = branch;
            branchSelection = request.Selection.GetBranch(branch.Id);
            nodes = request.Plan.ExecutionSlice.Nodes.ToDictionary(static node => node.Id);
            sourcesByNode = request.Plan.InputContract.Sources.ToDictionary(static source => source.Node);
            traversalsByNode = request.Plan.InputContract.Traversals.ToDictionary(static traversal => traversal.Input.Traversal);
            branchNodes = branchSelection.ReachableNodes.ToHashSet();
            parameters = request.Plan.InputContract.Parameters.ToDictionary(static parameter => parameter.Definition.Id);
            placements = branchSelection.PlacementBindings.ToDictionary(static placement => placement.Input);
            bindingAliases = CreateBindingAliases();
            selectedFieldContracts = branchSelection.Fields;
            (requiredIdentityBindings, requiredRelationshipReferences) = SelectBranchRelationshipInternals();
        }

        IReadOnlyDictionary<ValueBindingId, string> CreateBindingAliases()
        {
            Dictionary<ValueBindingId, string> aliases = [];

            foreach (var source in request.Plan.InputContract.Sources)
            {
                aliases.TryAdd(source.Binding, ShapeAlias(source.Shape));
            }

            foreach (var traversal in request.Plan.InputContract.Traversals)
            {
                aliases.TryAdd(traversal.Result, ShapeAlias(traversal.ResultShape));
            }

            foreach (var node in nodes.Values)
            {
                switch (node.CanonicalNode)
                {
                    case ProjectQueryNode project:
                        aliases.TryAdd(project.ResultBinding, ShapeAlias(project.ResultShape));
                        break;
                    case AggregateQueryNode aggregate:
                        aliases.TryAdd(aggregate.ResultBinding, ShapeAlias(aggregate.ResultShape));
                        break;
                }
            }
            aliases.TryAdd(branch.Binding, ShapeAlias(branch.Shape));
            return aliases;
        }

        public PostgresRelationQueryCompiledArtifact Compile(RelationQueryNativeCompilationRequest nativeRequest)
        {
            var prepared = Prepare();
            var provenance = RelationQueryNativeCompilationProvenanceFactory.Create(
                nativeRequest,
                branch.Id,
                CompilerProfile,
                storageBinding.ConventionSetVersion
                ?? PostgresRelationQueryTargetProfile.DefaultConventionSetVersion,
                [.. coveredNodes.OrderBy(static node => node.Value, StringComparer.Ordinal)],
                [.. coveredAssignments.OrderBy(static assignment => assignment.Value, StringComparer.Ordinal)],
                [.. selectedFieldContracts.Select(static field => field.Input.Id)
                    .OrderBy(static input => input.Value, StringComparer.Ordinal)]);
            var fingerprint = PostgresRelationQueryArtifactFingerprinter.Compute(
                PostgresRelationQueryCompiledArtifact.CurrentSchemaVersion,
                branch,
                prepared.Statement,
                storageBinding,
                prepared.SelectedFields,
                prepared.Terminal.ResultFields,
                prepared.Terminal.Presence,
                prepared.SuppliedFields,
                prepared.Parameters,
                paging,
                prepared.Terminal.RelationKey,
                prepared.Terminal.Invariants,
                [.. decisions],
                provenance);
            return new(
                PostgresRelationQueryCompiledArtifact.CurrentSchemaVersion,
                branch,
                prepared.Statement,
                storageBinding,
                prepared.SelectedFields,
                prepared.Terminal.ResultFields,
                prepared.Terminal.Presence,
                prepared.SuppliedFields,
                prepared.Parameters,
                paging,
                prepared.Terminal.RelationKey,
                prepared.Terminal.Invariants,
                [.. decisions],
                provenance,
                fingerprint);
        }

        public void Validate() => _ = Prepare();

        PreparedBranch Prepare()
        {
            HashSet<RelationQueryInputId> branchInputs =
            [
                .. branchSelection.Sources.Select(static source => source.Input.Id),
                .. branchSelection.Traversals.Select(static traversal => traversal.Input.Id)
            ];
            var executionDomains = storageBinding.Tables
                .Where(table => branchInputs.Contains(table.Input))
                .Select(table => request.Placement.SourceInstances.Single(source => source.Id == table.Source).ExecutionDomain)
                .Distinct()
                .ToArray();
            if (executionDomains.Length > 1)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.CrossSourceJoin,
                    "A native PostgreSQL statement cannot cross declared source execution domains.",
                    branch.Node);
            }
            if (request.ProfileFeasibility.Observability.OccurrenceProvenance
                != RelationQueryOccurrenceProvenanceMode.NotRequested)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.ResultObservabilityUnsupported,
                    "The current PostgreSQL compiler returns values and binding-presence evidence, not contributor-occurrence lineage.",
                    branch.Node);
            }

            ValidateRelationRootCorrelation();

            var scope = CompileNode(branch.Node);
            var terminal = CompileTerminal(scope);
            var statement = terminal.Query.ToCommandTemplate(PostgresSqlDialect.Instance);
            var supplied = CreateSuppliedBindings(statement);
            var parameterBindings = CreateParameterBindings(statement);
            var selected = CreateSelectedFields();
            return new(statement, selected, terminal, supplied, parameterBindings);
        }

        void ValidateRelationRootCorrelation()
        {
            if (branch.Kind != RelationQueryNativeResultKind.RelationRows
                || request.Plan.ExecutionSlice.RelationOutput is not { } relation
                || relation.Definition.Mode == RelationOutputMode.Set)
            {
                return;
            }

            var rootPlacements = placements.Values
                .Where(placement => placement.Binding == relation.RootBinding)
                .ToArray();
            if (rootPlacements.Length != 1
                || rootPlacements[0].Acquisition != RelationQuerySourceAcquisitionKind.Supplied)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "The current PostgreSQL compiler can correlate rooted non-set relation rows only when exactly one root occurrence is supplied per invocation.",
                    branch.Node);
            }

            decisions.Add(Decision(
                PostgresRelationQueryLoweringDecisionKind.RelationRootCorrelation,
                "postgres/supplied-root-invocation-correlation/v1",
                branch.Node,
                [rootPlacements[0].Id]));
        }

        Scope CompileNode(QueryNodeId node)
        {
            if (scopes.TryGetValue(node, out var cached))
            {
                return cached;
            }

            if (!nodes.TryGetValue(node, out var execution))
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    $"Branch node '{node.Value}' is absent from the demand-scoped execution slice.",
                    node);
            }
            if (!coveredNodes.Add(node))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "The selected branch contains a cycle.", node);
            }

            var compiled = execution.CanonicalNode switch
            {
                SourceQueryNode source => CompileSource(execution, source),
                TraverseRelationshipQueryNode traversal => CompileTraversal(execution, traversal),
                FilterQueryNode filter => CompileFilter(execution, filter),
                ProjectQueryNode project => CompileProject(execution, project),
                JoinQueryNode join => CompileJoin(execution, join),
                TemporalJoinQueryNode temporal => CompileTemporalJoin(execution, temporal),
                DistinctQueryNode distinct => CompileDistinct(execution, distinct),
                AggregateQueryNode aggregate => CompileAggregate(execution, aggregate),
                OrderQueryNode order => CompileOrder(execution, order),
                PageQueryNode page => CompilePage(execution, page),
                ExpandCollectionQueryNode => throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                    "The current PostgreSQL compiler does not yet lower canonical collection expansion.",
                    node),
                _ => throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                    $"Logical node '{execution.CanonicalNode.GetType().Name}' is outside the current PostgreSQL compiler closure.",
                    node)
            };
            scopes.Add(node, compiled);
            return compiled;
        }

        Scope CompileSource(RelationQueryExecutionNode execution, SourceQueryNode source)
        {
            if (!sourcesByNode.TryGetValue(source.Id, out var contract))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    "Source node has no demand-scoped source contract.", source.Id);
            }

            if (!placements.TryGetValue(contract.Input.Id, out var placement))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch,
                    "Source contract has no exact placement binding.", source.Id, contract.Input.Id);
            }

            if (placement.Acquisition == RelationQuerySourceAcquisitionKind.Supplied
                && !IsSupportedSuppliedSingleton(source, placement))
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "The current PostgreSQL compiler can lower a supplied source only when it is the single relation-root occurrence for this invocation; a supplied source set cannot be collapsed to one parameter row.",
                    source.Id,
                    contract.Input.Id);
            }

            var fields = selectedFieldContracts.Where(field => field.Input.Producer == source.Id).ToArray();
            var table = placement.Acquisition == RelationQuerySourceAcquisitionKind.Supplied
                ? null
                : storageBinding.ResolveTable(contract.Input.Id);
            string? sourceAlias = null;
            var builder = table is null
                ? new SqlSelectBuilder()
                : CreatePhysicalBuilder(table, out sourceAlias);
            Dictionary<FieldKey, ScopedValue> values = [];
            Dictionary<ValueBindingId, ScopedIdentity> identities = [];
            Dictionary<RelationshipKey, ScopedValue> references = [];

            foreach (var field in fields)
            {
                var valueContract = RequireValueContract(field.Input);
                var alias = ValueAlias(field.Input.Field.Shape, field.Input.Field.Path);
                ScopedValue value;
                if (table is null)
                {
                    if (valueContract is
                        {
                            Presence: FieldPresence.Optional,
                            Nullability: FieldNullability.Nullable
                        })
                    {
                        throw Fail(
                            PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                            "One supplied PostgreSQL parameter cannot distinguish semantic Undefined from explicit Null.",
                            source.Id,
                            field.Input.Id);
                    }
                    var encoding = ResolveEncoding(valueContract, source.Id);
                    var runtimeBinding = SuppliedPrefix + field.Input.Id.Value;
                    builder.Select(SqlExpression.RuntimeParameter(runtimeBinding), alias);
                    value = new(alias, valueContract, encoding, Text: null,
                        PostgresRelationQueryOrderingCapability.None, field.Input.Id, Placement: null,
                        RuntimeTextBinding: encoding == PostgresRelationQueryValueEncoding.Text
                            ? runtimeBinding
                            : null);
                }
                else
                {
                    var physical = ResolveField(table, field.Input.Id, source.Id);
                    ValidatePhysicalValue(valueContract, physical, source.Id, field.Input.Id);
                    builder.Select(SqlExpression.Column(sourceAlias!, physical.ColumnName), alias);
                    value = new(alias, valueContract, Convert(physical.ScalarType), physical.TextSemantics,
                        physical.Ordering, field.Input.Id, table.PlacementBinding);
                }
                values.Add(new(field.Input.Binding, field.Input.Field.Path), value);
            }

            if (table is not null)
            {
                AddPhysicalInternals(builder, sourceAlias!, table, source.Binding, values, identities, references);
            }
            else
            {
                AddSuppliedReferences(contract, values, references);
            }

            EnsureProjection(builder, values, identities, references);
            decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.SourceTable,
                table is null ? "postgres/supplied-single-row/v1" : "postgres/table-scan/v1", source.Id,
                table is null ? [] : [table.PlacementBinding]));
            return new(builder.BuildQuery(), values, identities, references,
                new Dictionary<ValueBindingId, OuterPresence>(), []);
        }

        bool IsSupportedSuppliedSingleton(
            SourceQueryNode source,
            RelationQuerySourcePlacementBinding placement)
        {
            var relation = request.Plan.ExecutionSlice.RelationOutput;
            return relation is not null
                   && relation.Definition.Mode != RelationOutputMode.Set
                   && relation.RootBinding == source.Binding
                   && placement.Binding == source.Binding
                   && placements.Values.Count(candidate => candidate.Binding == relation.RootBinding) == 1;
        }

        Scope CompileTraversal(
            RelationQueryExecutionNode execution,
            TraverseRelationshipQueryNode traversal)
        {
            if (!traversalsByNode.TryGetValue(traversal.Id, out var contract))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.RelationshipEndpointMissing,
                    "Traversal node has no demand-scoped relationship contract.", traversal.Id);
            }

            var left = CompileUnaryInput(execution);
            var table = ResolveTable(contract.Input.Id, traversal.Id);
            var right = CompileTraversalTarget(contract, table);
            if (contract.JoinKind == JoinKind.Left)
            {
                right = EnsureOuterPresenceMarkers(right, traversal.Id);
            }

            var leftAlias = RelationAlias($"{ShapeAlias(contract.FromShape)}_rows");
            var rightAlias = RelationAlias($"{ShapeAlias(contract.ResultShape)}_rows");
            var leftEnvironment = CreateEnvironment(left, leftAlias);
            var rightEnvironment = CreateEnvironment(right, rightAlias);

            CompiledExpression leftKey;
            CompiledExpression rightKey;
            if (contract.Input.Direction == RelationshipTraversalDirection.Forward)
            {
                leftKey = ResolveForwardReference(left, leftEnvironment, contract);
                rightKey = ResolveIdentity(right, rightEnvironment, contract.Result, contract.Input.Id, traversal.Id);
            }
            else
            {
                leftKey = ResolveIdentity(left, leftEnvironment, contract.From, contract.Input.Id, traversal.Id);
                rightKey = ResolveReference(right, rightEnvironment, contract.Result, contract.Input.Id, traversal.Id);
            }
            var predicate = CompileEquality(leftKey, rightKey, traversal.Id, nullSafe: false);
            var builder = new SqlSelectBuilder(left.Query, leftAlias);
            builder.Join(right.Query, rightAlias, ConvertJoin(contract.JoinKind, traversal.Id), predicate.Expression);
            var combined = ProjectCombined(builder, left, leftEnvironment, right, rightEnvironment);
            if (contract.JoinKind == JoinKind.Left)
            {
                AddOuterPresence(combined, right, rightEnvironment);
            }

            decisions.Add(Decision(
                PostgresRelationQueryLoweringDecisionKind.RelationshipTraversalJoin,
                contract.Input.Direction == RelationshipTraversalDirection.Forward
                    ? "postgres/relationship-forward-identity-join/v1"
                    : "postgres/relationship-inverse-reference-join/v1",
                traversal.Id,
                [table.PlacementBinding],
                contract.Definition.Id));
            return combined.Build(builder.BuildQuery());
        }

        Scope CompileTraversalTarget(
            RelationQueryTraversalInputContract contract,
            PostgresRelationQueryTableBinding table)
        {
            var tableAlias = RelationAlias(table.TableName);
            var builder = new SqlSelectBuilder(
                new SqlQualifiedTable(table.SchemaName, table.TableName),
                tableAlias);
            Dictionary<FieldKey, ScopedValue> values = [];
            Dictionary<ValueBindingId, ScopedIdentity> identities = [];
            Dictionary<RelationshipKey, ScopedValue> references = [];
            foreach (var field in selectedFieldContracts.Where(field => field.Input.Producer == contract.Input.Traversal))
            {
                var physical = ResolveField(table, field.Input.Id, contract.Input.Traversal);
                var valueContract = RequireValueContract(field.Input);
                ValidatePhysicalValue(valueContract, physical, contract.Input.Traversal, field.Input.Id);
                var alias = ValueAlias(field.Input.Field.Shape, field.Input.Field.Path);
                builder.Select(SqlExpression.Column(tableAlias, physical.ColumnName), alias);
                values.Add(new(field.Input.Binding, field.Input.Field.Path), new(
                    alias,
                    valueContract,
                    Convert(physical.ScalarType),
                    physical.TextSemantics,
                    physical.Ordering,
                    field.Input.Id,
                    table.PlacementBinding));
            }
            AddPhysicalInternals(builder, tableAlias, table, contract.Result, values, identities, references);
            EnsureProjection(builder, values, identities, references);
            return new(builder.BuildQuery(), values, identities, references,
                new Dictionary<ValueBindingId, OuterPresence>(), []);
        }

        Scope CompileFilter(RelationQueryExecutionNode execution, FilterQueryNode filter)
        {
            var input = CompileUnaryInput(execution);
            var alias = RelationAlias("filtered_rows");
            var environment = CreateEnvironment(input, alias);
            var site = execution.ExpressionSites.Single(static site => site.Kind == RelationQueryExpressionSiteKind.FilterPredicate);
            var predicate = CompileExpression(filter.Predicate, site, environment, requireNonNull: true);
            RequireBoolean(predicate, execution.Id, "filter predicate");
            var builder = new SqlSelectBuilder(input.Query, alias);
            PassThrough(builder, input, environment);
            builder.Where(predicate.Expression);
            ApplyOrder(builder, input, alias);
            decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.Filter,
                "postgres/where-predicate/v1", execution.Id));
            return input.WithQuery(builder.BuildQuery());
        }

        Scope CompileProject(RelationQueryExecutionNode execution, ProjectQueryNode project)
        {
            var input = CompileUnaryInput(execution);
            var alias = RelationAlias($"{ShapeAlias(project.ResultShape)}_input");
            var environment = CreateEnvironment(input, alias);
            var builder = new SqlSelectBuilder(input.Query, alias);
            Dictionary<FieldKey, ScopedValue> values = [];
            foreach (var assignment in execution.ProjectionAssignments.Where(assignment =>
                         IsOutputPathDemanded(project.ResultBinding, assignment.Definition.Target)))
            {
                var compiled = CompileExpression(
                    assignment.Definition.Value,
                    assignment.ValueSite,
                    environment,
                    requireNonNull: false);
                var resultAlias = ValueAlias(project.ResultShape, assignment.Definition.Target);
                builder.Select(compiled.Expression, resultAlias);
                values.Add(new(project.ResultBinding, assignment.Definition.Target), compiled.ToScoped(resultAlias,
                    assignment.Definition.Id));
                coveredAssignments.Add(assignment.Definition.Id);
            }
            Dictionary<ValueBindingId, OuterPresence> outerPresence = [];
            foreach (var presence in input.OuterPresence)
            {
                builder.Select(SqlExpression.Column(alias, presence.Value.Alias), presence.Value.Alias);
                outerPresence.Add(presence.Key, presence.Value);
            }
            foreach (var ordering in input.Orderings)
            {
                builder.Select(SqlExpression.Column(alias, ordering.Alias), ordering.Alias);
            }
            ApplyOrder(builder, input, alias);
            if (values.Count == 0)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "Demanded projection retained no assignments.", project.Id);
            }

            decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.Projection,
                "postgres/select-projection/v1", execution.Id));
            return new(builder.BuildQuery(), values,
                new Dictionary<ValueBindingId, ScopedIdentity>(),
                new Dictionary<RelationshipKey, ScopedValue>(),
                outerPresence, input.Orderings);
        }

        Scope CompileJoin(RelationQueryExecutionNode execution, JoinQueryNode join)
        {
            if (execution.LogicalPlan.EffectiveInputs.Length != 2)
            {
                throw Topology(execution.Id, "An explicit join requires two effective inputs.");
            }

            var left = CompileNode(execution.LogicalPlan.EffectiveInputs[0]);
            var right = CompileNode(execution.LogicalPlan.EffectiveInputs[1]);
            if (join.Kind == JoinKind.Left)
            {
                right = EnsureOuterPresenceMarkers(right, join.Id);
            }

            var leftAlias = RelationAlias("join_left_rows");
            var rightAlias = RelationAlias("join_right_rows");
            var leftEnvironment = CreateEnvironment(left, leftAlias);
            var rightEnvironment = CreateEnvironment(right, rightAlias);
            var environment = Merge(leftEnvironment, rightEnvironment, execution.Id);
            var site = execution.ExpressionSites.Single(static site =>
                site.Kind == RelationQueryExpressionSiteKind.JoinPredicate);
            var predicate = CompileExpression(join.Predicate, site, environment, requireNonNull: true);
            RequireBoolean(predicate, join.Id, "join predicate");
            var builder = new SqlSelectBuilder(left.Query, leftAlias);
            builder.Join(right.Query, rightAlias, ConvertJoin(join.Kind, join.Id), predicate.Expression);
            var combined = ProjectCombined(builder, left, leftEnvironment, right, rightEnvironment);
            if (join.Kind == JoinKind.Left)
            {
                AddOuterPresence(combined, right, rightEnvironment);
            }

            decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.ExplicitJoin,
                "postgres/explicit-derived-join/v1", join.Id));
            return combined.Build(builder.BuildQuery());
        }

        Scope CompileTemporalJoin(RelationQueryExecutionNode execution, TemporalJoinQueryNode temporal)
        {
            if (execution.LogicalPlan.EffectiveInputs.Length != 2 || execution.TemporalJoin is null)
            {
                throw Topology(execution.Id, "A temporal join requires two effective inputs and prepared temporal semantics.");
            }

            var left = CompileNode(execution.LogicalPlan.EffectiveInputs[0]);
            var right = CompileNode(execution.LogicalPlan.EffectiveInputs[1]);
            if (temporal.Kind == JoinKind.Left)
            {
                right = EnsureOuterPresenceMarkers(right, temporal.Id);
            }

            var leftAlias = RelationAlias("temporal_left_rows");
            var rightAlias = RelationAlias("temporal_right_rows");
            var leftEnvironment = CreateEnvironment(left, leftAlias);
            var rightEnvironment = CreateEnvironment(right, rightAlias);
            var environment = Merge(leftEnvironment, rightEnvironment, execution.Id);
            var prepared = execution.TemporalJoin;
            var correlation = CompileExpression(
                temporal.Correlation,
                prepared.CorrelationSite,
                environment,
                requireNonNull: true);
            RequireBoolean(correlation, temporal.Id, "temporal correlation");
            var temporalPredicate = CompileTemporalPredicate(prepared, environment, temporal.Id);
            var predicate = SqlExpression.Binary(
                SqlBinaryOperator.And,
                correlation.Expression,
                temporalPredicate);
            var builder = new SqlSelectBuilder(left.Query, leftAlias);
            builder.Join(right.Query, rightAlias, ConvertJoin(temporal.Kind, temporal.Id), predicate);
            var combined = ProjectCombined(builder, left, leftEnvironment, right, rightEnvironment);
            if (temporal.Kind == JoinKind.Left)
            {
                AddOuterPresence(combined, right, rightEnvironment);
            }

            decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.TemporalJoin,
                "postgres/valid-time-derived-join/v1", temporal.Id));
            return combined.Build(builder.BuildQuery());
        }

        Scope CompileDistinct(RelationQueryExecutionNode execution, DistinctQueryNode distinct)
        {
            var input = CompileUnaryInput(execution);
            if (!distinct.Keys.IsDefaultOrEmpty)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                    "The current PostgreSQL compiler supports whole-row distinctness; keyed representative selection requires explicit ordering semantics.",
                    distinct.Id);
            }
            if (input.Identities.Count != 0 || input.References.Count != 0 || input.OuterPresence.Count != 0)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Whole-row DISTINCT cannot include hidden identity or relationship columns without changing canonical row equality.",
                    distinct.Id);
            }
            if (!input.Orderings.IsDefaultOrEmpty)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Whole-row DISTINCT cannot promise the preceding canonical order without an explicit representative-order strategy.",
                    distinct.Id);
            }
            var alias = RelationAlias("distinct_input");
            var environment = CreateEnvironment(input, alias);
            var builder = new SqlSelectBuilder(input.Query, alias);
            foreach (var value in input.Values.OrderBy(static pair => pair.Key, FieldKeyComparer.Instance))
            {
                var distinctValue = RequireEqualitySemantics(environment.Values[value.Key], distinct.Id);
                builder.Select(distinctValue.Expression, value.Value.Alias);
            }
            builder.Distinct();
            decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.Distinct,
                "postgres/select-distinct/v1", distinct.Id));
            return new(builder.BuildQuery(), input.Values,
                new Dictionary<ValueBindingId, ScopedIdentity>(),
                new Dictionary<RelationshipKey, ScopedValue>(),
                new Dictionary<ValueBindingId, OuterPresence>(), []);
        }

        Scope CompileAggregate(RelationQueryExecutionNode execution, AggregateQueryNode aggregate)
        {
            var input = CompileUnaryInput(execution);
            var alias = RelationAlias($"{ShapeAlias(aggregate.ResultShape)}_input");
            var environment = CreateEnvironment(input, alias);
            var builder = new SqlSelectBuilder(input.Query, alias);
            Dictionary<FieldKey, ScopedValue> values = [];
            // Every grouping participates in row partitioning even when its projected target is not demanded.
            // Dropping an unselected grouping would turn per-key aggregates into one global aggregate.
            foreach (var grouping in execution.AggregateGroupings)
            {
                var compiled = CompileExpression(grouping.Definition.Key, grouping.KeySite, environment, requireNonNull: true);
                if (!compiled.PresenceDependencies.IsDefaultOrEmpty)
                {
                    throw Fail(
                        PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        "Canonical grouping over an outer-absent value requires explicit presence keys.",
                        aggregate.Id);
                }
                compiled = RequireEqualitySemantics(compiled, aggregate.Id);
                var resultAlias = ValueAlias(aggregate.ResultShape, grouping.Definition.Target);
                builder.Select(compiled.Expression, resultAlias);
                builder.GroupBy(compiled.Expression);
                values.Add(new(aggregate.ResultBinding, grouping.Definition.Target),
                    compiled.ToScoped(resultAlias, grouping.Definition.Id));
                coveredAssignments.Add(grouping.Definition.Id);
            }
            foreach (var assignment in execution.AggregateAssignments.Where(assignment =>
                         IsOutputPathDemanded(aggregate.ResultBinding, assignment.Definition.Target)))
            {
                var compiled = CompileAggregateAssignment(
                    assignment,
                    environment,
                    aggregate.Id,
                    groupedInputIsNonEmpty: !execution.AggregateGroupings.IsDefaultOrEmpty
                                            && assignment.Definition.Filter is null);
                var resultAlias = ValueAlias(aggregate.ResultShape, assignment.Definition.Target);
                builder.Select(compiled.Expression, resultAlias);
                values.Add(new(aggregate.ResultBinding, assignment.Definition.Target),
                    compiled.ToScoped(resultAlias, assignment.Definition.Id));
                coveredAssignments.Add(assignment.Definition.Id);
            }
            decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.Aggregation,
                "postgres/grouped-aggregate/v1", aggregate.Id));
            return new(builder.BuildQuery(), values,
                new Dictionary<ValueBindingId, ScopedIdentity>(),
                new Dictionary<RelationshipKey, ScopedValue>(),
                new Dictionary<ValueBindingId, OuterPresence>(), []);
        }

        Scope CompileOrder(RelationQueryExecutionNode execution, OrderQueryNode order)
        {
            var input = CompileUnaryInput(execution);
            var alias = RelationAlias("ordered_rows");
            var environment = CreateEnvironment(input, alias);
            var builder = new SqlSelectBuilder(input.Query, alias);
            PassThrough(builder, input, environment);
            List<ScopedOrder> orderings = [];
            for (var index = 0; index < order.Orderings.Length; index++)
            {
                var definition = order.Orderings[index];
                var site = execution.OrderKeys.Single(candidate => candidate.Ordinal == index);
                var compiled = CompileExpression(definition.Key, site, environment, requireNonNull: false);
                compiled = RequireOrderable(compiled, order.Id);
                var keyAlias = ValueAlias("__order", ExpressionAlias(definition.Key, "expression"));
                builder.Select(compiled.Expression, keyAlias);
                var direction = definition.Direction == QuerySortDirection.Ascending
                    ? SqlSortDirection.Ascending
                    : SqlSortDirection.Descending;
                var nulls = definition.NullPlacement == QueryNullPlacement.First
                    ? SqlNullPlacement.First
                    : SqlNullPlacement.Last;
                builder.OrderBy(compiled.Expression, direction, nulls);
                orderings.Add(new(keyAlias, direction, nulls, compiled));
            }
            RequireStableOrdering(orderings, order.Id);
            decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.Ordering,
                "postgres/order-by/v1", order.Id));
            return input.WithQuery(builder.BuildQuery(), [.. orderings]);
        }

        Scope CompilePage(RelationQueryExecutionNode execution, PageQueryNode page)
        {
            var input = CompileUnaryInput(execution);
            if (input.Orderings.IsDefaultOrEmpty)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Canonical PostgreSQL paging requires a preceding explicit order node.", page.Id);
            }

            RequireStableOrdering(input.Orderings, page.Id);
            if (page.Page.Limit > PostgresRelationQueryTargetProfile.MaximumPageSize)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    $"Page size {page.Page.Limit} exceeds the current PostgreSQL boundary of {PostgresRelationQueryTargetProfile.MaximumPageSize}.",
                    page.Id);
            }

            var alias = RelationAlias("paged_rows");
            var environment = CreateEnvironment(input, alias);
            var builder = new SqlSelectBuilder(input.Query, alias);
            PassThrough(builder, input, environment);
            foreach (var ordering in input.Orderings)
            {
                builder.OrderBy(SqlExpression.Column(alias, ordering.Alias), ordering.Direction, ordering.NullPlacement);
            }
            switch (page.Page)
            {
                case OffsetPageDefinition offset:
                    builder.OffsetLimit(offset.Offset, offset.Limit);
                    paging = new(PostgresRelationQueryPagingKind.Offset, offset.Limit, offset.Offset,
                        StableOrderingInputs(input.Orderings, page.Id));
                    decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.OffsetPaging,
                        "postgres/offset-limit/v1", page.Id));
                    break;
                case KeysetPageDefinition keyset:
                    if (keyset.After.IsDefaultOrEmpty)
                    {
                        if (!execution.KeysetBoundaries.IsDefaultOrEmpty)
                        {
                            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                                "An initial keyset page cannot retain continuation-expression metadata.", page.Id);
                        }
                        builder.Limit(keyset.Limit);
                        paging = new(PostgresRelationQueryPagingKind.Keyset, keyset.Limit, 0,
                            StableOrderingInputs(input.Orderings, page.Id));
                        decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.KeysetPaging,
                            "postgres/initial-keyset-limit/v1", page.Id));
                        break;
                    }
                    if (keyset.After.Length != input.Orderings.Length
                        || execution.KeysetBoundaries.Length != input.Orderings.Length)
                    {
                        throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                            "Keyset continuation expressions must align exactly with ordered keys.", page.Id);
                    }
                    var terms = ImmutableArray.CreateBuilder<SqlKeysetTerm>(input.Orderings.Length);
                    for (var index = 0; index < input.Orderings.Length; index++)
                    {
                        var ordering = input.Orderings[index];
                        var continuation = CompileExpression(
                            keyset.After[index],
                            execution.KeysetBoundaries.Single(candidate => candidate.Ordinal == index),
                            environment,
                            requireNonNull: false);
                        var key = new CompiledExpression(
                            SqlExpression.Column(alias, ordering.Alias),
                            ordering.Expression.Contract,
                            ordering.Expression.Encoding,
                            ordering.Expression.Text,
                            ordering.Expression.Ordering,
                            ordering.Expression.SourceInput,
                            ordering.Expression.Placement,
                            Assignment: null,
                            RuntimeTextBinding: ordering.Expression.RuntimeTextBinding,
                            ConstantText: ordering.Expression.ConstantText);
                        (key, continuation) = PrepareComparison(key, continuation, page.Id, ordering: true);
                        terms.Add(new(key.Expression, continuation.Expression, ordering.Direction, ordering.NullPlacement));
                    }
                    builder.Where(SqlExpression.KeysetAfter(terms.MoveToImmutable()));
                    builder.Limit(keyset.Limit);
                    paging = new(PostgresRelationQueryPagingKind.Keyset, keyset.Limit, 0,
                        StableOrderingInputs(input.Orderings, page.Id));
                    decisions.Add(Decision(PostgresRelationQueryLoweringDecisionKind.KeysetPaging,
                        "postgres/null-aware-keyset/v1", page.Id));
                    break;
                default:
                    throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedLogicalOperator,
                        "Unknown canonical paging definition.", page.Id);
            }
            return input.WithQuery(builder.BuildQuery());
        }

        static void RequireStableOrdering(
            IReadOnlyList<ScopedOrder> orderings,
            QueryNodeId node)
        {
            if (orderings.Count == 0)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Canonical stable ordering requires at least one ordering key.",
                    node);
            }

            var final = orderings[^1].Expression;
            const PostgresRelationQueryOrderingCapability required =
                PostgresRelationQueryOrderingCapability.Exact
                | PostgresRelationQueryOrderingCapability.StableUnique;
            if ((final.Ordering & required) != required
                || !IsRequiredNonNull(final.Contract)
                || !final.PresenceDependencies.IsDefaultOrEmpty)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "The final PostgreSQL ordering key must be exact, stable, unique, required, non-null, and independent of outer-null extension.",
                    node,
                    final.SourceInput);
            }
        }

        Scope CompileUnaryInput(RelationQueryExecutionNode execution)
        {
            if (execution.LogicalPlan.EffectiveInputs.Length != 1)
            {
                throw Topology(execution.Id, "A unary logical node requires one effective input.");
            }

            return CompileNode(execution.LogicalPlan.EffectiveInputs[0]);
        }

        TerminalResult CompileTerminal(Scope scope)
        {
            var alias = RelationAlias($"{ShapeAlias(branch.Shape)}_result");
            var environment = CreateEnvironment(scope, alias);
            var builder = new SqlSelectBuilder(scope.Query, alias);
            var resultFields = ImmutableArray.CreateBuilder<PostgresRelationQueryResultFieldBinding>();
            foreach (var field in branch.Fields)
            {
                var key = new FieldKey(branch.Binding, field.Path);
                if (!environment.Values.TryGetValue(key, out var value))
                {
                    throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                        $"Demanded result field '{branch.Binding.Value}:{field.Path}' is absent from the compiled scope.",
                        branch.Node);
                }

                var resultAlias = ResultAlias(field.Path);
                builder.Select(value.Expression, resultAlias);
                resultFields.Add(new(
                    resultAlias,
                    field,
                    value.Contract,
                    value.Encoding,
                    value.Assignment,
                    value.PresenceDependencies));
            }

            var presence = ImmutableArray.CreateBuilder<PostgresRelationQueryPresenceBinding>();
            foreach (var item in scope.OuterPresence.OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal))
            {
                var resultAlias = ResultAlias("__presence", BindingAlias(item.Key));
                builder.Select(SqlExpression.Column(alias, item.Value.Alias), resultAlias);
                presence.Add(new(item.Key, resultAlias, item.Value.Placement));
            }

            PostgresRelationQueryRelationKeyBinding? relationKey = null;
            var invariants = ImmutableArray.CreateBuilder<PostgresRelationQueryInvariantBinding>();
            if (branch.Kind == RelationQueryNativeResultKind.RelationRows)
            {
                var relation = request.Plan.ExecutionSlice.RelationOutput
                    ?? throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.RelationTerminalUnsupported,
                        "Relation branch has no compiled relation terminal metadata.", branch.Node);
                if (relation.KeySite is { } keySite)
                {
                    var compiled = CompileExpression(
                        relation.Definition.Key!, keySite, environment, requireNonNull: true);
                    var keyAlias = ResultAlias("__relation_key");
                    builder.Select(compiled.Expression, keyAlias);
                    relationKey = new(keyAlias, RequireKnown(keySite, branch.Node, "relation key"), compiled.Encoding);
                }
                foreach (var invariant in relation.Invariants)
                {
                    var compiled = CompileExpression(
                        invariant.Definition.Expression,
                        invariant.PredicateSite,
                        environment,
                        requireNonNull: true);
                    RequireBoolean(compiled, branch.Node, $"relation invariant '{invariant.Definition.Name}'");
                    var invariantAlias = ResultAlias("__invariant", invariant.Definition.Name);
                    builder.Select(compiled.Expression, invariantAlias);
                    invariants.Add(new(invariant.Definition.Name, invariantAlias));
                }
            }

            if (resultFields.Count == 0 && presence.Count == 0 && relationKey is null && invariants.Count == 0)
            {
                builder.Select(SqlExpression.Constant(true), ResultAlias("__row"));
            }

            ApplyOrder(builder, scope, alias);
            return new(builder.BuildQuery(), resultFields.ToImmutable(), presence.ToImmutable(), relationKey,
                invariants.ToImmutable());
        }

        CompiledExpression CompileAggregateAssignment(
            RelationQueryAggregateAssignmentExecution assignment,
            Environment environment,
            QueryNodeId node,
            bool groupedInputIsNonEmpty)
        {
            var operation = assignment.Definition.Operation;
            CompiledExpression? value = null;
            if (assignment.ValueSite is { } valueSite)
            {
                var requireNonNull = operation != AggregateOperator.Count;
                value = CompileExpression(
                    assignment.Definition.Value!,
                    valueSite,
                    environment,
                    requireNonNull);
                if (requireNonNull
                    && !IsRequiredNonNull(Analyze(assignment.Definition.Value!, valueSite, "aggregate-value")))
                {
                    throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        "PostgreSQL value aggregates cannot preserve canonical semantics for null or missing operands.",
                        node);
                }
            }
            SqlExpression? filter = null;
            if (assignment.FilterSite is { } filterSite)
            {
                var compiledFilter = CompileExpression(
                    assignment.Definition.Filter!, filterSite, environment, requireNonNull: true);
                RequireBoolean(compiledFilter, node, "aggregate filter");
                filter = compiledFilter.Expression;
            }
            var resultContract = RequireAggregateResultContract(assignment, node);
            return operation switch
            {
                AggregateOperator.Count => new(
                    SqlExpression.Aggregate(
                        SqlAggregateFunction.Count,
                        value?.Expression,
                        filter),
                    resultContract,
                    PostgresRelationQueryValueEncoding.Int64,
                    null,
                    PostgresRelationQueryOrderingCapability.None,
                    null,
                    null,
                    assignment.Definition.Id),
                AggregateOperator.Sum => CompileSum(value!.Value, filter, resultContract, assignment.Definition.Id, node),
                AggregateOperator.Min => CompileNullableAggregate(
                    SqlAggregateFunction.Minimum, value!.Value, filter, resultContract,
                    assignment.Definition.Id, node, groupedInputIsNonEmpty && filter is null),
                AggregateOperator.Max => CompileNullableAggregate(
                    SqlAggregateFunction.Maximum, value!.Value, filter, resultContract,
                    assignment.Definition.Id, node, groupedInputIsNonEmpty && filter is null),
                AggregateOperator.Average => CompileAverage(value!.Value, filter, resultContract,
                    assignment.Definition.Id, node, groupedInputIsNonEmpty && filter is null),
                _ => throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    $"Aggregate operation '{operation}' is outside the exact current PostgreSQL compiler closure.", node)
            };
        }

        CompiledExpression CompileSum(
            CompiledExpression value,
            SqlExpression? filter,
            ValueContract contract,
            QueryAssignmentId assignment,
            QueryNodeId node)
        {
            RequireNumeric(value, node, "sum operand");
            if (ResolveEncoding(contract, node) != PostgresRelationQueryValueEncoding.Numeric)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Canonical sum must expose the exact decimal result domain.", node);
            }

            RequireDecimalAggregateEvidence(
                value,
                PostgresRelationQueryDecimalAggregateGuarantee.SumIntermediateRange,
                node,
                "sum");
            var sum = SqlExpression.Aggregate(SqlAggregateFunction.Sum, value.Expression, filter);
            return new(SqlExpression.Coalesce(sum, SqlExpression.Constant(0m)), contract,
                PostgresRelationQueryValueEncoding.Numeric, null, PostgresRelationQueryOrderingCapability.None,
                null, null, assignment);
        }

        CompiledExpression CompileAverage(
            CompiledExpression value,
            SqlExpression? filter,
            ValueContract contract,
            QueryAssignmentId assignment,
            QueryNodeId node,
            bool groupedInputIsNonEmpty)
        {
            RequireNumeric(value, node, "average operand");
            if (ResolveEncoding(contract, node) != PostgresRelationQueryValueEncoding.Numeric)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Canonical average must expose the exact decimal result domain.", node);
            }

            RequireAggregateAbsence(contract, node, "average", groupedInputIsNonEmpty);
            RequireDecimalAggregateEvidence(
                value,
                PostgresRelationQueryDecimalAggregateGuarantee.SumIntermediateRange
                | PostgresRelationQueryDecimalAggregateGuarantee.AverageRounding,
                node,
                "average");
            return new(SqlExpression.Aggregate(SqlAggregateFunction.Average, value.Expression, filter), contract,
                PostgresRelationQueryValueEncoding.Numeric, null, PostgresRelationQueryOrderingCapability.None,
                null, null, assignment);
        }

        CompiledExpression CompileNullableAggregate(
            SqlAggregateFunction function,
            CompiledExpression value,
            SqlExpression? filter,
            ValueContract contract,
            QueryAssignmentId assignment,
            QueryNodeId node,
            bool groupedInputIsNonEmpty)
        {
            if (ResolveEncoding(contract, node) != value.Encoding)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    "Canonical minimum/maximum result encoding must exactly match its operand encoding.", node);
            }

            if (value.Encoding == PostgresRelationQueryValueEncoding.Text)
            {
                value = RequireOrderable(value, node);
            }

            RequireAggregateAbsence(
                contract,
                node,
                function == SqlAggregateFunction.Minimum ? "minimum" : "maximum",
                groupedInputIsNonEmpty);
            return new(SqlExpression.Aggregate(function, value.Expression, filter), contract, value.Encoding,
                value.Text, PostgresRelationQueryOrderingCapability.None, null, null, assignment);
        }

        void RequireDecimalAggregateEvidence(
            CompiledExpression value,
            PostgresRelationQueryDecimalAggregateGuarantee guarantee,
            QueryNodeId node,
            string operation)
        {
            if (value.SourceInput is not { } input)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    $"Canonical decimal {operation} requires a direct plan-affine field with aggregate-domain evidence.",
                    node);
            }
            var field = storageBinding.Tables
                .SelectMany(static table => table.Fields)
                .SingleOrDefault(candidate => candidate.Input == input);
            if (field?.DecimalAggregates is not { } evidence
                || (evidence.Guarantees & guarantee) != guarantee)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                    $"Canonical decimal {operation} requires explicit persisted '{guarantee}' evidence for '{input.Value}'.",
                    node,
                    input);
            }
        }

        static void RequireAggregateAbsence(
            ValueContract contract,
            QueryNodeId node,
            string operation,
            bool groupedInputIsNonEmpty)
        {
            if (groupedInputIsNonEmpty
                || contract.Presence == FieldPresence.Optional
                && contract.Nullability == FieldNullability.NonNullable)
            {
                return;
            }

            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.AggregateUnsupported,
                $"Canonical {operation} of an empty input is Undefined, but the output contract cannot decode SQL NULL as missing.",
                node);
        }

        ValueContract RequireAggregateResultContract(
            RelationQueryAggregateAssignmentExecution assignment,
            QueryNodeId node)
        {
            var aggregate = (AggregateQueryNode)nodes[node].CanonicalNode;
            var bindingShape = nodes[node].OutputBindings.SingleOrDefault(binding =>
                binding.Binding == aggregate.ResultBinding);
            if (bindingShape.Shape is { } shape
                && TryResolveShapeFieldContract(shape, assignment.Definition.Target, out var contract))
            {
                return contract;
            }

            throw Fail(
                PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Aggregate target '{assignment.Definition.Target}' cannot be resolved in its exact result-shape snapshot.",
                node);
        }

        bool TryResolveShapeFieldContract(
            QualifiedShapeId shapeId,
            FieldPath path,
            out ValueContract contract)
        {
            contract = null!;
            var graph = request.Plan.Provenance.ShapeDocuments
                .SingleOrDefault(document => document.Graph.Id == shapeId.GraphId)
                ?.Graph;
            if (graph is null
                || !graph.TryGetShape(shapeId.ShapeId, out var shape)
                || path.Segments.IsDefaultOrEmpty)
            {
                return false;
            }

            ValueContract? current = null;
            for (var index = 0; index < path.Segments.Length; index++)
            {
                var segment = path.Segments[index];
                if (index == 0)
                {
                    if (segment.Kind != SegmentKind.Field
                        || string.IsNullOrWhiteSpace(segment.Segment)
                        || !shape.TryGetField(segment.Segment, out var field))
                    {
                        return false;
                    }

                    current = ValueContract.FromField(field);
                    continue;
                }

                if (!TryNavigateShapeField(graph, current!, segment, out current))
                {
                    return false;
                }
            }

            contract = current!;
            return true;
        }

        static bool TryNavigateShapeField(
            ShapeGraph graph,
            ValueContract current,
            FieldPathSegment segment,
            out ValueContract? next)
        {
            next = null;
            var effectiveType = current.GetEffectiveType();
            if (segment.Kind == SegmentKind.Element)
            {
                if (effectiveType is not ArrayTypeRef array)
                {
                    return false;
                }

                next = ComposeShapePathValue(current, new(array.ElementType));
                return true;
            }

            if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
            {
                return false;
            }

            switch (effectiveType)
            {
                case ObjectTypeRef objectType:
                    {
                        var field = objectType.Fields.FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, segment.Segment, StringComparison.Ordinal));
                        if (field is null)
                        {
                            return false;
                        }

                        ValueContract child = new(
                            field.Type,
                            cardinality: field.Cardinality,
                            presence: field.Presence,
                            nullability: field.Nullability);
                        next = ComposeShapePathValue(current, child);
                        return true;
                    }
                case NamedTypeRef named
                    when graph.TryGetType(named.TypeId, out var definition)
                         && definition is TypeDefinition.Structural structural
                         && structural.TryGetField(segment.Segment, out var field):
                    next = ComposeShapePathValue(
                        current,
                        new(
                            field.Type,
                            cardinality: field.Cardinality,
                            presence: field.Presence,
                            nullability: field.Nullability));
                    return true;
                default:
                    return false;
            }
        }

        static ValueContract ComposeShapePathValue(
            ValueContract parent,
            ValueContract child) => new(
            child.Type,
            child.Shape,
            child.Cardinality,
            parent.Presence == FieldPresence.Optional || child.Presence == FieldPresence.Optional
                ? FieldPresence.Optional
                : FieldPresence.Required,
            parent.Nullability == FieldNullability.Nullable || child.Nullability == FieldNullability.Nullable
                ? FieldNullability.Nullable
                : FieldNullability.NonNullable);

        SqlExpression CompileTemporalPredicate(
            RelationQueryTemporalJoinExecution temporal,
            Environment environment,
            QueryNodeId node)
        {
            if (temporal.Domain is null)
            {
                if (temporal.Definition.Match is TemporalIntervalOverlapMatch
                    && temporal.Intervals.All(static interval =>
                        interval.Lower.IsStructurallyUnbounded
                        && interval.Upper.IsStructurallyUnbounded))
                {
                    return SqlExpression.Constant(true);
                }
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    "A temporal join without one exact scalar domain cannot be lowered as an unconstrained join.",
                    node);
            }
            return temporal.Definition.Match switch
            {
                TemporalPointInIntervalMatch => CompilePointInInterval(temporal, environment, node),
                TemporalIntervalOverlapMatch => CompileIntervalOverlap(temporal, environment, node),
                _ => throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    "Unknown temporal join match.", node)
            };
        }

        SqlExpression CompilePointInInterval(
            RelationQueryTemporalJoinExecution temporal,
            Environment environment,
            QueryNodeId node)
        {
            var point = CompileExpression(
                ((TemporalPointInIntervalMatch)temporal.Definition.Match).Point,
                temporal.PointSite!, environment, requireNonNull: true);
            var interval = temporal.Intervals[0];
            var lower = CompileBound(interval.Lower, environment, node);
            var upper = CompileBound(interval.Upper, environment, node);
            RequireIntervalValidity(interval, lower, upper, node);
            return AndBounds(
                ComparePointToLower(point, lower, interval.Lower, node),
                ComparePointToUpper(point, upper, interval.Upper, node));
        }

        SqlExpression CompileIntervalOverlap(
            RelationQueryTemporalJoinExecution temporal,
            Environment environment,
            QueryNodeId node)
        {
            var left = temporal.Intervals[0];
            var right = temporal.Intervals[1];
            var leftLower = CompileBound(left.Lower, environment, node);
            var leftUpper = CompileBound(left.Upper, environment, node);
            var rightLower = CompileBound(right.Lower, environment, node);
            var rightUpper = CompileBound(right.Upper, environment, node);
            RequireIntervalValidity(left, leftLower, leftUpper, node);
            RequireIntervalValidity(right, rightLower, rightUpper, node);
            var first = CompareUpperToLower(leftUpper, left.Upper, rightLower, right.Lower, node);
            var second = CompareUpperToLower(rightUpper, right.Upper, leftLower, left.Lower, node);
            var leftNonEmpty = CompileIntervalNonEmpty(left, leftLower, leftUpper, node);
            var rightNonEmpty = CompileIntervalNonEmpty(right, rightLower, rightUpper, node);
            return AndBounds(AndBounds(first, second), AndBounds(leftNonEmpty, rightNonEmpty));
        }

        SqlExpression? CompileIntervalNonEmpty(
            RelationQueryTemporalIntervalExecution interval,
            CompiledExpression? lower,
            CompiledExpression? upper,
            QueryNodeId node)
        {
            if (lower is null || upper is null)
            {
                return null;
            }

            var lowerDefinition = (ExpressionTemporalIntervalBound)interval.Lower.Definition;
            var upperDefinition = (ExpressionTemporalIntervalBound)interval.Upper.Definition;
            if (lowerDefinition.Inclusion == TemporalBoundaryInclusion.Exclusive
                && upperDefinition.Inclusion == TemporalBoundaryInclusion.Exclusive)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    "The current PostgreSQL compiler cannot prove a doubly-exclusive bounded interval non-empty in the canonical discrete temporal domain.",
                    node);
            }

            var comparison = lowerDefinition.Inclusion == TemporalBoundaryInclusion.Inclusive
                             && upperDefinition.Inclusion == TemporalBoundaryInclusion.Inclusive
                ? SqlBinaryOperator.LessThanOrEqual
                : SqlBinaryOperator.LessThan;
            return CompareTemporal(
                lower.Value,
                upper.Value,
                comparison,
                interval.Lower,
                interval.Upper,
                node);
        }

        CompiledExpression? CompileBound(
            RelationQueryTemporalBoundExecution bound,
            Environment environment,
            QueryNodeId node)
        {
            if (bound.IsStructurallyUnbounded)
            {
                return null;
            }

            var definition = (ExpressionTemporalIntervalBound)bound.Definition;
            var compiled = CompileExpression(definition.Value, bound.ValueSite!, environment,
                requireNonNull: definition.NullBehavior == TemporalNullBoundBehavior.Invalid);
            if (definition.NullBehavior == TemporalNullBoundBehavior.Invalid
                && !IsRequiredNonNull(bound.ValueSite!.Analysis.KnownResult))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    "A temporal endpoint with Invalid null behavior is not proven required and non-null.", node);
            }
            if (definition.NullBehavior == TemporalNullBoundBehavior.Unbounded
                && (compiled.Contract.Presence != FieldPresence.Required
                    || compiled.Contract.Nullability != FieldNullability.Nullable
                    || !compiled.PresenceDependencies.IsDefaultOrEmpty))
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    "A null-as-unbounded temporal endpoint must encode explicit Null only; semantic missing or outer-binding absence cannot share SQL NULL.",
                    node);
            }
            return compiled;
        }

        void RequireIntervalValidity(
            RelationQueryTemporalIntervalExecution interval,
            CompiledExpression? lower,
            CompiledExpression? upper,
            QueryNodeId node)
        {
            if (lower is null || upper is null)
            {
                return;
            }

            if (lower.Value.SourceInput is not { } lowerInput
                || upper.Value.SourceInput is not { } upperInput
                || lower.Value.Placement is not { } lowerPlacement
                || upper.Value.Placement is not { } upperPlacement
                || lowerPlacement != upperPlacement)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    "A bounded temporal interval requires trusted validity evidence for two direct fields on one PostgreSQL table.",
                    node);
            }

            var lowerField = selectedFieldContracts.SingleOrDefault(field => field.Input.Id == lowerInput);
            var upperField = selectedFieldContracts.SingleOrDefault(field => field.Input.Id == upperInput);
            var table = storageBinding.Tables.SingleOrDefault(candidate =>
                candidate.PlacementBinding == lowerPlacement);
            if (lowerField is null || upperField is null || table is null)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    "A temporal interval endpoint is not attributable to an exact selected PostgreSQL field.",
                    node);
            }

            PostgresRelationQueryIntervalValidityBinding validity;
            try
            {
                validity = table.ResolveIntervalValidity(lowerInput, upperInput);
            }
            catch (KeyNotFoundException)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    $"PostgreSQL table '{table.PlacementBinding.Value}' lacks trusted interval-validity evidence for the canonical endpoint pair.",
                    node,
                    lowerInput);
            }

            var lowerDefinition = (ExpressionTemporalIntervalBound)interval.Lower.Definition;
            var upperDefinition = (ExpressionTemporalIntervalBound)interval.Upper.Definition;
            if (validity.LowerPath != lowerField.Input.Field.Path
                || validity.UpperPath != upperField.Input.Field.Path
                || validity.LowerNullBehavior != lowerDefinition.NullBehavior
                || validity.UpperNullBehavior != upperDefinition.NullBehavior)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.TemporalJoinUnsupported,
                    "PostgreSQL interval-validity evidence does not match the canonical endpoint paths and null-bound semantics.",
                    node,
                    lowerInput);
            }
        }

        SqlExpression? ComparePointToLower(
            CompiledExpression point,
            CompiledExpression? lower,
            RelationQueryTemporalBoundExecution definition,
            QueryNodeId node)
        {
            if (lower is null)
            {
                return null;
            }

            var op = ((ExpressionTemporalIntervalBound)definition.Definition).Inclusion == TemporalBoundaryInclusion.Inclusive
                ? SqlBinaryOperator.GreaterThanOrEqual
                : SqlBinaryOperator.GreaterThan;
            return CompareTemporal(point, lower.Value, op, leftBound: null, definition, node);
        }

        SqlExpression? ComparePointToUpper(
            CompiledExpression point,
            CompiledExpression? upper,
            RelationQueryTemporalBoundExecution definition,
            QueryNodeId node)
        {
            if (upper is null)
            {
                return null;
            }

            var op = ((ExpressionTemporalIntervalBound)definition.Definition).Inclusion == TemporalBoundaryInclusion.Inclusive
                ? SqlBinaryOperator.LessThanOrEqual
                : SqlBinaryOperator.LessThan;
            return CompareTemporal(point, upper.Value, op, leftBound: null, definition, node);
        }

        SqlExpression? CompareUpperToLower(
            CompiledExpression? upper,
            RelationQueryTemporalBoundExecution upperDefinition,
            CompiledExpression? lower,
            RelationQueryTemporalBoundExecution lowerDefinition,
            QueryNodeId node)
        {
            if (upper is null || lower is null)
            {
                return null;
            }

            var inclusive = ((ExpressionTemporalIntervalBound)upperDefinition.Definition).Inclusion
                                == TemporalBoundaryInclusion.Inclusive
                            && ((ExpressionTemporalIntervalBound)lowerDefinition.Definition).Inclusion
                                == TemporalBoundaryInclusion.Inclusive;
            return CompareTemporal(
                upper.Value,
                lower.Value,
                inclusive ? SqlBinaryOperator.GreaterThanOrEqual : SqlBinaryOperator.GreaterThan,
                upperDefinition,
                lowerDefinition,
                node);
        }

        SqlExpression CompareTemporal(
            CompiledExpression left,
            CompiledExpression right,
            SqlBinaryOperator @operator,
            RelationQueryTemporalBoundExecution? leftBound,
            RelationQueryTemporalBoundExecution? rightBound,
            QueryNodeId node)
        {
            (left, right) = PrepareComparison(left, right, node, ordering: true);
            var result = SqlExpression.Binary(@operator, left.Expression, right.Expression);
            if (rightBound?.Definition is ExpressionTemporalIntervalBound
                { NullBehavior: TemporalNullBoundBehavior.Unbounded })
            {
                result = SqlExpression.Binary(
                    SqlBinaryOperator.Or,
                    SqlExpression.IsNull(right.Expression),
                    result);
            }
            if (leftBound?.Definition is ExpressionTemporalIntervalBound
                { NullBehavior: TemporalNullBoundBehavior.Unbounded })
            {
                result = SqlExpression.Binary(
                    SqlBinaryOperator.Or,
                    SqlExpression.IsNull(left.Expression),
                    result);
            }
            return result;
        }

        static SqlExpression AndBounds(SqlExpression? left, SqlExpression? right) =>
            left is null ? right ?? SqlExpression.Constant(true)
            : right is null ? left
            : SqlExpression.Binary(SqlBinaryOperator.And, left, right);

        CompiledExpression CompileExpression(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            Environment environment,
            bool requireNonNull)
        {
            var result = expression switch
            {
                FieldExpr field => CompileField(field.Path, field.Binding, site, environment),
                FieldRefExpr field => CompileField(field.Path, explicitBinding: null, site, environment),
                ParameterExpr parameter => CompileParameter(parameter, site),
                ConstantExpr constant => CompileConstant(constant.Value, site, expression),
                LiteralExpr literal => CompileConstant(literal.Value, site, expression),
                UnaryExpr { Operator: UnaryOperator.Not } unary => CompileNot(unary, site, environment),
                BinaryExpr binary => CompileBinary(binary, site, environment),
                ConditionalExpr conditional => CompileConditional(conditional, site, environment),
                CallExpr call when string.Equals(call.Function, ExprFunctionNames.EndsWith, StringComparison.Ordinal)
                                   && call.Arguments.Length == 2 => CompileEndsWith(call, site, environment),
                CallExpr call when string.Equals(call.Function, ExprFunctionNames.StartsWith, StringComparison.Ordinal)
                                   && call.Arguments.Length == 2 => CompileStartsWith(call, site, environment),
                CallExpr call when string.Equals(call.Function, ExprFunctionNames.TextContains, StringComparison.Ordinal)
                                   && call.Arguments.Length == 2 => CompileTextContains(call, site, environment),
                AggregateExpr => throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Embedded aggregate expressions are unsupported; use a canonical aggregate node.",
                    site.Node ?? branch.Node),
                _ => throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Expression node '{expression.GetType().Name}' is outside the current PostgreSQL compiler closure.",
                    site.Node ?? branch.Node)
            };
            if (result.Contract.Presence == FieldPresence.Optional
                && result.Contract.Nullability == FieldNullability.Nullable)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "One PostgreSQL scalar cannot distinguish semantic Undefined from explicit Null for an optional nullable expression.",
                    site.Node ?? branch.Node);
            }
            if (requireNonNull && !IsRequiredNonNull(Analyze(expression, site, "required-value")))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Expression may be missing or null where exact PostgreSQL lowering requires a value.",
                    site.Node ?? branch.Node);
            }

            if (expressionDecisions.Add(site.Analysis.Site.Id))
            {
                decisions.Add(new(
                    PostgresRelationQueryLoweringDecisionKind.Expression,
                    "postgres/closed-expression/v1",
                    site.Node ?? branch.Node,
                    site.Analysis.Site.Id,
                    site.Assignment));
            }
            return result;
        }

        CompiledExpression CompileField(
            FieldPath path,
            ValueBindingId? explicitBinding,
            RelationQueryExpressionSiteAnalysis site,
            Environment environment)
        {
            var resolved = ResolveFieldRoot(path, explicitBinding, site);
            if (!environment.Values.TryGetValue(resolved, out var value))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Field '{resolved.Binding.Value}:{resolved.Path}' is absent from the compiled SQL scope.",
                    site.Node ?? branch.Node);
            }

            return value;
        }

        CompiledExpression CompileParameter(ParameterExpr parameter, RelationQueryExpressionSiteAnalysis site)
        {
            QueryParameterId id = new(parameter.Parameter);
            if (!parameters.TryGetValue(id, out var contract))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Canonical parameter '{parameter.Parameter}' is absent from the demand-scoped input contract.",
                    site.Node ?? branch.Node);
            }

            if (contract.Definition.Presence == FieldPresence.Optional
                && contract.Definition.DefaultKind == QueryParameterDefaultKind.None)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                    $"Optional parameter '{parameter.Parameter}' has no default and cannot encode semantic undefined.",
                    site.Node ?? branch.Node,
                    contract.Input.Id);
            }
            return new(
                SqlExpression.RuntimeParameter(ParameterPrefix + id.Value),
                contract.ValueContract,
                ResolveEncoding(contract.ValueContract, site.Node ?? branch.Node),
                null,
                PostgresRelationQueryOrderingCapability.None,
                null,
                null,
                null,
                RuntimeTextBinding: ResolveEncoding(contract.ValueContract, site.Node ?? branch.Node)
                                    == PostgresRelationQueryValueEncoding.Text
                    ? ParameterPrefix + id.Value
                    : null);
        }

        CompiledExpression CompileConstant(
            ObservationValue value,
            RelationQueryExpressionSiteAnalysis site,
            Expr expression)
        {
            var contract = Analyze(expression, site, "constant");
            var encoding = ResolveEncoding(contract, site.Node ?? branch.Node);
            var converted = value.Kind == ObservationValueKind.Null
                ? null
                : PostgresRelationQueryValueConverter.Convert(value, encoding, site.Analysis.Site.Id.Value);
            return new(SqlExpression.Constant(converted), contract, encoding, null,
                PostgresRelationQueryOrderingCapability.None, null, null, null,
                ConstantText: encoding == PostgresRelationQueryValueEncoding.Text
                              && value.Kind == ObservationValueKind.String
                    ? value.String
                    : null,
                IsNullTextConstant: encoding == PostgresRelationQueryValueEncoding.Text
                                    && value.Kind == ObservationValueKind.Null);
        }

        CompiledExpression CompileNot(
            UnaryExpr unary,
            RelationQueryExpressionSiteAnalysis site,
            Environment environment)
        {
            var operand = CompileExpression(unary.Operand, site, environment, requireNonNull: true);
            RequireBoolean(operand, site.Node ?? branch.Node, "Boolean negation");
            return new(SqlExpression.Unary(SqlUnaryOperator.Not, operand.Expression),
                RequireKnown(site, site.Node ?? branch.Node, "Boolean negation"),
                PostgresRelationQueryValueEncoding.Boolean, null,
                PostgresRelationQueryOrderingCapability.None, null, null, null,
                operand.PresenceDependencies);
        }

        CompiledExpression CompileBinary(
            BinaryExpr binary,
            RelationQueryExpressionSiteAnalysis site,
            Environment environment)
        {
            var leftContract = Analyze(binary.Left, site, "left");
            var rightContract = Analyze(binary.Right, site, "right");
            var node = site.Node ?? branch.Node;
            if (binary.Operator is BinaryOperator.Eq or BinaryOperator.Ne)
            {
                var equalityLeft = CompileExpression(binary.Left, site, environment, requireNonNull: false);
                var equalityRight = CompileExpression(binary.Right, site, environment, requireNonNull: false);
                RequireCompatibleNullEquality(equalityLeft, equalityRight, node);
                (equalityLeft, equalityRight) = PrepareComparison(
                    equalityLeft,
                    equalityRight,
                    node,
                    ordering: false);
                return new(SqlExpression.Binary(
                        binary.Operator == BinaryOperator.Eq
                            ? SqlBinaryOperator.IsNotDistinctFrom
                            : SqlBinaryOperator.IsDistinctFrom,
                        equalityLeft.Expression,
                        equalityRight.Expression),
                    RequireKnown(site, node, "equality result"),
                    PostgresRelationQueryValueEncoding.Boolean, null,
                    PostgresRelationQueryOrderingCapability.None, null, null, null,
                    []);
            }
            if (!IsRequiredNonNull(leftContract) || !IsRequiredNonNull(rightContract))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"Binary operator '{binary.Operator}' has a missing or null operand.",
                    node);
            }

            var left = CompileExpression(binary.Left, site, environment, requireNonNull: true);
            var right = CompileExpression(binary.Right, site, environment, requireNonNull: true);
            if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
            {
                RequireBoolean(left, node, "Boolean binary operand");
                RequireBoolean(right, node, "Boolean binary operand");
                return new(SqlExpression.Binary(
                    binary.Operator == BinaryOperator.And ? SqlBinaryOperator.And : SqlBinaryOperator.Or,
                    left.Expression,
                    right.Expression),
                    RequireKnown(site, node, "Boolean binary result"),
                    PostgresRelationQueryValueEncoding.Boolean, null,
                    PostgresRelationQueryOrderingCapability.None, null, null, null,
                    CombinePresence(left, right));
            }
            if (binary.Operator is BinaryOperator.Gt or BinaryOperator.Ge or BinaryOperator.Lt or BinaryOperator.Le)
            {
                var ordering = binary.Operator is not (BinaryOperator.Eq or BinaryOperator.Ne);
                (left, right) = PrepareComparison(left, right, node, ordering);
                return new(SqlExpression.Binary(Convert(binary.Operator), left.Expression, right.Expression),
                    RequireKnown(site, node, "comparison result"),
                    PostgresRelationQueryValueEncoding.Boolean, null,
                    PostgresRelationQueryOrderingCapability.None, null, null, null,
                    CombinePresence(left, right));
            }
            if (binary.Operator is BinaryOperator.Add or BinaryOperator.Sub or BinaryOperator.Mul)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "The current PostgreSQL compiler does not lower canonical arithmetic without explicit checked intermediate-domain evidence.",
                    node);
            }
            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"Binary operator '{binary.Operator}' is outside the exact current PostgreSQL compiler closure.", node);
        }

        static void RequireCompatibleNullEquality(
            CompiledExpression left,
            CompiledExpression right,
            QueryNodeId node)
        {
            var leftNull = ResolveSqlNullMeaning(left);
            var rightNull = ResolveSqlNullMeaning(right);
            if (leftNull == SqlNullMeaning.None || rightNull == SqlNullMeaning.None)
            {
                return;
            }

            if (leftNull == SqlNullMeaning.Ambiguous || rightNull == SqlNullMeaning.Ambiguous)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "PostgreSQL SQL NULL cannot represent both canonical Undefined and explicit Null in equality.",
                    node);
            }
            if (leftNull != rightNull)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Compared SQL NULL values represent different canonical meanings (Undefined versus explicit Null).",
                    node);
            }
        }

        static SqlNullMeaning ResolveSqlNullMeaning(CompiledExpression expression)
        {
            var canBeUndefined = expression.Contract.Presence == FieldPresence.Optional
                                 || !expression.PresenceDependencies.IsDefaultOrEmpty;
            var canBeExplicitNull = expression.Contract.Nullability == FieldNullability.Nullable;
            return (canBeUndefined, canBeExplicitNull) switch
            {
                (false, false) => SqlNullMeaning.None,
                (false, true) => SqlNullMeaning.ExplicitNull,
                (true, false) => SqlNullMeaning.Undefined,
                _ => SqlNullMeaning.Ambiguous
            };
        }

        CompiledExpression CompileConditional(
            ConditionalExpr conditional,
            RelationQueryExpressionSiteAnalysis site,
            Environment environment)
        {
            var test = CompileExpression(conditional.Test, site, environment, requireNonNull: true);
            RequireBoolean(test, site.Node ?? branch.Node, "conditional test");
            var whenTrue = CompileExpression(conditional.IfTrue, site, environment, requireNonNull: false);
            var whenFalse = CompileExpression(conditional.IfFalse, site, environment, requireNonNull: false);
            if (whenTrue.Encoding != whenFalse.Encoding)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Conditional branches require the same exact PostgreSQL scalar encoding.", site.Node ?? branch.Node);
            }

            if (!PresenceEqual(whenTrue.PresenceDependencies, whenFalse.PresenceDependencies))
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "Conditional branches have different outer-presence dependencies and require branch-sensitive presence metadata.",
                    site.Node ?? branch.Node);
            }
            return new(SqlExpression.Conditional(test.Expression, whenTrue.Expression, whenFalse.Expression),
                RequireKnown(site, site.Node ?? branch.Node, "conditional result"), whenTrue.Encoding,
                CompatibleText(whenTrue.Text, whenFalse.Text),
                whenTrue.Ordering & whenFalse.Ordering & PostgresRelationQueryOrderingCapability.Exact,
                whenTrue.SourceInput == whenFalse.SourceInput ? whenTrue.SourceInput : null,
                whenTrue.Placement == whenFalse.Placement ? whenTrue.Placement : null,
                null,
                CombinePresence(test, whenTrue, whenFalse),
                RuntimeTextBinding: whenTrue.RuntimeTextBinding is not null
                                    && string.Equals(
                                        whenTrue.RuntimeTextBinding,
                                        whenFalse.RuntimeTextBinding,
                                        StringComparison.Ordinal)
                    ? whenTrue.RuntimeTextBinding
                    : null,
                ConstantText: whenTrue.ConstantText is not null
                              && string.Equals(whenTrue.ConstantText, whenFalse.ConstantText, StringComparison.Ordinal)
                    ? whenTrue.ConstantText
                    : null,
                IsNullTextConstant: whenTrue.IsNullTextConstant && whenFalse.IsNullTextConstant);
        }

        static bool PresenceEqual(
            ImmutableArray<ValueBindingId> left,
            ImmutableArray<ValueBindingId> right) =>
            (left.IsDefault ? [] : left).SequenceEqual(right.IsDefault ? [] : right);

        CompiledExpression CompileEndsWith(
            CallExpr call,
            RelationQueryExpressionSiteAnalysis site,
            Environment environment)
        {
            var value = CompileExpression(call.Arguments[0], site, environment, requireNonNull: true);
            var suffix = CompileExpression(call.Arguments[1], site, environment, requireNonNull: true);
            if (value.Encoding != PostgresRelationQueryValueEncoding.Text
                || suffix.Encoding != PostgresRelationQueryValueEncoding.Text)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "EndsWith requires two text operands.", site.Node ?? branch.Node);
            }

            (value, suffix) = PrepareComparison(value, suffix, site.Node ?? branch.Node, ordering: false);
            var right = SqlExpression.Function(
                SqlFunction.Right,
                value.Expression,
                SqlExpression.Function(SqlFunction.Length, suffix.Expression));
            return new(SqlExpression.Binary(SqlBinaryOperator.Equal, right, suffix.Expression),
                RequireKnown(site, site.Node ?? branch.Node, "EndsWith result"),
                PostgresRelationQueryValueEncoding.Boolean, null,
                PostgresRelationQueryOrderingCapability.None, null, null, null,
                CombinePresence(value, suffix));
        }

        CompiledExpression CompileStartsWith(
            CallExpr call,
            RelationQueryExpressionSiteAnalysis site,
            Environment environment)
        {
            var value = CompileExpression(call.Arguments[0], site, environment, requireNonNull: true);
            var prefix = CompileExpression(call.Arguments[1], site, environment, requireNonNull: true);
            RequireTextOperands(value, prefix, site.Node ?? branch.Node, "StartsWith");
            (value, prefix) = PrepareComparison(value, prefix, site.Node ?? branch.Node, ordering: false);
            var left = SqlExpression.Function(
                SqlFunction.Left,
                value.Expression,
                SqlExpression.Function(SqlFunction.Length, prefix.Expression));
            return new(SqlExpression.Binary(SqlBinaryOperator.Equal, left, prefix.Expression),
                RequireKnown(site, site.Node ?? branch.Node, "StartsWith result"),
                PostgresRelationQueryValueEncoding.Boolean, null,
                PostgresRelationQueryOrderingCapability.None, null, null, null,
                CombinePresence(value, prefix));
        }

        CompiledExpression CompileTextContains(
            CallExpr call,
            RelationQueryExpressionSiteAnalysis site,
            Environment environment)
        {
            var value = CompileExpression(call.Arguments[0], site, environment, requireNonNull: true);
            var substring = CompileExpression(call.Arguments[1], site, environment, requireNonNull: true);
            RequireTextOperands(value, substring, site.Node ?? branch.Node, "TextContains");
            (value, substring) = PrepareComparison(value, substring, site.Node ?? branch.Node, ordering: false);
            var position = SqlExpression.Function(
                SqlFunction.StringPosition,
                value.Expression,
                substring.Expression);
            return new(SqlExpression.Binary(
                    SqlBinaryOperator.GreaterThan,
                    position,
                    SqlExpression.Constant(0)),
                RequireKnown(site, site.Node ?? branch.Node, "TextContains result"),
                PostgresRelationQueryValueEncoding.Boolean, null,
                PostgresRelationQueryOrderingCapability.None, null, null, null,
                CombinePresence(value, substring));
        }

        static void RequireTextOperands(
            CompiledExpression value,
            CompiledExpression search,
            QueryNodeId node,
            string operation)
        {
            if (value.Encoding == PostgresRelationQueryValueEncoding.Text
                && search.Encoding == PostgresRelationQueryValueEncoding.Text)
            {
                return;
            }

            throw Fail(
                PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"{operation} requires two text operands.",
                node);
        }

        static ImmutableArray<ValueBindingId> CombinePresence(params CompiledExpression[] expressions) =>
        [
            .. expressions.SelectMany(static expression =>
                    expression.PresenceDependencies.IsDefault
                        ? []
                        : expression.PresenceDependencies)
                .Distinct()
                .OrderBy(static binding => binding.Value, StringComparer.Ordinal)
        ];

        (CompiledExpression Left, CompiledExpression Right) PrepareComparison(
            CompiledExpression left,
            CompiledExpression right,
            QueryNodeId node,
            bool ordering)
        {
            if (left.Encoding != right.Encoding)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    "Compared operands do not share one exact PostgreSQL scalar encoding.", node);
            }

            if (left.Encoding != PostgresRelationQueryValueEncoding.Text)
            {
                return (left, right);
            }

            var evidence = ChooseTextEvidence(left.Text, right.Text, ordering, node);
            if (ordering)
            {
                var orderingDomain = evidence.OrderingDomain!;
                RequireOrderingDomain(left, orderingDomain, node);
                RequireOrderingDomain(right, orderingDomain, node);
            }
            return (
                left with { Expression = SqlExpression.Collate(left.Expression, evidence.Collation), Text = evidence },
                right with { Expression = SqlExpression.Collate(right.Expression, evidence.Collation), Text = evidence });
        }

        static PostgresRelationQueryTextSemantics ChooseTextEvidence(
            PostgresRelationQueryTextSemantics? left,
            PostgresRelationQueryTextSemantics? right,
            bool ordering,
            QueryNodeId node)
        {
            var evidence = left ?? right;
            var leftCompatible = left is null
                                 || left.Equality == PostgresRelationQueryTextEqualitySemantics.Ordinal
                                 && (!ordering
                                     || left.Ordering == PostgresRelationQueryTextOrderingSemantics.Ordinal
                                     && left.OrderingDomain is not null);
            var rightCompatible = right is null
                                  || right.Equality == PostgresRelationQueryTextEqualitySemantics.Ordinal
                                  && (!ordering
                                      || right.Ordering == PostgresRelationQueryTextOrderingSemantics.Ordinal
                                      && right.OrderingDomain is not null);
            if (evidence is null
                || !leftCompatible
                || !rightCompatible
                || left is not null && right is not null
                    && !string.Equals(left.Collation, right.Collation, StringComparison.Ordinal))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    ordering
                        ? "Canonical ordinal text ordering lacks compatible PostgreSQL collation and constrained-domain evidence."
                        : "Canonical ordinal text equality lacks compatible PostgreSQL collation evidence.",
                    node);
            }
            return evidence;
        }

        void RequireOrderingDomain(
            CompiledExpression operand,
            PostgresRelationQueryTextOrderingDomainEvidence orderingDomain,
            QueryNodeId node)
        {
            if (operand.ConstantText is { } constant)
            {
                if (!orderingDomain.IsSatisfiedBy(constant))
                {
                    throw Fail(
                        PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        $"Ordered text constant violates ordering-domain strategy '{orderingDomain.Strategy}'.",
                        node);
                }
                return;
            }
            if (operand.IsNullTextConstant)
            {
                return;
            }

            if (operand.RuntimeTextBinding is { } runtimeBinding)
            {
                if (runtimeTextOrderingDomains.TryGetValue(runtimeBinding, out var current)
                    && !Equals(current, orderingDomain))
                {
                    throw Fail(
                        PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        $"Runtime text input '{runtimeBinding}' is used with incompatible ordering-domain evidence.",
                        node);
                }
                runtimeTextOrderingDomains[runtimeBinding] = orderingDomain;
                return;
            }
            if (operand.Placement is not null
                && operand.Text?.OrderingDomain is { } physicalDomain
                && string.Equals(physicalDomain.Strategy, orderingDomain.Strategy, StringComparison.Ordinal))
            {
                return;
            }
            throw Fail(
                PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                "Ordered text expression does not retain complete constrained-domain provenance.",
                node,
                operand.SourceInput);
        }

        CompiledExpression CompileEquality(
            CompiledExpression left,
            CompiledExpression right,
            QueryNodeId node,
            bool nullSafe)
        {
            (left, right) = PrepareComparison(left, right, node, ordering: false);
            return new(SqlExpression.Binary(
                nullSafe ? SqlBinaryOperator.IsNotDistinctFrom : SqlBinaryOperator.Equal,
                left.Expression,
                right.Expression),
                BooleanContract,
                PostgresRelationQueryValueEncoding.Boolean,
                null,
                PostgresRelationQueryOrderingCapability.None,
                null,
                null,
                null,
                CombinePresence(left, right));
        }

        CompiledExpression RequireOrderable(CompiledExpression value, QueryNodeId node)
        {
            if (value.Encoding == PostgresRelationQueryValueEncoding.Bytea
                || value.Encoding == PostgresRelationQueryValueEncoding.Boolean)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    $"PostgreSQL encoding '{value.Encoding}' has no declared canonical ordering.", node);
            }

            if (value.Encoding == PostgresRelationQueryValueEncoding.Text)
            {
                var evidence = ChooseTextEvidence(value.Text, value.Text, ordering: true, node);
                RequireOrderingDomain(value, evidence.OrderingDomain!, node);
                value = value with { Expression = SqlExpression.Collate(value.Expression, evidence.Collation) };
            }
            if (value.SourceInput is not null
                && !value.Ordering.HasFlag(PostgresRelationQueryOrderingCapability.Exact))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "A physically sourced ordering key lacks exact canonical ordering evidence.", node,
                    value.SourceInput);
            }
            return value;
        }

        CompiledExpression RequireEqualitySemantics(CompiledExpression value, QueryNodeId node)
        {
            if (value.Encoding != PostgresRelationQueryValueEncoding.Text)
            {
                return value;
            }

            var evidence = ChooseTextEvidence(value.Text, value.Text, ordering: false, node);
            return value with
            {
                Expression = SqlExpression.Collate(value.Expression, evidence.Collation),
                Text = evidence
            };
        }

        static void RequireBoolean(CompiledExpression expression, QueryNodeId node, string operation)
        {
            if (expression.Encoding == PostgresRelationQueryValueEncoding.Boolean)
            {
                return;
            }

            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"Canonical {operation} requires a Boolean value.", node);
        }

        static void RequireNumeric(CompiledExpression expression, QueryNodeId node, string operation)
        {
            if (expression.Encoding is PostgresRelationQueryValueEncoding.Int32
                or PostgresRelationQueryValueEncoding.Int64
                or PostgresRelationQueryValueEncoding.Numeric)
            {
                return;
            }

            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"Canonical {operation} requires an exact numeric value.", node);
        }

        ValueContract Analyze(
            Expr expression,
            RelationQueryExpressionSiteAnalysis site,
            string operand)
        {
            var parent = site.Analysis.Site;
            var analysis = ExprAnalyzer.Analyze(
                new ExprSite(
                    new($"{parent.Id.Value}/postgres/{operand}"),
                    expression,
                    parent.Scope,
                    ExprExpectation.Any,
                    parent.CapabilityProfile,
                    parent.DiagnosticLocation),
                site.Analysis.Semantics);
            if (analysis.IsValid && analysis.KnownResult is { } result)
            {
                return result;
            }

            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                $"The {operand} expression has no valid known value contract for exact PostgreSQL lowering.",
                site.Node ?? branch.Node);
        }

        static ValueContract RequireKnown(
            RelationQueryExpressionSiteAnalysis site,
            QueryNodeId node,
            string operation) =>
            site.Analysis.KnownResult
            ?? throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Canonical {operation} has no known value contract.", node);

        FieldKey ResolveFieldRoot(
            FieldPath path,
            ValueBindingId? explicitBinding,
            RelationQueryExpressionSiteAnalysis site)
        {
            if (explicitBinding is { } binding)
            {
                return new(binding, path);
            }

            var candidates = site.Analysis.Requirements.Fields
                .Where(requirement => requirement.WasUnqualified && requirement.Path == path)
                .ToArray();
            if (candidates.Length != 1
                || candidates[0].Root != ExprFieldRootKind.Binding
                || candidates[0].Binding is not { } resolved)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedExpression,
                    $"Unqualified field '{path}' does not resolve to one named binding.",
                    site.Node ?? branch.Node);
            }
            return new(resolved, candidates[0].Path);
        }

        Environment CreateEnvironment(Scope scope, string sourceAlias)
        {
            Dictionary<FieldKey, CompiledExpression> values = [];
            foreach (var pair in scope.Values)
            {
                values.Add(pair.Key, pair.Value.Qualify(sourceAlias));
            }

            return new(values,
                scope.Identities.ToDictionary(static pair => pair.Key,
                    pair => pair.Value.Qualify(sourceAlias)),
                scope.References.ToDictionary(static pair => pair.Key,
                    pair => pair.Value.Qualify(sourceAlias)),
                sourceAlias);
        }

        static Environment Merge(Environment left, Environment right, QueryNodeId node)
        {
            if (left.Values.Keys.Intersect(right.Values.Keys).Any()
                || left.Identities.Keys.Intersect(right.Identities.Keys).Any()
                || left.References.Keys.Intersect(right.References.Keys).Any())
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology,
                    "Joined input scopes contain duplicate canonical bindings.", node);
            }

            return new(
                left.Values.Concat(right.Values).ToDictionary(),
                left.Identities.Concat(right.Identities).ToDictionary(),
                left.References.Concat(right.References).ToDictionary(),
                sourceAlias: null);
        }

        CombinedScope ProjectCombined(
            SqlSelectBuilder builder,
            Scope left,
            Environment leftEnvironment,
            Scope right,
            Environment rightEnvironment)
        {
            CombinedScope combined = new();
            combined.Add(builder, left, leftEnvironment);
            combined.Add(builder, right, rightEnvironment);
            return combined;
        }

        Scope EnsureOuterPresenceMarkers(Scope scope, QueryNodeId node)
        {
            var represented = scope.OuterPresence.Keys.ToHashSet();
            var missing = scope.Values.Keys.Select(static key => key.Binding)
                .Distinct()
                .Where(binding => !represented.Contains(binding))
                .OrderBy(static binding => binding.Value, StringComparer.Ordinal)
                .ToArray();
            if (missing.Length == 0)
            {
                return scope;
            }

            var sourceAlias = RelationAlias("outer_rows");
            var environment = CreateEnvironment(scope, sourceAlias);
            var builder = new SqlSelectBuilder(scope.Query, sourceAlias);
            PassThrough(builder, scope, environment);
            Dictionary<ValueBindingId, OuterPresence> markers = new(scope.OuterPresence);
            foreach (var binding in missing)
            {
                var placements = scope.Values
                    .Where(pair => pair.Key.Binding == binding)
                    .Select(static pair => pair.Value.Placement)
                    .Where(static placement => placement is not null)
                    .Select(static placement => placement!.Value)
                    .Distinct()
                    .ToArray();
                if (placements.Length != 1)
                {
                    throw Fail(
                        PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                        $"Outer-joined binding '{binding.Value}' has no single placement attribution for a structural row-presence marker.",
                        node);
                }
                var markerAlias = ValueAlias("__presence", BindingAlias(binding));
                builder.Select(SqlExpression.Constant(true), markerAlias);
                markers.Add(binding, new(markerAlias, placements[0]));
            }
            ApplyOrder(builder, scope, sourceAlias);
            return new(
                builder.BuildQuery(),
                scope.Values,
                scope.Identities,
                scope.References,
                markers,
                scope.Orderings);
        }

        static void AddOuterPresence(
            CombinedScope combined,
            Scope right,
            Environment rightEnvironment)
        {
            foreach (var existing in right.OuterPresence)
            {
                var qualified = SqlExpression.Column(
                    rightEnvironment.SourceAliasFor(existing.Value.Alias), existing.Value.Alias);
                combined.OuterPresence[existing.Key] = existing.Value with { Expression = qualified };
                combined.AddPresenceDependency(existing.Key);
            }
        }

        CompiledExpression ResolveForwardReference(
            Scope scope,
            Environment environment,
            RelationQueryTraversalInputContract traversal)
        {
            var key = new RelationshipKey(traversal.From, traversal.Input.Id);
            if (environment.References.TryGetValue(key, out var reference))
            {
                return reference;
            }

            var field = new FieldKey(traversal.From, traversal.Definition.SourceReference);
            if (environment.Values.TryGetValue(field, out var supplied))
            {
                return supplied;
            }

            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.RelationshipEndpointMissing,
                $"Relationship '{traversal.Definition.Id.Value}' has no source-reference value in the left scope.",
                traversal.Input.Traversal,
                traversal.Input.Id);
        }

        static CompiledExpression ResolveReference(
            Scope scope,
            Environment environment,
            ValueBindingId binding,
            RelationQueryInputId traversal,
            QueryNodeId node)
        {
            if (environment.References.TryGetValue(new(binding, traversal), out var reference))
            {
                return reference;
            }

            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.RelationshipEndpointMissing,
                "Inverse traversal target has no physical source-reference column.", node, traversal);
        }

        static CompiledExpression ResolveIdentity(
            Scope scope,
            Environment environment,
            ValueBindingId binding,
            RelationQueryInputId traversal,
            QueryNodeId node)
        {
            if (environment.Identities.TryGetValue(binding, out var identity))
            {
                return identity.Expression;
            }

            throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.RelationshipEndpointMissing,
                $"Traversal endpoint binding '{binding.Value}' has no unique non-null observation identity.",
                node,
                traversal);
        }

        void AddPhysicalInternals(
            SqlSelectBuilder builder,
            string tableAlias,
            PostgresRelationQueryTableBinding table,
            ValueBindingId binding,
            IReadOnlyDictionary<FieldKey, ScopedValue> values,
            IDictionary<ValueBindingId, ScopedIdentity> identities,
            IDictionary<RelationshipKey, ScopedValue> references)
        {
            if (requiredIdentityBindings.Contains(binding) && table.Identity is { } identity)
            {
                var key = new FieldKey(binding, identity.SemanticPath);
                var alias = values.TryGetValue(key, out var selectedIdentity)
                    ? selectedIdentity.Alias
                    : ValueAlias(table.Shape, identity.SemanticPath);
                if (selectedIdentity.Alias is null)
                {
                    builder.Select(SqlExpression.Column(tableAlias, identity.ColumnName), alias);
                }

                identities.Add(binding, new(alias, table.PlacementBinding,
                    new(alias, IdentityContract(identity), Convert(identity.ScalarType), identity.TextSemantics,
                        PostgresRelationQueryOrderingCapability.Exact | PostgresRelationQueryOrderingCapability.StableUnique,
                        null, table.PlacementBinding)));
            }
            foreach (var reference in table.RelationshipReferences.Where(reference =>
                         requiredRelationshipReferences.Contains(new(binding, reference.Input))))
            {
                var key = new FieldKey(binding, reference.SemanticPath);
                var alias = values.TryGetValue(key, out var selectedReference)
                    ? selectedReference.Alias
                    : ValueAlias(table.Shape, reference.SemanticPath);
                if (selectedReference.Alias is null)
                {
                    builder.Select(SqlExpression.Column(tableAlias, reference.ColumnName), alias);
                }

                references.Add(new(binding, reference.Input), new(
                    alias,
                    ReferenceContract(reference),
                    Convert(reference.ScalarType),
                    reference.TextSemantics,
                    PostgresRelationQueryOrderingCapability.None,
                    null,
                    table.PlacementBinding));
            }
        }

        void AddSuppliedReferences(
            RelationQuerySourceInputContract source,
            IReadOnlyDictionary<FieldKey, ScopedValue> values,
            Dictionary<RelationshipKey, ScopedValue> references)
        {
            foreach (var traversal in branchSelection.Traversals.Where(candidate =>
                         requiredRelationshipReferences.Contains(new(source.Binding, candidate.Input.Id))
                         &&
                         candidate.Input.Direction == RelationshipTraversalDirection.Forward
                         && candidate.From == source.Binding))
            {
                if (values.TryGetValue(new(source.Binding, traversal.Definition.SourceReference), out var reference))
                {
                    references.TryAdd(new(source.Binding, traversal.Input.Id), reference);
                }
            }
        }

        static void EnsureProjection(
            SqlSelectBuilder builder,
            IReadOnlyDictionary<FieldKey, ScopedValue> values,
            IReadOnlyDictionary<ValueBindingId, ScopedIdentity> identities,
            IReadOnlyDictionary<RelationshipKey, ScopedValue> references)
        {
            if (values.Count == 0 && identities.Count == 0 && references.Count == 0)
            {
                builder.Select(SqlExpression.Constant(true), "__row");
            }
        }

        static void PassThrough(SqlSelectBuilder builder, Scope scope, Environment environment)
        {
            HashSet<string> selectedAliases = new(StringComparer.Ordinal);
            foreach (var pair in scope.Values.OrderBy(static pair => pair.Key, FieldKeyComparer.Instance))
            {
                if (selectedAliases.Add(pair.Value.Alias))
                {
                    builder.Select(environment.Values[pair.Key].Expression, pair.Value.Alias);
                }
            }
            foreach (var pair in scope.Identities.OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal))
            {
                if (selectedAliases.Add(pair.Value.Alias))
                {
                    builder.Select(environment.Identities[pair.Key].Expression.Expression, pair.Value.Alias);
                }
            }
            foreach (var pair in scope.References.OrderBy(static pair => pair.Key, RelationshipKeyComparer.Instance))
            {
                if (selectedAliases.Add(pair.Value.Alias))
                {
                    builder.Select(environment.References[pair.Key].Expression, pair.Value.Alias);
                }
            }
            foreach (var pair in scope.OuterPresence.OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal))
            {
                if (selectedAliases.Add(pair.Value.Alias))
                {
                    builder.Select(
                        SqlExpression.Column(environment.SourceAliasFor(pair.Value.Alias), pair.Value.Alias),
                        pair.Value.Alias);
                }
            }
            foreach (var ordering in scope.Orderings)
            {
                if (selectedAliases.Add(ordering.Alias))
                {
                    builder.Select(
                        SqlExpression.Column(environment.SourceAliasFor(ordering.Alias), ordering.Alias),
                        ordering.Alias);
                }
            }
        }

        static void ApplyOrder(
            SqlSelectBuilder builder,
            Scope scope,
            string sourceAlias)
        {
            foreach (var ordering in scope.Orderings)
            {
                builder.OrderBy(SqlExpression.Column(sourceAlias, ordering.Alias),
                    ordering.Direction, ordering.NullPlacement);
            }
        }

        SqlSelectBuilder CreatePhysicalBuilder(
            PostgresRelationQueryTableBinding table,
            out string alias)
        {
            alias = RelationAlias(table.TableName);
            return new(new SqlQualifiedTable(table.SchemaName, table.TableName), alias);
        }

        PostgresRelationQueryTableBinding ResolveTable(RelationQueryInputId input, QueryNodeId node)
        {
            try
            {
                return storageBinding.ResolveTable(input);
            }
            catch (KeyNotFoundException)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.StorageBindingMismatch,
                    $"Compiled input '{input.Value}' has no PostgreSQL table binding.", node, input);
            }
        }

        bool IsOutputPathDemanded(ValueBindingId binding, FieldPath path)
        {
            if (branch.Binding == binding && branch.Fields.Any(field => field.Path == path))
            {
                return true;
            }

            foreach (var node in nodes.Values.Where(node => branchNodes.Contains(node.Id)))
            {
                if (node.ExpressionSites.Any(site => site.Analysis.Requirements.Fields.Any(requirement =>
                        requirement.Root == ExprFieldRootKind.Binding
                        && requirement.Binding == binding
                        && requirement.Path == path)))
                {
                    return true;
                }
            }

            var relation = request.Plan.ExecutionSlice.RelationOutput;
            if (relation?.KeySite?.Analysis.Requirements.Fields.Any(requirement =>
                    requirement.Root == ExprFieldRootKind.Binding
                    && requirement.Binding == binding
                    && requirement.Path == path) == true)
            {
                return true;
            }
            return relation?.Invariants.Any(invariant =>
                invariant.PredicateSite.Analysis.Requirements.Fields.Any(requirement =>
                    requirement.Root == ExprFieldRootKind.Binding
                    && requirement.Binding == binding
                    && requirement.Path == path)) == true;
        }

        static PostgresRelationQueryFieldBinding ResolveField(
            PostgresRelationQueryTableBinding table,
            RelationQueryInputId input,
            QueryNodeId node)
        {
            try
            {
                return table.ResolveField(input);
            }
            catch (KeyNotFoundException)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.FieldBindingMissing,
                    $"Demanded field input '{input.Value}' has no physical PostgreSQL column.", node, input);
            }
        }

        (IReadOnlySet<ValueBindingId> Identities, IReadOnlySet<RelationshipKey> References)
            SelectBranchRelationshipInternals()
        {
            HashSet<ValueBindingId> identities = [];
            HashSet<RelationshipKey> references = [];
            foreach (var traversal in branchSelection.Traversals)
            {
                if (traversal.Input.Direction == RelationshipTraversalDirection.Forward)
                {
                    identities.Add(traversal.Result);
                    references.Add(new(traversal.From, traversal.Input.Id));
                }
                else
                {
                    identities.Add(traversal.From);
                    references.Add(new(traversal.Result, traversal.Input.Id));
                }
            }
            return (identities, references);
        }

        ImmutableArray<PostgresRelationQuerySelectedField> CreateSelectedFields()
        {
            ImmutableArray<PostgresRelationQuerySelectedField>.Builder selected =
                ImmutableArray.CreateBuilder<PostgresRelationQuerySelectedField>();
            foreach (var field in selectedFieldContracts)
            {
                var placement = placements.Values.Single(candidate =>
                    candidate.Binding == field.Input.Binding
                    && candidate.Node == field.Input.Producer);
                if (placement.Acquisition == RelationQuerySourceAcquisitionKind.Supplied)
                {
                    continue;
                }

                var table = storageBinding.ResolveTable(placement.Input);
                var physical = table.ResolveField(field.Input.Id);
                selected.Add(new(field.Input.Id, field.Input.Field, table.PlacementBinding, physical.ColumnName));
            }
            return selected.ToImmutable();
        }

        ImmutableArray<PostgresRelationQuerySuppliedFieldBinding> CreateSuppliedBindings(
            SqlCommandTemplate statement)
        {
            var contracts = selectedFieldContracts.ToDictionary(static field => field.Input.Id);
            var result = ImmutableArray.CreateBuilder<PostgresRelationQuerySuppliedFieldBinding>();
            foreach (var slot in statement.Parameters.Where(static parameter =>
                         parameter.Kind == SqlParameterBindingKind.Runtime
                         && parameter.Binding!.StartsWith(SuppliedPrefix, StringComparison.Ordinal)))
            {
                RelationQueryInputId input = new(slot.Binding![SuppliedPrefix.Length..]);
                if (!contracts.TryGetValue(input, out var contract))
                {
                    throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                        $"SQL supplied slot '{slot.Binding}' is absent from the branch input contract.", branch.Node, input);
                }

                var valueContract = RequireValueContract(contract.Input);
                result.Add(new(slot.Position, input, contract.Input.Field, valueContract,
                    ResolveEncoding(valueContract, branch.Node),
                    runtimeTextOrderingDomains.GetValueOrDefault(slot.Binding!)));
            }
            return result.ToImmutable();
        }

        ImmutableArray<PostgresRelationQueryParameterBinding> CreateParameterBindings(SqlCommandTemplate statement)
        {
            var result = ImmutableArray.CreateBuilder<PostgresRelationQueryParameterBinding>();
            foreach (var slot in statement.Parameters.Where(static parameter =>
                         parameter.Kind == SqlParameterBindingKind.Runtime
                         && parameter.Binding!.StartsWith(ParameterPrefix, StringComparison.Ordinal)))
            {
                QueryParameterId parameter = new(slot.Binding![ParameterPrefix.Length..]);
                if (!parameters.TryGetValue(parameter, out var contract))
                {
                    throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.ParameterUnsupported,
                        $"SQL parameter slot '{slot.Binding}' is absent from the branch input contract.", branch.Node);
                }

                result.Add(new(slot.Position, contract.Definition, contract.ValueContract,
                    ResolveEncoding(contract.ValueContract, branch.Node),
                    runtimeTextOrderingDomains.GetValueOrDefault(slot.Binding!)));
            }
            return result.ToImmutable();
        }

        ImmutableArray<RelationQueryInputId> StableOrderingInputs(
            ImmutableArray<ScopedOrder> orderings,
            QueryNodeId node)
        {
            var inputs = orderings.Select(static ordering => ordering.Expression.SourceInput).ToArray();
            if (inputs.Any(static input => input is null))
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.PagingUnstable,
                    "Stable paging requires every ordering key to retain exact field-input provenance.", node);
            }

            return [.. inputs.Select(static input => input!.Value)];
        }

        static ValueContract RequireValueContract(RelationQueryFieldInput field) =>
            field.ValueContract
            ?? throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                $"Field input '{field.Id.Value}' has no known semantic value contract.", field.Producer, field.Id);

        static void ValidatePhysicalValue(
            ValueContract contract,
            PostgresRelationQueryFieldBinding physical,
            QueryNodeId node,
            RelationQueryInputId input)
        {
            if (PostgresRelationQueryBindingSemanticValidator.GetValueSemanticsMismatch(
                    contract,
                    physical.ScalarType,
                    physical.MissingValueEncoding,
                    physical.NullValueEncoding,
                    physical.NumericDomain,
                    physical.TemporalDomain) is { } mismatch)
            {
                throw Fail(
                    PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    mismatch,
                    node,
                    input);
            }
        }

        static PostgresRelationQueryValueEncoding ResolveEncoding(ValueContract contract, QueryNodeId node)
        {
            if (contract.Cardinality != FieldCardinality.Single)
            {
                throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                    "The current PostgreSQL compiler requires single-valued scalar fields.", node);
            }

            if (PostgresRelationQueryScalarCatalog.TryFromSemanticType(
                    contract.GetEffectiveType(),
                    out var scalarType))
                return Convert(scalarType);

            throw Fail(
                PostgresRelationQueryCompilationDiagnosticCodes.GuaranteeUnavailable,
                "Semantic value has no exact scalar encoding in the current PostgreSQL compiler.",
                node);
        }

        static PostgresRelationQueryValueEncoding Convert(PostgresRelationQueryScalarType scalar) =>
            PostgresRelationQueryScalarCatalog.ToValueEncoding(scalar);

        static SqlJoinKind ConvertJoin(JoinKind kind, QueryNodeId node) => kind switch
        {
            JoinKind.Inner => SqlJoinKind.Inner,
            JoinKind.Left => SqlJoinKind.Left,
            _ => throw Fail(PostgresRelationQueryCompilationDiagnosticCodes.JoinUnsupported,
                $"The current PostgreSQL compiler supports inner and left joins, not '{kind}'.", node)
        };

        static SqlBinaryOperator Convert(BinaryOperator @operator) => @operator switch
        {
            BinaryOperator.Eq => SqlBinaryOperator.Equal,
            BinaryOperator.Ne => SqlBinaryOperator.NotEqual,
            BinaryOperator.Gt => SqlBinaryOperator.GreaterThan,
            BinaryOperator.Ge => SqlBinaryOperator.GreaterThanOrEqual,
            BinaryOperator.Lt => SqlBinaryOperator.LessThan,
            BinaryOperator.Le => SqlBinaryOperator.LessThanOrEqual,
            BinaryOperator.Add => SqlBinaryOperator.Add,
            BinaryOperator.Sub => SqlBinaryOperator.Subtract,
            BinaryOperator.Mul => SqlBinaryOperator.Multiply,
            _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported PostgreSQL operator.")
        };

        PostgresRelationQueryLoweringDecision Decision(
            PostgresRelationQueryLoweringDecisionKind kind,
            string strategy,
            QueryNodeId node,
            ImmutableArray<RelationQuerySourcePlacementBindingId> placementBindings = default,
            RelationshipId? relationship = null) =>
            new(kind, strategy, node, relationship: relationship, placementBindings: placementBindings);

        string RelationAlias(string hint) => relationAliases.Allocate(hint, hint, fallback: "rows");

        string ValueAlias(QualifiedShapeId shape, FieldPath path)
        {
            var graph = shape.GraphId.Value;
            var semanticShape = shape.ShapeId.Value;
            var semanticPath = PathKey(path);
            return valueAliases.Allocate(
                $"{ShapeAlias(shape)}__{PathAlias(path)}",
                $"{graph.Length}:{graph}|{semanticShape.Length}:{semanticShape}|{semanticPath.Length}:{semanticPath}",
                fallback: "value");
        }

        string ValueAlias(string scope, string value) =>
            valueAliases.Allocate(
                $"{scope}__{value}",
                $"{scope.Length}:{scope}|{value.Length}:{value}",
                fallback: "value");

        string ResultAlias(FieldPath path) =>
            resultAliases.Allocate(PathAlias(path), PathKey(path), fallback: "result");

        string ResultAlias(params string[] parts) => resultAliases.Allocate(
            string.Join("__", parts),
            string.Join('|', parts.Select(static part => $"{part.Length}:{part}")),
            fallback: "result");

        static string PathAlias(FieldPath path) =>
            string.Join("__", path.Segments.Select(static segment =>
                segment.TryGetFieldIdentity(out var field) ? field : "item"));

        static string PathKey(FieldPath path) => string.Join(
            '/',
            path.Segments.Select(static segment =>
                $"{(int)segment.Kind}:{segment.Segment?.Length ?? 0}:{segment.Segment}"));

        string BindingAlias(ValueBindingId binding) =>
            bindingAliases.GetValueOrDefault(binding, "binding");

        static string ShapeAlias(QualifiedShapeId shape)
        {
            var value = shape.ShapeId.Value;
            if (!value.StartsWith(ClrShapeIdentityConvention.ShapeIdPrefix, StringComparison.Ordinal))
            {
                return value;
            }

            var clrIdentity = value[ClrShapeIdentityConvention.ShapeIdPrefix.Length..];
            var genericArguments = clrIdentity.IndexOf('<', StringComparison.Ordinal);
            var definition = genericArguments >= 0 ? clrIdentity[..genericArguments] : clrIdentity;
            var separator = definition.LastIndexOfAny(['.', '+']);
            return separator >= 0 && separator + 1 < definition.Length
                ? definition[(separator + 1)..]
                : definition;
        }

        static string ExpressionAlias(Expr expression, string fallback) => expression switch
        {
            FieldExpr field => PathAlias(field.Path),
            FieldRefExpr field => PathAlias(field.Path),
            ParameterExpr parameter => parameter.Parameter,
            _ => fallback
        };

        static bool IsRequiredNonNull(ValueContract? contract) => contract is
        {
            Presence: FieldPresence.Required,
            Nullability: FieldNullability.NonNullable
        };

        static PostgresRelationQueryTextSemantics? CompatibleText(
            PostgresRelationQueryTextSemantics? left,
            PostgresRelationQueryTextSemantics? right) =>
            Equals(left, right) ? left : null;

        static ValueContract IdentityContract(PostgresRelationQueryIdentityBinding identity) =>
            new(Type(identity.ScalarType), presence: FieldPresence.Required, nullability: FieldNullability.NonNullable);

        static ValueContract ReferenceContract(PostgresRelationQueryRelationshipReferenceBinding reference) =>
            new(
                Type(reference.ScalarType),
                presence: reference.MissingValueEncoding == PostgresRelationQueryMissingValueEncoding.Prohibited
                    ? FieldPresence.Required
                    : FieldPresence.Optional,
                nullability: reference.NullValueEncoding == PostgresRelationQueryNullValueEncoding.Prohibited
                    ? FieldNullability.NonNullable
                    : FieldNullability.Nullable);

        static ScalarTypeRef Type(PostgresRelationQueryScalarType scalar) => new(scalar switch
        {
            PostgresRelationQueryScalarType.Boolean => ScalarTypeKind.Bool,
            PostgresRelationQueryScalarType.Int32 => ScalarTypeKind.Int32,
            PostgresRelationQueryScalarType.Int64 => ScalarTypeKind.Int64,
            PostgresRelationQueryScalarType.Numeric => ScalarTypeKind.Decimal,
            PostgresRelationQueryScalarType.Text => ScalarTypeKind.String,
            PostgresRelationQueryScalarType.Uuid => ScalarTypeKind.Guid,
            PostgresRelationQueryScalarType.Date => ScalarTypeKind.Date,
            PostgresRelationQueryScalarType.Timestamp => ScalarTypeKind.DateTime,
            PostgresRelationQueryScalarType.TimestampWithTimeZone => ScalarTypeKind.Instant,
            PostgresRelationQueryScalarType.Bytea => ScalarTypeKind.Bytes,
            _ => throw new ArgumentOutOfRangeException(nameof(scalar), scalar, "Unsupported PostgreSQL scalar type.")
        });

        static ValueContract BooleanContract { get; } = new(
            new ScalarTypeRef(ScalarTypeKind.Bool),
            presence: FieldPresence.Required,
            nullability: FieldNullability.NonNullable);

        static BranchCompilationException Topology(QueryNodeId node, string message) =>
            Fail(PostgresRelationQueryCompilationDiagnosticCodes.UnsupportedBranchTopology, message, node);
    }

    readonly record struct ContextualEvaluation(
        RelationQueryBoundRealizationReport Report,
        ImmutableArray<RelationQueryNativeCompilationDiagnostic> Diagnostics);

    readonly record struct ContextualFailure(
        RelationQueryBoundAssessmentStatus Status,
        string Code,
        string Message,
        RelationQueryNativeResultBranchId? Branch = null,
        QueryNodeId? Node = null,
        RelationQueryInputId? Input = null);

    readonly record struct AssessmentSite(
        QueryNodeId? Node,
        RelationQueryInputId? Input,
        FieldPath? Field,
        RelationQuerySourcePlacementBindingId? Placement);

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

    static BranchCompilationException Fail(
        string code,
        string message,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null) =>
        new(code, message, node, input);

    readonly record struct FieldKey(ValueBindingId Binding, FieldPath Path);

    enum SqlNullMeaning
    {
        None = 0,
        ExplicitNull = 1,
        Undefined = 2,
        Ambiguous = 3
    }
    readonly record struct RelationshipKey(ValueBindingId Binding, RelationQueryInputId Traversal);

    sealed class FieldKeyComparer : IComparer<FieldKey>
    {
        public static FieldKeyComparer Instance { get; } = new();

        public int Compare(FieldKey x, FieldKey y)
        {
            var binding = StringComparer.Ordinal.Compare(x.Binding.Value, y.Binding.Value);
            return binding != 0 ? binding : StringComparer.Ordinal.Compare(x.Path.ToString(), y.Path.ToString());
        }
    }

    sealed class RelationshipKeyComparer : IComparer<RelationshipKey>
    {
        public static RelationshipKeyComparer Instance { get; } = new();

        public int Compare(RelationshipKey x, RelationshipKey y)
        {
            var binding = StringComparer.Ordinal.Compare(x.Binding.Value, y.Binding.Value);
            return binding != 0
                ? binding
                : StringComparer.Ordinal.Compare(x.Traversal.Value, y.Traversal.Value);
        }
    }

    readonly record struct ScopedValue(
    string Alias,
    ValueContract Contract,
    PostgresRelationQueryValueEncoding Encoding,
    PostgresRelationQueryTextSemantics? Text,
    PostgresRelationQueryOrderingCapability Ordering,
    RelationQueryInputId? SourceInput,
    RelationQuerySourcePlacementBindingId? Placement,
    QueryAssignmentId? Assignment = null,
    ImmutableArray<ValueBindingId> PresenceDependencies = default,
    string? RuntimeTextBinding = null,
    string? ConstantText = null,
    bool IsNullTextConstant = false)
    {
        public CompiledExpression Qualify(string sourceAlias) => new(
            SqlExpression.Column(sourceAlias, Alias), Contract, Encoding, Text, Ordering, SourceInput,
            Placement, Assignment, PresenceDependencies, RuntimeTextBinding, ConstantText, IsNullTextConstant);
    }

    readonly record struct ScopedIdentity(
        string Alias,
        RelationQuerySourcePlacementBindingId Placement,
        ScopedValue Value)
    {
        public QualifiedIdentity Qualify(string sourceAlias) => new(Value.Qualify(sourceAlias), Alias, Placement);
    }

    readonly record struct QualifiedIdentity(
        CompiledExpression Expression,
        string Alias,
        RelationQuerySourcePlacementBindingId Placement);

    readonly record struct OuterPresence(
        string Alias,
        RelationQuerySourcePlacementBindingId Placement,
        SqlExpression? Expression = null);

    readonly record struct CompiledExpression(
        SqlExpression Expression,
        ValueContract Contract,
        PostgresRelationQueryValueEncoding Encoding,
        PostgresRelationQueryTextSemantics? Text,
        PostgresRelationQueryOrderingCapability Ordering,
        RelationQueryInputId? SourceInput,
        RelationQuerySourcePlacementBindingId? Placement,
        QueryAssignmentId? Assignment,
        ImmutableArray<ValueBindingId> PresenceDependencies = default,
        string? RuntimeTextBinding = null,
        string? ConstantText = null,
        bool IsNullTextConstant = false)
    {
        public ScopedValue ToScoped(string alias, QueryAssignmentId? assignment = null) => new(
            alias, Contract, Encoding, Text, Ordering, SourceInput, Placement, assignment ?? Assignment,
            PresenceDependencies, RuntimeTextBinding, ConstantText, IsNullTextConstant);
    }

    readonly record struct ScopedOrder(
        string Alias,
        SqlSortDirection Direction,
        SqlNullPlacement NullPlacement,
        CompiledExpression Expression);

    sealed class Scope(
        SqlSelectQuery query,
        IReadOnlyDictionary<FieldKey, ScopedValue> values,
        IReadOnlyDictionary<ValueBindingId, ScopedIdentity> identities,
        IReadOnlyDictionary<RelationshipKey, ScopedValue> references,
        IReadOnlyDictionary<ValueBindingId, OuterPresence> outerPresence,
        ImmutableArray<ScopedOrder> orderings)
    {
        public SqlSelectQuery Query { get; } = query;
        public IReadOnlyDictionary<FieldKey, ScopedValue> Values { get; } = values;
        public IReadOnlyDictionary<ValueBindingId, ScopedIdentity> Identities { get; } = identities;
        public IReadOnlyDictionary<RelationshipKey, ScopedValue> References { get; } = references;
        public IReadOnlyDictionary<ValueBindingId, OuterPresence> OuterPresence { get; } = outerPresence;
        public ImmutableArray<ScopedOrder> Orderings { get; } = orderings;

        public Scope WithQuery(SqlSelectQuery replacement, ImmutableArray<ScopedOrder>? orderings = null) =>
            new(replacement, Values, Identities, References, OuterPresence, orderings ?? Orderings);
    }

    sealed class Environment(
        IReadOnlyDictionary<FieldKey, CompiledExpression> values,
        IReadOnlyDictionary<ValueBindingId, QualifiedIdentity> identities,
        IReadOnlyDictionary<RelationshipKey, CompiledExpression> references,
        string? sourceAlias)
    {
        public IReadOnlyDictionary<FieldKey, CompiledExpression> Values { get; } = values;
        public IReadOnlyDictionary<ValueBindingId, QualifiedIdentity> Identities { get; } = identities;
        public IReadOnlyDictionary<RelationshipKey, CompiledExpression> References { get; } = references;

        public string SourceAliasFor(string alias)
        {
            if (sourceAlias is not null)
            {
                return sourceAlias;
            }

            throw new InvalidOperationException($"Cannot recover a single derived source alias for '{alias}'.");
        }
    }

    sealed class CombinedScope
    {
        readonly HashSet<string> selectedAliases = new(StringComparer.Ordinal);
        public Dictionary<FieldKey, ScopedValue> Values { get; } = [];
        public Dictionary<ValueBindingId, ScopedIdentity> Identities { get; } = [];
        public Dictionary<RelationshipKey, ScopedValue> References { get; } = [];
        public Dictionary<ValueBindingId, OuterPresence> OuterPresence { get; } = [];

        public void AddPresenceDependency(ValueBindingId binding)
        {
            foreach (var key in Values.Keys.Where(key => key.Binding == binding).ToArray())
            {
                var value = Values[key];
                var dependencies = value.PresenceDependencies.IsDefault
                    ? [binding]
                    : value.PresenceDependencies.Contains(binding)
                        ? value.PresenceDependencies
                        : [.. value.PresenceDependencies, binding];
                Values[key] = value with
                {
                    PresenceDependencies =
                    [
                        .. dependencies.OrderBy(static dependency => dependency.Value, StringComparer.Ordinal)
                    ]
                };
            }
        }

        public void Add(SqlSelectBuilder builder, Scope scope, Environment environment)
        {
            foreach (var pair in scope.Values)
            {
                if (selectedAliases.Add(pair.Value.Alias))
                {
                    builder.Select(environment.Values[pair.Key].Expression, pair.Value.Alias);
                }

                Values.Add(pair.Key, pair.Value);
            }
            foreach (var pair in scope.Identities)
            {
                if (selectedAliases.Add(pair.Value.Alias))
                {
                    builder.Select(environment.Identities[pair.Key].Expression.Expression, pair.Value.Alias);
                }

                Identities.Add(pair.Key, pair.Value);
            }
            foreach (var pair in scope.References)
            {
                if (selectedAliases.Add(pair.Value.Alias))
                {
                    builder.Select(environment.References[pair.Key].Expression, pair.Value.Alias);
                }

                References.Add(pair.Key, pair.Value);
            }
            foreach (var pair in scope.OuterPresence)
            {
                var expression = pair.Value.Expression
                                 ?? SqlExpression.Column(environment.SourceAliasFor(pair.Value.Alias), pair.Value.Alias);
                if (selectedAliases.Add(pair.Value.Alias))
                {
                    builder.Select(expression, pair.Value.Alias);
                }

                OuterPresence.Add(pair.Key, pair.Value with { Expression = null });
            }
        }

        public Scope Build(SqlSelectQuery query) =>
            new(query, Values, Identities, References, OuterPresence, []);
    }

    readonly record struct TerminalResult(
        SqlSelectQuery Query,
        ImmutableArray<PostgresRelationQueryResultFieldBinding> ResultFields,
        ImmutableArray<PostgresRelationQueryPresenceBinding> Presence,
        PostgresRelationQueryRelationKeyBinding? RelationKey,
        ImmutableArray<PostgresRelationQueryInvariantBinding> Invariants);

    readonly record struct PreparedBranch(
        SqlCommandTemplate Statement,
        ImmutableArray<PostgresRelationQuerySelectedField> SelectedFields,
        TerminalResult Terminal,
        ImmutableArray<PostgresRelationQuerySuppliedFieldBinding> SuppliedFields,
        ImmutableArray<PostgresRelationQueryParameterBinding> Parameters);
}

static class PostgresRelationQueryArtifactFingerprinter
{
    const string Algorithm = "sha256";
    const string Canonicalization = "cohesive.relations.postgres-artifact/v4-c14n/v1";

    public static PostgresRelationQueryArtifactFingerprint Compute(
        string schemaVersion,
        RelationQueryNativeResultBranch branch,
        SqlCommandTemplate statement,
        PostgresRelationQueryStorageBinding storageBinding,
        ImmutableArray<PostgresRelationQuerySelectedField> selectedFields,
        ImmutableArray<PostgresRelationQueryResultFieldBinding> resultFields,
        ImmutableArray<PostgresRelationQueryPresenceBinding> presenceBindings,
        ImmutableArray<PostgresRelationQuerySuppliedFieldBinding> suppliedFields,
        ImmutableArray<PostgresRelationQueryParameterBinding> parameters,
        PostgresRelationQueryPagingContract? paging,
        PostgresRelationQueryRelationKeyBinding? relationKey,
        ImmutableArray<PostgresRelationQueryInvariantBinding> invariants,
        ImmutableArray<PostgresRelationQueryLoweringDecision> decisions,
        RelationQueryNativeCompilationProvenance provenance)
    {
        StringBuilder canonical = new();
        var jsonOptions = RelationQueryJsonSerializer.CreateOptions();
        Append(canonical, Canonicalization);
        Append(canonical, schemaVersion);
        Append(canonical, JsonSerializer.Serialize(branch, jsonOptions));
        Append(canonical, statement.Text);
        Append(canonical, storageBinding.Fingerprint.Value);
        Append(canonical, JsonSerializer.Serialize(statement.Parameters, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(selectedFields, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(resultFields, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(presenceBindings, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(suppliedFields, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(parameters, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(paging, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(relationKey, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(invariants, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(decisions, jsonOptions));
        Append(canonical, JsonSerializer.Serialize(provenance, jsonOptions));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new(Algorithm, Canonicalization, Convert.ToHexStringLower(hash));
    }

    static void Append(StringBuilder builder, string? value) =>
        builder.Append(value is null ? -1 : Encoding.UTF8.GetByteCount(value))
            .Append(':')
            .Append(value)
            .Append(';');
}
