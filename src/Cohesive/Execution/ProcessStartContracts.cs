using System.Text.Json.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable wire names for Process-start admission.</summary>
public static class ProcessStartWireNames
{
    /// <summary>Canonical semantic authority that owns Process-start admission.</summary>
    public const string SemanticAuthority = "cohesive.execution.process-start";

    /// <summary>Canonical protocol-neutral Process-start action name.</summary>
    public const string Start = "start";

    /// <summary>Canonical semantic path of a Process-start request.</summary>
    public static ExecutionSemanticPath RequestPath { get; } = new(["requests", Start]);
}

/// <summary>Stable diagnostic codes for canonical Process-start conflicts.</summary>
public static class ProcessStartDiagnosticCodes
{
    /// <summary>A stable start-command identity was reused for different canonical content.</summary>
    public const string CommandIdentityConflict = "execution.start.command.identityConflict";

    /// <summary>A start idempotency key was reused for a different semantic intent.</summary>
    public const string CommandIdempotencyConflict = "execution.start.command.idempotencyConflict";

    /// <summary>The requested logical Process instance was already started by another request.</summary>
    public const string InstanceConflict = "execution.start.instance.conflict";
}

/// <summary>Canonical request to create the initial attempt of one logical Process instance.</summary>
/// <remarks>
/// Start is an admission operation and deliberately does not extend <see cref="ProcessControlCommand"/>, whose
/// variants operate only after an instance exists. A transport adapter MUST replace client-supplied authority,
/// issuance time, and provenance with a trusted, server-materialized <see cref="ProcessControlCommandContext"/>
/// before constructing this request. The context type is reused because command identity, idempotency identity,
/// Process-instance identity, authority evidence, issuance time, and provenance have the same semantics at both
/// boundaries.
/// </remarks>
public sealed record ProcessStartRequest
{
    /// <summary>Current canonical Process-start request schema version.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-process-start-request/v1");

    /// <summary>Creates a canonical Process-start request.</summary>
    /// <param name="schemaVersion">Exact Process-start request schema version.</param>
    /// <param name="definition">Exact Process definition revision and fingerprint to pin.</param>
    /// <param name="context">
    /// Trusted server-materialized command identity, idempotency identity, authority, issuance, and provenance.
    /// </param>
    /// <param name="initialContinuation">Stable logical Process instance and its initial attempt.</param>
    /// <param name="input">
    /// Optional typed initial Process input, or <see langword="null"/> when the Process has no input boundary.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="context"/>, or <paramref name="initialContinuation"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema version is unsupported; context and continuation address different instances; or
    /// <paramref name="input"/> is unknown or failed rather than materialized start input.
    /// </exception>
    [JsonConstructor]
    public ProcessStartRequest(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionReference definition,
        ProcessControlCommandContext context,
        ProcessContinuationIdentity initialContinuation,
        PortableValue? input = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported Process-start request schema version.", nameof(schemaVersion));
        }

        Definition = Guard.RequireNotNull(definition);
        Context = Guard.RequireNotNull(context);
        InitialContinuation = Guard.RequireNotNull(initialContinuation);
        if (context.ProcessInstanceId != initialContinuation.ProcessInstanceId)
        {
            throw new ArgumentException(
                "Process-start context and continuation must address the same logical instance.",
                nameof(initialContinuation));
        }

        if (input is { State: PortableValueState.Unknown or PortableValueState.Failed })
        {
            throw new ArgumentException(
                "Process-start input must be materialized rather than unknown or failed.",
                nameof(input));
        }

        SchemaVersion = schemaVersion;
        Input = input;
    }

    /// <summary>Exact Process-start request schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Exact Process definition revision and fingerprint to pin for the instance.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Trusted command identity, authority, issuance, and provenance.</summary>
    public ProcessControlCommandContext Context { get; }

    /// <summary>Stable logical Process instance and its initial attempt.</summary>
    public ProcessContinuationIdentity InitialContinuation { get; }

    /// <summary>Optional typed initial Process input.</summary>
    public PortableValue? Input { get; }

    /// <summary>Determines whether another request expresses the same logical idempotent start intent.</summary>
    /// <param name="candidate">Candidate retry carrying the same logical idempotency identity.</param>
    /// <returns>
    /// <see langword="true"/> when all semantic start content is equal after excluding occurrence identity and
    /// issuance time; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This comparison deliberately ignores <see cref="ProcessControlCommandContext.CommandId"/> and
    /// <see cref="ProcessControlCommandContext.IssuedAtUtc"/> while retaining server authorization and provenance.
    /// </remarks>
    public bool HasSameIdempotentIntent(ProcessStartRequest candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return SchemaVersion == candidate.SchemaVersion
            && Definition == candidate.Definition
            && Context.IdempotencyKey == candidate.Context.IdempotencyKey
            && Context.ProcessInstanceId == candidate.Context.ProcessInstanceId
            && Context.Authorization == candidate.Context.Authorization
            && Context.Provenance == candidate.Context.Provenance
            && InitialContinuation == candidate.InitialContinuation
            && Input == candidate.Input;
    }
}

