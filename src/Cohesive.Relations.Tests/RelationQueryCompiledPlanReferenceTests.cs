using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryCompiledPlanReferenceTests
{
    [Fact]
    public void From_CachesExactPortablePlanAttribution()
    {
        var plan = Compile();

        var first = RelationQueryCompiledPlanReference.From(plan);
        var second = RelationQueryCompiledPlanReference.From(plan);

        Assert.Same(first, second);
        Assert.Equal(plan.Provenance.CompilerProfile, first.CompilerProfile);
        Assert.Equal(plan.Provenance.DefinitionDocument.SchemaVersion, first.DefinitionSchemaVersion);
        Assert.Equal(plan.Provenance.DefinitionFingerprint, first.DefinitionFingerprint);
        Assert.Equal(plan.Provenance.RelationshipCatalogFingerprint, first.RelationshipCatalogFingerprint);
        Assert.Equal("sha256", first.ShapeSnapshotsFingerprint.Algorithm);
        Assert.Equal("relation-query-plan-shapes/v1-c14n/v2", first.ShapeSnapshotsFingerprint.Canonicalization);
        Assert.Equal("sha256", first.DemandFingerprint.Algorithm);
        Assert.Equal("relation-query-plan-demand/v1-c14n/v1", first.DemandFingerprint.Canonicalization);
        Assert.Equal(
            plan.RequirementGraph.Inputs.Select(static input => input.Id.Value).Order(StringComparer.Ordinal),
            first.Inputs.Select(static input => input.Value));
    }

    [Fact]
    public void Constructor_NormalizesInputOrderAndRejectsInvalidInputSets()
    {
        var derived = RelationQueryCompiledPlanReference.From(Compile());
        var reversed = derived.Inputs.Reverse().ToImmutableArray();

        var reference = new RelationQueryCompiledPlanReference(
            derived.CompilerProfile,
            derived.DefinitionSchemaVersion,
            derived.DefinitionFingerprint,
            derived.ShapeSnapshotsFingerprint,
            derived.RelationshipCatalogFingerprint,
            derived.DemandFingerprint,
            reversed);

        Assert.Equal(derived.Inputs.ToArray(), reference.Inputs.ToArray());
        Assert.Throws<ArgumentException>(() => new RelationQueryCompiledPlanReference(
            derived.CompilerProfile,
            derived.DefinitionSchemaVersion,
            derived.DefinitionFingerprint,
            derived.ShapeSnapshotsFingerprint,
            derived.RelationshipCatalogFingerprint,
            derived.DemandFingerprint,
            []));
        Assert.Throws<ArgumentException>(() => new RelationQueryCompiledPlanReference(
            derived.CompilerProfile,
            derived.DefinitionSchemaVersion,
            derived.DefinitionFingerprint,
            derived.ShapeSnapshotsFingerprint,
            derived.RelationshipCatalogFingerprint,
            derived.DemandFingerprint,
            [derived.Inputs[0], derived.Inputs[0]]));
        Assert.Throws<ArgumentException>(() => new RelationQueryCompiledPlanReference(
            derived.CompilerProfile,
            derived.DefinitionSchemaVersion,
            derived.DefinitionFingerprint,
            derived.ShapeSnapshotsFingerprint,
            derived.RelationshipCatalogFingerprint,
            derived.DemandFingerprint,
            [default]));
    }

    [Fact]
    public void Reference_RoundTripsThroughThePortableRelationsJsonProfile()
    {
        var expected = RelationQueryCompiledPlanReference.From(Compile());
        var options = RelationQueryJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(expected, options);
        var actual = JsonSerializer.Deserialize<RelationQueryCompiledPlanReference>(json, options);

        Assert.NotNull(actual);
        Assert.Equal(expected.CompilerProfile, actual.CompilerProfile);
        Assert.Equal(expected.DefinitionSchemaVersion, actual.DefinitionSchemaVersion);
        Assert.Equal(expected.DefinitionFingerprint, actual.DefinitionFingerprint);
        Assert.Equal(expected.ShapeSnapshotsFingerprint, actual.ShapeSnapshotsFingerprint);
        Assert.Equal(expected.RelationshipCatalogFingerprint, actual.RelationshipCatalogFingerprint);
        Assert.Equal(expected.DemandFingerprint, actual.DemandFingerprint);
        Assert.Equal(expected.Inputs.ToArray(), actual.Inputs.ToArray());
    }

    static CompiledRelationQueryPlan Compile()
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(result.IsSuccessful);
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }
}
