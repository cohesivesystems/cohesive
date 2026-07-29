using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>Stable wire names for bounded manual Control operations.</summary>
public static class ControlLimitUpdateWireNames
{
    /// <summary>Canonical semantic authority that owns bounded manual Control limit updates.</summary>
    public const string SemanticAuthority = "cohesive.control.limit-update";

    /// <summary>Protocol-neutral operation identity for requesting a complete bounded operating point.</summary>
    public const string UpdateLimits = "updateLimits";

    /// <summary>Canonical semantic path of a bounded manual Control limit-update command.</summary>
    public static ExecutionSemanticPath CommandPath { get; } = new(["commands", UpdateLimits]);
}

/// <summary>Outcome of reducing one bounded manual limit-update command.</summary>
public enum ControlLimitUpdateDecisionDisposition
{
    /// <summary>No outcome was declared; invalid in a canonical decision.</summary>
    Unspecified = 0,

    /// <summary>The command was accepted durably and is awaiting an invariant-preserving application point.</summary>
    Accepted = 1,

    /// <summary>An exact command or idempotent-intent replay returned its retained receipt without mutation.</summary>
    Replayed = 2,

    /// <summary>The command's authorization scope did not match the controlled loop.</summary>
    Unauthorized = 3,

    /// <summary>The command addressed a different or no-longer-current loop, definition, target, epoch, or revision.</summary>
    Stale = 4,

    /// <summary>The stable command identity was reused with different canonical content.</summary>
    IdentityConflict = 5,

    /// <summary>The idempotency key was reused for a different semantic intent.</summary>
    IdempotencyConflict = 6,

    /// <summary>The complete requested operating point violated definition shape, hard limits, or budgets.</summary>
    OutOfBounds = 7,

    /// <summary>Another accepted update is still awaiting its application point.</summary>
    PendingConflict = 8,

    /// <summary>The command or durable state violated a non-fence invariant.</summary>
    Invalid = 9
}

/// <summary>Disclosure level carried by a transport-neutral manual limit-update result.</summary>
public enum ControlLimitUpdateResultDisclosure
{
    /// <summary>Operating-point values are withheld; this is the safe default projection.</summary>
    Redacted = 0,

    /// <summary>The caller was authorized to inspect requested and effective operating-point values.</summary>
    Authorized = 1
}