/// <summary>Durable acceptance evidence for the one start of a logical Process instance.</summary>
public sealed record ProcessStartReceipt
{
    /// <summary>Creates durable Process-start acceptance evidence.</summary>
    /// <param name="request">Exact canonical request that won admission.</param>
    /// <param name="acceptedAtUtc">Explicit UTC time at which the instance was admitted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="acceptedAtUtc"/> is not UTC or precedes trusted request issuance.
    /// </exception>
    [JsonConstructor]
    public ProcessStartReceipt(ProcessStartRequest request, DateTimeOffset acceptedAtUtc)
    {
        Request = Guard.RequireNotNull(request);
        ExecutionObservationRequirements.RequireUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        if (acceptedAtUtc < request.Context.IssuedAtUtc)
        {
            throw new ArgumentException(
                "Process-start acceptance cannot precede trusted request issuance.",
                nameof(acceptedAtUtc));
        }

        AcceptedAtUtc = acceptedAtUtc;
    }

    /// <summary>Exact canonical request that won admission.</summary>
    public ProcessStartRequest Request { get; }

    /// <summary>Explicit UTC Process-start acceptance time.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }

    /// <summary>Creates the canonical initial control state implied by this receipt.</summary>
    /// <returns>Initial running state at the first ready boundary and initial control revision.</returns>
    public ProcessControlState CreateInitialState() =>
        ProcessControlState.Create(
            Request.Definition,
            Request.Context.Authorization.AuthorityScope,
            Request.InitialContinuation.ProcessInstanceId,
            Request.InitialContinuation.ProcessAttemptId,
            AcceptedAtUtc);
}

/// <summary>Observable disposition of one deterministic Process-start decision.</summary>
public enum ProcessStartDisposition
{
    /// <summary>No disposition was supplied; invalid in a Process-start result.</summary>
    Unspecified = 0,

    /// <summary>The request won admission and created the initial Process attempt.</summary>
    Accepted = 1,

    /// <summary>A prior semantically equal start admission was deterministically reused.</summary>
    Replayed = 2,

    /// <summary>The stable command identity was reused for different canonical content.</summary>
    CommandIdentityConflict = 3,

    /// <summary>The idempotency identity was reused for a different semantic intent.</summary>
    IdempotencyConflict = 4,

    /// <summary>The logical Process instance was already admitted by another start request.</summary>
    InstanceConflict = 5
}

/// <summary>Non-sensitive public admission summary returned for an accepted or replayed start.</summary>
public sealed record ProcessStartAdmission
{
    /// <summary>Creates a public Process-start admission summary.</summary>
    /// <param name="definition">Exact pinned Process definition.</param>
    /// <param name="continuation">Initial logical Process instance and attempt.</param>
    /// <param name="controlRevision">Initial semantic Process-control revision.</param>
    /// <param name="acceptedAtUtc">Explicit UTC start-admission time.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="controlRevision"/> is default or <paramref name="acceptedAtUtc"/> is not UTC.
    /// </exception>
    [JsonConstructor]
    public ProcessStartAdmission(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        ProcessControlRevision controlRevision,
        DateTimeOffset acceptedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(controlRevision.Value))
        {
            throw new ArgumentException("A Process-start admission requires a control revision.", nameof(controlRevision));
        }

        ExecutionObservationRequirements.RequireUtc(acceptedAtUtc, nameof(acceptedAtUtc));
        Definition = Guard.RequireNotNull(definition);
        Continuation = Guard.RequireNotNull(continuation);
        ControlRevision = controlRevision;
        AcceptedAtUtc = acceptedAtUtc;
    }

    /// <summary>Exact pinned Process definition.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Initial logical Process instance and attempt.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Initial semantic Process-control revision.</summary>
    public ProcessControlRevision ControlRevision { get; }

    /// <summary>Explicit UTC start-admission time.</summary>
    public DateTimeOffset AcceptedAtUtc { get; }

    internal static ProcessStartAdmission FromReceipt(ProcessStartReceipt receipt) =>
        new(
            receipt.Request.Definition,
            receipt.Request.InitialContinuation,
            ProcessControlRevision.Initial,
            receipt.AcceptedAtUtc);
}

