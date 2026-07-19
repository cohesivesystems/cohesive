using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Compiles and weakly caches fail-closed CLR materialization kernels over canonical relation output rows.
/// </summary>
/// <remarks>
/// Compilation consumes only the final relation terminal described by a successful static plan. It does not
/// interpret relation nodes, acquire data, or provide a second query execution path.
/// </remarks>
public sealed class RelationDtoMapperCompiler
{
    readonly ConditionalWeakTable<CompiledRelationQueryPlan, ConcurrentDictionary<CacheKey, Lazy<object>>> cache = [];

    /// <summary>Shared compiler whose cache does not extend the lifetime of compiled relation plans.</summary>
    public static RelationDtoMapperCompiler Default { get; } = new();

    /// <summary>Creates an independent mapper compiler and cache.</summary>
    public RelationDtoMapperCompiler()
    {
    }

    /// <summary>Compiles or retrieves one canonical relation-output-to-CLR DTO kernel.</summary>
    /// <typeparam name="TOutput">CLR DTO type to materialize.</typeparam>
    /// <param name="plan">Successful target-independent plan exposing a canonical relation terminal.</param>
    /// <param name="profile">Member-binding profile, or <see langword="null"/> for the conventional profile.</param>
    /// <param name="options">Compilation options, or <see langword="null"/> for conventional options.</param>
    /// <returns>
    /// A cached mapper and its diagnostics, or fail-closed diagnostics when the plan and target contract cannot be
    /// mapped without ambiguity.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A plan shape snapshot cannot be represented by the compiled-plan canonicalization profile.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A plan shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A plan shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public RelationDtoMapperCompilationResult<TOutput> Compile<TOutput>(
        CompiledRelationQueryPlan plan,
        RelationDtoMapperProfile? profile = null,
        RelationDtoMapperCompilationOptions? options = null
        )
    {
        ArgumentNullException.ThrowIfNull(plan);
        profile ??= RelationDtoMapperProfile.Conventional;
        options ??= RelationDtoMapperCompilationOptions.Conventional;

        var key = new CacheKey(typeof(TOutput), ProfileFingerprint: profile.Fingerprint, OptionsFingerprint: options.Fingerprint);
        var planCache = cache.GetValue(plan, static _ => new());
        var lazy = planCache.GetOrAdd(
            key,
            _ => new(
                () => RelationDtoMapperBuilder.Compile<TOutput>(plan, profile, options),
                LazyThreadSafetyMode.ExecutionAndPublication
            ));
        return (RelationDtoMapperCompilationResult<TOutput>)lazy.Value;
    }

    readonly record struct CacheKey(Type OutputType, string ProfileFingerprint, string OptionsFingerprint);
}

/// <summary>
/// Runtime-compiled CLR materializer for one exact canonical relation terminal and target DTO contract.
/// </summary>
/// <typeparam name="TOutput">CLR DTO type produced by this mapper.</typeparam>
public sealed class CompiledRelationDtoMapper<TOutput>
{
    readonly Func<ObservationValue, TOutput> kernel;

    internal CompiledRelationDtoMapper(
        RelationDtoMapperDescriptor descriptor,
        Func<ObservationValue, TOutput> kernel)
    {
        Descriptor = descriptor;
        this.kernel = kernel;
    }

    /// <summary>Explainable plan, target, profile, options, and resolved member decisions.</summary>
    public RelationDtoMapperDescriptor Descriptor { get; }

    internal Func<ObservationValue, TOutput> MaterializationKernel => kernel;