/// <summary>
/// Canonical bounded command requesting a complete effective operating point without changing hard constraints.
/// </summary>
/// <remarks>
/// The command is an operational input, not a mutation of <see cref="ControlLoopDefinition.HardLimits"/> or
/// <see cref="ControlLoopDefinition.Budgets"/>. Acceptance records a pending request. Only a later exact
/// <see cref="ControlApplicationPoint"/> may make <see cref="RequestedOperatingPoint"/> effective.
/// </remarks>
public sealed record ControlLimitUpdateCommand
{
    /// <summary>Creates a canonical manual limit-update command.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="commandId">Stable identity retained across transport retry and replay.</param>
    /// <param name="idempotencyKey">Stable key reused for the same semantic update intent.</param>
    /// <param name="loopId">Exact controlled loop.</param>
    /// <param name="definitionFingerprint">Exact canonical definition content under which the request was formed.</param>
    /// <param name="target">Exact controlled Process, materialization, or runtime subject.</param>
    /// <param name="epoch">Exact attempt, generation, or other controlled epoch.</param>
    /// <param name="expectedRevision">Exact durable revision and optimistic fence observed by the issuer.</param>
    /// <param name="requestedOperatingPoint">Complete desired operating point within current immutable bounds.</param>
    /// <param name="authorization">Attributable authorization evidence for this command.</param>
    /// <param name="issuedAtUtc">Explicit UTC command-issuance observation.</param>
    /// <param name="provenance">Producer and source attribution for the command.</param>
    /// <exception cref="ArgumentException">
    /// A schema or identity is default, or <paramref name="issuedAtUtc"/> is not UTC.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definitionFingerprint"/>, <paramref name="target"/>,
    /// <paramref name="requestedOperatingPoint"/>, <paramref name="authorization"/>, or
    /// <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ControlLimitUpdateCommand(
        ExecutionIrSchemaVersion schemaVersion,
        EmissionId commandId,
        InteractionIdempotencyKey idempotencyKey,
        ControlLoopId loopId,
        ExecutionDefinitionFingerprint definitionFingerprint,
        string target,
        ControlEpochId epoch,
        ControlRevision expectedRevision,
        ControlOperatingPoint requestedOperatingPoint,
        ProcessControlAuthorizationContext authorization,
        DateTimeOffset issuedAtUtc,
        ExecutionProvenance provenance)
    {
        if (schemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(commandId.Value)
            || string.IsNullOrWhiteSpace(idempotencyKey.Value)
            || string.IsNullOrWhiteSpace(loopId.Value)
            || string.IsNullOrWhiteSpace(epoch.Value))
        {
            throw new ArgumentException(
                "A limit-update command requires non-default schema, command, idempotency, loop, and epoch identities.",
                nameof(commandId));
        }

        ControlRevision.RequireDefined(expectedRevision, nameof(expectedRevision));
        ControlObservation.RequireUtc(issuedAtUtc, nameof(issuedAtUtc));
        SchemaVersion = schemaVersion;
        CommandId = commandId;
        IdempotencyKey = idempotencyKey;
        LoopId = loopId;
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        Target = Guard.RequireNotNullOrWhiteSpace(target);
        Epoch = epoch;
        ExpectedRevision = expectedRevision;
        RequestedOperatingPoint = Guard.RequireNotNull(requestedOperatingPoint);
        Authorization = Guard.RequireNotNull(authorization);
        IssuedAtUtc = issuedAtUtc;
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable command identity retained across retry and replay.</summary>
    public EmissionId CommandId { get; }

    /// <summary>Logical command-deduplication key.</summary>
    public InteractionIdempotencyKey IdempotencyKey { get; }

    /// <summary>Exact controlled loop.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Fingerprint of the exact canonical definition content under which the request was formed.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Exact controlled Process, materialization, or runtime subject.</summary>
    public string Target { get; }

    /// <summary>Exact attempt, generation, or other controlled epoch.</summary>
    public ControlEpochId Epoch { get; }

    /// <summary>Exact durable revision and optimistic fence observed by the issuer.</summary>
    public ControlRevision ExpectedRevision { get; }

    /// <summary>Complete desired operating point within current immutable bounds.</summary>
    public ControlOperatingPoint RequestedOperatingPoint { get; }

    /// <summary>Attributable authorization evidence for this command.</summary>
    public ProcessControlAuthorizationContext Authorization { get; }

    /// <summary>Explicit UTC time at which the canonical command was issued.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>Producer and source attribution for the command.</summary>
    public ExecutionProvenance Provenance { get; }
}

/// <summary>Durable evidence that one bounded manual limit-update command was accepted.</summary>
public sealed record ControlLimitUpdateReceipt
{
    /// <summary>Creates an accepted command receipt.</summary>
    /// <param name="command">Exact accepted canonical command.</param>
    /// <param name="acceptedRevision">Durable state revision after recording the pending request.</param>
    /// <param name="acceptedAtUtc">Explicit UTC acceptance time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="acceptedRevision"/> is not exactly one revision after the command fence,
    /// or <paramref name="acceptedAtUtc"/> is not UTC or precedes command issuance.
    /// </exception>
    [JsonConstructor]
    public ControlLimitUpdateReceipt(
        ControlLimitUpdateCommand command,
        ControlRevision acceptedRevision,
        DateTimeOffset acceptedAtUtc)
    {
        Command = Guard.RequireNotNull(command);
        ControlRevision.RequireDefined(acceptedRevision, nameof(acceptedRevision));
        ControlObservation.RequireUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        if (command.ExpectedRevision.Ordinal == long.MaxValue
            || acceptedRevision.Ordinal != command.ExpectedRevision.Ordinal + 1)
        {
            throw new ArgumentException(
                "A limit-update acceptance revision must immediately follow the command's expected revision.",
                nameof(acceptedRevision));
        }
        if (acceptedAtUtc < command.IssuedAtUtc)
            throw new ArgumentException("A limit-update receipt cannot precede command issuance.", nameof(acceptedAtUtc));

        AcceptedRevision = acceptedRevision;
        AcceptedAtUtc = acceptedAtUtc;
    }

