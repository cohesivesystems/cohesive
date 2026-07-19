using Cohesive.Adapters.Cosmos;
using Cohesive.Relations.Model;
using Cohesive.Storage;

namespace Cohesive.Tests.Model;

public sealed class EntityRepositoryContractsTests
{
    [Fact]
    public void EntityReadOptions_WithExpectedConcurrencyToken_StoresValue()
    {
        var options = new EntityReadOptions(expectedConcurrencyToken: new("etag-7"));

        Assert.Equal(new EntityConcurrencyToken("etag-7"), options.ExpectedConcurrencyToken);
    }

    [Fact]
    public void EntityReadOptions_WithoutExpectedConcurrencyToken_KeepsNull()
    {
        var options = new EntityReadOptions();

        Assert.Null(options.ExpectedConcurrencyToken);
    }

    [Fact]
    public void EntityReadOptions_WithFieldSelection_StoresSelection()
    {
        var selection = FieldSelection.ForFields("Name", "Name");
        var options = new EntityReadOptions(fieldSelection: selection);

        Assert.Same(selection, options.FieldSelection);
        Assert.Equal(["Name"], options.Fields);
    }

    [Fact]
    public void FieldSelection_WithWhitespaceField_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() => FieldSelection.ForFields("Name", " "));

        Assert.Contains("must not be null, empty, or whitespace", error.Message);
    }

    [Fact]
    public void CosmosEntityOutboxRepository_ValidateReadPreconditions_WithExpectedConcurrencyTokenMismatch_ThrowsConcurrencyConflict()
    {
        var document = CreateDocument(
            observationId: "obs-1",
            version: 7,
            etag: "etag-actual");

        var error = Assert.Throws<ObservationConcurrencyConflictException>(() =>
            CosmosEntityOutboxRepository.ValidateReadPreconditions(
                entityType: "Sample",
                id: "obs-1",
                document: document,
                read: new EntityReadOptions(expectedConcurrencyToken: new("etag-expected"))));

        Assert.Contains("expected ETag 'etag-expected' but found 'etag-actual'", error.Message);
    }

    [Fact]
    public void CosmosEntityOutboxRepository_ValidateReadPreconditions_WithExpectedConcurrencyTokenMatch_DoesNotThrow()
    {
        var document = CreateDocument(
            observationId: "obs-1",
            version: 7,
            etag: "etag-7");

        CosmosEntityOutboxRepository.ValidateReadPreconditions(
            entityType: "Sample",
            id: "obs-1",
            document: document,
            read: new EntityReadOptions(expectedConcurrencyToken: new("etag-7")));
    }

    [Fact]
    public void CosmosObservationOutboxRepositoryOptions_RequireDistinctNonemptyDocumentKinds()
    {
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new() { EntityDocumentKind = " " }));
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new() { OutboxDocumentKind = string.Empty }));
        Assert.Throws<ArgumentException>(() =>
            CosmosObservationOutboxRepositoryOptions.RequireValid(new()
            {
                EntityDocumentKind = "document",
                OutboxDocumentKind = "document"
            }));

        var options = new CosmosObservationOutboxRepositoryOptions
        {
            EntityDocumentKind = "entity-v2",
            OutboxDocumentKind = "outbox-v2"
        };
        Assert.Same(options, CosmosObservationOutboxRepositoryOptions.RequireValid(options));
    }

    static CosmosObservationContainerDocument CreateDocument(
        string observationId,
        long version,
        string? etag) =>
        new(
            Id: observationId,
            PartitionKey: observationId,
            DocumentKind: "entity",
            ObservationType: "Sample",
            ObservationId: observationId,
            ObservationVersion: version,
            Observation: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["Name"] = ObservationValue.FromString("alpha")
            },
            ETag: etag);
}
