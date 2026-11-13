using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Integrations;

/// <summary>Compact observed group fence, independent of each member's desired state and ledger.</summary>
/// <param name="Id">Stable host-defined control-scope identity, such as a tenant/source/partition.</param>
/// <param name="Revision">Monotonic revision for group state and membership changes.</param>
/// <param name="Desired">Group admission policy; initially Paused.</param>
/// <param name="MemberCount">Number of registered consumers; membership is append-only.</param>
/// <param name="LastChange">Last desired-state command, or null before any group state change.</param>
public sealed record IntegrationAdmissionGroupSnapshot(string Id, long Revision, IntegrationDesiredState Desired,
    int MemberCount, IntegrationLifecycleChange? LastChange);

/// <summary>Authorized intent to create a new locally paused member of a group.</summary>
/// <param name="Consumer">Stable consumer identity; also the registration replay key within this group.</param>
/// <param name="ExpectedRevision">Exact group revision observed before registration.</param>
/// <param name="Authorization">Authenticated actor and policy decision evidence.</param>
/// <param name="AtUtc">Original UTC command observation time.</param>
public sealed record IntegrationGroupRegistration(IngestionLedgerAddress Consumer, long ExpectedRevision,
    ProcessControlAuthorizationContext Authorization, DateTimeOffset AtUtc);

/// <summary>Original membership receipt. The controller is a local handle, not a serializable capability.</summary>
public sealed class IntegrationGroupMember
{
    internal IntegrationGroupMember(IntegrationGroupRegistration command, long revision, InMemoryIntegrationLifecycle controller)
    { Command = command; Revision = revision; Controller = controller; }
    /// <summary>Original registration intent and attribution.</summary>
    public IntegrationGroupRegistration Command { get; }
    /// <summary>Group revision after registration.</summary>
    public long Revision { get; }
    /// <summary>Unique group-owned controller for this consumer; it cannot be detached from the group.</summary>
    public InMemoryIntegrationLifecycle Controller { get; }
}

/// <summary>Local shared admission authority for independently progressing consumers.</summary>
/// <remarks>Group changes, membership and member admissions use one lock. Group pause does not mutate local intent,
/// cancel admitted work or stop reconciliation. Effective admission requires both local and group Active state.
/// This is process-local, not durable/distributed. The owning lifecycle scope prevents a second standalone
/// or grouped controller for a registered consumer. Independent host scopes remain separate authorities.
/// Membership is append-only: no detach/removal, nested groups or transfer of existing controllers is supported.
/// Authorization runs outside the lock; authentication and policy freshness remain host-owned.</remarks>
public sealed class InMemoryIntegrationAdmissionGroup
{
    internal object Sync { get; } = new();
    readonly string id;
    readonly InMemoryIntegrationLifecycleScope scope;
    IntegrationAdmissionGroupSnapshot snapshot;
    readonly IntegrationDesiredStateControl control = new();
    readonly Dictionary<IngestionLedgerAddress, IntegrationGroupMember> members = new();
    readonly Func<IngestionLedgerAddress?, IntegrationLifecycleAction, ProcessControlAuthorizationContext, bool> authorize;
    readonly Func<IngestionLedgerAddress, IntegrationFailureResolutionRequest, ProcessActivationDecision?>? verifyFailureResolution;
    internal string Id => id;
    internal long Revision => control.Revision;
    internal IntegrationDesiredState Desired => control.Desired;

    /// <summary>Creates a paused group with explicit group and consumer authorization policies.</summary>
    /// <param name="scope">Host ownership authority.</param>
    /// <param name="id">Stable nonempty bounded control-scope identity.</param>
    /// <param name="authorize">One policy for group commands (null consumer) and member actions; registration requires both decisions.</param>
    /// <param name="verifyFailureResolution">Optional trusted per-consumer verifier with the lifecycle verifier's complete-effect-accounting obligations.</param>
    /// <exception cref="ArgumentException">Group identity is invalid.</exception>
    /// <exception cref="ArgumentNullException">A required policy is null.</exception>
    internal InMemoryIntegrationAdmissionGroup(InMemoryIntegrationLifecycleScope scope, string id,
        Func<IngestionLedgerAddress?, IntegrationLifecycleAction, ProcessControlAuthorizationContext, bool> authorize,
        Func<IngestionLedgerAddress, IntegrationFailureResolutionRequest, ProcessActivationDecision?>? verifyFailureResolution = null)
    {
        IngestionLedgerAddress.Require(id);
        ArgumentNullException.ThrowIfNull(authorize);
        this.scope = scope;
        this.id = id; this.authorize = authorize;
        this.verifyFailureResolution = verifyFailureResolution;
        snapshot = new(id, 0, IntegrationDesiredState.Paused, 0, null);
    }