    /// <summary>Exact accepted canonical command.</summary>
    public ControlLimitUpdateCommand Command { get; }

    /// <summary>Durable state revision after recording the pending request.</summary>
    public ControlRevision AcceptedRevision { get; }

    /// <summary>Explicit UTC command-acceptance time.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }
}

/// <summary>Durable evidence that a bounded manual limit update became effective at one exact safe point.</summary>
public sealed record ControlLimitUpdateActuation
{
    /// <summary>Creates a manual limit-update actuation receipt.</summary>
    /// <param name="id">Stable actuation identity derived from the command and safe-point evidence.</param>
    /// <param name="receipt">Exact accepted command receipt.</param>
    /// <param name="applicationPoint">Exact invariant-preserving runtime cut authorizing application.</param>
    /// <param name="priorOperatingPoint">Complete effective point before application.</param>
    /// <param name="revision">Durable state revision after application.</param>
    /// <param name="appliedAtUtc">Explicit UTC application time.</param>
    /// <exception cref="ArgumentException">
    /// An identity, fence, revision, point, or chronology invariant conflicts.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="receipt"/>, <paramref name="applicationPoint"/>, or
    /// <paramref name="priorOperatingPoint"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ControlLimitUpdateActuation(
        ControlActuationId id,
        ControlLimitUpdateReceipt receipt,
        ControlApplicationPoint applicationPoint,
        ControlOperatingPoint priorOperatingPoint,
        ControlRevision revision,
        DateTimeOffset appliedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A limit-update actuation requires a stable identity.", nameof(id));
        Receipt = Guard.RequireNotNull(receipt);
        ApplicationPoint = Guard.RequireNotNull(applicationPoint);
        PriorOperatingPoint = Guard.RequireNotNull(priorOperatingPoint);
        ControlRevision.RequireDefined(revision, nameof(revision));
        ControlObservation.RequireUtc(appliedAtUtc, nameof(appliedAtUtc));

        var command = receipt.Command;
        if (applicationPoint.SchemaVersion != command.SchemaVersion
            || applicationPoint.LoopId != command.LoopId
            || applicationPoint.DefinitionFingerprint != command.DefinitionFingerprint
            || !string.Equals(applicationPoint.Target, command.Target, StringComparison.Ordinal)
            || applicationPoint.Epoch != command.Epoch
            || applicationPoint.ExpectedRevision != receipt.AcceptedRevision
            || receipt.AcceptedRevision.Ordinal == long.MaxValue
            || revision.Ordinal != receipt.AcceptedRevision.Ordinal + 1)
        {
            throw new ArgumentException(
                "A limit-update command, acceptance, application point, and revision must share one exact fence.",
                nameof(applicationPoint));
        }
        if (priorOperatingPoint == command.RequestedOperatingPoint)
            throw new ArgumentException("A limit update must change the effective operating point.", nameof(priorOperatingPoint));
        if (applicationPoint.ObservedAtUtc <= receipt.AcceptedAtUtc || appliedAtUtc < applicationPoint.ObservedAtUtc)
            throw new ArgumentException("A limit-update actuation must follow acceptance and its later safe point.", nameof(appliedAtUtc));

