using System.Collections.Immutable;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>
/// Pure deterministic reference reducer for bounded manual operating-limit updates.
/// </summary>
/// <remarks>
/// Submission validates authorization, optimistic fences, immutable definition bounds, and idempotency before it
/// records a pending request. Submission never changes the effective operating point. <see cref="Apply"/> is the
/// only transition that changes the point, and only after exact later safe-point evidence has been validated.
/// </remarks>
public static class ControlLimitUpdateReferenceReducer
{
    /// <summary>Reduces one manual update command against complete durable state.</summary>
    /// <param name="definition">Canonical bounded control-loop definition.</param>
    /// <param name="state">Complete durable manual limit-update state.</param>
    /// <param name="command">Canonical update command.</param>
    /// <param name="decidedAtUtc">Explicit UTC command-decision time.</param>
    /// <returns>An accepted, replayed, or precise rejection decision and complete resulting state.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="state"/>, or <paramref name="command"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="decidedAtUtc"/> is not UTC.</exception>
    public static ControlLimitUpdateDecision Submit(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ControlLimitUpdateCommand command,
        DateTimeOffset decidedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ControlObservation.RequireUtc(decidedAtUtc, nameof(decidedAtUtc));

        var validation = ValidateState(definition, state);
        if (!validation.IsValid)
            return Reject(state, ControlLimitUpdateDecisionDisposition.Invalid, validation.Diagnostics);

        if (command.Authorization.AuthorityScope != state.AuthorityScope)
        {
            return Reject(
                state,
                ControlLimitUpdateDecisionDisposition.Unauthorized,
                Diagnostic(
                    ControlDiagnosticCodes.LimitUpdateUnauthorized,
                    "The command authorization scope does not own this controlled loop.",
                    "/authorization/authorityScope"));
        }

        var replay = ResolveReplay(state, command);
        if (replay is not null)
            return replay;

        var stale = ValidateCommandFence(state, command);
        if (stale is not null)
            return stale;

        if (state.Revision.Ordinal == long.MaxValue)
        {
            return Reject(
                state,
                ControlLimitUpdateDecisionDisposition.Invalid,
                RevisionExhaustedDiagnostic(state));
        }

        if (decidedAtUtc < command.IssuedAtUtc || decidedAtUtc < state.UpdatedAtUtc)
        {
            return Reject(
                state,
                ControlLimitUpdateDecisionDisposition.Invalid,
                Diagnostic(
                    ControlDiagnosticCodes.LimitUpdateInvalid,
                    "Command acceptance must follow issuance and the latest durable state transition.",
                    "/issuedAtUtc",
                    expected: $">= {state.UpdatedAtUtc:O}",
                    observed: command.IssuedAtUtc.ToString("O")));
        }

        if (state.PendingLimitUpdate is not null)
        {
            return Reject(
                state,
                ControlLimitUpdateDecisionDisposition.PendingConflict,
                Diagnostic(
                    ControlDiagnosticCodes.LimitUpdatePending,
                    "Another accepted manual update is awaiting an invariant-preserving application point.",
                    "/state/pendingUpdate",
                    expected: "no pending update",
                    observed: state.PendingLimitUpdate.Command.CommandId.Value));
        }

        var pointValidation = definition.ValidateOperatingPoint(command.RequestedOperatingPoint);
        if (!pointValidation.IsValid)
        {
            return Reject(
                state,
                ControlLimitUpdateDecisionDisposition.OutOfBounds,
                pointValidation.Diagnostics);
        }

        if (!TryGetApplicationKind(
            state.OperatingPoint,
            command.RequestedOperatingPoint,
            out _,
            out var transitionDiagnostic))
        {
            return Reject(
                state,
                ControlLimitUpdateDecisionDisposition.Invalid,
                [transitionDiagnostic!]);
        }

        var acceptedRevision = state.Revision.Next();
        var receipt = new ControlLimitUpdateReceipt(command, acceptedRevision, decidedAtUtc);
        var next = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            state.LoopId,
            state.Target,
            state.Epoch,
            acceptedRevision,
            state.DefinitionFingerprint,
            state.OperatingPoint,
            healthyObservationCount: 0,
            state.CreatedAtUtc,
            updatedAtUtc: decidedAtUtc,
            lastEvaluatedAtUtc: state.LastEvaluatedAtUtc,
            lastClassification: state.LastClassification,
            cooldownUntilUtc: state.CooldownUntilUtc,
            lastObservation: state.LastObservation,
            pendingRecommendation: null,
            lastActuation: state.LastActuation,
            lastApplicationFence: state.LastApplicationFence,
            authorityScope: state.AuthorityScope,
            limitUpdateActuations: state.LimitUpdateActuations,
            pendingLimitUpdate: receipt);

        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlLimitUpdateDecisionDisposition.Accepted,
            next,
            receipt);
    }

    /// <summary>Attempts to make the pending update effective at an exact later application point.</summary>
    /// <param name="definition">Canonical bounded control-loop definition.</param>
    /// <param name="state">Complete durable manual limit-update state.</param>
    /// <param name="applicationPoint">Exact invariant-preserving runtime cut.</param>
    /// <param name="appliedAtUtc">Explicit UTC application time.</param>
    /// <returns>An applied, replayed, deferred, or rejected result and complete resulting state.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="state"/>, or <paramref name="applicationPoint"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="appliedAtUtc"/> is not UTC.</exception>
    public static ControlLimitUpdateActuationResult Apply(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ControlApplicationPoint applicationPoint,
        DateTimeOffset appliedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(applicationPoint);
        ControlObservation.RequireUtc(appliedAtUtc, nameof(appliedAtUtc));

        var validation = ValidateState(definition, state);
        if (!validation.IsValid)
            return RejectedActuation(state, validation.Diagnostics);

        foreach (var priorActuation in state.LimitUpdateActuations)
        {
            if (!HasSameApplicationPointScope(priorActuation.ApplicationPoint, applicationPoint))
                continue;
            if (priorActuation.ApplicationPoint != applicationPoint)
            {
                return RejectedActuation(
                    state,
                    Diagnostic(
                        ControlDiagnosticCodes.LimitUpdateApplicationPointConflict,
                        "The application-point identity was reused with different safe-point evidence.",
                        "/applicationPoint/id"));
            }

            return new(
                ControlLoopDefinition.CurrentSchemaVersion,
                ControlActuationDisposition.Replayed,
                state,
                priorActuation);
        }

        if (state.Revision.Ordinal == long.MaxValue)
            return RejectedActuation(state, RevisionExhaustedDiagnostic(state));

        var pending = state.PendingLimitUpdate;
        if (pending is null)
        {
            return new(
                ControlLoopDefinition.CurrentSchemaVersion,
                ControlActuationDisposition.Deferred,
                state,
                diagnostics: Diagnostic(
                    ControlDiagnosticCodes.LimitUpdateAbsent,
                    "No accepted manual update is awaiting a safe application point.",
                    "/state/pendingUpdate",
                    DiagnosticSeverity.Info));
        }

        _ = TryGetApplicationKind(
            state.OperatingPoint,
            pending.Command.RequestedOperatingPoint,
            out var requiredKind,
            out _);

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (applicationPoint.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || applicationPoint.LoopId != state.LoopId
            || applicationPoint.DefinitionFingerprint != state.DefinitionFingerprint
            || !string.Equals(applicationPoint.Target, state.Target, StringComparison.Ordinal)
            || applicationPoint.Epoch != state.Epoch
            || applicationPoint.ExpectedRevision != state.Revision
            || pending.AcceptedRevision != state.Revision
            || !string.Equals(applicationPoint.Authority, definition.ApplicationAuthority, StringComparison.Ordinal)
            || applicationPoint.Kind != requiredKind)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ApplicationFenceMismatch,
                "The application point does not address the pending update's exact loop, definition, target, epoch, revision, authority, and cut kind.",
                "/applicationPoint",
                expected: $"{state.LoopId.Value}/{state.Target}/{state.Epoch.Value}/{state.Revision.Value}/{definition.ApplicationAuthority}/{requiredKind}",
                observed: $"{applicationPoint.LoopId.Value}/{applicationPoint.Target}/{applicationPoint.Epoch.Value}/{applicationPoint.ExpectedRevision.Value}/{applicationPoint.Authority}/{applicationPoint.Kind}"));
        }
        if (state.LastApplicationFence is { } lastFence && applicationPoint.Fence.Ordinal <= lastFence.Ordinal)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ApplicationFenceMismatch,
                "The application fence must be strictly later than the last applied runtime fence.",
                "/applicationPoint/fence",
                expected: $"> {lastFence.Value}",
                observed: applicationPoint.Fence.Value));
        }
        if (applicationPoint.ObservedAtUtc <= pending.AcceptedAtUtc
            || appliedAtUtc < applicationPoint.ObservedAtUtc
            || appliedAtUtc < state.UpdatedAtUtc)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ApplicationPointInvalid,
                "Actuation must occur at an exact safe point strictly later than command acceptance.",
                "/applicationPoint/observedAtUtc",
                expected: $"> {pending.AcceptedAtUtc:O}",
                observed: applicationPoint.ObservedAtUtc.ToString("O")));
        }

        var pointValidation = definition.ValidateOperatingPoint(pending.Command.RequestedOperatingPoint);
        diagnostics.AddRange(pointValidation.Diagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return RejectedActuation(state, [.. diagnostics]);

        var nextRevision = state.Revision.Next();
        var actuation = new ControlLimitUpdateActuation(
            ControlDerivedIdentity.LimitUpdateActuation(pending, applicationPoint),
            pending,
            applicationPoint,
            state.OperatingPoint,
            nextRevision,
            appliedAtUtc);
        var nextActuations = ImmutableArray.CreateBuilder<ControlLimitUpdateActuation>(state.LimitUpdateActuations.Length + 1);
        nextActuations.AddRange(state.LimitUpdateActuations);
        nextActuations.Add(actuation);
        var next = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            state.LoopId,
            state.Target,
            state.Epoch,
            nextRevision,
            state.DefinitionFingerprint,
            pending.Command.RequestedOperatingPoint,
            healthyObservationCount: 0,
            state.CreatedAtUtc,
            updatedAtUtc: appliedAtUtc,
            lastEvaluatedAtUtc: state.LastEvaluatedAtUtc,
            lastClassification: state.LastClassification,
            cooldownUntilUtc: null,
            lastObservation: state.LastObservation,
            pendingRecommendation: null,
            lastActuation: state.LastActuation,
            lastApplicationFence: applicationPoint.Fence,
            authorityScope: state.AuthorityScope,
            limitUpdateActuations: nextActuations.MoveToImmutable(),
            pendingLimitUpdate: null);

        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlActuationDisposition.Applied,
            next,
            actuation);
    }

    /// <summary>Validates complete durable manual limit-update state against its canonical loop definition.</summary>
    /// <param name="definition">Canonical bounded control-loop definition.</param>
    /// <param name="state">State to validate.</param>
    /// <returns>Structured diagnostics; an empty result proves the retained transition chain and bounds.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="state"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult ValidateState(
        ControlLoopDefinition definition,
        ControlLoopState state) =>
        AimdControlReferenceRegulator.ValidateState(definition, state);

    static ControlLimitUpdateDecision? ResolveReplay(
        ControlLoopState state,
        ControlLimitUpdateCommand command)
    {
        var sameIdentity = state.FindLimitUpdateReceipt(command.CommandId);
        ControlLimitUpdateReceipt? sameIdempotency = null;
        foreach (var receipt in state.LimitUpdateReceipts)
        {
            if (receipt.Command.IdempotencyKey == command.IdempotencyKey)
                sameIdempotency = receipt;
        }

        if (sameIdentity is not null)
        {
            return sameIdentity.Command == command
                ? new(
                    ControlLoopDefinition.CurrentSchemaVersion,
                    ControlLimitUpdateDecisionDisposition.Replayed,
                    state,
                    sameIdentity)
                : Reject(
                    state,
                    ControlLimitUpdateDecisionDisposition.IdentityConflict,
                    Diagnostic(
                        ControlDiagnosticCodes.LimitUpdateIdentityConflict,
                        "The stable command identity was reused with different canonical content.",
                        "/commandId"));
        }

        if (sameIdempotency is not null)
        {
            return HasSameIdempotentIntent(sameIdempotency.Command, command)
                ? new(
                    ControlLoopDefinition.CurrentSchemaVersion,
                    ControlLimitUpdateDecisionDisposition.Replayed,
                    state,
                    sameIdempotency)
                : Reject(
                    state,
                    ControlLimitUpdateDecisionDisposition.IdempotencyConflict,
                    Diagnostic(
                        ControlDiagnosticCodes.LimitUpdateIdempotencyConflict,
                        "The idempotency key was reused for a different semantic update intent.",
                        "/idempotencyKey"));
        }

        return null;
    }

    static ControlLimitUpdateDecision? ValidateCommandFence(
        ControlLoopState state,
        ControlLimitUpdateCommand command)
    {
        string? location = null;
        string? expected = null;
        string? observed = null;
        if (command.LoopId != state.LoopId)
        {
            location = "/loopId";
            expected = state.LoopId.Value;
            observed = command.LoopId.Value;
        }
        else if (command.DefinitionFingerprint != state.DefinitionFingerprint)
        {
            location = "/definitionFingerprint";
            expected = state.DefinitionFingerprint.Value;
            observed = command.DefinitionFingerprint.Value;
        }
        else if (!string.Equals(command.Target, state.Target, StringComparison.Ordinal))
        {
            location = "/target";
            expected = state.Target;
            observed = command.Target;
        }
        else if (command.Epoch != state.Epoch)
        {
            location = "/epoch";
            expected = state.Epoch.Value;
            observed = command.Epoch.Value;
        }
        else if (command.ExpectedRevision != state.Revision)
        {
            location = "/expectedRevision";
            expected = state.Revision.Value;
            observed = command.ExpectedRevision.Value;
        }

        return location is null
            ? null
            : Reject(
                state,
                ControlLimitUpdateDecisionDisposition.Stale,
                Diagnostic(
                    ControlDiagnosticCodes.LimitUpdateStaleFence,
                    "The manual update command does not address the current exact control fence.",
                    location,
                    expected,
                    observed));
    }

    /// <summary>Compares the complete semantic intent covered by a command's idempotency key.</summary>
    /// <param name="prior">Retained canonical command.</param>
    /// <param name="candidate">Candidate command to compare.</param>
    /// <returns>
    /// <see langword="true"/> when both commands have the same idempotency key, exact address, optimistic fence,
    /// requested point, authorization, and provenance; command occurrence identity and issuance time are excluded.
    /// </returns>
    /// <exception cref="ArgumentNullException">A command is <see langword="null"/>.</exception>
    public static bool HasSameIdempotentIntent(
        ControlLimitUpdateCommand prior,
        ControlLimitUpdateCommand candidate)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(candidate);
        return prior.SchemaVersion == candidate.SchemaVersion
        && prior.IdempotencyKey == candidate.IdempotencyKey
        && prior.LoopId == candidate.LoopId
        && prior.DefinitionFingerprint == candidate.DefinitionFingerprint
        && string.Equals(prior.Target, candidate.Target, StringComparison.Ordinal)
        && prior.Epoch == candidate.Epoch
        && prior.ExpectedRevision == candidate.ExpectedRevision
        && prior.RequestedOperatingPoint == candidate.RequestedOperatingPoint
        && prior.Authorization == candidate.Authorization
        && prior.Provenance == candidate.Provenance;
    }

    internal static bool TryGetApplicationKind(
        ControlOperatingPoint prior,
        ControlOperatingPoint requested,
        out ControlApplicationPointKind kind,
        out DocumentValidationDiagnostic? diagnostic)
    {
        kind = default;
        diagnostic = null;
        if (prior.Values.Length != requested.Values.Length)
        {
            diagnostic = CreateDiagnostic(
                ControlDiagnosticCodes.LimitUpdateInvalid,
                "A manual update must preserve the complete operating-point shape.",
                "/requestedOperatingPoint");
            return false;
        }

        ControlApplicationPointKind? required = null;
        for (var index = 0; index < prior.Values.Length; index++)
        {
            var before = prior.Values[index];
            var after = requested.Values[index];
            if (before.Actuator != after.Actuator)
            {
                diagnostic = CreateDiagnostic(
                    ControlDiagnosticCodes.LimitUpdateInvalid,
                    "A manual update must preserve the complete operating-point shape.",
                    "/requestedOperatingPoint");
                return false;
            }
            if (before == after)
                continue;

            var candidate = ControlApplicationPointCatalog.ForActuator(before.Actuator);
            if (required is not null && required != candidate)
            {
                diagnostic = CreateDiagnostic(
                    ControlDiagnosticCodes.LimitUpdateInvalid,
                    "One atomic manual update may change only actuators sharing the same invariant-preserving cut kind.",
                    "/requestedOperatingPoint",
                    expected: required.ToString(),
                    observed: candidate.ToString());
                return false;
            }
            required = candidate;
        }

        if (required is null)
        {
            diagnostic = CreateDiagnostic(
                ControlDiagnosticCodes.LimitUpdateInvalid,
                "A manual update must change at least one bounded actuator.",
                "/requestedOperatingPoint");
            return false;
        }

        kind = required.Value;
        return true;
    }

    static bool HasSameApplicationPointScope(
        ControlApplicationPoint prior,
        ControlApplicationPoint candidate) =>
        prior.LoopId == candidate.LoopId
        && string.Equals(prior.Target, candidate.Target, StringComparison.Ordinal)
        && prior.Epoch == candidate.Epoch
        && prior.Id == candidate.Id;

    static ControlLimitUpdateDecision Reject(
        ControlLoopState state,
        ControlLimitUpdateDecisionDisposition disposition,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            disposition,
            state,
            diagnostics: diagnostics);

    static ControlLimitUpdateActuationResult RejectedActuation(
        ControlLoopState state,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlActuationDisposition.Rejected,
            state,
            diagnostics: diagnostics);

    static ImmutableArray<DocumentValidationDiagnostic> Diagnostic(
        string code,
        string message,
        string location,
        string? expected = null,
        string? observed = null) =>
        [CreateDiagnostic(code, message, location, expected, observed)];

    static ImmutableArray<DocumentValidationDiagnostic> Diagnostic(
        string code,
        string message,
        string location,
        DiagnosticSeverity severity) =>
        [CreateDiagnostic(code, message, location, severity: severity)];

    static ImmutableArray<DocumentValidationDiagnostic> RevisionExhaustedDiagnostic(
        ControlLoopState state) =>
        Diagnostic(
            ControlDiagnosticCodes.RevisionExhausted,
            "The control revision space is exhausted; begin a new control epoch before accepting another update.",
            "/state/revision",
            expected: $"< {long.MaxValue}",
            observed: state.Revision.Value);

    static DocumentValidationDiagnostic CreateDiagnostic(
        string code,
        string message,
        string location,
        string? expected = null,
        string? observed = null,
        DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new(
            code,
            severity,
            message,
            location,
            Evidence: new(
                stage: "control-limit-update-reference",
                expected: expected,
                observed: observed));
}
