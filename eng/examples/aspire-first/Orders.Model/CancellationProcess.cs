using Cohesive.Processes.Authoring;
using System.Text.Json.Serialization;

namespace AspireFirst.Orders;

/// <summary>Cancellation intent for the order and its recorded reservations.</summary>
/// <param name="OrderId">Existing order identity.</param>
public sealed record CancelOrder(string OrderId);

/// <summary>Closed cancellation progress and rejection outcomes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CancellationOutcome>))]
public enum CancellationOutcome
{
    /// <summary>This occurrence exclusively claimed the cancellation.</summary>
    Claimed,
    /// <summary>All reservations were released and the order was cancelled.</summary>
    Cancelled,
    /// <summary>The reservation records its completed release.</summary>
    Released,
    /// <summary>An earlier cancellation completed.</summary>
    AlreadyCancelled,
    /// <summary>The original cancellation occurrence must resume.</summary>
    InProgress,
    /// <summary>The current lifecycle does not permit cancellation.</summary>
    NotSubmitted,
    /// <summary>Completion requires the cancelling lifecycle state.</summary>
    NotCancelling,
    /// <summary>The order does not exist.</summary>
    OrderMissing,
    /// <summary>Reservation evidence is missing or inconsistent.</summary>
    ReservationConflict,
    /// <summary>A required inventory entity is absent.</summary>
    InventoryMissing,
    /// <summary>A guarded release rejected and requires operator review.</summary>
    ReleaseRejected
}

/// <summary>Cancellation decision or terminal process result.</summary>
/// <param name="Outcome">Declared progress or domain rejection.</param>
public sealed record CancellationResult(CancellationOutcome Outcome);