    /// <summary>Observes group policy without scanning member admissions or constructing member snapshots.</summary>
    /// <returns>The latest cached immutable group state; no lock or allocation per read, and not permission to dispatch.</returns>
    public IntegrationAdmissionGroupSnapshot Inspect() => Volatile.Read(ref snapshot);
    internal IntegrationAdmissionGroupSnapshot Snapshot() => Inspect();
    void PublishSnapshot() => Volatile.Write(ref snapshot, new(id, Revision, Desired, members.Count, control.LastChange));

    /// <summary>Changes group policy with authorization, exact command replay and a shared revision fence.</summary>
    /// <param name="request">Desired-state command using the group revision.</param>
    /// <returns>Typed disposition and original receipt on acceptance or exact replay.</returns>
    /// <exception cref="ArgumentNullException">Request or authorization is null.</exception>
    /// <exception cref="ArgumentException">Command identity, state, revision or UTC time is invalid.</exception>
    public IntegrationLifecycleResult<IntegrationChangeOutcome, IntegrationLifecycleChangeReceipt> Change(IntegrationLifecycleChange request)
    {
        IntegrationDesiredStateControl.Validate(request);
        if (!authorize(null, IntegrationLifecycleAction.Change, request.Authorization)) return new(IntegrationChangeOutcome.Unauthorized);
        lock (Sync)
        {
            var result = control.Apply(request);
            if (result.Outcome == IntegrationChangeOutcome.Applied) PublishSnapshot();
            return result;
        }
    }

    /// <summary>Registers one unique locally paused consumer; new membership increments the group fence.</summary>
    /// <param name="request">Original attributable registration intent; reuse unchanged for exact retry.</param>
    /// <returns>Member controller and original receipt on acceptance/replay, or a typed change rejection.</returns>
    /// <exception cref="ArgumentNullException">A required request field is null.</exception>
    /// <exception cref="ArgumentException">Consumer identity, revision or UTC time is invalid.</exception>
    /// <remarks>Registration is allowed while the group is paused. Such a member remains fenced by the pause,
    /// even after local activation or binding a new provider. An existing consumer with changed registration
    /// intent is rejected rather than replacing its controller. A consumer already owned elsewhere in this host scope
    /// returns AlreadyOwned without advancing revision. Replay rechecks both authorization policies.</remarks>
    public IntegrationLifecycleResult<IntegrationChangeOutcome, IntegrationGroupMember> Register(IntegrationGroupRegistration request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Consumer);
        request.Consumer.Validate();
        IntegrationDesiredStateControl.ValidateEnvelope(request.Authorization, request.ExpectedRevision, request.AtUtc);
        if (!authorize(null, IntegrationLifecycleAction.RegisterMember, request.Authorization)
            || !authorize(request.Consumer, IntegrationLifecycleAction.RegisterMember, request.Authorization))
            return new(IntegrationChangeOutcome.Unauthorized);
        lock (Sync)
        {
            if (members.TryGetValue(request.Consumer, out var original))
                return original.Command == request ? new(IntegrationChangeOutcome.Replayed, original)
                    : new(IntegrationChangeOutcome.IdentityConflict);
            if (request.ExpectedRevision != Revision) return new(IntegrationChangeOutcome.RevisionConflict);
            if (!control.CanAdvance) return new(IntegrationChangeOutcome.RevisionExhausted);
            if (!scope.TryClaim(request.Consumer, () => new InMemoryIntegrationLifecycle(request.Consumer,
                (action, actor) => authorize(request.Consumer, action, actor),
                verifyFailureResolution is null ? null : resolution => verifyFailureResolution(request.Consumer, resolution), this), out var member))
                return new(IntegrationChangeOutcome.AlreadyOwned);
            control.AdvanceRevision();
            var receipt = new IntegrationGroupMember(request, Revision, member!);
            members.Add(request.Consumer, receipt);
            PublishSnapshot();
            return new(IntegrationChangeOutcome.Applied, receipt);
        }
    }
}
