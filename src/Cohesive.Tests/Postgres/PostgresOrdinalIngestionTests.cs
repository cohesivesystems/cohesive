using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresOrdinalIngestionTests
{
    sealed class NativeFactAttribute : FactAttribute
    {
        public NativeFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")))
                Skip = "Requires disposable PostgreSQL via COHESIVE_POSTGRES_TEST_CONNECTION_STRING.";
        }
    }

    [NativeFact]
    public void NativeOrdinalAdmissionPreservesProvenanceValidationAndOwnedPayloads()
    {
        using var fixture = new PostgresOrdinalIngestionFixture(Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!, 3);
        using var command = fixture.CreateCommand();
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var selected = ImmutableHashSet.Create("Id", "Amount");
        var ordinal = fixture.Repository.ReadSnapshot(reader, selected, ordinalStorage: true);
        var canonical = fixture.Repository.ReadSnapshot(reader, selected, ordinalStorage: false);
        Assert.Equal("row-1", ordinal.Entity.EntityId.Value);
        Assert.Equal(1, ordinal.Entity.Version);
        Assert.Equal("tenant", ordinal.PartitionKey);
        Assert.Equal(reader.GetFieldValue<uint>(5).ToString(System.Globalization.CultureInfo.InvariantCulture), ordinal.ConcurrencyToken.Value);
        Assert.Equal(canonical.Entity.EntityId, ordinal.Entity.EntityId);
        Assert.Equal(canonical.Entity.Version, ordinal.Entity.Version);
        Assert.Equal(canonical.PartitionKey, ordinal.PartitionKey);
        Assert.Equal(canonical.ConcurrencyToken, ordinal.ConcurrencyToken);
        Assert.Equal(canonical.LoadedFields, ordinal.LoadedFields);
        Assert.Equal(canonical.Entity.Observation.Value, ordinal.Entity.Observation.Value);
        Assert.IsAssignableFrom<IOrdinalObservationFieldReader>(ordinal.Entity.Observation.Fields);
        var dto = fixture.Materializer.Materialize(ordinal.Entity.Observation);
        var original = dto.Payload![0];
        dto.Payload[0] ^= 0xff;
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        var nullRow = fixture.Repository.ReadSnapshot(reader, null);
        Assert.Equal(ObservationValueKind.Null, nullRow.Entity.Observation.GetField("Payload").Kind);
        reader.Dispose();
        Assert.Equal(original, fixture.Materializer.Materialize(ordinal.Entity.Observation).Payload![0]);

        using var invalid = fixture.Connection.CreateCommand();
        invalid.CommandText = "SELECT 'bad', 'tenant', NULL::numeric, NULL::bytea, 1::bigint, '1'::xid";
        using var bad = invalid.ExecuteReader();
        Assert.True(bad.Read());
        Assert.Throws<ArgumentException>(() => fixture.Repository.ReadSnapshot(bad, null, ordinalStorage: true));
        Assert.Throws<ArgumentException>(() => fixture.Repository.ReadSnapshot(bad, null, ordinalStorage: false));
    }
}