        Id = id;
        Revision = revision;
        AppliedAtUtc = appliedAtUtc;
    }

    /// <summary>Stable actuation identity.</summary>
    public ControlActuationId Id { get; }

    /// <summary>Exact accepted command receipt.</summary>
    public ControlLimitUpdateReceipt Receipt { get; }

    /// <summary>Exact invariant-preserving runtime cut authorizing application.</summary>
    public ControlApplicationPoint ApplicationPoint { get; }

    /// <summary>Complete effective operating point before application.</summary>
    public ControlOperatingPoint PriorOperatingPoint { get; }

    /// <summary>Complete effective operating point after application.</summary>
    [JsonIgnore]
    public ControlOperatingPoint OperatingPoint => Receipt.Command.RequestedOperatingPoint;

    /// <summary>Durable state revision before application.</summary>
    [JsonIgnore]
    public ControlRevision PriorRevision => Receipt.AcceptedRevision;

    /// <summary>Durable state revision after application.</summary>
    public ControlRevision Revision { get; }

    /// <summary>Explicit UTC application time.</summary>
    public DateTimeOffset AppliedAtUtc { get; }
}

/// <summary>Complete result of reducing one bounded manual limit-update command.</summary>
public sealed record ControlLimitUpdateDecision
{
    /// <summary>Creates a manual limit-update command decision.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="disposition">Command-reduction outcome.</param>
    /// <param name="state">Complete durable state after the decision.</param>
    /// <param name="receipt">Accepted or replayed receipt, when applicable.</param>
    /// <param name="diagnostics">Structured diagnostics in any producer order.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Schema, disposition, receipt, state, or diagnostics conflict.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ControlLimitUpdateDecision(
        ExecutionIrSchemaVersion schemaVersion,
        ControlLimitUpdateDecisionDisposition disposition,
        ControlLimitUpdateState state,
        ControlLimitUpdateReceipt? receipt = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (schemaVersion != ControlLoopDefinition.CurrentSchemaVersion)
            throw new ArgumentException("A limit-update decision requires the current Control schema version.", nameof(schemaVersion));
        if (!Enum.IsDefined(disposition) || disposition == ControlLimitUpdateDecisionDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported limit-update disposition.");
        State = Guard.RequireNotNull(state);
        if (disposition == ControlLimitUpdateDecisionDisposition.Accepted
            && (receipt is null || state.PendingUpdate != receipt))
        {
            throw new ArgumentException("An accepted decision must expose the state's exact pending receipt.", nameof(receipt));
        }
        if (disposition == ControlLimitUpdateDecisionDisposition.Replayed
            && (receipt is null || !state.Receipts.Contains(receipt)))
        {
            throw new ArgumentException("A replayed decision requires its retained receipt.", nameof(receipt));
        }
        if (disposition is not (ControlLimitUpdateDecisionDisposition.Accepted or ControlLimitUpdateDecisionDisposition.Replayed)
            && receipt is not null)
        {
            throw new ArgumentException("A rejected decision cannot expose a command receipt.", nameof(receipt));
        }
        if (diagnostics.IsDefault)
            diagnostics = [];
        if (diagnostics.Any(static diagnostic =>
            diagnostic is null
            || string.IsNullOrWhiteSpace(diagnostic.Code)
            || string.IsNullOrWhiteSpace(diagnostic.Message)))
        {
            throw new ArgumentException("Limit-update diagnostics require non-empty code and message.", nameof(diagnostics));
        }

        SchemaVersion = schemaVersion;
        Disposition = disposition;
        Receipt = receipt;
        Diagnostics = [.. diagnostics.OrderBy(static diagnostic => diagnostic, DocumentValidationDiagnosticComparer.Ordinal)];
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Command-reduction outcome.</summary>
    public ControlLimitUpdateDecisionDisposition Disposition { get; }

    /// <summary>Complete durable state after the decision.</summary>
    public ControlLimitUpdateState State { get; }

    /// <summary>Accepted or replayed command receipt, when applicable.</summary>
    public ControlLimitUpdateReceipt? Receipt { get; }

    /// <summary>Structured diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Compares decisions structurally.</summary>
    /// <param name="other">Decision to compare.</param>
    /// <returns><see langword="true"/> when outcome, state, receipt, and diagnostics are equal.</returns>
    public bool Equals(ControlLimitUpdateDecision? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Disposition == other.Disposition
        && State == other.State
        && Receipt == other.Receipt
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from the complete decision.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Disposition);
        hash.Add(State);
        hash.Add(Receipt);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}

/// <summary>Complete result of attempting to apply one pending manual update at a safe point.</summary>
public sealed record ControlLimitUpdateActuationResult
{
    /// <summary>Creates a limit-update safe-point actuation result.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="disposition">Application outcome.</param>
    /// <param name="state">Complete durable state after the attempt.</param>
    /// <param name="actuation">Applied or replayed receipt, when applicable.</param>
    /// <param name="diagnostics">Structured diagnostics in any producer order.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Schema, disposition, receipt, state, or diagnostics conflict.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ControlLimitUpdateActuationResult(
        ExecutionIrSchemaVersion schemaVersion,
        ControlActuationDisposition disposition,
        ControlLimitUpdateState state,
        ControlLimitUpdateActuation? actuation = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (schemaVersion != ControlLoopDefinition.CurrentSchemaVersion)
            throw new ArgumentException("A limit-update actuation result requires the current Control schema version.", nameof(schemaVersion));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported control-actuation disposition.");
        State = Guard.RequireNotNull(state);
        if (disposition is ControlActuationDisposition.Applied or ControlActuationDisposition.Replayed
            && actuation is null)
        {
            throw new ArgumentException(
                "An applied or replayed result must expose a retained actuation.",
                nameof(actuation));
        }
        if (disposition == ControlActuationDisposition.Applied
            && state.LastActuation != actuation)
        {
            throw new ArgumentException(
                "An applied result must expose the state's latest actuation.",
                nameof(actuation));
        }
        if (disposition == ControlActuationDisposition.Replayed
            && !state.Actuations.Contains(actuation!))
        {
            throw new ArgumentException(
                "A replayed result must expose an actuation retained by the durable ledger.",
                nameof(actuation));
        }
        if (disposition is ControlActuationDisposition.Deferred or ControlActuationDisposition.Rejected
            && actuation is not null)
        {
            throw new ArgumentException("A deferred or rejected result cannot expose an actuation.", nameof(actuation));
        }
        if (diagnostics.IsDefault)
            diagnostics = [];
        if (diagnostics.Any(static diagnostic =>
            diagnostic is null
            || string.IsNullOrWhiteSpace(diagnostic.Code)
            || string.IsNullOrWhiteSpace(diagnostic.Message)))
        {
            throw new ArgumentException("Limit-update diagnostics require non-empty code and message.", nameof(diagnostics));
        }