    /// <summary>Materializes typed rows from an exact canonical relation execution.</summary>
    /// <param name="execution">Canonical execution whose relation terminal will be consumed.</param>
    /// <param name="failurePolicy">Policy applied when individual rows cannot be materialized.</param>
    /// <param name="cancellationToken">Token observed before validation and between rows.</param>
    /// <returns>
    /// Typed rows paired with their exact source rows, failures, diagnostics, and the exact supplied execution.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="execution"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failurePolicy"/> is unsupported.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public RelationDtoMappingResult<TOutput> Map(
        RelationQueryExecutionResult execution,
        RelationDtoMappingFailurePolicy failurePolicy = RelationDtoMappingFailurePolicy.Strict,
        CancellationToken cancellationToken = default
        )
    {
        ArgumentNullException.ThrowIfNull(execution);
        ValidatePolicy(failurePolicy);
        cancellationToken.ThrowIfCancellationRequested();
        return MapCore(execution, physicalExecution: null, failurePolicy, cancellationToken);
    }

    /// <summary>Materializes typed rows from the canonical interpretation inside a physical execution.</summary>
    /// <param name="execution">Physical execution whose exact canonical interpretation will be consumed.</param>
    /// <param name="failurePolicy">Policy applied when individual rows cannot be materialized.</param>
    /// <param name="cancellationToken">Token observed before validation and between rows.</param>
    /// <returns>
    /// Typed rows paired with exact canonical and physical provenance, or a fail-closed result when interpretation
    /// was unavailable.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="execution"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failurePolicy"/> is unsupported.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public RelationDtoMappingResult<TOutput> Map(
        RelationQueryPhysicalExecutionResult execution,
        RelationDtoMappingFailurePolicy failurePolicy = RelationDtoMappingFailurePolicy.Strict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ValidatePolicy(failurePolicy);
        cancellationToken.ThrowIfCancellationRequested();

        if (execution.Interpretation is null)
        {
            var diagnostic = RuntimeDiagnostic(
                RelationDtoMapperDiagnosticCodes.PhysicalInterpretationUnavailable,
                "Physical execution did not produce a canonical relation interpretation.",
                evaluation: execution.Evidence?.Evaluation);
            return new(
                RelationDtoMappingStatus.Failed,
                execution: null,
                physicalExecution: execution,
                rows: [],
                failedRows: [],
                diagnostics: [diagnostic]);
        }

        var mapped = MapCore(execution.Interpretation, execution, failurePolicy, cancellationToken);
        if (execution.Status == RelationQueryExecutionStatus.Succeeded)
            return mapped;

        return new(
            execution.Status == RelationQueryExecutionStatus.Incomplete
                && mapped.Status != RelationDtoMappingStatus.Failed
                    ? RelationDtoMappingStatus.Incomplete
                    : RelationDtoMappingStatus.Failed,
            mapped.Execution,
            execution,
            execution.Status == RelationQueryExecutionStatus.Failed ? [] : mapped.Rows,
            mapped.FailedRows,
            mapped.Diagnostics);
    }

    RelationDtoMappingResult<TOutput> MapCore(
        RelationQueryExecutionResult execution,
        RelationQueryPhysicalExecutionResult? physicalExecution,
        RelationDtoMappingFailurePolicy failurePolicy,
        CancellationToken cancellationToken)
    {
        if (!MatchesPlanReference(execution.PlanReference, Descriptor.PlanReference))
        {
            var diagnostic = RuntimeDiagnostic(
                RelationDtoMapperDiagnosticCodes.PlanMismatch,
                "The canonical execution does not carry the exact plan reference compiled into this mapper.",
                evaluation: execution.Evaluation);
            return Failure(execution, physicalExecution, diagnostic);
        }

        var relation = execution.Relation;
        if (relation is null
            || relation.Relation != Descriptor.Relation
            || relation.Shape != Descriptor.OutputShape
            || relation.Mode != Descriptor.OutputMode)
        {
            var diagnostic = RuntimeDiagnostic(
                RelationDtoMapperDiagnosticCodes.RelationTerminalMismatch,
                "The canonical execution does not expose the relation terminal compiled into this mapper.",
                evaluation: execution.Evaluation);
            return Failure(execution, physicalExecution, diagnostic);
        }

        if (execution.Status == RelationQueryExecutionStatus.Failed)
        {
            var diagnostic = RuntimeDiagnostic(
                RelationDtoMapperDiagnosticCodes.RelationTerminalMismatch,
                "The canonical relation execution failed and cannot produce trustworthy DTO rows.",
                evaluation: execution.Evaluation);
            return Failure(execution, physicalExecution, diagnostic);
        }

        var mappedRows = ImmutableArray.CreateBuilder<RelationDtoMappedRow<TOutput>>(relation.Rows.Length);
        var failedRows = ImmutableArray.CreateBuilder<RelationDtoRowFailure>();
        var diagnostics = ImmutableArray.CreateBuilder<RelationDtoMapperDiagnostic>();

        for (var index = 0; index < relation.Rows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = relation.Rows[index];
            if (row.Shape != Descriptor.OutputShape)
            {
                AddFailure(
                    index,
                    row,
                    RuntimeDiagnostic(
                        RelationDtoMapperDiagnosticCodes.RowShapeMismatch,
                        "Canonical relation output row shape does not match the compiled DTO kernel.",
                        field: null,
                        targetMember: null,
                        evaluation: execution.Evaluation,
                        occurrence: row.Root?.Id,
                        rowIndex: index),
                    failedRows,
                    diagnostics);
                continue;
            }

            try
            {
                mappedRows.Add(new(row, kernel(row.Value)));
            }
            catch (RelationDtoRowMappingException exception)
            {
                AddFailure(
                    index,
                    row,
                    RuntimeDiagnostic(
                        RelationDtoMapperDiagnosticCodes.RuntimeFieldConversionFailed,
                        exception.DiagnosticMessage,
                        exception.Binding.OutputField.Path,
                        exception.Binding.TargetMember,
                        exception.Binding.OutputReference?.Node,
                        exception.Binding.Assignment,
                        execution.Evaluation,
                        row.Root?.Id,
                        index),
                    failedRows,
                    diagnostics);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              and not OutOfMemoryException
                                              and not StackOverflowException
                                              and not AccessViolationException)
            {
                AddFailure(
                    index,
                    row,
                    RuntimeDiagnostic(
                        RelationDtoMapperDiagnosticCodes.RuntimeFieldConversionFailed,
                        $"The CLR DTO constructor or member initializer failed with '{exception.GetType().Name}'.",
                        evaluation: execution.Evaluation,
                        occurrence: row.Root?.Id,
                        rowIndex: index),
                    failedRows,
                    diagnostics);
            }
        }

        var sourceIncomplete = execution.Status == RelationQueryExecutionStatus.Incomplete
                               || relation.State == RelationQueryExecutionOutputState.Incomplete
                               || relation.Rows.Any(static row => !row.IsComplete);
        if (failedRows.Count == 0)
        {
            return new(
                sourceIncomplete ? RelationDtoMappingStatus.Incomplete : RelationDtoMappingStatus.Succeeded,
                execution,
                physicalExecution,
                mappedRows.MoveToImmutable(),
                [],
                diagnostics.ToImmutable());
        }

        return failurePolicy switch
        {
            RelationDtoMappingFailurePolicy.Strict => new(
                RelationDtoMappingStatus.Failed,
                execution,
                physicalExecution,
                [],
                failedRows.ToImmutable(),
                diagnostics.ToImmutable()),
            RelationDtoMappingFailurePolicy.CollectDiagnostics => new(
                RelationDtoMappingStatus.Incomplete,
                execution,
                physicalExecution,
                mappedRows.ToImmutable(),
                failedRows.ToImmutable(),
                diagnostics.ToImmutable()),
            RelationDtoMappingFailurePolicy.SkipInvalidRows => new(
                sourceIncomplete
                    ? RelationDtoMappingStatus.Incomplete
                    : RelationDtoMappingStatus.SucceededWithSkippedRows,
                execution,
                physicalExecution,
                mappedRows.ToImmutable(),
                failedRows.ToImmutable(),
                diagnostics.ToImmutable()),
            _ => throw new UnreachableException()
        };
    }

    RelationDtoMappingResult<TOutput> Failure(
        RelationQueryExecutionResult execution,
        RelationQueryPhysicalExecutionResult? physicalExecution,
        RelationDtoMapperDiagnostic diagnostic) =>
        new(
            RelationDtoMappingStatus.Failed,
            execution,
            physicalExecution,
            rows: [],
            failedRows: [],
            diagnostics: [diagnostic]);

    RelationDtoMapperDiagnostic RuntimeDiagnostic(
        string code,
        string message,
        FieldPath? field = null,
        string? targetMember = null,
        QueryNodeId? node = null,
        QueryAssignmentId? assignment = null,
        RelationQueryEvaluationId? evaluation = null,
        RelationQueryOccurrenceId? occurrence = null,
        int? rowIndex = null) =>
        new(
            code,
            DiagnosticSeverity.Error,
            RelationDtoMapperDiagnosticPhase.Runtime,
            message,
            Descriptor.Relation,
            Descriptor.OutputShape,
            field,
            targetMember,
            node,
            assignment,
            evaluation,
            occurrence,
            rowIndex);

    static void AddFailure(
        int rowIndex,
        RelationQueryOutputRow row,
        RelationDtoMapperDiagnostic diagnostic,
        ImmutableArray<RelationDtoRowFailure>.Builder failedRows,
        ImmutableArray<RelationDtoMapperDiagnostic>.Builder diagnostics)
    {
        failedRows.Add(new(rowIndex, row, [diagnostic]));
        diagnostics.Add(diagnostic);
    }

    static void ValidatePolicy(RelationDtoMappingFailurePolicy failurePolicy)
    {
        if (!Enum.IsDefined(failurePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failurePolicy), failurePolicy, "Unsupported DTO mapping failure policy.");
        }
    }

    static bool MatchesPlanReference(
        RelationQueryCompiledPlanReference execution,
        RelationQueryCompiledPlanReference compiled) =>
        string.Equals(execution.CompilerProfile, compiled.CompilerProfile, StringComparison.Ordinal)
        && string.Equals(execution.DefinitionSchemaVersion, compiled.DefinitionSchemaVersion, StringComparison.Ordinal)
        && Equals(execution.DefinitionFingerprint, compiled.DefinitionFingerprint)
        && Equals(execution.ShapeSnapshotsFingerprint, compiled.ShapeSnapshotsFingerprint)
        && Equals(execution.RelationshipCatalogFingerprint, compiled.RelationshipCatalogFingerprint)
        && Equals(execution.DemandFingerprint, compiled.DemandFingerprint)
        && execution.Inputs.SequenceEqual(compiled.Inputs);
}
