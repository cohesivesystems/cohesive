using System.Collections.Immutable;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Storage;

namespace Cohesive.Tests.Model;

public sealed class InMemoryEntityRelationQuerySourceReaderTests
{
    static readonly GraphId Graph = new("tests/in-memory-entity-reader/v1");
    static readonly QualifiedShapeId Shape = new(Graph, SampleEntity.Instance.Definition.Shape.Id);
    static readonly FieldPath NamePath = FieldPath.FromField("Name");
    static readonly FieldPath CustomerIdsPath = FieldPath.FromField("CustomerIds");
    static readonly FieldPath VersionPath = FieldPath.FromField("SourceEntityVersion");

    [Fact]
    public async Task ObservationVersionProjection_UsesSnapshotMetadataAndChangesConventionalSourceIdentity()
    {
        var projected = CreateFixture(
            snapshots:
            [
                VersionedSnapshot(
                    "entity-a",
                    version: 7,
                    ("SourceEntityVersion", ObservationValue.FromInt64(999)))
            ],
            observationVersionSemanticPath: VersionPath);
        var payload = CreateFixture(
            snapshots:
            [
                VersionedSnapshot(
                    "entity-a",
                    version: 7,
                    ("SourceEntityVersion", ObservationValue.FromInt64(999)))
            ]);

        var projectedResult = await projected.Reader.ReadAsync(Request(
            projected,
            [SemanticField(projected, VersionPath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));
        var payloadResult = await payload.Reader.ReadAsync(Request(
            payload,
            [SemanticField(VersionPath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        Assert.Equal(VersionPath, projected.Registration.ObservationVersionSemanticPath);
        Assert.Equal(VersionPath, projected.Reader.ObservationVersionSemanticPath);
        Assert.Equal(
            EntityRelationQuerySourceRegistration.ObservationVersionSourceSelector,
            projected.Registration.FieldSourceSelector(VersionPath));
        Assert.Equal(7, projectedResult.Observations.Single().Fields.Single().Value!.Value.Int64);
        Assert.Equal(999, payloadResult.Observations.Single().Fields.Single().Value!.Value.Int64);
        Assert.NotEqual(projected.Registration.Source.Id, payload.Registration.Source.Id);
        Assert.Throws<ArgumentException>(() => EntityRelationQuerySourceRegistration.InMemory(
            Shape,
            new InMemoryEntityOutboxRepository(
                SampleEntity.Instance.Definition,
                partitionKeyFieldName: "PartitionKey"),
            RelationQueryLogicalPartitionIdentity.WholeSource,
            identitySourceSelector: EntityRelationQuerySourceRegistration.ObservationVersionSourceSelector,
            observationVersionSemanticPath: VersionPath));
    }

    [Fact]
    public async Task BoundedEnumeration_ProjectsExactFieldStatesAndDeterministicIdentityOrder()
    {
        var fixture = CreateFixture(
            snapshots:
            [
                PartialSnapshot(
                    "entity-d",
                    new HashSet<string>(["Other"], StringComparer.Ordinal),
                    ("Other", ObservationValue.FromString("loaded"))),
                Snapshot("entity-c", ("Other", ObservationValue.FromString("present"))),
                Snapshot("entity-b", ("Name", ObservationValue.Null)),
                Snapshot("entity-a", ("Name", ObservationValue.FromString("Alpha")))
            ]);
        var field = SemanticField(NamePath);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [field],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal(["entity-a", "entity-b", "entity-c", "entity-d"],
            result.Observations.Select(static row => row.Identity));
        Assert.Equal(RelationQuerySourceReadFieldState.Value, result.Observations[0].Fields.Single().State);
        Assert.Equal("Alpha", result.Observations[0].Fields.Single().Value!.Value.String);
        Assert.Equal(RelationQuerySourceReadFieldState.Null, result.Observations[1].Fields.Single().State);
        Assert.Equal(RelationQuerySourceReadFieldState.Missing, result.Observations[2].Fields.Single().State);
        Assert.Equal(RelationQuerySourceReadFieldState.Inconclusive, result.Observations[3].Fields.Single().State);
        Assert.Contains("snapshot/", result.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedEnumeration_ReturnsAttributablePartialRowsWhenBounded()
    {
        var fixture = CreateFixture(
            snapshots:
            [
                Snapshot("entity-c", ("Name", ObservationValue.FromString("Charlie"))),
                Snapshot("entity-a", ("Name", ObservationValue.FromString("Alpha"))),
                Snapshot("entity-b", ("Name", ObservationValue.FromString("Beta")))
            ]);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 2)));

        Assert.Equal(RelationQuerySourceReadState.Partial, result.State);
        Assert.Equal(["entity-a", "entity-b"], result.Observations.Select(static row => row.Identity));
        Assert.Equal(RelationQueryEvidenceCompleteness.Partial, result.Completeness);
    }

    [Fact]
    public async Task Projection_ReportsUnsupportedElementPathAsFailedFieldEvidence()
    {
        var fixture = CreateFixture(
            snapshots: [Snapshot("entity-a", ("CustomerIds", ObservationValue.FromArray([ObservationValue.FromString("customer-1")])))]);
        var elementPath = new FieldPath([FieldPathSegment.ForField("CustomerIds"), FieldPathSegment.Element()]);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(elementPath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal(RelationQuerySourceReadFieldState.Failed, Assert.Single(result.Observations).Fields.Single().State);
    }

    [Fact]
    public async Task IdentityBatch_ReturnsCompleteSubsetNotFoundAndBoundaryEvidence()
    {
        var fixture = CreateFixture(
            snapshots:
            [
                Snapshot("entity-a", ("Name", ObservationValue.FromString("Alpha"))),
                Snapshot("entity-b", ("Name", ObservationValue.FromString("Beta")))
            ],
            limits: new(maximumBatchSize: 2, maximumBufferedRows: 10, maximumFanOut: 10, maximumConcurrency: 1));
        var fields = ImmutableArray.Create(SemanticField(NamePath));

        var subset = await fixture.Reader.ReadAsync(Request(
            fixture,
            fields,
            new RelationQueryIdentityBatchLookup(["entity-a", "entity-missing"])));
        var missing = await fixture.Reader.ReadAsync(Request(
            fixture,
            fields,
            new RelationQueryIdentityBatchLookup(["entity-missing"])));
        var beyondBatch = await fixture.Reader.ReadAsync(Request(
            fixture,
            fields,
            new RelationQueryIdentityBatchLookup(["entity-a", "entity-b", "entity-c"])));

        Assert.Equal(RelationQuerySourceReadState.Complete, subset.State);
        Assert.Equal("entity-a", Assert.Single(subset.Observations).Identity);
        Assert.Equal(RelationQuerySourceReadState.NotFound, missing.State);
        Assert.Empty(missing.Observations);
        Assert.Equal(RelationQuerySourceReadState.Inconclusive, beyondBatch.State);
        Assert.Contains("batch-boundary-exceeded", beyondBatch.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelationshipBatch_MatchesScalarAndCollectionReferencesAndReturnsCorrelationField()
    {
        var fixture = CreateFixture(
            snapshots:
            [
                Snapshot("entity-b", ("CustomerIds", ObservationValue.FromArray(
                    [ObservationValue.FromString("customer-2"), ObservationValue.FromString("customer-1")]))),
                Snapshot("entity-a", ("CustomerIds", ObservationValue.FromString("customer-1"))),
                Snapshot("entity-c", ("CustomerIds", ObservationValue.FromString("customer-3")))
            ]);
        var correlation = new RelationQuerySourceReadField(
            input: null,
            CustomerIdsPath,
            fixture.Registration.RelationshipKeySourceSelector(CustomerIdsPath),
            RelationQuerySourceReadFieldPurpose.Correlation);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [correlation],
            new RelationQueryRelationshipKeyBatchLookup(
                CustomerIdsPath,
                correlation.SourceSelector,
                ["customer-1"])));

        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal(["entity-a", "entity-b"], result.Observations.Select(static row => row.Identity));
        Assert.All(result.Observations, row =>
        {
            var field = Assert.Single(row.Fields);
            Assert.Equal(RelationQuerySourceReadFieldPurpose.Correlation, field.Field.Purpose);
            Assert.Equal(RelationQuerySourceReadFieldState.Value, field.State);
        });
    }

    [Fact]
    public async Task RelationshipBatch_FailsClosedForInconclusiveInvalidAndExcessiveFanOut()
    {
        var referenceSelector = new Func<ReaderFixture, string>(fixture =>
            fixture.Registration.RelationshipKeySourceSelector(CustomerIdsPath));
        var partial = CreateFixture(snapshots:
        [
            PartialSnapshot(
                "entity-a",
                new HashSet<string>(["Name"], StringComparer.Ordinal),
                ("Name", ObservationValue.FromString("loaded")))
        ]);
        var invalid = CreateFixture(snapshots:
        [
            Snapshot("entity-a", ("CustomerIds", ObservationValue.FromInt64(42)))
        ]);
        var excessive = CreateFixture(
            snapshots:
            [
                Snapshot("entity-a", ("CustomerIds", ObservationValue.FromString("customer-1"))),
                Snapshot("entity-b", ("CustomerIds", ObservationValue.FromString("customer-1")))
            ],
            limits: new(maximumBatchSize: 10, maximumBufferedRows: 10, maximumFanOut: 1, maximumConcurrency: 1));

        var partialResult = await partial.Reader.ReadAsync(RelationshipRequest(partial, referenceSelector(partial)));
        var invalidResult = await invalid.Reader.ReadAsync(RelationshipRequest(invalid, referenceSelector(invalid)));
        var excessiveResult = await excessive.Reader.ReadAsync(RelationshipRequest(excessive, referenceSelector(excessive)));

        Assert.Equal(RelationQuerySourceReadState.Inconclusive, partialResult.State);
        Assert.Equal(RelationQuerySourceReadState.Failed, invalidResult.State);
        Assert.Equal(RelationQuerySourceReadState.Inconclusive, excessiveResult.State);
        Assert.Empty(partialResult.Observations);
        Assert.Empty(invalidResult.Observations);
        Assert.Empty(excessiveResult.Observations);
    }

    [Fact]
    public async Task RelationshipBatch_ReturnsInconclusiveWhenOneReferenceExceedsNormalizationBoundary()
    {
        var fixture = CreateFixture(
            snapshots:
            [
                Snapshot(
                    "entity-a",
                    ("CustomerIds", ObservationValue.FromArray(
                    [
                        ObservationValue.FromString("customer-1"),
                        ObservationValue.FromString("customer-2"),
                        ObservationValue.FromString("customer-3")
                    ])))
            ],
            limits: new(maximumBatchSize: 2, maximumBufferedRows: 10, maximumFanOut: 10, maximumConcurrency: 1));
        var selector = fixture.Registration.RelationshipKeySourceSelector(CustomerIdsPath);

        var result = await fixture.Reader.ReadAsync(RelationshipRequest(fixture, selector));

        Assert.Equal(RelationQuerySourceReadState.Inconclusive, result.State);
        Assert.Empty(result.Observations);
        Assert.Contains("relationship-reference-key-boundary-exceeded", result.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectorValidation_InvokesOnlyPoliciesRequiredByEachFieldPurpose()
    {
        var semanticFixture = CreateFixture(
            snapshots: [Snapshot("entity-a", ("Name", ObservationValue.FromString("Alpha")))],
            relationshipKeySourceSelector: static _ => throw new InvalidOperationException("Unused relationship selector."));
        var correlationFixture = CreateFixture(
            snapshots: [Snapshot("entity-a", ("CustomerIds", ObservationValue.FromString("customer-1")))],
            fieldSourceSelector: static _ => throw new InvalidOperationException("Unused field selector."));
        var correlationSelector = correlationFixture.Registration.RelationshipKeySourceSelector(CustomerIdsPath);

        var semantic = await semanticFixture.Reader.ReadAsync(Request(
            semanticFixture,
            [SemanticField(NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));
        var correlation = await correlationFixture.Reader.ReadAsync(
            RelationshipRequest(correlationFixture, correlationSelector));

        Assert.Equal(RelationQuerySourceReadState.Complete, semantic.State);
        Assert.Equal(RelationQuerySourceReadState.Complete, correlation.State);
    }

    [Fact]
    public async Task SelectorValidation_PropagatesCancellationFromRequiredPolicy()
    {
        var fixture = CreateFixture(
            snapshots: [Snapshot("entity-a", ("Name", ObservationValue.FromString("Alpha")))],
            fieldSourceSelector: static _ => throw new OperationCanceledException("Selector canceled."));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Reader.ReadAsync(Request(
                fixture,
                [SemanticField(NamePath)],
                new RelationQueryBoundedEnumeration(maximumRows: 10))));
    }

    [Fact]
    public async Task Reader_RejectsAffinityMismatchesDuplicateIdentitiesAndCancellation()
    {
        var fixture = CreateFixture(
            snapshots:
            [
                SnapshotInPartition("shared", "tenant-a", ("Name", ObservationValue.FromString("Alpha"))),
                SnapshotInPartition("shared", "tenant-b", ("Name", ObservationValue.FromString("Beta")))
            ]);
        var request = Request(
            fixture,
            [SemanticField(NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var wrongSource = new RelationQuerySourceReadRequest(
            request.PhysicalPlan,
            request.Stage,
            request.PlacementBinding,
            new("foreign-source"),
            request.Shape,
            request.IdentitySelector,
            request.Fields,
            request.Constraint,
            request.MaximumBufferedRows);

        var duplicate = await fixture.Reader.ReadAsync(request);
        var mismatch = await fixture.Reader.ReadAsync(wrongSource);
        using CancellationTokenSource canceled = new();
        await canceled.CancelAsync();

        Assert.Equal(RelationQuerySourceReadState.Failed, duplicate.State);
        Assert.Contains("duplicate-observation-identity", duplicate.EvidenceReference, StringComparison.Ordinal);
        Assert.Equal(RelationQuerySourceReadState.Failed, mismatch.State);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await fixture.Reader.ReadAsync(request, canceled.Token));
    }

    static RelationQuerySourceReadRequest RelationshipRequest(ReaderFixture fixture, string selector) => Request(
        fixture,
        [new RelationQuerySourceReadField(
            input: null,
            CustomerIdsPath,
            selector,
            RelationQuerySourceReadFieldPurpose.Correlation)],
        new RelationQueryRelationshipKeyBatchLookup(CustomerIdsPath, selector, ["customer-1"]));

    static RelationQuerySourceReadField SemanticField(FieldPath path) => new(
        new RelationQueryInputId($"field/{Uri.EscapeDataString(path.ToString())}"),
        path,
        path.ToString(),
        RelationQuerySourceReadFieldPurpose.SemanticInput);

    static RelationQuerySourceReadField SemanticField(ReaderFixture fixture, FieldPath path) => new(
        new RelationQueryInputId($"field/{Uri.EscapeDataString(path.ToString())}"),
        path,
        fixture.Registration.FieldSourceSelector(path),
        RelationQuerySourceReadFieldPurpose.SemanticInput);

    static RelationQuerySourceReadRequest Request(
        ReaderFixture fixture,
        ImmutableArray<RelationQuerySourceReadField> fields,
        RelationQuerySourceReadConstraint constraint,
        long maximumBufferedRows = 100) => new(
        new("sha256", "tests/canonicalization-v1", "0123456789abcdef"),
        new("read/source"),
        new("placement/source"),
        fixture.Registration.Source.Id,
        fixture.Registration.Shape,
        fixture.Registration.IdentitySourceSelector,
        fields,
        constraint,
        maximumBufferedRows);

    static ReaderFixture CreateFixture(
        ImmutableArray<EntitySnapshot> snapshots,
        RelationQuerySourcePlacementLimits? limits = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        RelationQueryPlacementFieldSelector? relationshipKeySourceSelector = null,
        FieldPath? observationVersionSemanticPath = null)
    {
        var repository = new InMemoryEntityOutboxRepository(
            SampleEntity.Instance.Definition,
            partitionKeyFieldName: "PartitionKey",
            seedSnapshots: snapshots);
        var registration = EntityRelationQuerySourceRegistration.InMemory(
            Shape,
            repository,
            RelationQueryLogicalPartitionIdentity.WholeSource,
            limits: limits,
            fieldSourceSelector: fieldSourceSelector,
            relationshipKeySourceSelector: relationshipKeySourceSelector,
            observationVersionSemanticPath: observationVersionSemanticPath);
        return new(
            registration,
            Assert.IsType<InMemoryEntityRelationQuerySourceReader>(registration.Reader));
    }

    static EntitySnapshot Snapshot(
        string id,
        params (string Name, ObservationValue Value)[] fields) =>
        Snapshot(id, fields, "tenant-a", loadedFields: null);

    static EntitySnapshot PartialSnapshot(
        string id,
        IReadOnlySet<string> loadedFields,
        params (string Name, ObservationValue Value)[] fields) =>
        Snapshot(id, fields, "tenant-a", loadedFields);

    static EntitySnapshot SnapshotInPartition(
        string id,
        string partitionKey,
        params (string Name, ObservationValue Value)[] fields) =>
        Snapshot(id, fields, partitionKey, loadedFields: null);

    static EntitySnapshot VersionedSnapshot(
        string id,
        long version,
        params (string Name, ObservationValue Value)[] fields) => new(
        new(
            SampleEntity.Instance.Definition.Shape.Id,
            id,
            fields.ToDictionary(static field => field.Name, static field => field.Value, StringComparer.Ordinal),
            version),
        "tenant-a",
        new($"seed/tenant-a/{id}"));

    static EntitySnapshot Snapshot(
        string id,
        IEnumerable<(string Name, ObservationValue Value)> fields,
        string partitionKey,
        IReadOnlySet<string>? loadedFields) => new(
        new(
            SampleEntity.Instance.Definition.Shape.Id,
            id,
            fields.ToDictionary(static field => field.Name, static field => field.Value, StringComparer.Ordinal)),
        partitionKey,
        new($"seed/{partitionKey}/{id}"),
        loadedFields);

    sealed record ReaderFixture(
        EntityRelationQuerySourceRegistration Registration,
        InMemoryEntityRelationQuerySourceReader Reader);

    sealed class SampleEntity : Entity<SampleEntity>
    {
        public SampleEntity()
            : base(nameof(SampleEntity))
        {
            PartitionKey = WriteOnceField<string>(nameof(PartitionKey));
            Name = WriteOnceField<string>(nameof(Name));
            CustomerIds = WriteOnceField<string[]>(nameof(CustomerIds));
            SourceEntityVersion = WriteOnceField<long>(nameof(SourceEntityVersion));
        }

        public Field<string> PartitionKey { get; }

        public Field<string> Name { get; }

        public Field<string[]> CustomerIds { get; }

        public Field<long> SourceEntityVersion { get; }
    }
}