/// <summary>Safe canonical result of a Process-start admission decision.</summary>
/// <remarks>
/// The result deliberately exposes only a non-sensitive admission summary. Typed start input and authorization
/// evidence remain in the durable <see cref="ProcessStartReceipt"/> carried by the execution decision, not in the
/// transport-facing result.
/// </remarks>
public sealed record ProcessStartResult
{
    /// <summary>Creates a canonical Process-start result.</summary>
    /// <param name="disposition">Accepted, replayed, or precise conflict disposition.</param>
    /// <param name="admission">Public admission summary for accepted and replayed results.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Admission presence contradicts <paramref name="disposition"/>.
    /// </exception>
    [JsonConstructor]
    public ProcessStartResult(ProcessStartDisposition disposition, ProcessStartAdmission? admission = null)
    {
        if (!Enum.IsDefined(disposition) || disposition == ProcessStartDisposition.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Process-start result disposition must be explicit.");
        }

        var isAdmission = disposition is ProcessStartDisposition.Accepted or ProcessStartDisposition.Replayed;
        if (isAdmission != (admission is not null))
        {
            throw new ArgumentException(
                "Only accepted and replayed Process starts carry an admission summary.",
                nameof(admission));
        }

        Disposition = disposition;
        Admission = admission;
    }

    /// <summary>Accepted, replayed, or precise conflict disposition.</summary>
    public ProcessStartDisposition Disposition { get; }

    /// <summary>Non-sensitive admission summary for accepted and replayed starts.</summary>
    public ProcessStartAdmission? Admission { get; }

    /// <summary>Stable diagnostic code for a conflict, or <see langword="null"/> for admission.</summary>
    [JsonIgnore]
    public string? DiagnosticCode => Disposition switch
    {
        ProcessStartDisposition.CommandIdentityConflict => ProcessStartDiagnosticCodes.CommandIdentityConflict,
        ProcessStartDisposition.IdempotencyConflict => ProcessStartDiagnosticCodes.CommandIdempotencyConflict,
        ProcessStartDisposition.InstanceConflict => ProcessStartDiagnosticCodes.InstanceConflict,
        _ => null
    };

    /// <summary>Whether this result describes a rejected start conflict.</summary>
    [JsonIgnore]
    public bool IsConflict => DiagnosticCode is not null;

    /// <summary>Creates a result for a newly accepted receipt.</summary>
    /// <param name="receipt">Durable receipt that won Process-start admission.</param>
    /// <returns>An accepted result containing a non-sensitive summary of <paramref name="receipt"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    public static ProcessStartResult Accepted(ProcessStartReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new(ProcessStartDisposition.Accepted, ProcessStartAdmission.FromReceipt(receipt));
    }

    /// <summary>Creates a result that reuses a prior accepted receipt.</summary>
    /// <param name="receipt">Durable receipt whose admission is being replayed.</param>
    /// <returns>A replayed result containing the prior non-sensitive admission summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    public static ProcessStartResult Replayed(ProcessStartReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new(ProcessStartDisposition.Replayed, ProcessStartAdmission.FromReceipt(receipt));
    }

    /// <summary>Creates a precise rejected Process-start conflict.</summary>
    /// <param name="disposition">One of the three conflict dispositions.</param>
    /// <returns>A conflict result without admission or sensitive request content.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is not a conflict.</exception>
    public static ProcessStartResult Conflict(ProcessStartDisposition disposition)
    {
        if (disposition is not (ProcessStartDisposition.CommandIdentityConflict
            or ProcessStartDisposition.IdempotencyConflict
            or ProcessStartDisposition.InstanceConflict))
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "A Process-start conflict factory requires a conflict disposition.");
        }

        return new(disposition);
    }
}

