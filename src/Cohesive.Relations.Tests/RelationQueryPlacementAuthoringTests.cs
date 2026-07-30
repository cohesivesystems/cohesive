using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryPlacementAuthoringTests
{
    [Fact]
    public void Explicit_authoring_lowers_to_the_same_normalized_v3_artifact_as_low_level_construction()
    {
        var fixture = CreatePortableQuery();
        var profile = ProfileFor(fixture.Plan);
        var limits = new RelationQuerySourcePlacementLimits(64, 4_096, 32, 3);
        var options = new RelationQueryPlacementAuthoringOptions(
            authority: "tests/placement-profile/v1",
            conventionSetVersion: "tests/placement-conventions/v1",
            fieldSourceSelector: static path => $"scoped/{path}");
        var sourceId = new RelationQuerySourceInstanceId("source/loads");
        var domain = new RelationQueryExecutionDomainId("domain/search");
        var placementId = new RelationQuerySourcePlacementBindingId("placement/loads");
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);
        var builder = RelationQueryPlacement.For(fixture.Plan, options);
        var source = builder.Source(
            sourceKey: "loads-authoring-key",
            targetProfile: profile,
            executionDomain: domain,
            limits: limits,
            id: sourceId);

        var fluent = builder.Place(sourceContract, source, fixture.SourceShape)
            .WithId(placementId)
            .WithAcquisition(RelationQuerySourceAcquisitionKind.BoundedEnumeration)
            .Identity(load => load.Id)
            .FieldsBySemanticPath()
            .Partition(load => load.TenantId);
        var authored = builder.Build().RequireValue();

        Assert.IsType<RelationQueryPlacementInputBuilder<PortableLoad>>(fluent);
        Assert.Equal("loads-authoring-key", source.SourceKey);
        var expectedDecisions = ExplicitDecisions(
            sourceId,
            placementId,
            sourceContract.Fields.Select(static field => field.Input.Id));
        var expectedBinding = new RelationQuerySourcePlacementBinding(
            placementId,
            sourceContract.Input.Id,
            sourceContract.Node,
            sourceContract.Binding,
            sourceContract.Shape,
            sourceId,
            RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            RelationQuerySourcePlacementOrigin.Explicit,
            new(sourceContract.Shape, "id", FieldPath.FromField("id")),
            [
                .. sourceContract.Fields.Select(static field => new RelationQuerySourceFieldBinding(
                    field.Input.Id,
                    field.Input.Field.Path,
                    field.Input.Field.Path.ToString()))
            ],
            partition: new("tenant_id"));
        var expected = new RelationQuerySourcePlacement(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(fixture.Plan),
            "tests/placement-conventions/v1",
            [new(sourceId, domain, profile, limits)],
            [expectedBinding],
            configurationDecisions: expectedDecisions);

        Assert.Equal(expected.Fingerprint, authored.Placement.Fingerprint);
        var jsonOptions = RelationQueryJsonSerializer.CreateOptions();
        Assert.Equal(
            JsonSerializer.Serialize(expected, jsonOptions),
            JsonSerializer.Serialize(authored.Placement, jsonOptions));
        Assert.All(
            authored.Placement.Bindings.Single().Fields,
            field => Assert.Equal(field.SemanticPath.ToString(), field.SourceSelector));
        Assert.Equal(
            FieldPath.FromField("id"),
            authored.Placement.Bindings.Single().Identity!.SemanticPath);
        Assert.All(
            authored.Placement.ConfigurationDecisions.Where(static decision =>
                decision.Setting.Contains("/field/", StringComparison.Ordinal)),
            decision => Assert.Equal(EffectiveConfigurationOrigin.Explicit, decision.Origin));

        var placed = authored.GetInput(fluent);
        Assert.Equal(sourceContract.Shape, placed.Shape);
        Assert.Equal(
            FieldPath.FromField("status"),
            placed.GetField(load => load.Status).Input.Field.Path);
    }

    [Fact]
    public void PlaceSource_records_convention_selection_origin_and_framework_provenance()
    {
        var fixture = CreatePortableQuery();
        var builder = RelationQueryPlacement.For(fixture.Plan);
        var source = builder.Source(
            sourceKey: "loads",
            targetProfile: ProfileFor(fixture.Plan));

        builder.PlaceSource(source, fixture.SourceShape).FieldsBySemanticPath();
        var placement = builder.Build().RequireValue().Placement;
        var binding = Assert.Single(placement.Bindings);

        Assert.Equal(RelationQuerySourcePlacementOrigin.Convention, binding.Origin);
        var sourceDecision = Assert.Single(
            placement.ConfigurationDecisions,
            decision => decision.Setting.EndsWith("/source", StringComparison.Ordinal));
        Assert.Equal(EffectiveConfigurationOrigin.FrameworkDefault, sourceDecision.Origin);
        Assert.Equal(RelationQueryPlacementBuilder.FrameworkDefaultAuthority, sourceDecision.Authority);
    }

    [Fact]
    public void Scoped_defaults_are_attributed_and_explicit_semantic_path_mapping_takes_precedence()
    {
        var fixture = CreatePortableQuery();
        var profile = ProfileFor(fixture.Plan);
        var options = new RelationQueryPlacementAuthoringOptions(
            authority: "tests/scoped-placement/v3",
            conventionSetVersion: "tests/scoped-conventions/v3",
            defaultLimits: new(25, 250, 5, 2),
            identitySourceSelector: "scoped_identity",
            fieldSourceSelector: static path => $"document.{path}");
        var builder = RelationQueryPlacement.For(fixture.Plan, options);
        var source = builder.Source(sourceKey: "loads", targetProfile: profile);
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);

        builder.Place(sourceContract, source, fixture.SourceShape)
            .Identity("local_identity")
            .FieldsBySemanticPath();
        var placement = builder.Build().RequireValue().Placement;
        var binding = Assert.Single(placement.Bindings);

        Assert.Equal("local_identity", binding.Identity!.SourceSelector);
        Assert.All(binding.Fields, field => Assert.Equal(field.SemanticPath.ToString(), field.SourceSelector));
        Assert.Contains(
            placement.ConfigurationDecisions,
            decision => decision.Setting.EndsWith("/limits/maximum-batch-size", StringComparison.Ordinal)
                && decision.Origin == EffectiveConfigurationOrigin.ScopedProfile
                && decision.Authority == options.Authority);
        Assert.Contains(
            placement.ConfigurationDecisions,
            decision => decision.Setting.EndsWith("/identity/source-selector", StringComparison.Ordinal)
                && decision.Origin == EffectiveConfigurationOrigin.Explicit);
        Assert.All(
            placement.ConfigurationDecisions.Where(static decision =>
                decision.Setting.Contains("/field/", StringComparison.Ordinal)),
            decision => Assert.Equal(EffectiveConfigurationOrigin.Explicit, decision.Origin));
    }

    [Fact]
    public void Imported_CLR_member_overrides_drive_typed_selectors_and_placed_field_resolution()
    {
        var fixture = CreateImportedQuery();
        var profile = ProfileFor(fixture.Plan);
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);
        var builder = RelationQueryPlacement.For(fixture.Plan);
        var source = builder.Source(sourceKey: "imported-loads", targetProfile: profile);

        builder.Place(sourceContract, source, fixture.SourceShape)
            .Identity(load => load.Id)
            .Field(load => load.Status, "payload.state")
            .FieldsBySemanticPath();
        var authored = builder.Build().RequireValue();
        var binding = Assert.Single(authored.Placement.Bindings);
        var placed = authored.GetInput<ImportedLoad>(sourceContract.Input.Id);

        Assert.Equal("wire_id", binding.Identity!.SourceSelector);
        Assert.Equal(FieldPath.FromField("wire_id"), binding.Identity.SemanticPath);
        Assert.Equal(
            "payload.state",
            binding.Fields.Single(field => field.SemanticPath == FieldPath.FromField("wire_status")).SourceSelector);
        Assert.Equal(
            FieldPath.FromField("wire_status"),
            placed.GetField(load => load.Status).Input.Field.Path);

        var structuralBuilder = RelationQueryPlacement.For(fixture.Plan);
        var structuralSource = structuralBuilder.Source(sourceKey: "imported-loads", targetProfile: profile);
        var structuralHandle = structuralBuilder.Place(sourceContract, structuralSource)
            .Identity("wire_id")
            .Field(FieldPath.FromField("wire_status"), "payload.state")
            .FieldsBySemanticPath();
        var structurallyAuthored = structuralBuilder.Build().RequireValue();
        var structuralInput = structurallyAuthored.GetInput(structuralHandle);

        Assert.Equal(sourceContract.Shape, structuralInput.Shape);
        Assert.Equal(
            "payload.state",
            structuralInput.Binding.Fields.Single(field =>
                field.SemanticPath == FieldPath.FromField("wire_status")).SourceSelector);
    }

    [Fact]
    public void Typed_identity_preserves_semantic_path_when_the_physical_selector_diverges()
    {
        var fixture = CreatePortableQuery();
        var profile = ProfileFor(fixture.Plan);
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);

        var typedBuilder = RelationQueryPlacement.For(fixture.Plan);
        var typedSource = typedBuilder.Source(sourceKey: "loads", targetProfile: profile);
        typedBuilder.Place(sourceContract, typedSource, fixture.SourceShape)
            .Identity(load => load.Id, "document_key")
            .FieldsBySemanticPath();
        var typed = typedBuilder.Build().RequireValue().Placement;

        var structuralBuilder = RelationQueryPlacement.For(fixture.Plan);
        var structuralSource = structuralBuilder.Source(sourceKey: "loads", targetProfile: profile);
        structuralBuilder.Place(sourceContract, structuralSource)
            .Identity(FieldPath.FromField("id"), "document_key")
            .FieldsBySemanticPath();
        var structural = structuralBuilder.Build().RequireValue().Placement;

        var physicalOnlyBuilder = RelationQueryPlacement.For(fixture.Plan);
        var physicalOnlySource = physicalOnlyBuilder.Source(sourceKey: "loads", targetProfile: profile);
        physicalOnlyBuilder.Place(sourceContract, physicalOnlySource)
            .Identity("document_key")
            .FieldsBySemanticPath();
        var physicalOnly = physicalOnlyBuilder.Build().RequireValue().Placement;

        var typedIdentity = Assert.Single(typed.Bindings).Identity!;
        Assert.Equal("document_key", typedIdentity.SourceSelector);
        Assert.Equal(FieldPath.FromField("id"), typedIdentity.SemanticPath);
        Assert.Equal(typed.Fingerprint, structural.Fingerprint);
        Assert.Null(Assert.Single(physicalOnly.Bindings).Identity!.SemanticPath);
        Assert.NotEqual(typed.Fingerprint, physicalOnly.Fingerprint);
    }

    [Fact]
    public void Incomplete_or_incompatible_authoring_fails_closed_with_structured_diagnostics()
    {
        var fixture = CreatePortableQuery();
        var profile = ProfileFor(fixture.Plan);
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);
        var incompleteBuilder = RelationQueryPlacement.For(fixture.Plan);
        _ = incompleteBuilder.Source(sourceKey: "loads", targetProfile: profile);

        var incomplete = incompleteBuilder.Build();

        Assert.False(incomplete.IsSuccess);
        var missing = Assert.Single(incomplete.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.PlacementMissing);
        Assert.Equal(sourceContract.Input.Id, missing.Input);
        var exception = Assert.Throws<RelationQueryArtifactAuthoringException>(incomplete.RequireValue);
        Assert.Equal(incomplete.Diagnostics, exception.Diagnostics);

        var incompatibleBuilder = RelationQueryPlacement.For(fixture.Plan);
        var source = incompatibleBuilder.Source(sourceKey: "loads", targetProfile: profile);
        incompatibleBuilder.Place(sourceContract, source)
            .WithAcquisition(RelationQuerySourceAcquisitionKind.Supplied)
            .Field(FieldPath.FromField("not_demanded"), "missing");

        var incompatible = incompatibleBuilder.Build();

        Assert.False(incompatible.IsSuccess);
        Assert.Contains(
            incompatible.Diagnostics,
            static diagnostic => diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.AcquisitionMismatch
                && diagnostic.Input is not null
                && diagnostic.Setting is not null);
        Assert.Contains(
            incompatible.Diagnostics,
            static diagnostic => diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.FieldBindingInvalid
                && diagnostic.SemanticPath == FieldPath.FromField("not_demanded"));
        Assert.False(incompatible.TryGetValue(out _));
    }

    [Fact]
    public void Configuration_decisions_round_trip_and_participate_in_the_v3_fingerprint()
    {
        var fixture = CreatePortableQuery();
        var profile = ProfileFor(fixture.Plan);
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);
        var builder = RelationQueryPlacement.For(fixture.Plan);
        var source = builder.Source(sourceKey: "loads", targetProfile: profile);
        var inputHandle = builder.Place(sourceContract, source).FieldsBySemanticPath();
        var authored = builder.Build().RequireValue();
        var placement = authored.Placement;
        var options = RelationQueryJsonSerializer.CreateOptions();

        var secondBuilder = RelationQueryPlacement.For(fixture.Plan);
        var secondSource = secondBuilder.Source(sourceKey: "loads", targetProfile: profile);
        var secondInputHandle = secondBuilder.Place(sourceContract, secondSource).FieldsBySemanticPath();
        var secondAuthored = secondBuilder.Build().RequireValue();
        var lateHandle = builder.Place(sourceContract, source).FieldsBySemanticPath();

        var json = JsonSerializer.Serialize(placement, options);
        var roundTrip = JsonSerializer.Deserialize<RelationQuerySourcePlacement>(json, options);
        var changedDecisions = placement.ConfigurationDecisions
            .Select((decision, index) => index == 0
                ? new EffectiveConfigurationDecision(
                    decision.Setting,
                    decision.Origin,
                    decision.Authority + "/changed")
                : decision)
            .ToImmutableArray();
        var changed = new RelationQuerySourcePlacement(
            placement.SchemaVersion,
            placement.Plan,
            placement.ConventionSetVersion,
            placement.SourceInstances,
            placement.Bindings,
            configurationDecisions: changedDecisions);

        Assert.NotNull(roundTrip);
        Assert.Equal(sourceContract.Input.Id, authored.GetInput(inputHandle).Binding.Input);
        Assert.Throws<ArgumentException>(() => authored.GetInput(secondInputHandle));
        Assert.Throws<ArgumentException>(() => authored.GetInput(lateHandle));
        Assert.Equal(placement.Fingerprint, secondAuthored.Placement.Fingerprint);
        Assert.Equal(
            placement.SourceInstances.Select(static instance => instance.Id),
            secondAuthored.Placement.SourceInstances.Select(static instance => instance.Id));
        Assert.Equal(RelationQuerySourcePlacement.CurrentSchemaVersion, roundTrip.SchemaVersion);
        Assert.Equal(
            placement.ConfigurationDecisions.Select(ConfigurationDecisionSignature),
            roundTrip.ConfigurationDecisions.Select(ConfigurationDecisionSignature));
        Assert.Equal(placement.Fingerprint, roundTrip.Fingerprint);
        Assert.NotEqual(placement.Fingerprint, changed.Fingerprint);
        Assert.Throws<ArgumentException>(() => new RelationQuerySourcePlacement(
            placement.SchemaVersion,
            placement.Plan,
            placement.ConventionSetVersion,
            placement.SourceInstances,
            placement.Bindings,
            configurationDecisions:
            [
                placement.ConfigurationDecisions[0],
                placement.ConfigurationDecisions[0]
            ]));
    }

    [Fact]
    public void Persisted_configuration_decisions_are_optional_but_must_name_actual_consistent_facts()
    {
        var fixture = CreatePortableQuery();
        var builder = RelationQueryPlacement.For(fixture.Plan);
        var source = builder.Source(sourceKey: "loads", targetProfile: ProfileFor(fixture.Plan));
        builder.PlaceSource(source, fixture.SourceShape).FieldsBySemanticPath();
        var placement = builder.Build().RequireValue().Placement;
        var conventionDecision = Assert.Single(
            placement.ConfigurationDecisions,
            static decision => decision.Setting == "placement/convention-set-version");

        var partial = new RelationQuerySourcePlacement(
            placement.SchemaVersion,
            placement.Plan,
            placement.ConventionSetVersion,
            placement.SourceInstances,
            placement.Bindings,
            configurationDecisions: [conventionDecision]);

        Assert.Equal(conventionDecision, Assert.Single(partial.ConfigurationDecisions));
        Assert.Throws<ArgumentException>(() => new RelationQuerySourcePlacement(
            placement.SchemaVersion,
            placement.Plan,
            placement.ConventionSetVersion,
            placement.SourceInstances,
            placement.Bindings,
            configurationDecisions:
            [
                new(
                    "foreign/not-an-artifact-fact",
                    EffectiveConfigurationOrigin.FrameworkDefault,
                    RelationQueryPlacementBuilder.FrameworkDefaultAuthority)
            ]));

        var binding = Assert.Single(placement.Bindings);
        var sourceSetting = $"placement/{Uri.EscapeDataString(binding.Id.Value)}/source";
        EffectiveConfigurationOrigin[] invalidConventionOrigins =
        [
            EffectiveConfigurationOrigin.Explicit,
            EffectiveConfigurationOrigin.ScopedProfile
        ];
        foreach (var invalidConventionOrigin in invalidConventionOrigins)
        {
            Assert.Throws<ArgumentException>(() => new RelationQuerySourcePlacement(
                placement.SchemaVersion,
                placement.Plan,
                placement.ConventionSetVersion,
                placement.SourceInstances,
                placement.Bindings,
                configurationDecisions:
                [
                    new(
                        sourceSetting,
                        invalidConventionOrigin,
                        "tests/invalid-source-selection-origin/v1")
                ]));
        }

        var explicitBinding = new RelationQuerySourcePlacementBinding(
            binding.Id,
            binding.Input,
            binding.Node,
            binding.Binding,
            binding.Shape,
            binding.Source,
            binding.Kind,
            binding.Acquisition,
            RelationQuerySourcePlacementOrigin.Explicit,
            binding.Identity,
            binding.Fields,
            binding.RelationshipKeys,
            binding.Partition);
        Assert.Throws<ArgumentException>(() => new RelationQuerySourcePlacement(
            placement.SchemaVersion,
            placement.Plan,
            placement.ConventionSetVersion,
            placement.SourceInstances,
            [explicitBinding],
            configurationDecisions:
            [
                new(
                    sourceSetting,
                    EffectiveConfigurationOrigin.FrameworkDefault,
                    RelationQueryPlacementBuilder.FrameworkDefaultAuthority)
            ]));
    }

    [Fact]
    public void Duplicate_foreign_and_semantically_inapplicable_declarations_have_stable_diagnostic_codes()
    {
        var plan = CompileLoadCustomerRelation();
        var profile = ProfileFor(plan);
        var sourceContract = Assert.Single(plan.InputContract.Sources);
        var traversalContract = Assert.Single(plan.InputContract.Traversals);
        var duplicateBuilder = RelationQueryPlacement.For(plan);
        var duplicateId = new RelationQuerySourceInstanceId("source/duplicate");
        var source = duplicateBuilder.Source(sourceKey: "first", targetProfile: profile, id: duplicateId);
        _ = duplicateBuilder.Source(sourceKey: "second", targetProfile: profile, id: duplicateId);
        var traversalSource = duplicateBuilder.Source(sourceKey: "traversal", targetProfile: profile);
        duplicateBuilder.Place(sourceContract, source);
        duplicateBuilder.Place(sourceContract, source);
        duplicateBuilder.Place(traversalContract, traversalSource).RelationshipKey("customer_id");

        var duplicate = duplicateBuilder.Build();

        Assert.False(duplicate.IsSuccess);
        Assert.Contains(duplicate.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.SourceConflict);
        Assert.Contains(duplicate.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.PlacementConflict);
        Assert.Contains(duplicate.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.RelationshipKeyBindingInvalid);

        var portable = CreatePortableQuery();
        var foreign = CreateImportedQuery();
        var foreignContract = Assert.Single(foreign.Plan.InputContract.Sources);
        var staleBuilder = RelationQueryPlacement.For(portable.Plan);
        var staleSource = staleBuilder.Source(
            sourceKey: "loads",
            targetProfile: ProfileFor(portable.Plan));
        staleBuilder.Place(foreignContract, staleSource);

        var stale = staleBuilder.Build();

        Assert.False(stale.IsSuccess);
        Assert.Contains(stale.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.InputInvalid);
    }

    [Fact]
    public void Foreign_ambiguous_and_shape_mismatched_selections_have_stable_diagnostic_codes()
    {
        var portable = CreatePortableQuery();
        var sourceContract = Assert.Single(portable.Plan.InputContract.Sources);
        var foreignSourceBuilder = RelationQueryPlacement.For(portable.Plan);
        var foreignSource = foreignSourceBuilder.Source(
            sourceKey: "foreign-loads",
            targetProfile: ProfileFor(portable.Plan));
        var sourceMismatchBuilder = RelationQueryPlacement.For(portable.Plan);
        sourceMismatchBuilder.Place(sourceContract, foreignSource);

        var sourceMismatch = sourceMismatchBuilder.Build();

        Assert.False(sourceMismatch.IsSuccess);
        Assert.Contains(sourceMismatch.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.SourceUnknown);

        var joinPlan = CompileExplicitJoinQuery();
        Assert.Equal(2, joinPlan.InputContract.Sources.Length);
        var ambiguousBuilder = RelationQueryPlacement.For(joinPlan);
        var ambiguousSource = ambiguousBuilder.Source(
            sourceKey: "joined",
            targetProfile: ProfileFor(joinPlan));
        ambiguousBuilder.PlaceSource(ambiguousSource);

        var ambiguous = ambiguousBuilder.Build();

        Assert.False(ambiguous.IsSuccess);
        Assert.Contains(ambiguous.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.InputAmbiguous);

        var imported = CreateImportedQuery();
        var shapeMismatchBuilder = RelationQueryPlacement.For(portable.Plan);
        var shapeMismatchSource = shapeMismatchBuilder.Source(
            sourceKey: "loads",
            targetProfile: ProfileFor(portable.Plan));
        shapeMismatchBuilder.Place(sourceContract, shapeMismatchSource, imported.SourceShape);

        var shapeMismatch = shapeMismatchBuilder.Build();

        Assert.False(shapeMismatch.IsSuccess);
        Assert.Contains(shapeMismatch.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.ShapeMismatch);
    }

    [Fact]
    public void Typed_placement_requires_the_exact_semantic_shape_snapshot_but_accepts_rehydrated_documents()
    {
        var fixture = CreateImportedQuery();
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var rehydratedDocument = JsonSerializer.Deserialize<ShapeGraphDocument>(
            JsonSerializer.Serialize(fixture.SourceShape.Document, jsonOptions),
            jsonOptions)
            ?? throw new InvalidOperationException("Failed to rehydrate the imported shape document.");
        var equivalentShape = CreateImportedShape(rehydratedDocument, fixture.SourceShape.Id);
        var equivalentBuilder = RelationQueryPlacement.For(fixture.Plan);
        var equivalentSource = equivalentBuilder.Source(
            sourceKey: "loads",
            targetProfile: ProfileFor(fixture.Plan));
        equivalentBuilder.Place(sourceContract, equivalentSource, equivalentShape).FieldsBySemanticPath();

        var equivalent = equivalentBuilder.Build();

        Assert.NotSame(fixture.SourceShape.Document, rehydratedDocument);
        Assert.True(equivalent.IsSuccess, string.Join(Environment.NewLine, equivalent.Diagnostics));

        var graph = rehydratedDocument.Graph;
        var changedDocument = new ShapeGraphDocument(
            rehydratedDocument.SchemaVersion,
            new ShapeGraph(
                graph.Id,
                [.. graph.Shapes, new Shape(new("unrelated-same-graph-shape"), [])],
                graph.NamedTypes,
                annotations: graph.Annotations),
            rehydratedDocument.Metadata);
        var changedShape = CreateImportedShape(changedDocument, fixture.SourceShape.Id);
        var mismatchBuilder = RelationQueryPlacement.For(fixture.Plan);
        var mismatchSource = mismatchBuilder.Source(
            sourceKey: "loads",
            targetProfile: ProfileFor(fixture.Plan));
        mismatchBuilder.Place(sourceContract, mismatchSource, changedShape).FieldsBySemanticPath();

        var mismatch = mismatchBuilder.Build();

        Assert.False(mismatch.IsSuccess);
        var diagnostic = Assert.Single(mismatch.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.ShapeMismatch);
        Assert.Equal(sourceContract.Input.Id, diagnostic.Input);
        Assert.Contains("exact graph", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicting_identity_configuration_and_incompatible_profile_have_stable_diagnostic_codes()
    {
        var fixture = CreatePortableQuery();
        var sourceContract = Assert.Single(fixture.Plan.InputContract.Sources);
        var conflictBuilder = RelationQueryPlacement.For(fixture.Plan);
        var source = conflictBuilder.Source(
            sourceKey: "loads",
            targetProfile: ProfileFor(fixture.Plan));
        conflictBuilder.Place(sourceContract, source)
            .WithId(new("placement/first"))
            .WithId(new("placement/second"))
            .Identity("id")
            .Identity("document_id");

        var conflicts = conflictBuilder.Build();

        Assert.False(conflicts.IsSuccess);
        Assert.Contains(conflicts.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.ConfigurationInvalid
            && diagnostic.Setting == "placement/placement%2Ffirst/id");
        Assert.Contains(conflicts.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.IdentityBindingInvalid);

        var incompatibleProfile = new RelationQueryTargetCapabilityProfile(
            new("tests/placement-target"),
            new("tests/placement-target/incompatible-v1"),
            ["unsupported-definition-schema"],
            [fixture.Plan.Provenance.CompilerProfile]);
        var profileBuilder = RelationQueryPlacement.For(fixture.Plan);
        var incompatibleSource = profileBuilder.Source(
            sourceKey: "loads",
            targetProfile: incompatibleProfile);
        profileBuilder.Place(sourceContract, incompatibleSource);

        var profileMismatch = profileBuilder.Build();

        Assert.False(profileMismatch.IsSuccess);
        Assert.Contains(profileMismatch.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.TargetProfileMismatch);
    }

    [Fact]
    public void Duplicate_binding_identity_is_reported_as_an_artifact_diagnostic()
    {
        var plan = CompileLoadCustomerRelation();
        var profile = ProfileFor(plan);
        var sourceContract = Assert.Single(plan.InputContract.Sources);
        var traversalContract = Assert.Single(plan.InputContract.Traversals);
        var builder = RelationQueryPlacement.For(plan);
        var source = builder.Source(sourceKey: "loads", targetProfile: profile);
        var relatedSource = builder.Source(sourceKey: "customers", targetProfile: profile);
        var duplicateBinding = new RelationQuerySourcePlacementBindingId("placement/duplicate");
        builder.Place(sourceContract, source)
            .WithId(duplicateBinding)
            .FieldsBySemanticPath();
        builder.Place(traversalContract, relatedSource)
            .WithId(duplicateBinding)
            .FieldsBySemanticPath();

        var result = builder.Build();

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.ArtifactInvalid);
    }

    [Fact]
    public void Source_and_relationship_traversal_demands_receive_valid_default_acquisition()
    {
        var plan = CompileLoadCustomerRelation();
        var profile = ProfileFor(plan);
        var sourceContract = Assert.Single(plan.InputContract.Sources);
        var traversalContract = Assert.Single(plan.InputContract.Traversals);
        var builder = RelationQueryPlacement.For(plan);
        var source = builder.Source(sourceKey: "loads", targetProfile: profile);
        var relatedSource = builder.Source(sourceKey: "customers", targetProfile: profile);
        var sourceHandle = builder.Place(sourceContract, source).FieldsBySemanticPath();
        var traversalHandle = builder.Place(traversalContract, relatedSource).FieldsBySemanticPath();

        var authored = builder.Build().RequireValue();
        var sourceInput = authored.GetInput(sourceHandle);
        var traversalInput = authored.GetInput(traversalHandle);

        Assert.Equal(RelationQuerySourceAcquisitionKind.Supplied, sourceInput.Binding.Acquisition);
        Assert.Equal(RelationQuerySourcePlacementBindingKind.SourceSet, sourceInput.Binding.Kind);
        Assert.Null(sourceInput.Binding.Identity);
        Assert.Equal(sourceContract.Fields.Length, sourceInput.Binding.Fields.Length);
        Assert.Equal(RelationQuerySourceAcquisitionKind.BoundedLookup, traversalInput.Binding.Acquisition);
        Assert.Equal(RelationQuerySourcePlacementBindingKind.RelationshipTraversal, traversalInput.Binding.Kind);
        Assert.NotNull(traversalInput.Binding.Identity);
        Assert.Equal(traversalContract.Fields.Length, traversalInput.Binding.Fields.Length);
        Assert.Empty(traversalInput.Binding.RelationshipKeys);
    }

    static ImmutableArray<EffectiveConfigurationDecision> ExplicitDecisions(
        RelationQuerySourceInstanceId source,
        RelationQuerySourcePlacementBindingId placement,
        IEnumerable<RelationQueryInputId> fields)
    {
        var sourceSetting = $"source/{Uri.EscapeDataString(source.Value)}";
        var placementSetting = $"placement/{Uri.EscapeDataString(placement.Value)}";
        List<EffectiveConfigurationDecision> decisions =
        [
            Scoped("placement/convention-set-version"),
            Explicit($"{sourceSetting}/id"),
            Explicit($"{sourceSetting}/execution-domain"),
            Explicit($"{sourceSetting}/target-profile"),
            Explicit($"{sourceSetting}/limits/maximum-batch-size"),
            Explicit($"{sourceSetting}/limits/maximum-buffered-rows"),
            Explicit($"{sourceSetting}/limits/maximum-fan-out"),
            Explicit($"{sourceSetting}/limits/maximum-concurrency"),
            Explicit($"{placementSetting}/id"),
            Explicit($"{placementSetting}/source"),
            Explicit($"{placementSetting}/acquisition"),
            Explicit($"{placementSetting}/identity/semantic-path"),
            Explicit($"{placementSetting}/identity/source-selector"),
            Explicit($"{placementSetting}/partition/source-selector")
        ];
        decisions.AddRange(fields.Select(field =>
            Explicit($"{placementSetting}/field/{Uri.EscapeDataString(field.Value)}/source-selector")));
        return [.. decisions];
    }

    static EffectiveConfigurationDecision Explicit(string setting) => new(
        setting,
        EffectiveConfigurationOrigin.Explicit,
        RelationQueryPlacementBuilder.ExplicitDeclarationAuthority);

    static EffectiveConfigurationDecision Scoped(string setting) => new(
        setting,
        EffectiveConfigurationOrigin.ScopedProfile,
        "tests/placement-profile/v1");

    static string ConfigurationDecisionSignature(EffectiveConfigurationDecision decision) =>
        $"{decision.Setting}|{decision.Origin}|{decision.Authority}";

    static RelationQueryTargetCapabilityProfile ProfileFor(CompiledRelationQueryPlan plan) => new(
        new("tests/placement-target"),
        new("tests/placement-target/v1"),
        [plan.Provenance.DefinitionDocument.SchemaVersion],
        [plan.Provenance.CompilerProfile]);

    static PortableFixture CreatePortableQuery()
    {
        var author = RelationQuery.Expression();
        var sourceShape = author.Clr.Shape<PortableLoad>();
        var loads = author.Source(sourceShape);
        var projected = author.Project(
            loads.Node,
            (PortableLoad load) => new PortableLoadRow
            {
                Id = load.Id,
                Status = load.Status,
                TenantId = load.TenantId
            },
            loads.Binding);
        var query = author.BuildQuery(
            new("placement-portable-loads"),
            new("PlacementPortableLoads"),
            author.Rows(projected.Node, projected.Binding, id: "rows"));
        return new(sourceShape, Compile(query.CreateDocument(), author.ShapeDocuments));
    }

    static ImportedFixture CreateImportedQuery()
    {
        var graphId = new GraphId("tests/imported-placement/v1");
        var qualifiedShape = new QualifiedShapeId(graphId, new("wire-load"));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    qualifiedShape.ShapeId,
                    [
                        new FieldDefinition(new("wire_id"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new FieldDefinition(new("wire_status"), new ScalarTypeRef(ScalarTypeKind.String))
                    ])
            ]));
        var context = new RelationQueryClrAuthoringContext();
        var sourceShape = CreateImportedShape(context, document, qualifiedShape);
        var author = RelationQuery.Expression(context);
        var loads = author.Source(sourceShape);
        var projected = author.Project(
            loads.Node,
            (ImportedLoad load) => new ImportedLoadRow { Id = load.Id, Status = load.Status },
            loads.Binding);
        var query = author.BuildQuery(
            new("placement-imported-loads"),
            new("PlacementImportedLoads"),
            author.Rows(projected.Node, projected.Binding, id: "rows"));
        return new(sourceShape, Compile(query.CreateDocument(), author.ShapeDocuments));
    }

    static RelationQueryClrShape<ImportedLoad> CreateImportedShape(
        ShapeGraphDocument document,
        QualifiedShapeId qualifiedShape) =>
        CreateImportedShape(new RelationQueryClrAuthoringContext(), document, qualifiedShape);

    static RelationQueryClrShape<ImportedLoad> CreateImportedShape(
        RelationQueryClrAuthoringContext context,
        ShapeGraphDocument document,
        QualifiedShapeId qualifiedShape) =>
        context.Shape<ImportedLoad>(
            document,
            qualifiedShape,
            new Dictionary<PropertyInfo, FieldPath>
            {
                [Property<ImportedLoad>(nameof(ImportedLoad.Id))] = FieldPath.FromField("wire_id"),
                [Property<ImportedLoad>(nameof(ImportedLoad.Status))] = FieldPath.FromField("wire_status")
            });

    static CompiledRelationQueryPlan Compile(
        RelationQueryDocument document,
        ImmutableArray<ShapeGraphDocument> shapeDocuments)
    {
        var result = RelationQueryStaticCompiler.Compile(new(document, shapeDocuments));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static CompiledRelationQueryPlan CompileLoadCustomerRelation()
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static CompiledRelationQueryPlan CompileExplicitJoinQuery()
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.ExplicitJoinQueryDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static PropertyInfo Property<T>(string name) =>
        typeof(T).GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException($"Test property '{typeof(T).Name}.{name}' was not found.");

    sealed class PortableLoad
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("tenant_id")]
        public required string TenantId { get; init; }
    }

    sealed class PortableLoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("tenant_id")]
        public required string TenantId { get; init; }
    }

    sealed class ImportedLoad
    {
        public required string Id { get; init; }
        public required string Status { get; init; }
    }

    sealed class ImportedLoadRow
    {
        public required string Id { get; init; }
        public required string Status { get; init; }
    }

    sealed record PortableFixture(
        RelationQueryClrShape<PortableLoad> SourceShape,
        CompiledRelationQueryPlan Plan);

    sealed record ImportedFixture(
        RelationQueryClrShape<ImportedLoad> SourceShape,
        CompiledRelationQueryPlan Plan);
}
