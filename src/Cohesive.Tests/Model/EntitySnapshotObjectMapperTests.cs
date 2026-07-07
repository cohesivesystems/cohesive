using Cohesive.Relations.Model;
using Cohesive.Storage;

namespace Cohesive.Tests.Model;

public sealed class EntitySnapshotObjectMapperTests
{
    [Fact]
    public void Map_UsesObservationObjectMapperForActualSnapshotLayout()
    {
        var mapper = EntitySnapshotObjectMapper.Create<NoteProjection, NoteResource>(
            readOptions: EntityReadOptions.ForFields(nameof(NoteProjection.Id), nameof(NoteProjection.Name)),
            map: static (projection, snapshot) => new(
                projection.Id,
                projection.Name,
                snapshot.Entity.Version,
                snapshot.ConcurrencyToken.Value));

        var first = mapper.Map(CreateSnapshot(
            id: "note-1",
            fields: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(NoteProjection.Id)] = ObservationValue.FromString("note-1"),
                [nameof(NoteProjection.Name)] = ObservationValue.FromString("Alpha")
            }));
        var second = mapper.Map(CreateSnapshot(
            id: "note-2",
            fields: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                [nameof(NoteProjection.Name)] = ObservationValue.FromString("Bravo"),
                [nameof(NoteProjection.Id)] = ObservationValue.FromString("note-2")
            }));

        Assert.Equal("note-1", first.Id);
        Assert.Equal("Alpha", first.Name);
        Assert.Equal("note-2", second.Id);
        Assert.Equal("Bravo", second.Name);
        Assert.Equal(
            [nameof(NoteProjection.Id), nameof(NoteProjection.Name)],
            mapper.ReadOptions.Fields!.OrderBy(static field => field, StringComparer.Ordinal));
        Assert.Equal(
            mapper.ReadOptions.Fields!.OrderBy(static field => field, StringComparer.Ordinal),
            mapper.ReadOptions.FieldSelection.Fields!.OrderBy(static field => field, StringComparer.Ordinal));
    }

    [Fact]
    public void Create_WithFullReadOptions_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            EntitySnapshotObjectMapper.Create<NoteProjection, NoteResource>(
                EntityReadOptions.Full,
                static (projection, snapshot) => new(
                    projection.Id,
                    projection.Name,
                    snapshot.Entity.Version,
                    snapshot.ConcurrencyToken.Value)));

        Assert.Contains("non-empty field projection", error.Message);
    }

    static EntitySnapshot CreateSnapshot(string id, IReadOnlyDictionary<string, ObservationValue> fields) => new(
        Entity: new(new("shape.note"), id, fields, version: 7),
        PartitionKey: "tenant-a",
        ConcurrencyToken: new("etag-1"));

    sealed record NoteProjection(string Id, string Name);

    sealed record NoteResource(string Id, string Name, long EntityVersion, string ConcurrencyToken);
}