/// <summary>Cancels a bounded set of order reservations through durable sequential occurrences.</summary>
/// <remarks>
/// Every mutation uses a durable transition receipt;
/// after interruption the original continuation identity must resume to replay the winning claim and release.
/// A different continuation observes InProgress and cannot take over. The sequence is recoverable, not atomic
/// across entities. Reservation creation or alteration outside the example fulfillment process is unsupported
/// while cancellation is running. The complete observed sequence is retained across activations; database row order is not assumed.
/// </remarks>
/// <param name="domain">Canonical entity and transition authority.</param>
/// <param name="queries">Canonical bounded reservation query.</param>
[Cohesive.Processes.Authoring.Process(id: "fulfillment/cancel", revision: "2")]
public sealed partial class CancellationProcess(FulfillmentDomain domain, FulfillmentQueries queries)
    : Cohesive.Processes.Authoring.ProcessModel<CancelOrder, CancellationResult>
{
    /// <summary>Maximum reservation facts admitted into one cancellation.</summary>
    public const int MaximumReservations = 1000;

    /// <summary>Stable checkpoint separating complete preflight from cancellation ownership.</summary>
    public const string PreflightCompletedNode = "fulfillment/cancel/preflight-complete";

    /// <summary>Bounded native query selected for cancellation evidence.</summary>
    public Cohesive.Processes.Authoring.ProcessQuery<string, Reservation[]> Reservations { get; } = new(
        queries.ReservationsByOrder, new("2"), maximumRows: MaximumReservations, maximumBytes: 1_000_000,
        consistency: Cohesive.Relations.Execution.RelationQueryConsistency.IndependentSources);

    /// <summary>Versioned point-read projection with explicit absence and concrete validation fields.</summary>
    public EntityPointRead<Reservation, string, CancellationReservation> ReservationEvidence { get; } = domain.Reservations.PointRead(
        new("fulfillment/cancellation/reservation-evidence"), new("1"),
        reservation => reservation == null
            ? new CancellationReservation(false, "", "", "", "", 0, false)
            : new CancellationReservation(true, reservation.Id, reservation.OrderId, reservation.Partition, reservation.Sku, reservation.Quantity, reservation.Released));

    /// <inheritdoc />
    protected override async Cohesive.Processes.Authoring.ProcessTask<CancellationResult> Execute(
        Cohesive.Processes.Authoring.ProcessContext process, CancelOrder input)
    {
        var order = await process.PointRead(domain.Orders, input.OrderId);
        if (order.Entity == null)
            return new CancellationResult(CancellationOutcome.OrderMissing);
        var reservations = await process.Query(Reservations, input.OrderId);
        if (reservations.Length == 0)
            return new CancellationResult(CancellationOutcome.ReservationConflict);
        // Freeze source order before any preflight or claim. New executions may observe another order;
        // recovery of this execution always uses this captured sequence.
        await process.DurableCut();
        if (order.Entity.Status != OrderStatus.Submitted)
        {
            var previous = await process.Transition(domain.OrderTransitions.BeginCancellation, input.OrderId);
            return new CancellationResult(previous.Outcome);
        }
        var validated = await process.RepeatAcrossActivation(
            occurrence: Validate(new CancellationProgress(input.OrderId, reservations, 0)),
            continueWhen: state => state.Index < state.Reservations.Length,
            progress: state => state.Index,
            policy: new Cohesive.Processes.IR.ProcessRecurrencePolicy(MaximumReservations, 1),
            exhausted: Exhausted,
            stalled: Stalled);
        await process.DurableCut(id: new(PreflightCompletedNode));
        var claim = await process.Transition(domain.OrderTransitions.BeginCancellation, input.OrderId);
        if (claim.Outcome != CancellationOutcome.Claimed)
            return new CancellationResult(claim.Outcome);
        var releasedAll = await process.RepeatAcrossActivation(
            occurrence: Release(new CancellationProgress(validated.OrderId, validated.Reservations, 0)),
            continueWhen: state => state.Index < state.Reservations.Length,
            progress: state => state.Index,
            policy: new Cohesive.Processes.IR.ProcessRecurrencePolicy(MaximumReservations, 1),
            exhausted: Exhausted,
            stalled: Stalled);

        var finalReservations = await process.Query(Reservations, input.OrderId);
        if (finalReservations.Length != releasedAll.Reservations.Length)
            return new CancellationResult(CancellationOutcome.ReservationConflict);
        var verified = await process.RepeatAcrossActivation(
            occurrence: Verify(new CancellationProgress(releasedAll.OrderId, releasedAll.Reservations, 0)),
            continueWhen: state => state.Index < state.Reservations.Length,
            progress: state => state.Index,
            policy: new Cohesive.Processes.IR.ProcessRecurrencePolicy(MaximumReservations, 1),
            exhausted: Exhausted,
            stalled: Stalled);

        async ProcessTask<CancellationProgress> Verify(CancellationProgress state)
        {
            var reservation = await process.Query(ReservationEvidence, state.Reservations[state.Index].Id);
            if (!reservation.Exists || reservation.OrderId != state.OrderId
                || reservation.Partition != FulfillmentStoragePolicy.LocalPartition || !reservation.Released
                || reservation.Sku != state.Reservations[state.Index].Sku
                || reservation.Quantity != state.Reservations[state.Index].Quantity)
                await process.Succeed(new CancellationResult(CancellationOutcome.ReservationConflict));
            return new CancellationProgress(state.OrderId, state.Reservations, state.Index + 1);
        }

        async ProcessTask<CancellationProgress> Validate(CancellationProgress state)
        {
            var reservation = await process.Query(ReservationEvidence, state.Reservations[state.Index].Id);
            if (!reservation.Exists || reservation.OrderId != state.OrderId
                || reservation.Partition != FulfillmentStoragePolicy.LocalPartition || reservation.Quantity <= 0
                || reservation.Released || reservation.Id != state.Reservations[state.Index].Id
                || reservation.Sku != state.Reservations[state.Index].Sku
                || reservation.Quantity != state.Reservations[state.Index].Quantity)
                await process.Succeed(new CancellationResult(CancellationOutcome.ReservationConflict));
            var inventory = await process.PointRead(domain.Inventory, reservation.Sku);
            if (inventory.Entity == null)
                await process.Succeed(new CancellationResult(CancellationOutcome.InventoryMissing));
            return new CancellationProgress(state.OrderId, state.Reservations, state.Index + 1);
        }

        async ProcessTask<CancellationProgress> Release(CancellationProgress state)
        {
            var reservation = await process.Query(ReservationEvidence, state.Reservations[state.Index].Id);
            if (!reservation.Exists || reservation.OrderId != state.OrderId
                || reservation.Partition != FulfillmentStoragePolicy.LocalPartition || reservation.Released
                || reservation.Sku != state.Reservations[state.Index].Sku
                || reservation.Quantity != state.Reservations[state.Index].Quantity)
                await process.Succeed(new CancellationResult(CancellationOutcome.ReservationConflict));
            // Retain this validated evidence before any entity mutation. A crash after marking released
            // must replay the same observation rather than mistake our own commit for foreign mutation.
            await process.DurableCut();
            var released = await process.Transition(domain.InventoryTransitions.Release, reservation.Sku, new StockQuantity(reservation.Quantity));
            if (released.Outcome != StockOutcome.Released)
                await process.Succeed(new CancellationResult(CancellationOutcome.ReleaseRejected));
            var marked = await process.Transition(domain.ReservationTransitions.MarkReleased, reservation.Id);
            if (marked.Outcome != CancellationOutcome.Released)
                await process.Succeed(new CancellationResult(marked.Outcome));
            return new CancellationProgress(state.OrderId, state.Reservations, state.Index + 1);
        }
        async ProcessTask Exhausted() { await process.Succeed(new CancellationResult(CancellationOutcome.ReservationConflict)); }
        async ProcessTask Stalled() { await process.Succeed(new CancellationResult(CancellationOutcome.ReservationConflict)); }

        var completed = await process.Transition(domain.OrderTransitions.CompleteCancellation, input.OrderId);
        return new CancellationResult(completed.Outcome);
    }
}

/// <summary>Explicit absence and fields of the observed reservation, projected by a versioned point-read attachment.</summary>
/// <param name="Exists">Whether the trusted partition contains the reservation.</param>
/// <param name="Id">Reservation identity, or empty when absent.</param>
/// <param name="OrderId">Owning order identity, or empty when absent.</param>
/// <param name="Partition">Observed partition, or empty when absent.</param>
/// <param name="Sku">Inventory identity, or empty when absent.</param>
/// <param name="Quantity">Observed quantity, or zero when absent.</param>
/// <param name="Released">Recorded release state, false when absent.</param>
public sealed record CancellationReservation(bool Exists, string Id, string OrderId, string Partition, string Sku, int Quantity, bool Released);

/// <summary>Immutable reservation evidence and bounded cursor retained across cancellation activations.</summary>
/// <param name="OrderId">Order owning every reservation.</param>
/// <param name="Reservations">Complete captured sequence; never reordered or filtered as releases complete.</param>
/// <param name="Index">Zero-based next reservation position.</param>
public sealed record CancellationProgress(string OrderId, Reservation[] Reservations, int Index);
