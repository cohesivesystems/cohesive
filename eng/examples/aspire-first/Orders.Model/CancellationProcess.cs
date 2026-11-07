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

/// <summary>Cancels the example's one-item fulfillment; unsupported reservation sets fail before the claim.</summary>
/// <remarks>
/// The reservation identity must equal the order identity. Every mutation uses a durable transition receipt;
/// after interruption the original continuation identity must resume to replay the winning claim and release.
/// A different continuation observes InProgress and cannot take over. The sequence is recoverable, not atomic
/// across entities. Reservation creation or alteration outside the example fulfillment process is unsupported
/// while cancellation is running. Queries retain released facts so retries can validate the same evidence.
/// </remarks>
/// <param name="domain">Canonical entity and transition authority.</param>
/// <param name="queries">Canonical bounded reservation query.</param>
[Cohesive.Processes.Authoring.Process(id: "fulfillment/cancel", revision: "1")]
public sealed partial class CancellationProcess(FulfillmentDomain domain, FulfillmentQueries queries)
    : Cohesive.Processes.Authoring.ProcessModel<CancelOrder, CancellationResult>
{
    /// <summary>Bounded native query selected for cancellation evidence.</summary>
    public Cohesive.Processes.Authoring.ProcessQuery<string, Reservation[]> Reservations { get; } = new(
        queries.ReservationsByOrder, new("1"), maximumRows: 1000, maximumBytes: 1_000_000,
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
        if (reservations.Length != 1)
            return new CancellationResult(CancellationOutcome.ReservationConflict);
        var reservation = await process.Query(ReservationEvidence, input.OrderId);
        if (!reservation.Exists)
            return new CancellationResult(CancellationOutcome.ReservationConflict);
        if (reservation.Id != input.OrderId || reservation.OrderId != input.OrderId
            || reservation.Partition != FulfillmentStoragePolicy.LocalPartition || reservation.Quantity <= 0
            || (order.Entity.Status == OrderStatus.Submitted && reservation.Released))
            return new CancellationResult(CancellationOutcome.ReservationConflict);
        var inventory = await process.PointRead(domain.Inventory, reservation.Sku);
        if (inventory.Entity == null)
            return new CancellationResult(CancellationOutcome.InventoryMissing);

        // Re-enter this transition on every retry. Only the original receipt can replay Claimed.
        var claim = await process.Transition(domain.OrderTransitions.BeginCancellation, input.OrderId);
        if (claim.Outcome != CancellationOutcome.Claimed)
            return new CancellationResult(claim.Outcome);
        var released = await process.Transition(domain.InventoryTransitions.Release, reservation.Sku, new StockQuantity(reservation.Quantity));
        if (released.Outcome != StockOutcome.Released)
            return new CancellationResult(CancellationOutcome.ReleaseRejected);
        var marked = await process.Transition(domain.ReservationTransitions.MarkReleased, reservation.Id);
        if (marked.Outcome != CancellationOutcome.Released)
            return new CancellationResult(marked.Outcome);
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
