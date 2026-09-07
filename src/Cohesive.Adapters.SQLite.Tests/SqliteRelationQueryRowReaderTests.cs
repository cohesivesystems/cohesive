using Cohesive.Adapters.SQLite.TestFixtures;
using Cohesive.Model;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteRelationQueryRowReaderTests
{
    [Fact]
    public void TypedRowsMatchCanonicalValuesAndRetainOwnedBytesAcrossReaderLifetimes()
    {
        using var fixture = new SqliteTypedRowFixture();
        SqliteTypedRowFixture.Row first;
        using (var command = fixture.CreateCommand())
        using (var reader = command.ExecuteReader())
        {
            var rows = fixture.Mapping.Bind(reader);
            Assert.True(reader.Read());
            first = rows.ReadCurrent();
            var canonical = fixture.Artifact.ReadCurrentRow(reader).Value;
            var second = rows.ReadCurrent();
            Assert.Equal(canonical.Fields![nameof(first.Id)].Int64, first.Id);
            Assert.Equal(canonical.Fields[nameof(first.Label)].String, first.Label);
            Assert.Equal(canonical.Fields[nameof(first.Payload)].Bytes.ToArray(), first.Payload);
            Assert.NotSame(first.Payload, second.Payload);
            first.Payload![0] = 127;
            Assert.Equal(0, second.Payload![0]);
            Assert.Equal(0, canonical.Fields[nameof(first.Payload)].Bytes.Span[0]);
            Assert.Equal(0, rows.ReadCurrent().Payload![0]);
            Assert.True(reader.Read());
            Assert.True(rows.TryGetField(nameof(first.Payload), out var nullable));
            Assert.Equal(ObservationValueKind.Null, nullable.Kind);
            Assert.Null(rows.ReadCurrent().Payload);
            Assert.True(reader.Read());
            Assert.False(rows.TryGetField(nameof(first.Payload), out _));
            Assert.Null(rows.ReadCurrent().Payload);
            Assert.False(reader.Read());
            Assert.Throws<InvalidOperationException>(() => rows.ReadCurrent());
        }
        Assert.Equal(127, first.Payload![0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(65536)]
    public void TypedReadingAllocatesOnePayloadBufferAndBoundedRowOverhead(int length)
    {
        using var fixture = new SqliteTypedRowFixture(length);
        using var command = fixture.CreateCommand();
        using var reader = command.ExecuteReader();
        var rows = fixture.Mapping.Bind(reader);
        Assert.True(reader.Read());
        for (var index = 0; index < 64; index++) rows.ReadCurrent();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var value = rows.ReadCurrent();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(length, value.Payload!.Length);
        Assert.InRange(allocated, length, length + 256L);
    }

    [Fact]
    public void BindingRejectsWrongShapeContractsAndReaderWidth()
    {
        using var fixture = new SqliteTypedRowFixture();
        var original = fixture.Shape.Graph.GetShape(fixture.Shape.ShapeId);
        Shape changed = new(original.Id,
            [.. original.Fields.Select(field => field.Name.Value == nameof(SqliteTypedRowFixture.Row.Payload)
                ? new FieldDefinition(field.Name, new ScalarTypeRef(ScalarTypeKind.String), presence: field.Presence,
                    nullability: field.Nullability) : field)]);
        ShapeGraph graph = new(fixture.Shape.Graph.Id, [changed]);
        Assert.Throws<ArgumentException>(() => fixture.Artifact.CreateRowMapping<SqliteTypedRowFixture.Row>(new(graph, changed.Id)));
        using var command = fixture.Database.CreateCommand(fixture.Connection, null, "SELECT 1");
        using var reader = command.ExecuteReader();
        Assert.Throws<ArgumentException>(() => fixture.Mapping.Bind(reader));
    }

    [Fact]
    public void MappingIsSharedButReadersAndMutableResultsAreIndependent()
    {
        using var fixture = new SqliteTypedRowFixture();
        using var firstCommand = fixture.CreateCommand();
        using var secondCommand = fixture.CreateCommand();
        using var first = firstCommand.ExecuteReader();
        using var second = secondCommand.ExecuteReader();
        var a = fixture.Mapping.Bind(first);
        var b = fixture.Mapping.Bind(second);
        Assert.Same(a.Layout, b.Layout);
        Assert.True(first.Read());
        Assert.True(second.Read());
        var value = a.ReadCurrent();
        value.Payload![0] = 1;
        Assert.Equal(0, b.ReadCurrent().Payload![0]);
        Assert.True(first.Read());
        Assert.Equal(2, a.ReadCurrent().Id);
        Assert.Equal(1, b.ReadCurrent().Id);
    }

    [Theory]
    [InlineData("presence", "2", false)]
    [InlineData("presence", "0", false)]
    [InlineData("payload", "'text'", false)]
    [InlineData("binding", "0", false)]
    [InlineData("required", "0", false)]
    [InlineData("absent", "0", true)]
    public void PresenceAndStorageViolationsFailWithoutCollapsingWholeBindingAbsence(string target, string replacement, bool absent)
    {
        using var fixture = new SqliteTypedRowFixture();
        var artifact = fixture.Artifact;
        var payload = artifact.ResultFields.Single(field => field.Field.Path.ToString() == nameof(SqliteTypedRowFixture.Row.Payload));
        // Build a malformed native row using the artifact's layout, without altering schema contracts or SQL compilation.
        using var command = fixture.CreateCommand();
        using var original = command.ExecuteReader();
        Assert.True(original.Read());
        using var malformed = fixture.Connection.CreateCommand();
        var columns = new string[original.FieldCount];
        for (var index = 0; index < columns.Length; index++)
        {
            var name = "$value" + index;
            malformed.Parameters.AddWithValue(name, original.GetValue(index));
            columns[index] = name;
        }
        if (target == "presence") columns[payload.PresenceOrdinal] = replacement;
        if (target == "payload") columns[payload.ValueOrdinal] = replacement;
        if (target is "binding" or "absent") columns[artifact.BindingPresenceOrdinal] = replacement;
        if (target == "required")
        {
            var required = artifact.ResultFields.Single(field => field.Field.Path.ToString() == nameof(SqliteTypedRowFixture.Row.Id));
            columns[required.PresenceOrdinal] = "0";
            columns[required.ValueOrdinal] = "NULL";
        }
        if (absent)
        {
            foreach (var field in artifact.ResultFields)
            {
                columns[field.PresenceOrdinal] = "0";
                columns[field.ValueOrdinal] = "NULL";
            }
        }
        malformed.CommandText = "SELECT " + string.Join(", ", columns);
        using var reader = malformed.ExecuteReader();
        var rows = fixture.Mapping.Bind(reader);
        Assert.True(reader.Read());
        if (absent)
        {
            Assert.False(rows.TryReadCurrent(out var value));
            Assert.Null(value);
            Assert.Throws<InvalidOperationException>(() => rows.ReadCurrent());
        }
        else if (target == "payload")
        {
            Assert.Throws<ArgumentException>(() => rows.ReadCurrent());
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => rows.ReadCurrent());
        }
    }

    [Fact]
    public void OrdinalBoundsAndConfiguredMissingPolicyRemainExplicit()
    {
        using var fixture = new SqliteTypedRowFixture();
        var mapping = fixture.Artifact.CreateRowMapping<SqliteTypedRowFixture.Row>(fixture.Shape,
            configure: builder => builder.WithMissingFieldBehavior(ObservationMissingFieldBehavior.Throw));
        using var command = fixture.CreateCommand();
        using var reader = command.ExecuteReader();
        var rows = mapping.Bind(reader);
        Assert.False(rows.TryGetField(-1, out _));
        Assert.False(rows.TryGetField(fixture.Artifact.ResultFields.Length, out _));
        Assert.False(rows.TryGetBytes(-1, out _));
        Assert.False(rows.TryGetField("unknown", out _));
        Assert.True(reader.Read());
        Assert.NotNull(rows.ReadCurrent().Payload);
        Assert.True(reader.Read());
        Assert.Null(rows.ReadCurrent().Payload); // Explicit null still passes strict missing-field policy.
        Assert.True(reader.Read());
        Assert.Throws<InvalidOperationException>(() => rows.ReadCurrent());
    }
}