/// <summary>Indexed durable evidence supplied to deterministic Process-start evaluation.</summary>
/// <remarks>
/// Adapters resolve these entries through atomic or fenced registry access paths. The semantic evaluator remains
/// independent of the physical index, transaction, or consistency mechanism used to obtain them.
/// </remarks>
public sealed record ProcessStartRegistryEvidence
{
    /// <summary>Creates indexed Process-start registry evidence.</summary>
    /// <param name="sameCommandIdentity">Receipt found by stable command identity.</param>
    /// <param name="sameIdempotencyKey">Receipt found by scoped idempotency identity.</param>
    /// <param name="existingInstanceReceipt">Winning receipt for the requested logical instance.</param>
    /// <param name="existingInstanceState">Current state of the requested logical instance.</param>
    /// <exception cref="ArgumentException">
    /// Instance receipt and state presence differ, or their definition, authority, instance, initial attempt, or
    /// creation time contradict each other.
    /// </exception>
    [JsonConstructor]
    public ProcessStartRegistryEvidence(
        ProcessStartReceipt? sameCommandIdentity = null,
        ProcessStartReceipt? sameIdempotencyKey = null,
        ProcessStartReceipt? existingInstanceReceipt = null,
        ProcessControlState? existingInstanceState = null)
    {
        if ((existingInstanceReceipt is null) != (existingInstanceState is null))
        {
            throw new ArgumentException(
                "Existing Process-start receipt and current state must be supplied together.",
                nameof(existingInstanceState));
        }

        if (existingInstanceReceipt is not null && existingInstanceState is not null)
        {
            ValidateInstancePair(existingInstanceReceipt, existingInstanceState);
        }

        SameCommandIdentity = sameCommandIdentity;
        SameIdempotencyKey = sameIdempotencyKey;
        ExistingInstanceReceipt = existingInstanceReceipt;
        ExistingInstanceState = existingInstanceState;
    }

    /// <summary>Empty evidence for a registry in which no matching identity exists.</summary>
    public static ProcessStartRegistryEvidence Empty { get; } = new();

    /// <summary>Receipt found by stable command identity.</summary>
    public ProcessStartReceipt? SameCommandIdentity { get; }

    /// <summary>Receipt found by scoped idempotency identity.</summary>
    public ProcessStartReceipt? SameIdempotencyKey { get; }

    /// <summary>Winning receipt for the requested logical Process instance.</summary>
    public ProcessStartReceipt? ExistingInstanceReceipt { get; }

    /// <summary>Current state of the requested logical Process instance.</summary>
    public ProcessControlState? ExistingInstanceState { get; }

    static void ValidateInstancePair(ProcessStartReceipt receipt, ProcessControlState state)
    {
        var request = receipt.Request;
        if (state.Definition != request.Definition
            || state.AuthorityScope != request.Context.Authorization.AuthorityScope
            || state.ProcessInstanceId != request.InitialContinuation.ProcessInstanceId
            || state.Attempts[0].AttemptId != request.InitialContinuation.ProcessAttemptId
            || state.CreatedAtUtc != receipt.AcceptedAtUtc)
        {
            throw new ArgumentException(
                "Existing Process state contradicts its durable start receipt.",
                nameof(state));
        }
    }
}

/// <summary>Canonical internal decision produced by deterministic Process-start evaluation.</summary>
public sealed record ProcessStartDecision
{
    /// <summary>Creates a Process-start execution decision.</summary>
    /// <param name="result">Safe transport-facing start result.</param>
    /// <param name="receipt">Durable winning receipt for accepted and replayed decisions.</param>
    /// <param name="state">Initial or current Process state for accepted and replayed decisions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Receipt or state presence contradicts the result disposition, or the receipt and state do not describe the
    /// same admission.
    /// </exception>
    [JsonConstructor]
    public ProcessStartDecision(
        ProcessStartResult result,
        ProcessStartReceipt? receipt = null,
        ProcessControlState? state = null)
    {
        Result = Guard.RequireNotNull(result);
        var admitted = result.Disposition is ProcessStartDisposition.Accepted or ProcessStartDisposition.Replayed;
        if (admitted != (receipt is not null && state is not null))
        {
            throw new ArgumentException(
                "Only accepted and replayed Process-start decisions carry receipt and state.",
                nameof(receipt));
        }

        if (receipt is not null && state is not null)
        {
            _ = new ProcessStartRegistryEvidence(existingInstanceReceipt: receipt, existingInstanceState: state);
            if (result.Admission != ProcessStartAdmission.FromReceipt(receipt))
            {
                throw new ArgumentException(
                    "Process-start result and durable receipt describe different admissions.",
                    nameof(result));
            }
        }

        Receipt = receipt;
        State = state;
    }

    /// <summary>Safe transport-facing Process-start result.</summary>
    public ProcessStartResult Result { get; }

    /// <summary>Durable winning receipt for accepted and replayed decisions.</summary>
    public ProcessStartReceipt? Receipt { get; }

    /// <summary>Initial or current Process state for accepted and replayed decisions.</summary>
    public ProcessControlState? State { get; }

    /// <summary>Whether the adapter must atomically persist <see cref="Receipt"/> and <see cref="State"/>.</summary>
    [JsonIgnore]
    public bool RequiresPersistence => Result.Disposition == ProcessStartDisposition.Accepted;
}

/// <summary>Reference semantic evaluator for Process-start admission, replay, and conflict decisions.</summary>
public sealed class ProcessStartReferenceEvaluator
{
    /// <summary>Creates a stateless deterministic Process-start reference evaluator.</summary>
    public ProcessStartReferenceEvaluator()
    {
    }