        SchemaVersion = schemaVersion;
        Disposition = disposition;
        Actuation = actuation;
        Diagnostics = [.. diagnostics.OrderBy(static diagnostic => diagnostic, DocumentValidationDiagnosticComparer.Ordinal)];
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Application outcome.</summary>
    public ControlActuationDisposition Disposition { get; }

    /// <summary>Complete durable state after the attempt.</summary>
    public ControlLimitUpdateState State { get; }

    /// <summary>Applied or replayed actuation receipt, when applicable.</summary>
    public ControlLimitUpdateActuation? Actuation { get; }

    /// <summary>Structured diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Compares results structurally.</summary>
    /// <param name="other">Result to compare.</param>
    /// <returns><see langword="true"/> when disposition, state, actuation, and diagnostics are equal.</returns>
    public bool Equals(ControlLimitUpdateActuationResult? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Disposition == other.Disposition
        && State == other.State
        && Actuation == other.Actuation
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from the complete result.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Disposition);
        hash.Add(State);
        hash.Add(Actuation);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Non-sensitive transport-neutral projection of one manual limit-update command decision.
/// </summary>
/// <remarks>
/// The projection never exposes command receipts, actor or authorization evidence, provenance, diagnostic messages,
/// or diagnostic evidence. <see cref="FromDecision"/> is safe by default and withholds operating-point values.
/// </remarks>
public sealed record ControlLimitUpdateResult
{
    /// <summary>Creates a validated transport-neutral result projection.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="disposition">Exact command-decision disposition.</param>
    /// <param name="loopId">Stable controlled loop identity.</param>
    /// <param name="target">Stable controlled Process, materialization, or runtime subject.</param>
    /// <param name="epoch">Current attempt, generation, or other controlled epoch.</param>
    /// <param name="revision">Current durable Control revision and optimistic fence.</param>
    /// <param name="diagnosticCodes">Stable diagnostic codes without messages or evidence.</param>
    /// <param name="disclosure">Whether operating-point values were authorized for disclosure.</param>
    /// <param name="requestedOperatingPoint">Requested point for an accepted or replayed command, when authorized.</param>
    /// <param name="effectiveOperatingPoint">Currently effective point, when authorized.</param>
    /// <exception cref="ArgumentException">
    /// Schema, identity, disposition, diagnostics, or disclosure invariants conflict, or an authorization failure
    /// is projected through this target-revealing result type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="disposition"/> or <paramref name="disclosure"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ControlLimitUpdateResult(
        ExecutionIrSchemaVersion schemaVersion,
        ControlLimitUpdateDecisionDisposition disposition,
        ControlLoopId loopId,
        string target,
        ControlEpochId epoch,
        ControlRevision revision,
        ImmutableArray<string> diagnosticCodes,
        ControlLimitUpdateResultDisclosure disclosure = ControlLimitUpdateResultDisclosure.Redacted,
        ControlOperatingPoint? requestedOperatingPoint = null,
        ControlOperatingPoint? effectiveOperatingPoint = null)
    {
        if (schemaVersion != ControlLoopDefinition.CurrentSchemaVersion)
            throw new ArgumentException("A limit-update result requires the current Control schema version.", nameof(schemaVersion));
        if (!Enum.IsDefined(disposition) || disposition == ControlLimitUpdateDecisionDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported limit-update disposition.");
        if (disposition == ControlLimitUpdateDecisionDisposition.Unauthorized)
        {
            throw new ArgumentException(
                "Authorization failures must use an opaque API boundary problem.",
                nameof(disposition));
        }
        if (!Enum.IsDefined(disclosure))
            throw new ArgumentOutOfRangeException(nameof(disclosure), disclosure, "Unsupported result disclosure level.");
        if (string.IsNullOrWhiteSpace(loopId.Value) || string.IsNullOrWhiteSpace(epoch.Value))
            throw new ArgumentException("A limit-update result requires loop and epoch identities.", nameof(loopId));
        ControlRevision.RequireDefined(revision, nameof(revision));
        if (diagnosticCodes.IsDefault)
            diagnosticCodes = [];
        if (diagnosticCodes.Any(static code => string.IsNullOrWhiteSpace(code)))
            throw new ArgumentException("Diagnostic codes cannot be null, empty, or white-space.", nameof(diagnosticCodes));
        if (disclosure == ControlLimitUpdateResultDisclosure.Redacted
            && (requestedOperatingPoint is not null || effectiveOperatingPoint is not null))
        {
            throw new ArgumentException("A redacted result cannot contain operating-point values.", nameof(disclosure));
        }
        if (disclosure == ControlLimitUpdateResultDisclosure.Authorized && effectiveOperatingPoint is null)
            throw new ArgumentException("An authorized result requires the currently effective operating point.", nameof(effectiveOperatingPoint));

        SchemaVersion = schemaVersion;
        Disposition = disposition;
        LoopId = loopId;
        Target = Guard.RequireNotNullOrWhiteSpace(target);
        Epoch = epoch;
        Revision = revision;
        DiagnosticCodes = [.. diagnosticCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Disclosure = disclosure;
        RequestedOperatingPoint = requestedOperatingPoint;
        EffectiveOperatingPoint = effectiveOperatingPoint;
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Exact command-decision disposition.</summary>
    public ControlLimitUpdateDecisionDisposition Disposition { get; }

    /// <summary>Stable controlled loop identity.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Stable controlled Process, materialization, or runtime subject.</summary>
    public string Target { get; }

    /// <summary>Current attempt, generation, or other controlled epoch.</summary>
    public ControlEpochId Epoch { get; }

    /// <summary>Current durable Control revision and optimistic fence.</summary>
    public ControlRevision Revision { get; }

    /// <summary>Stable diagnostic codes without messages or evidence, in deterministic ordinal order.</summary>
    public ImmutableArray<string> DiagnosticCodes { get; }

    /// <summary>Whether operating-point values were authorized for disclosure.</summary>
    public ControlLimitUpdateResultDisclosure Disclosure { get; }

    /// <summary>Requested point for an accepted or replayed command, when authorized and available.</summary>
    public ControlOperatingPoint? RequestedOperatingPoint { get; }

    /// <summary>Currently effective point, when authorized.</summary>
    public ControlOperatingPoint? EffectiveOperatingPoint { get; }

    /// <summary>Projects a decision using the safe default that withholds operating-point values.</summary>
    /// <param name="decision">Canonical command decision to project.</param>
    /// <returns>A redacted result containing stable identity, revision, disposition, and diagnostic codes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="decision"/> is an authorization failure that must use an opaque API boundary problem.
    /// </exception>
    public static ControlLimitUpdateResult FromDecision(ControlLimitUpdateDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        RequireDisclosable(decision);
        return Create(decision, ControlLimitUpdateResultDisclosure.Redacted);
    }

    /// <summary>Projects a decision after the caller has been authorized to inspect operating-point values.</summary>
    /// <param name="decision">Canonical command decision to project.</param>
    /// <returns>A result containing the effective point and the accepted or replayed requested point, when any.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="decision"/> is an authorization failure that must use an opaque API boundary problem.
    /// </exception>
    public static ControlLimitUpdateResult FromAuthorizedDecision(ControlLimitUpdateDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        RequireDisclosable(decision);
        return Create(decision, ControlLimitUpdateResultDisclosure.Authorized);
    }

    /// <summary>Compares projections structurally.</summary>
    /// <param name="other">Projection to compare.</param>
    /// <returns><see langword="true"/> when all disclosed transport-neutral content is equal.</returns>
    public bool Equals(ControlLimitUpdateResult? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Disposition == other.Disposition
        && LoopId == other.LoopId
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && Epoch == other.Epoch
        && Revision == other.Revision
        && DiagnosticCodes.SequenceEqual(other.DiagnosticCodes, StringComparer.Ordinal)
        && Disclosure == other.Disclosure
        && RequestedOperatingPoint == other.RequestedOperatingPoint
        && EffectiveOperatingPoint == other.EffectiveOperatingPoint;

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived only from disclosed transport-neutral content.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Disposition);
        hash.Add(LoopId);
        hash.Add(Target, StringComparer.Ordinal);
        hash.Add(Epoch);
        hash.Add(Revision);
        foreach (var code in DiagnosticCodes)
            hash.Add(code, StringComparer.Ordinal);
        hash.Add(Disclosure);
        hash.Add(RequestedOperatingPoint);
        hash.Add(EffectiveOperatingPoint);
        return hash.ToHashCode();
    }

    static ControlLimitUpdateResult Create(
        ControlLimitUpdateDecision decision,
        ControlLimitUpdateResultDisclosure disclosure)
    {
        var state = decision.State;
        return new(
            decision.SchemaVersion,
            decision.Disposition,
            state.LoopId,
            state.Target,
            state.Epoch,
            state.Revision,
            [.. decision.Diagnostics.Select(static diagnostic => diagnostic.Code)],
            disclosure,
            disclosure == ControlLimitUpdateResultDisclosure.Authorized
                ? decision.Receipt?.Command.RequestedOperatingPoint
                : null,
            disclosure == ControlLimitUpdateResultDisclosure.Authorized ? state.OperatingPoint : null);
    }

    static void RequireDisclosable(ControlLimitUpdateDecision decision)
    {
        if (decision.Disposition == ControlLimitUpdateDecisionDisposition.Unauthorized)
        {
            throw new InvalidOperationException(
                "Authorization failures cannot be projected with Control target or state identity.");
        }
    }
}
