using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Control;

/// <summary>
/// Complete durable state of the bounded manual limit-update interpretation for one control loop and epoch.
/// </summary>
/// <remarks>
/// The state has one effective <see cref="OperatingPoint"/>. Command acceptance advances the durable revision and
/// records <see cref="PendingUpdate"/> without changing that point. Safe-point actuation advances the revision again
/// and is the only transition that changes the effective point.
/// </remarks>
public sealed record ControlLimitUpdateState
{
    readonly ImmutableArray<ControlLimitUpdateReceipt> receipts;

    /// <summary>Creates complete durable manual limit-update state.</summary>
    /// <param name="schemaVersion">Exact portable Control schema version.</param>
    /// <param name="loopId">Stable controlled loop identity.</param>
    /// <param name="target">Stable controlled Process, materialization, or runtime subject.</param>
    /// <param name="epoch">Current attempt, generation, or other controlled epoch.</param>
    /// <param name="revision">Current durable state revision and optimistic fence.</param>
    /// <param name="definitionFingerprint">Fingerprint of the exact canonical loop definition owning this state.</param>
    /// <param name="authorityScope">Authority and optional tenant boundary authorized to issue updates.</param>
    /// <param name="operatingPoint">Currently effective complete operating point.</param>
    /// <param name="actuations">Applied update receipts in durable revision order.</param>
    /// <param name="createdAtUtc">Explicit UTC state-creation time.</param>
    /// <param name="updatedAtUtc">Explicit UTC time of the latest accepted command or actuation.</param>
    /// <param name="pendingUpdate">Latest accepted command awaiting a safe point, when any.</param>
    /// <exception cref="ArgumentException">
    /// Schema, identity, revision, chronology, command-ledger, pending, actuation, or operating-point invariants
    /// conflict.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/>, <paramref name="definitionFingerprint"/>,
    /// <paramref name="authorityScope"/>, or <paramref name="operatingPoint"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ControlLimitUpdateState(
        ExecutionIrSchemaVersion schemaVersion,
        ControlLoopId loopId,
        string target,
        ControlEpochId epoch,
        ControlRevision revision,
        ExecutionDefinitionFingerprint definitionFingerprint,
        InteractionAuthorityScope authorityScope,
        ControlOperatingPoint operatingPoint,
        ImmutableArray<ControlLimitUpdateActuation> actuations,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        ControlLimitUpdateReceipt? pendingUpdate = null)
    {
        if (schemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(loopId.Value)
            || string.IsNullOrWhiteSpace(epoch.Value))
        {
            throw new ArgumentException(
                "Limit-update state requires non-default schema, loop, and epoch identities.",
                nameof(loopId));
        }

        ControlRevision.RequireDefined(revision, nameof(revision));
        ControlObservation.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        ControlObservation.RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        if (updatedAtUtc < createdAtUtc)
            throw new ArgumentException("Limit-update state timestamps must be chronological.", nameof(updatedAtUtc));

        SchemaVersion = schemaVersion;
        LoopId = loopId;
        Target = Guard.RequireNotNullOrWhiteSpace(target);
        Epoch = epoch;
        Revision = revision;
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        AuthorityScope = Guard.RequireNotNull(authorityScope);
        OperatingPoint = Guard.RequireNotNull(operatingPoint);

        if (actuations.IsDefault)
            actuations = [];
        if (actuations.Any(static actuation => actuation is null))
            throw new ArgumentException("Limit-update actuations cannot contain null entries.", nameof(actuations));
        actuations = [.. actuations.OrderBy(static actuation => actuation.Revision.Ordinal)];
        if (actuations.GroupBy(static actuation => actuation.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Limit-update actuation identities must be unique.", nameof(actuations));
        if (actuations
            .GroupBy(static actuation => actuation.ApplicationPoint.Id)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Limit-update application-point identities must be unique.", nameof(actuations));
        }

        var receiptBuilder = ImmutableArray.CreateBuilder<ControlLimitUpdateReceipt>(
            actuations.Length + (pendingUpdate is null ? 0 : 1));
        foreach (var actuation in actuations)
        {
            var command = actuation.Receipt.Command;
            if (actuation.Id != ControlDerivedIdentity.LimitUpdateActuation(
                    actuation.Receipt,
                    actuation.ApplicationPoint)
                || command.LoopId != loopId
                || command.DefinitionFingerprint != DefinitionFingerprint
                || !string.Equals(command.Target, Target, StringComparison.Ordinal)
                || command.Epoch != epoch)
            {
                throw new ArgumentException(
                    "Every retained actuation must have its canonical identity and belong to this exact state fence.",
                    nameof(actuations));
            }
            receiptBuilder.Add(actuation.Receipt);
        }
        if (pendingUpdate is not null)
            receiptBuilder.Add(pendingUpdate);
        receipts = receiptBuilder.MoveToImmutable();
        if (receipts.GroupBy(static receipt => receipt.Command.CommandId).Any(static group => group.Count() > 1))
            throw new ArgumentException("Limit-update command identities must be unique.", nameof(actuations));
        if (receipts.GroupBy(static receipt => receipt.Command.IdempotencyKey).Any(static group => group.Count() > 1))
            throw new ArgumentException("Limit-update idempotency keys must be unique.", nameof(actuations));
        foreach (var receipt in receipts)
        {
            var command = receipt.Command;
            if (command.SchemaVersion != schemaVersion
                || command.LoopId != loopId
                || command.DefinitionFingerprint != DefinitionFingerprint
                || !string.Equals(command.Target, Target, StringComparison.Ordinal)
                || command.Epoch != epoch
                || command.Authorization.AuthorityScope != AuthorityScope)
            {
                throw new ArgumentException(
                    "Every retained command receipt must belong to the state's exact loop, definition, target, epoch, and authority scope.",
                    nameof(actuations));
            }
        }
        ValidateTransitionChain(actuations, pendingUpdate, createdAtUtc);

        if (pendingUpdate is not null)
        {
            if (pendingUpdate.AcceptedRevision != revision)
            {
                throw new ArgumentException(
                    "A pending update must be the latest retained receipt at the current revision.",
                    nameof(pendingUpdate));
            }
            if (!actuations.IsDefaultOrEmpty && actuations[^1].OperatingPoint != OperatingPoint)
            {
                throw new ArgumentException(
                    "Accepting a pending update cannot change the prior actuation's effective operating point.",
                    nameof(operatingPoint));
            }
            if (updatedAtUtc != pendingUpdate.AcceptedAtUtc)
                throw new ArgumentException("Pending-update acceptance must be the latest state change.", nameof(updatedAtUtc));
        }
        else if (actuations.IsDefaultOrEmpty)
        {
            if (revision != ControlRevision.Initial
                || updatedAtUtc != createdAtUtc)
            {
                throw new ArgumentException(
                    "State without accepted commands must be exact initial revision-one state.",
                    nameof(revision));
            }
        }
        else
        {
            if (actuations[^1].Revision != revision
                || actuations[^1].OperatingPoint != OperatingPoint
                || updatedAtUtc != actuations[^1].AppliedAtUtc)
            {
                throw new ArgumentException(
                    "State without a pending update must retain an exact actuation for every command receipt.",
                    nameof(actuations));
            }
        }

        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        PendingUpdate = pendingUpdate;
        Actuations = actuations;
    }

    /// <summary>Exact portable Control schema version.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable controlled loop identity.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Stable controlled Process, materialization, or runtime subject.</summary>
    public string Target { get; }

    /// <summary>Current attempt, generation, or other controlled epoch.</summary>
    public ControlEpochId Epoch { get; }

    /// <summary>Current durable state revision and optimistic fence.</summary>
    public ControlRevision Revision { get; }

    /// <summary>Fingerprint of the exact canonical loop definition owning this state.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Authority and optional tenant boundary authorized to issue updates.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Currently effective complete operating point.</summary>
    public ControlOperatingPoint OperatingPoint { get; }

    /// <summary>
    /// Accepted command receipts derived in durable revision order from <see cref="Actuations"/> and
    /// <see cref="PendingUpdate"/>.
    /// </summary>
    [JsonIgnore]
    public ImmutableArray<ControlLimitUpdateReceipt> Receipts => receipts;

    /// <summary>Explicit UTC state-creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Explicit UTC time of the latest accepted command or actuation.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Latest accepted command awaiting a safe point, when any.</summary>
    public ControlLimitUpdateReceipt? PendingUpdate { get; }

    /// <summary>Applied update receipts in durable revision order.</summary>
    public ImmutableArray<ControlLimitUpdateActuation> Actuations { get; }

    /// <summary>Latest applied update retained by <see cref="Actuations"/>, when any.</summary>
    [JsonIgnore]
    public ControlLimitUpdateActuation? LastActuation => Actuations.IsDefaultOrEmpty ? null : Actuations[^1];

    /// <summary>Fence of <see cref="LastActuation"/>, when any.</summary>
    [JsonIgnore]
    public ControlApplicationFence? LastApplicationFence => LastActuation?.ApplicationPoint.Fence;

    /// <summary>Creates initial manual limit-update state for a definition and new controlled epoch.</summary>
    /// <param name="definition">Canonical bounded control-loop definition.</param>
    /// <param name="epoch">New Process attempt, materialization generation, or other epoch.</param>
    /// <param name="authorityScope">Authority and optional tenant boundary authorized to issue updates.</param>
    /// <param name="createdAtUtc">Explicit UTC state-creation time.</param>
    /// <returns>Initial revision-one state at the definition's bounded initial operating point.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="authorityScope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="epoch"/> is default or <paramref name="createdAtUtc"/> is not UTC.
    /// </exception>
    public static ControlLimitUpdateState Create(
        ControlLoopDefinition definition,
        ControlEpochId epoch,
        InteractionAuthorityScope authorityScope,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(authorityScope);
        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            definition.Id,
            definition.Target,
            epoch,
            ControlRevision.Initial,
            definition.Fingerprint,
            authorityScope,
            definition.InitialOperatingPoint,
            [],
            createdAtUtc,
            createdAtUtc);
    }

    /// <summary>Compares durable states structurally.</summary>
    /// <param name="other">State to compare.</param>
    /// <returns><see langword="true"/> when identity, fences, point, ledger, and transition evidence are equal.</returns>
    public bool Equals(ControlLimitUpdateState? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && LoopId == other.LoopId
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && Epoch == other.Epoch
        && Revision == other.Revision
        && DefinitionFingerprint == other.DefinitionFingerprint
        && AuthorityScope == other.AuthorityScope
        && OperatingPoint == other.OperatingPoint
        && Actuations.SequenceEqual(other.Actuations)
        && CreatedAtUtc == other.CreatedAtUtc
        && UpdatedAtUtc == other.UpdatedAtUtc
        && PendingUpdate == other.PendingUpdate;

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from complete durable state.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(LoopId);
        hash.Add(Target, StringComparer.Ordinal);
        hash.Add(Epoch);
        hash.Add(Revision);
        hash.Add(DefinitionFingerprint);
        hash.Add(AuthorityScope);
        hash.Add(OperatingPoint);
        foreach (var actuation in Actuations)
            hash.Add(actuation);
        hash.Add(CreatedAtUtc);
        hash.Add(UpdatedAtUtc);
        hash.Add(PendingUpdate);
        return hash.ToHashCode();
    }

    static void ValidateTransitionChain(
        ImmutableArray<ControlLimitUpdateActuation> actuations,
        ControlLimitUpdateReceipt? pendingUpdate,
        DateTimeOffset createdAtUtc)
    {
        ControlRevision priorAppliedRevision = ControlRevision.Initial;
        var priorTransitionAtUtc = createdAtUtc;
        ControlOperatingPoint? priorOperatingPoint = null;
        for (var index = 0; index < actuations.Length; index++)
        {
            var actuation = actuations[index];
            var receipt = actuation.Receipt;
            if (receipt.Command.ExpectedRevision != priorAppliedRevision
                || receipt.AcceptedAtUtc < priorTransitionAtUtc)
            {
                throw new ArgumentException(
                    "Limit-update receipts must form a chronological accept/apply revision chain.",
                    nameof(actuations));
            }

            if (actuation.PriorRevision != receipt.AcceptedRevision
                || actuation.Revision.Ordinal != receipt.AcceptedRevision.Ordinal + 1
                || actuation.AppliedAtUtc < receipt.AcceptedAtUtc
                || priorOperatingPoint is not null && actuation.PriorOperatingPoint != priorOperatingPoint)
            {
                throw new ArgumentException(
                    "Limit-update actuations must pair one-to-one with receipts in chronological revision order.",
                    nameof(actuations));
            }
            if (index > 0 && actuation.ApplicationPoint.Fence.Ordinal <= actuations[index - 1].ApplicationPoint.Fence.Ordinal)
            {
                throw new ArgumentException(
                    "Limit-update application fences must increase strictly across the durable actuation ledger.",
                    nameof(actuations));
            }

            priorAppliedRevision = actuation.Revision;
            priorTransitionAtUtc = actuation.AppliedAtUtc;
            priorOperatingPoint = actuation.OperatingPoint;
        }

        if (pendingUpdate is not null
            && (pendingUpdate.Command.ExpectedRevision != priorAppliedRevision
                || pendingUpdate.AcceptedAtUtc < priorTransitionAtUtc))
        {
            throw new ArgumentException(
                "The pending limit-update receipt must immediately follow the durable actuation ledger.",
                nameof(pendingUpdate));
        }
    }
}