    /// <summary>Evaluates a Process-start request against already indexed durable registry evidence.</summary>
    /// <param name="request">Canonical request with trusted server-materialized context.</param>
    /// <param name="evidence">Receipts and state resolved by the adapter's indexed access paths.</param>
    /// <param name="observedAtUtc">Explicit UTC time of first admission observation.</param>
    /// <returns>
    /// An accepted decision with new receipt and initial state, a replay with the prior admission, or a precise
    /// conflict. Only an accepted result requires persistence.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="evidence"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="observedAtUtc"/> is not UTC or precedes trusted request issuance; replay evidence does not
    /// include the winning instance receipt and state; or indexed evidence does not actually match its lookup key.
    /// </exception>
    public ProcessStartDecision Evaluate(
        ProcessStartRequest request,
        ProcessStartRegistryEvidence evidence,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        ExecutionObservationRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtc < request.Context.IssuedAtUtc)
        {
            throw new ArgumentException(
                "Process-start observation cannot precede trusted request issuance.",
                nameof(observedAtUtc));
        }

        ValidateLookupEvidence(request, evidence);
        if (evidence.SameCommandIdentity is { } sameCommand)
        {
            return sameCommand.Request == request
                ? Replay(evidence, sameCommand)
                : Conflict(ProcessStartDisposition.CommandIdentityConflict);
        }

        if (evidence.SameIdempotencyKey is { } sameIdempotency)
        {
            return sameIdempotency.Request.HasSameIdempotentIntent(request)
                ? Replay(evidence, sameIdempotency)
                : Conflict(ProcessStartDisposition.IdempotencyConflict);
        }

        if (evidence.ExistingInstanceReceipt is not null)
        {
            return Conflict(ProcessStartDisposition.InstanceConflict);
        }

        var receipt = new ProcessStartReceipt(request, observedAtUtc);
        var state = receipt.CreateInitialState();
        return new(ProcessStartResult.Accepted(receipt), receipt, state);
    }

    static ProcessStartDecision Replay(
        ProcessStartRegistryEvidence evidence,
        ProcessStartReceipt matchingReceipt)
    {
        if (evidence.ExistingInstanceReceipt != matchingReceipt || evidence.ExistingInstanceState is null)
        {
            throw new ArgumentException(
                "Replay lookup evidence must include the winning receipt and current instance state.",
                nameof(evidence));
        }

        return new(
            ProcessStartResult.Replayed(matchingReceipt),
            matchingReceipt,
            evidence.ExistingInstanceState);
    }

    static ProcessStartDecision Conflict(ProcessStartDisposition disposition) =>
        new(ProcessStartResult.Conflict(disposition));

    static void ValidateLookupEvidence(ProcessStartRequest request, ProcessStartRegistryEvidence evidence)
    {
        var authorityScope = request.Context.Authorization.AuthorityScope;
        RequireAuthorityScope(evidence.SameCommandIdentity, authorityScope, evidence);
        RequireAuthorityScope(evidence.SameIdempotencyKey, authorityScope, evidence);
        RequireAuthorityScope(evidence.ExistingInstanceReceipt, authorityScope, evidence);

        if (evidence.SameCommandIdentity is { } sameCommand
            && sameCommand.Request.Context.CommandId != request.Context.CommandId)
        {
            throw new ArgumentException(
                "Command-identity registry evidence does not match the requested command identity.",
                nameof(evidence));
        }

        if (evidence.SameIdempotencyKey is { } sameIdempotency
            && sameIdempotency.Request.Context.IdempotencyKey != request.Context.IdempotencyKey)
        {
            throw new ArgumentException(
                "Idempotency registry evidence does not match the requested idempotency identity.",
                nameof(evidence));
        }

        if (evidence.ExistingInstanceReceipt is { } instance
            && instance.Request.InitialContinuation.ProcessInstanceId
                != request.InitialContinuation.ProcessInstanceId)
        {
            throw new ArgumentException(
                "Instance registry evidence does not match the requested Process instance.",
                nameof(evidence));
        }
    }

    static void RequireAuthorityScope(
        ProcessStartReceipt? receipt,
        InteractionAuthorityScope authorityScope,
        ProcessStartRegistryEvidence evidence)
    {
        if (receipt is not null
            && receipt.Request.Context.Authorization.AuthorityScope != authorityScope)
        {
            throw new ArgumentException(
                "Process-start registry evidence crosses the requested authority or tenant scope.",
                nameof(evidence));
        }
    }
}
