using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Acquisition;
using Cohesive.Storage.Materialization;
using JasperFx.Events;
using Marten;

namespace Cohesive.Adapters.Marten;

/// <summary>A relation input backed by one retained, append-only, string-keyed Marten stream.
/// Stream versions order commits within this input. Each event must describe one complete mutation,
/// including the old relationship fields for deletes and moves. The producer appends in the same
/// transaction as the relational write and retains the stream for the lifetime of its checkpoints.</summary>
/// <typeparam name="TEvent">Domain event contract stored in the stream.</typeparam>
public sealed class MartenMaterializationEventSource<TEvent> : IMaterializationRetainedChangeSource where TEvent : class
{
    static readonly JsonSerializerOptions Json = MaterializationJsonSerializer.CreateOptions();
    readonly PostgresMaterializationSource baseline;
    readonly IDocumentStore store;
    readonly string stream;
    readonly MaterializationAuthenticatedValueCodec cursor;
    readonly Func<TEvent, (RelationQuerySourceReadObservation? Before, RelationQuerySourceReadObservation? After)> images;
    readonly int maximumItems;
    readonly long maximumBytes;

    /// <param name="baseline">Existing PostgreSQL placement, bounded reader and exact input scope.</param>
    /// <param name="store">Borrowed Marten store for the same producer database.</param>
    /// <param name="stream">Dedicated stream for this input and logical partition; never truncate or reuse its key.</param>
    /// <param name="incarnation">Stable operator identity for this database, stream lifetime and projection revision.</param>
    /// <param name="key">Persistent cursor authentication key.</param>
    /// <param name="maximumItems">Maximum deliveries per page.</param>
    /// <param name="maximumBytes">Maximum canonical delivery bytes per page.</param>
    /// <param name="images">Projects immutable event content to the relation's before/after observations.</param>
    public MartenMaterializationEventSource(PostgresMaterializationSource baseline, IDocumentStore store,
        string stream, string incarnation, ReadOnlyMemory<byte> key, int maximumItems, long maximumBytes,
        Func<TEvent, (RelationQuerySourceReadObservation? Before, RelationQuerySourceReadObservation? After)> images)
    {
        this.baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(incarnation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (store.Storage.Database is not Weasel.Postgresql.PostgresqlDatabase nativeDatabase ||
            nativeDatabase.DataSource is not Npgsql.NpgsqlDataSource pool || !baseline.UsesDataSource(pool))
            throw new ArgumentException("Event streams and relation reads must share the admitted PostgreSQL pool.", nameof(store));
        if (!store.Options.Events.AllKnownEventTypes().Any(x => x.EventType == typeof(TEvent)))
            throw new ArgumentException("The event type must be registered on the Marten store.", nameof(store));
        if (store.Options.Events.StreamIdentity != StreamIdentity.AsString)
            throw new ArgumentException("Event materialization requires string stream identity.", nameof(store));
        this.stream = stream;
        this.images = images ?? throw new ArgumentNullException(nameof(images));
        this.maximumItems = maximumItems;
        this.maximumBytes = maximumBytes;
        var domain = new { Provider = "marten-stream/v1", Schema = store.Options.Events.DatabaseSchemaName,
            Stream = stream, Incarnation = incarnation, baseline.Scope };
        cursor = new("marten-v1/", StrictDocumentJson.GetCanonicalBytes(domain, Json), key.Span, 256);
        var native = baseline.Descriptor.CapabilityProfile;
        var feed = new MaterializationCapabilityEvidence(new("marten/retained-events/v1"),
            MaterializationCapabilityKind.SourceChangeDelivery, CapabilityRealizationKind.Constrained,
            [MaterializationGuaranteeKind.StableOrdering, MaterializationGuaranteeKind.BaselinePlusCatchUp,
             MaterializationGuaranteeKind.AtLeastOnceDelivery, MaterializationGuaranteeKind.RetainedHistoryStart,
             MaterializationGuaranteeKind.CompleteMutationDelivery, MaterializationGuaranteeKind.BeforeImage],
            [new(MaterializationLimitKind.ChangeItems, maximumItems), new(MaterializationLimitKind.ReadBytes, maximumBytes)],
            ["marten/stream/" + Uri.EscapeDataString(stream), "marten/incarnation/" + Uri.EscapeDataString(incarnation)],
            "Contiguous committed stream versions with producer-supplied before and after observations.");
        Descriptor = new(baseline.Descriptor.RelationReader, new(new(native.Id.Value + "/marten-events/v1"),
            native.Role, native.Subject, native.Evidence.Add(feed)));
    }

    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePage> ReadPageAsync(OperationContext context, MaterializationSourcePageRequest request) =>
        baseline.ReadPageAsync(context, request);

    /// <inheritdoc />
    public async ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(OperationContext context, MaterializationSourceScope scope)
    {
        Validate(context, scope);
        await using var session = store.QuerySession();
        var head = await HeadAsync(session, context.CancellationToken).ConfigureAwait(false);
        if (head == 0) return Position(0, Guid.Empty);
        var tail = await session.Events.FetchStreamAsync(stream, version: head, fromVersion: head,
            token: context.CancellationToken).ConfigureAwait(false);
        if (tail.Count != 1 || tail[0].Version != head)
            throw new InvalidOperationException("The retained stream head is unavailable.");
        return Position(head, tail[0].Id);
    }

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureRetainedStartPositionAsync(OperationContext context, MaterializationSourceScope scope)
    {
        Validate(context, scope);
        return ValueTask.FromResult(Position(0, Guid.Empty));
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationChangePage> ReadChangesAsync(OperationContext context, MaterializationChangeReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(context, request.Scope);
        if (request.MaximumDeliveries > maximumItems || request.MaximumBytes > maximumBytes)
            throw new ArgumentOutOfRangeException(nameof(request), "Page exceeds admitted event feed bounds.");
        if (request.AfterPosition.FormatVersion != 1)
            throw new ArgumentException("Unknown event cursor version.", nameof(request));
        var payload = cursor.Decode(request.AfterPosition.Value, nameof(request), "Marten stream version");
        if (payload.Length != sizeof(long) + 16) throw new ArgumentException("Invalid event cursor.", nameof(request));
        var through = BinaryPrimitives.ReadInt64BigEndian(payload);
        var anchor = new Guid(payload.AsSpan(sizeof(long)));
        await using var session = store.QuerySession();
        var head = await HeadAsync(session, context.CancellationToken).ConfigureAwait(false);
        if (through < 0 || through > head) throw new InvalidOperationException("Marten stream no longer covers the checkpoint.");
        var count = (int)Math.Min(head - through, request.MaximumDeliveries);
        var deliveries = ImmutableArray.CreateBuilder<MaterializationChangeDelivery>(count);
        long bytes = 0;
        if (count > 0 || through > 0)
        {
            var events = await session.Events.FetchStreamAsync(stream, fromVersion: Math.Max(1, through),
                version: through + count, token: context.CancellationToken).ConfigureAwait(false);
            var firstNew = through == 0 ? 0 : 1;
            if (events.Count != count + firstNew || (through > 0 &&
                (events[0].Version != through || events[0].Id != anchor)))
                throw new InvalidOperationException("Required event stream history is missing or has been replaced.");
            for (var i = firstNew; i < events.Count; i++)
            {
                var item = events[i];
                if (item.Version != through + 1 || item.Data is not TEvent value)
                    throw new InvalidOperationException("Event version or type differs from the admitted contract.");
                var (before, after) = images(value);
                var subject = (after ?? before)?.Identity ?? throw new InvalidOperationException("Event has no subject image.");
                var kind = before is null ? MaterializationChangeKind.Create :
                    after is null ? MaterializationChangeKind.Delete : MaterializationChangeKind.Update;
                var id = item.Id.ToString("D");
                var occurred = item.Timestamp.ToUniversalTime();
                var observed = context.UtcNow.ToUniversalTime();
                if (observed < occurred) observed = occurred;
                var change = new MaterializationChangeEnvelope(new(id), subject, request.Scope, request.Scope.Shape,
                    Position(item.Version, item.Id), kind, before, after, occurred, observed);
                var delivery = new MaterializationChangeDelivery(new(id), change, observed);
                var size = StrictDocumentJson.GetCanonicalBytes(delivery, Json).LongLength;
                if (size > request.MaximumBytes - bytes)
                {
                    if (deliveries.Count == 0) throw new InvalidOperationException("Event exceeds the admitted page byte limit.");
                    break;
                }
                deliveries.Add(delivery);
                bytes += size;
                through = item.Version;
                anchor = item.Id;
            }
        }
        return new(deliveries.ToImmutable(), Position(through, anchor), through == head
            ? MaterializationChangePageState.CaughtUp : MaterializationChangePageState.MoreAvailable);
    }

    async Task<long> HeadAsync(IQuerySession session, CancellationToken token) =>
        (await session.Events.FetchStreamStateAsync(stream, token).ConfigureAwait(false))?.Version ?? 0;

    MaterializationSourcePosition Position(long version, Guid anchor)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long) + 16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, version);
        anchor.TryWriteBytes(bytes[sizeof(long)..]);
        return new(1, baseline.Scope, cursor.Encode(bytes));
    }

    void Validate(OperationContext context, MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        if (scope != baseline.Scope) throw new ArgumentException("Event feed belongs to another relation scope.", nameof(scope));
    }
}
