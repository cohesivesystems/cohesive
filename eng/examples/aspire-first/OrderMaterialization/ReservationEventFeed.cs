using AspireFirst.Orders;
using Cohesive.Adapters.Marten;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Storage.Materialization;
using Marten;
using System.Text.Json;

namespace AspireFirst.Materialization;

/// <summary>Committed reservation mutation. A move retains both order references.</summary>
/// <param name="Before">Prior reservation, absent only on creation.</param>
/// <param name="After">New reservation, absent only on deletion.</param>
public sealed record ReservationChanged(Reservation? Before, Reservation? After);

/// <summary>Attaches a retained reservation stream to the existing order-details relation.</summary>
internal static class ReservationEventFeed
{
    internal static PostgresMaterializationStorageOptions Attach(PostgresMaterializationStorageOptions options,
        FulfillmentDomain domain, PreparedMaterialization materialization, IDocumentStore store,
        string stream, string incarnation, ReadOnlyMemory<byte> positionKey)
    {
        var input = materialization.Impact.Routes.Single(route =>
            route.ChangeShape == domain.Reservations.Definition.StateShape.QualifiedId).ChangeInput;
        return options with { EventSources = options.EventSources.Add(input, Bind) };

        IMaterializationRetainedChangeSource Bind(PostgresMaterializationSource baseline)
        {
            var shape = baseline.Scope.Shape;
            var fields = baseline.Scope.Placement.Fields.Select(f => (
                Name: f.SemanticPath.Segments.Single().Segment!,
                Read: new RelationQuerySourceReadField(f.Input, f.SemanticPath, f.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput))).ToArray();
            return new MartenMaterializationEventSource<ReservationChanged>(baseline, store, stream,
                incarnation, positionKey, FulfillmentMaterializations.MaximumItems, FulfillmentMaterializations.MaximumBytes,
                e => (Image(e.Before), Image(e.After)));

            RelationQuerySourceReadObservation? Image(Reservation? state)
            {
                if (state is null) return null;
                if (state.Partition != options.Reads.PartitionScope!.CanonicalValue)
                    throw new InvalidOperationException("Reservation event belongs to another logical partition.");
                var json = JsonSerializer.SerializeToElement(state);
                return new(state.Id, shape, [.. fields.Select(field => new RelationQuerySourceReadFieldResult(
                    field.Read, RelationQuerySourceReadFieldState.Value, ObservationValue.FromJsonElement(json.GetProperty(field.Name))))]);
            }
        }
    }
}
