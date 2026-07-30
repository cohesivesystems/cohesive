using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresRelationQueryBindingAuthoringTests
{
    static readonly FieldPath IdPath = FieldPath.FromField("id");
    static readonly FieldPath StatusPath = FieldPath.FromField("status");
    static readonly JsonSerializerOptions JsonOptions = RelationQueryJsonSerializer.CreateOptions();
    static readonly PostgresRelationQueryTextOrderingDomainEvidence AsciiOrderingDomain = new(
        validatedConstraintName: "ck_load_text_ascii",
        authority: "tests/postgres/text-order-domain/v1");
    static readonly PostgresRelationQueryTextSemantics OrdinalText = new(
        "C",
        PostgresRelationQueryTextEqualitySemantics.Ordinal,
        PostgresRelationQueryTextOrderingSemantics.Ordinal,
        AsciiOrderingDomain);
    static readonly PostgresRelationQueryColumnOptions IdOptions = new(
        scalarType: PostgresRelationQueryScalarType.Text,
        textSemantics: OrdinalText,
        ordering: PostgresRelationQueryOrderingCapability.Exact
            | PostgresRelationQueryOrderingCapability.StableUnique);
    static readonly PostgresRelationQueryColumnOptions StatusOptions = new(
        scalarType: PostgresRelationQueryScalarType.Text,
        textSemantics: OrdinalText);

    [Fact]
    public void Build_TypedAndStructuralExactBindingsProduceEquivalentArtifacts()
    {
        var fixture = CreateFixture();
        PostgresRelationQueryBindingId id = new("tests/postgres-binding/differential/v1");
        var typed = ConfigureTyped(fixture, id).Build().RequireValue();
        var structural = PostgresRelationQueryBinding.For(
                fixture.AuthoredPlacement,
                explicitAuthority: "tests/postgres-binding/local/v1")
            .Database(new("tests/postgres/primary"))
            .WithId(id)
            .Table(
                (RelationQueryPlacedInput)fixture.Placed,
                "loads_read",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(IdPath, "load_id", IdOptions)
                    .Column(StatusPath, "load_status", StatusOptions)
                    .Identity(IdPath, "load_id", IdOptions))
            .Build()
            .RequireValue();

        Assert.Equal(typed.Fingerprint, structural.Fingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(typed, JsonOptions),
            JsonSerializer.Serialize(structural, JsonOptions));
        Assert.Equal(fixture.AuthoredPlacement.Placement.Fingerprint, typed.PlacementFingerprint);
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(fixture.Plan)),
            typed.CompiledPlanFingerprint);
    }

    [Fact]
    public void Build_ConventionAndExplicitMappingsRetainEffectiveProvenance()
    {
        var fixture = CreateFixture();
        var convention = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Database(new("tests/postgres/primary"))
            .Table(fixture.Placed, "loads")
            .Build()
            .RequireValue();
        var explicitBinding = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Database(new("tests/postgres/primary"))
            .Table(
                fixture.Placed,
                "loads",
                table => table
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "load_id")
                    .Column(load => load.Status, "load_status")
                    .Identity(load => load.Id, "load_id"))
            .Build()
            .RequireValue();

        var conventionTable = Assert.Single(convention.Tables);
        var explicitTable = Assert.Single(explicitBinding.Tables);
        Assert.Equal("id", conventionTable.ResolveField(fixture.Placed.GetField(load => load.Id).Input.Id).ColumnName);
        Assert.Equal("status", conventionTable.ResolveField(fixture.Placed.GetField(load => load.Status).Input.Id).ColumnName);
        Assert.Equal("load_id", explicitTable.ResolveField(fixture.Placed.GetField(load => load.Id).Input.Id).ColumnName);
        Assert.Equal("load_status", explicitTable.ResolveField(fixture.Placed.GetField(load => load.Status).Input.Id).ColumnName);

        Assert.Equal(
            EffectiveConfigurationOrigin.AdapterConvention,
            FieldColumnDecision(convention, fixture.Placed.GetField(load => load.Id).Input.Id).Origin);
        Assert.Equal(
            EffectiveConfigurationOrigin.Explicit,
            FieldColumnDecision(explicitBinding, fixture.Placed.GetField(load => load.Id).Input.Id).Origin);
    }

    [Fact]
    public void Build_PartialColumnOptionsRetainPerSettingProvenance()
    {
        var fixture = CreateFixture();
        var binding = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Table(
                fixture.Placed,
                "loads",
                table => table.Column(
                    load => load.Status,
                    "load_status",
                    new(scalarType: PostgresRelationQueryScalarType.Text)))
            .Build()
            .RequireValue();
        var input = fixture.Placed.GetField(load => load.Status).Input.Id;

        Assert.Equal(
            EffectiveConfigurationOrigin.Explicit,
            FieldDecision(binding, input, "scalarType").Origin);
        Assert.Equal(
            EffectiveConfigurationOrigin.AdapterConvention,
            FieldDecision(binding, input, "missingValueEncoding").Origin);
        Assert.Equal(
            EffectiveConfigurationOrigin.AdapterConvention,
            FieldDecision(binding, input, "nullValueEncoding").Origin);
        Assert.Equal(
            EffectiveConfigurationOrigin.AdapterConvention,
            FieldDecision(binding, input, "textSemantics").Origin);
        Assert.Equal(
            EffectiveConfigurationOrigin.AdapterConvention,
            FieldDecision(binding, input, "ordering").Origin);
    }

    [Fact]
    public void Build_IncompatibleExplicitScalarTypeFailsClosed()
    {
        var fixture = CreateFixture();
        var result = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Table(
                fixture.Placed,
                "loads",
                table => table.Column(
                    load => load.Status,
                    "load_status",
                    new(scalarType: PostgresRelationQueryScalarType.Boolean)))
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorInvalid
            && diagnostic.SemanticPath == StatusPath);
    }

    [Fact]
    public void Build_DerivedIdentityChangesWithNormalizedFieldAndValueFacts()
    {
        var fixture = CreateFixture();
        var first = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Table(
                fixture.Placed,
                "loads",
                table => table.Column(load => load.Status, "status_a"))
            .Build()
            .RequireValue();
        var differentColumn = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Table(
                fixture.Placed,
                "loads",
                table => table.Column(load => load.Status, "status_b"))
            .Build()
            .RequireValue();
        var differentOrderingEvidence = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Table(
                fixture.Placed,
                "loads",
                table => table.Column(
                    load => load.Status,
                    "status_a",
                    new(ordering: PostgresRelationQueryOrderingCapability.StableUnique)))
            .Build()
            .RequireValue();

        Assert.NotEqual(first.Id, differentColumn.Id);
        Assert.NotEqual(first.Id, differentOrderingEvidence.Id);
    }

    [Fact]
    public void Build_UnusedExplicitColumnAndRelationshipSelectorsFailClosed()
    {
        var fixture = CreateFixture();
        var unknownPath = FieldPath.FromField("statuz");
        RelationQueryInputId unknownTraversal = new("tests/postgres/unknown-traversal");
        var result = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Table(
                fixture.Placed,
                "loads",
                table => table
                    .Column(unknownPath, "unused_status")
                    .RelationshipReference(unknownTraversal, IdPath, "unused_reference"))
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorUnknown
            && diagnostic.SemanticPath == unknownPath);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == PostgresRelationQueryBindingAuthoringDiagnosticCodes.SelectorUnknown
            && diagnostic.Input == unknownTraversal);
    }

    [Fact]
    public void Fingerprint_StructurallyEncodesFieldPathSegments()
    {
        var embeddedSeparator = FieldPath.FromField("customer.name");
        var navigated = new FieldPath(
        [
            FieldPathSegment.ForField("customer"),
            FieldPathSegment.ForField("name")
        ]);
        var first = CreateLowLevelBinding(embeddedSeparator);
        var second = CreateLowLevelBinding(navigated);

        Assert.Equal(embeddedSeparator.ToString(), navigated.ToString());
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Build_IncompleteAndAmbiguousMappingsFailClosedWithStructuredDiagnostics()
    {
        var fixture = CreateFixture();
        var incomplete = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Table(
                fixture.Placed,
                "loads",
                table => table
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "load_id")
                    .Identity(load => load.Id, "load_id"))
            .Build();
        var ambiguous = PostgresRelationQueryBinding.For(fixture.AuthoredPlacement)
            .Table(fixture.Placed, "loads")
            .Table(fixture.Placed, "loads_copy")
            .Build();

        Assert.False(incomplete.IsSuccess);
        Assert.Contains(incomplete.Diagnostics, diagnostic =>
            diagnostic.Code == PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing
            && diagnostic.SemanticPath == StatusPath);
        Assert.False(ambiguous.IsSuccess);
        Assert.Contains(ambiguous.Diagnostics, static diagnostic =>
            diagnostic.Code == PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingDuplicate);
    }

    [Fact]
    public void Build_PhysicalOnlyPlacementIdentityRequiresExplicitSemanticPath()
    {
        var fixture = CreateFixture();
        var placementBuilder = RelationQueryPlacement.For(fixture.Plan);
        var source = placementBuilder.Source(
            "tests/postgres/physical-only-identity",
            PostgresRelationQuerySourceTargetProfile.Default);
        var handle = placementBuilder.Place(
                Assert.Single(fixture.Plan.InputContract.Sources),
                source,
                fixture.Placed.ClrShape)
            .Identity("document_key")
            .FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var placed = placement.GetInput(handle);

        var result = PostgresRelationQueryBinding.For(placement)
            .Table(placed, "loads")
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == PostgresRelationQueryBindingAuthoringDiagnosticCodes.BindingMissing
            && diagnostic.Message.Contains("semantic identity path", StringComparison.Ordinal));
    }

    [Fact]
    public void Persistence_RoundTripPreservesFingerprintAndRejectsStaleAffinity()
    {
        var fixture = CreateFixture();
        var binding = ConfigureTyped(
                fixture,
                new("tests/postgres-binding/persistence/v1"))
            .Build()
            .RequireValue();

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<PostgresRelationQueryStorageBinding>(json, JsonOptions);

        Assert.NotNull(rehydrated);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        Assert.Equal(
            PostgresRelationQueryStorageBinding.CanonicalDatabaseSemanticsProfile,
            rehydrated.DatabaseSemanticsProfile);
        Assert.Equal(binding.CompiledPlanFingerprint, rehydrated.CompiledPlanFingerprint);
        Assert.Equal(binding.PlacementFingerprint, rehydrated.PlacementFingerprint);
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));

        var stalePlan = new RelationQueryPlanComponentFingerprint(
            "sha256",
            "tests/stale-plan-c14n/v1",
            "stale-plan");
        var stalePlacement = new RelationQuerySourcePlacementFingerprint(
            "sha256",
            "tests/stale-placement-c14n/v1",
            "stale-placement");
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryStorageBinding(
            binding.SchemaVersion,
            binding.Fingerprint,
            binding.DatabaseSemanticsProfile,
            binding.Id,
            binding.Database,
            binding.Target,
            binding.TargetProfile,
            binding.Tables,
            binding.Origin,
            binding.ConventionSetVersion,
            binding.ConfigurationDecisions,
            stalePlan,
            stalePlacement));
    }

    [Fact]
    public void RelationshipKeyBindings_RequireExactNumericAndTemporalDomainEvidence()
    {
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryIdentityBinding(
            IdPath,
            "numeric_id",
            PostgresRelationQueryScalarType.Numeric));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryRelationshipReferenceBinding(
            new("tests/postgres/temporal-reference"),
            StatusPath,
            "effective_at",
            PostgresRelationQueryScalarType.Timestamp,
            SourceReferenceUniqueness.NotGuaranteed,
            PostgresRelationQueryMissingValueEncoding.Prohibited,
            PostgresRelationQueryNullValueEncoding.Prohibited));
    }

    [Fact]
    public void RelationshipKeyDomainEvidence_RoundTripsAndParticipatesInFingerprint()
    {
        var binding = CreateKeyEvidenceBinding(
            numericAuthority: "tests/postgres/numeric-domain/v1",
            temporalAuthority: "tests/postgres/temporal-domain/v1");
        var changedNumeric = CreateKeyEvidenceBinding(
            numericAuthority: "tests/postgres/numeric-domain/v2",
            temporalAuthority: "tests/postgres/temporal-domain/v1");
        var changedTemporal = CreateKeyEvidenceBinding(
            numericAuthority: "tests/postgres/numeric-domain/v1",
            temporalAuthority: "tests/postgres/temporal-domain/v2");

        Assert.NotEqual(binding.Fingerprint, changedNumeric.Fingerprint);
        Assert.NotEqual(binding.Fingerprint, changedTemporal.Fingerprint);

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<PostgresRelationQueryStorageBinding>(json, JsonOptions);
        Assert.NotNull(rehydrated);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        var table = Assert.Single(rehydrated.Tables);
        Assert.Equal("tests/postgres/numeric-domain/v1", table.Identity?.NumericDomain?.Authority);
        Assert.Equal(
            "tests/postgres/temporal-domain/v1",
            Assert.Single(table.RelationshipReferences).TemporalDomain?.Authority);
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));

        var tampered = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        var persistedTable = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(tampered["tables"])));
        Assert.IsType<JsonObject>(persistedTable["identity"]!["numericDomain"])["authority"] =
            "tests/postgres/tampered-domain/v1";
        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<PostgresRelationQueryStorageBinding>(tampered.ToJsonString(), JsonOptions));
    }

    [Fact]
    public void TextOrderingEvidence_RequiresConstrainedDomainWhileEqualityRemainsIndependent()
    {
        var equalityOnly = new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal);

        Assert.Equal(PostgresRelationQueryTextEqualitySemantics.Ordinal, equalityOnly.Equality);
        Assert.Equal(PostgresRelationQueryTextOrderingSemantics.Unspecified, equalityOnly.Ordering);
        Assert.Null(equalityOnly.OrderingDomain);
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            PostgresRelationQueryTextOrderingSemantics.Ordinal));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryTextSemantics(
            "en_US",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            PostgresRelationQueryTextOrderingSemantics.Ordinal,
            AsciiOrderingDomain));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            orderingDomain: AsciiOrderingDomain));
        Assert.Throws<ArgumentException>(() => new PostgresRelationQueryTextOrderingDomainEvidence(
            "ck_load_text_ascii",
            "tests/postgres/text-order-domain/v1",
            "tests/postgres/unsupported-text-order-domain/v1"));
    }

    [Fact]
    public void TextOrderingDomainEvidence_RoundTripsAndParticipatesInFingerprint()
    {
        var binding = CreateTextOrderingEvidenceBinding("tests/postgres/text-order-domain/v1");
        var changedAuthority = CreateTextOrderingEvidenceBinding("tests/postgres/text-order-domain/v2");

        Assert.NotEqual(binding.Fingerprint, changedAuthority.Fingerprint);

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var rehydrated = JsonSerializer.Deserialize<PostgresRelationQueryStorageBinding>(json, JsonOptions);
        Assert.NotNull(rehydrated);
        Assert.Equal(binding.Fingerprint, rehydrated.Fingerprint);
        var orderingDomain = Assert.Single(Assert.Single(rehydrated.Tables).Fields)
            .TextSemantics?.OrderingDomain;
        Assert.NotNull(orderingDomain);
        Assert.Equal("ck_text_value_ascii", orderingDomain.ValidatedConstraintName);
        Assert.Equal("tests/postgres/text-order-domain/v1", orderingDomain.Authority);
        Assert.Equal(
            PostgresRelationQueryTextOrderingDomainEvidence.CanonicalAsciiStrategy,
            orderingDomain.Strategy);
        Assert.Equal(json, JsonSerializer.Serialize(rehydrated, JsonOptions));

        var tampered = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        var persistedTable = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(tampered["tables"])));
        var persistedField = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(persistedTable["fields"])));
        Assert.IsType<JsonObject>(persistedField["textSemantics"]!["orderingDomain"])["authority"] =
            "tests/postgres/tampered-text-order-domain/v1";
        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<PostgresRelationQueryStorageBinding>(tampered.ToJsonString(), JsonOptions));
    }

    [Fact]
    public void Build_ForeignPlacedInputFailsClosedAgainstExactPlacementAffinity()
    {
        var current = CreateFixture(placementConventionSetVersion: "tests/placement/v1");
        var stale = CreateFixture(placementConventionSetVersion: "tests/placement/v2");

        var result = PostgresRelationQueryBinding.For(current.AuthoredPlacement)
            .Table(stale.Placed, "loads")
            .Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == PostgresRelationQueryBindingAuthoringDiagnosticCodes.PlacementMismatch);
    }

    static PostgresRelationQueryStorageBindingBuilder ConfigureTyped(
        Fixture fixture,
        PostgresRelationQueryBindingId id) =>
        PostgresRelationQueryBinding.For(
                fixture.AuthoredPlacement,
                explicitAuthority: "tests/postgres-binding/local/v1")
            .Database(new("tests/postgres/primary"))
            .WithId(id)
            .Table(
                fixture.Placed,
                "loads_read",
                table => table
                    .Schema("transport")
                    .ColumnsExplicitly()
                    .Column(load => load.Id, "load_id", IdOptions)
                    .Column(load => load.Status, "load_status", StatusOptions)
                    .Identity(load => load.Id, "load_id", IdOptions));

    static EffectiveConfigurationDecision FieldColumnDecision(
        PostgresRelationQueryStorageBinding binding,
        RelationQueryInputId input) =>
        FieldDecision(binding, input, "columnName");

    static EffectiveConfigurationDecision FieldDecision(
        PostgresRelationQueryStorageBinding binding,
        RelationQueryInputId input,
        string setting) =>
        Assert.Single(binding.ConfigurationDecisions, decision =>
            decision.Setting.EndsWith(
                $"/field/{Uri.EscapeDataString(input.Value)}/{setting}",
                StringComparison.Ordinal));

    static PostgresRelationQueryStorageBinding CreateLowLevelBinding(FieldPath semanticPath)
    {
        var field = new PostgresRelationQueryFieldBinding(
            new("tests/postgres/field"),
            semanticPath,
            "value",
            PostgresRelationQueryScalarType.Text,
            PostgresRelationQueryMissingValueEncoding.Prohibited,
            PostgresRelationQueryNullValueEncoding.Prohibited);
        var table = new PostgresRelationQueryTableBinding(
            new("tests/postgres/source"),
            new("tests/postgres/placement"),
            new("tests/postgres/input"),
            new(new("tests/postgres/graph"), new("tests/postgres/shape")),
            "public",
            "documents",
            identity: null,
            fields: [field]);
        return new(
            new("tests/postgres-binding/fingerprint/v1"),
            new("tests/postgres/database"),
            PostgresRelationQueryTargetProfile.Target,
            PostgresRelationQueryTargetProfile.ProfileId,
            [table]);
    }

    static PostgresRelationQueryStorageBinding CreateKeyEvidenceBinding(
        string numericAuthority,
        string temporalAuthority)
    {
        var identity = new PostgresRelationQueryIdentityBinding(
            IdPath,
            "numeric_id",
            PostgresRelationQueryScalarType.Numeric,
            numericDomain: new(
                precision: 28,
                scale: 4,
                validatedConstraintName: "ck_numeric_id_clr_decimal",
                authority: numericAuthority));
        var reference = new PostgresRelationQueryRelationshipReferenceBinding(
            new("tests/postgres/temporal-reference"),
            StatusPath,
            "effective_at",
            PostgresRelationQueryScalarType.Timestamp,
            SourceReferenceUniqueness.NotGuaranteed,
            PostgresRelationQueryMissingValueEncoding.Prohibited,
            PostgresRelationQueryNullValueEncoding.Prohibited,
            temporalDomain: new(
                validatedConstraintName: "ck_effective_at_clr_timestamp",
                authority: temporalAuthority));
        var table = new PostgresRelationQueryTableBinding(
            new("tests/postgres/source"),
            new("tests/postgres/placement"),
            new("tests/postgres/input"),
            new(new("tests/postgres/graph"), new("tests/postgres/shape")),
            "public",
            "relationship_keys",
            identity,
            fields: [],
            relationshipReferences: [reference]);
        return new(
            new("tests/postgres-binding/key-evidence/v1"),
            new("tests/postgres/database"),
            PostgresRelationQueryTargetProfile.Target,
            PostgresRelationQueryTargetProfile.ProfileId,
            [table]);
    }

    static PostgresRelationQueryStorageBinding CreateTextOrderingEvidenceBinding(string authority)
    {
        var text = new PostgresRelationQueryTextSemantics(
            "C",
            PostgresRelationQueryTextEqualitySemantics.Ordinal,
            PostgresRelationQueryTextOrderingSemantics.Ordinal,
            new(
                validatedConstraintName: "ck_text_value_ascii",
                authority: authority));
        var field = new PostgresRelationQueryFieldBinding(
            new("tests/postgres/text-value"),
            StatusPath,
            "text_value",
            PostgresRelationQueryScalarType.Text,
            PostgresRelationQueryMissingValueEncoding.Prohibited,
            PostgresRelationQueryNullValueEncoding.Prohibited,
            text,
            PostgresRelationQueryOrderingCapability.Exact);
        var table = new PostgresRelationQueryTableBinding(
            new("tests/postgres/source"),
            new("tests/postgres/placement"),
            new("tests/postgres/input"),
            new(new("tests/postgres/graph"), new("tests/postgres/shape")),
            "public",
            "text_values",
            identity: null,
            fields: [field]);
        return new(
            new("tests/postgres-binding/text-order-domain/v1"),
            new("tests/postgres/database"),
            PostgresRelationQueryTargetProfile.Target,
            PostgresRelationQueryTargetProfile.ProfileId,
            [table]);
    }

    static Fixture CreateFixture(string? placementConventionSetVersion = null)
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<LoadDocument>();
        var status = author.Parameter<string>("status");
        var loads = author.Source(loadShape);
        var filtered = author.Filter(
            loads.Node,
            (LoadDocument load) => load.Status == status.Value,
            loads.Binding);
        var projected = author.Project(
            filtered,
            (LoadDocument load) => new LoadRow
            {
                Id = load.Id,
                Status = load.Status
            },
            loads.Binding);
        var ordered = author.Order(
            projected.Node,
            (LoadRow row) => row.Id,
            projected.Binding);
        var rows = author.Rows(ordered, projected.Binding, id: "rows");
        var query = author.BuildQuery(
            new("postgres-binding-authoring"),
            new("PostgresBindingAuthoring"),
            rows);
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));

        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var placementBuilder = RelationQueryPlacement.For(
            plan,
            placementConventionSetVersion is null
                ? null
                : new RelationQueryPlacementAuthoringOptions(
                    "tests/postgres-placement-profile/v1",
                    placementConventionSetVersion));
        var source = placementBuilder.Source(
            "tests/postgres/loads",
            PostgresRelationQuerySourceTargetProfile.Default);
        var placedSource = placementBuilder.PlaceSource(source, loadShape)
            .Identity(load => load.Id)
            .FieldsBySemanticPath();
        var authoredPlacement = placementBuilder.Build().RequireValue();
        return new(plan, authoredPlacement, authoredPlacement.GetInput(placedSource));
    }

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);

    sealed class LoadDocument
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    sealed class LoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }
    }

    sealed record Fixture(
        CompiledRelationQueryPlan Plan,
        RelationQueryAuthoredPlacement AuthoredPlacement,
        RelationQueryPlacedInput<LoadDocument> Placed);
}
