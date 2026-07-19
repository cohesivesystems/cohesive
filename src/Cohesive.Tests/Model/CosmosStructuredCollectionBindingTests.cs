using System.Collections.Immutable;
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Model;

public sealed class CosmosStructuredCollectionBindingTests
{
    static readonly FieldPath LocationPath = FieldPath.Parse("Location");
    static readonly FieldPath TypePath = FieldPath.Parse("Type");

    [Fact]
    public void CollectionScope_NormalizesChildOrderAndRetainsValueSemantics()
    {
        var first = Scope(children: [Child(TypePath), Child(LocationPath)]);
        var second = Scope(children: [Child(LocationPath), Child(TypePath)]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal([TypePath, LocationPath], first.ChildFields.Select(static child => child.ElementPath).ToArray());
        Assert.Equal(LocationPath, first.ResolveChild(LocationPath).DocumentPath);
        Assert.Throws<KeyNotFoundException>(() => first.ResolveChild(FieldPath.Parse("Unknown")));
    }

    [Fact]
    public void CollectionScope_RejectsDuplicateAndMalformedDirectChildBindings()
    {
        Assert.Throws<ArgumentNullException>(() => Scope(scopeProfile: null!));
        Assert.Throws<ArgumentException>(() => Scope(children: []));
        Assert.Throws<ArgumentException>(() => Scope(children: [Child(LocationPath), Child(LocationPath)]));
        Assert.Throws<ArgumentException>(() => new CosmosRelationQueryCollectionElementFieldBinding(
            FieldPath.Parse("Address.City"),
            LocationPath,
            CosmosRelationQueryCollectionElementValueDomain.String,
            AllComparisonCapabilities,
            "tests/cosmos-json-scalar/v1",
            Prohibited,
            Prohibited));
        Assert.Throws<ArgumentException>(() => new CosmosRelationQueryCollectionElementFieldBinding(
            LocationPath,
            FieldPath.Parse("Address.City"),
            CosmosRelationQueryCollectionElementValueDomain.String,
            AllComparisonCapabilities,
            "tests/cosmos-json-scalar/v1",
            Prohibited,
            Prohibited));
    }

    [Fact]
    public void CollectionEvidence_RejectsInvalidEnumsAndUnattributedExactCapabilities()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Child(
            LocationPath,
            valueDomain: (CosmosRelationQueryCollectionElementValueDomain)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Child(
            LocationPath,
            capabilities: (CosmosRelationQueryCollectionElementSemanticCapabilities)(1 << 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Child(
            LocationPath,
            missing: (CosmosRelationQueryStructuredCollectionAbsenceBehavior)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Child(
            LocationPath,
            @null: (CosmosRelationQueryStructuredCollectionAbsenceBehavior)99));
        Assert.Throws<ArgumentException>(() => Child(LocationPath, semanticProfile: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scope(
            elementScope: (CosmosRelationQueryCollectionElementScope)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scope(
            correlation: (CosmosRelationQueryCollectionCorrelationGuarantee)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scope(
            collectionMissing: (CosmosRelationQueryStructuredCollectionAbsenceBehavior)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scope(
            collectionNull: (CosmosRelationQueryStructuredCollectionAbsenceBehavior)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scope(
            nullElement: (CosmosRelationQueryStructuredCollectionAbsenceBehavior)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Scope(
            empty: (CosmosRelationQueryEmptyCollectionBehavior)99));
    }

    [Fact]
    public void FieldBinding_WithCollectionEvidenceRequiresAPropertyOnlyCollectionPath()
    {
        FieldPath expandedPath = new(
        [
            FieldPathSegment.ForField("Stops"),
            FieldPathSegment.Element()
        ]);

        Assert.Throws<ArgumentException>(() => new CosmosRelationQueryFieldBinding(
            new("field:stops"),
            expandedPath,
            Scope()));
    }

    [Fact]
    public void BindingFingerprint_ContainsEveryCollectionEvidenceFact()
    {
        var baseline = CreateStorageBinding(Scope()).Fingerprint;
        CosmosRelationQueryCollectionScopeEvidence[] variants =
        [
            Scope(elementScope: CosmosRelationQueryCollectionElementScope.Unproven),
            Scope(correlation: CosmosRelationQueryCollectionCorrelationGuarantee.Unproven),
            Scope(collectionMissing: CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven),
            Scope(collectionNull: CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven),
            Scope(nullElement: CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven),
            Scope(empty: CosmosRelationQueryEmptyCollectionBehavior.Unproven),
            Scope(scopeProfile: "tests/cosmos-json-array/v2"),
            Scope(children: [Child(FieldPath.Parse("City"), documentPath: LocationPath), Child(TypePath)]),
            Scope(children: [Child(LocationPath, documentPath: FieldPath.Parse("City")), Child(TypePath)]),
            Scope(children: [Child(LocationPath, valueDomain: CosmosRelationQueryCollectionElementValueDomain.Bool), Child(TypePath)]),
            Scope(children: [Child(LocationPath, capabilities: CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality), Child(TypePath)]),
            Scope(children: [Child(LocationPath, semanticProfile: "tests/cosmos-json-scalar/v2"), Child(TypePath)]),
            Scope(children: [Child(LocationPath, missing: CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven), Child(TypePath)]),
            Scope(children: [Child(LocationPath, @null: CosmosRelationQueryStructuredCollectionAbsenceBehavior.Unproven), Child(TypePath)]),
            Scope(children: [Child(LocationPath)])
        ];

        Assert.All(variants, variant => Assert.NotEqual(baseline, CreateStorageBinding(variant).Fingerprint));
        var withoutEvidence = CreateStorageBinding(collectionScope: null);
        Assert.NotEqual(baseline, withoutEvidence.Fingerprint);
        Assert.EndsWith("/v5", withoutEvidence.SchemaVersion, StringComparison.Ordinal);
        Assert.Contains("/v5-c14n/", withoutEvidence.Fingerprint.Canonicalization, StringComparison.Ordinal);
    }

    [Fact]
    public void SemanticPathConvention_DoesNotInventStructuredCollectionEvidence()
    {
        var shape = new QualifiedShapeId(new("tests/cosmos-convention"), new("Load"));
        var placement = new RelationQuerySourcePlacementBinding(
            new("placement:loads"),
            new("source:loads"),
            new("node:loads"),
            new("loads"),
            shape,
            new("cosmos:loads"),
            RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            RelationQuerySourcePlacementOrigin.Convention,
            fields:
            [
                new(new RelationQueryInputId("field:stops"), FieldPath.Parse("Stops"), "Stops")
            ]);

        var binding = CosmosRelationQueryStorageBinding.FromSemanticPathConvention(
            new("loads/v1"),
            placement,
            CosmosRelationQueryTargetProfile.Target,
            CosmosRelationQueryTargetProfile.ProfileId,
            new("https://localhost:8081/"),
            "operations",
            "loads",
            FieldPath.Parse("Id"));

        Assert.Null(Assert.Single(binding.Fields).CollectionScope);
    }

    [Fact]
    public void TargetProfileV2_AdvertisesOnlyDirectCurrentItemCollectionElementReads()
    {
        var capabilities = CosmosRelationQueryTargetProfile.Default.Capabilities
            .Select(static evidence => evidence.Capability)
            .ToArray();
        var structural = capabilities.OfType<StructuralRelationQueryCapability>().ToArray();

        Assert.EndsWith("/canonical-v2", CosmosRelationQueryTargetProfile.ProfileId.Value, StringComparison.Ordinal);
        Assert.EndsWith("/realization-policy-v2", CosmosRelationQueryTargetProfile.Policy.Id.Value, StringComparison.Ordinal);
        Assert.EndsWith("/compiler-v2", CosmosRelationQueryCompilerOptions.CurrentCompilerProfile, StringComparison.Ordinal);
        Assert.Contains(capabilities, static capability => capability is ExpressionRelationQueryCapability expression
            && expression.Capability == ExprCapabilities.ForFunction(ExprFunctionNames.Any));
        Assert.Contains(capabilities, static capability => capability is ExpressionRelationQueryCapability expression
            && expression.Capability == ExprCapabilities.CurrentItem);
        Assert.Contains(capabilities, static capability => capability is GuaranteeRelationQueryCapability guarantee
            && guarantee.Kind == RelationQueryGuaranteeCapabilityKind.CollectionElementCorrelation);
        Assert.Contains(structural, static capability =>
            capability.Role == RelationQueryStructuralCapabilityRole.CurrentItemRead
            && capability.PathKind == RelationQueryStructuralPathKind.CollectionElement);
        Assert.DoesNotContain(structural, static capability =>
            capability.PathKind == RelationQueryStructuralPathKind.CollectionElement
            && capability.Role != RelationQueryStructuralCapabilityRole.CurrentItemRead);
        Assert.DoesNotContain(structural, static capability =>
            capability.Role == RelationQueryStructuralCapabilityRole.CurrentItemRead
            && capability.PathKind != RelationQueryStructuralPathKind.CollectionElement);
        Assert.DoesNotContain(structural, static capability =>
            capability.PathKind == RelationQueryStructuralPathKind.NestedCollectionElement);
    }

    static CosmosRelationQueryStorageBinding CreateStorageBinding(
        CosmosRelationQueryCollectionScopeEvidence? collectionScope) => new(
        new("loads/v1"),
        new("cosmos:loads"),
        new("placement:loads"),
        CosmosRelationQueryTargetProfile.Target,
        CosmosRelationQueryTargetProfile.ProfileId,
        new Uri("https://localhost:8081/"),
        "operations",
        "loads",
        "c",
        FieldPath.Parse("Id"),
        [new(new RelationQueryInputId("field:stops"), FieldPath.Parse("Stops"), collectionScope)]);

    static CosmosRelationQueryCollectionScopeEvidence Scope(
        CosmosRelationQueryCollectionElementScope elementScope = CosmosRelationQueryCollectionElementScope.JsonArrayElement,
        CosmosRelationQueryCollectionCorrelationGuarantee correlation = CosmosRelationQueryCollectionCorrelationGuarantee.SameArrayElement,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior collectionMissing = CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior collectionNull = CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior nullElement = CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
        CosmosRelationQueryEmptyCollectionBehavior empty = CosmosRelationQueryEmptyCollectionBehavior.NoElements,
        string scopeProfile = "tests/cosmos-json-array/v1",
        ImmutableArray<CosmosRelationQueryCollectionElementFieldBinding> children = default) => new(
        scopeProfile,
        elementScope,
        correlation,
        collectionMissing,
        collectionNull,
        nullElement,
        empty,
        children.IsDefault ? [Child(LocationPath), Child(TypePath)] : children);

    static CosmosRelationQueryCollectionElementFieldBinding Child(
        FieldPath elementPath,
        FieldPath? documentPath = null,
        CosmosRelationQueryCollectionElementValueDomain valueDomain = CosmosRelationQueryCollectionElementValueDomain.String,
        CosmosRelationQueryCollectionElementSemanticCapabilities capabilities = AllComparisonCapabilities,
        string? semanticProfile = "tests/cosmos-json-scalar/v1",
        CosmosRelationQueryStructuredCollectionAbsenceBehavior missing = CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion,
        CosmosRelationQueryStructuredCollectionAbsenceBehavior @null = CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion) => new(
        elementPath,
        documentPath ?? elementPath,
        valueDomain,
        capabilities,
        semanticProfile,
        missing,
        @null);

    const CosmosRelationQueryCollectionElementSemanticCapabilities AllComparisonCapabilities =
        CosmosRelationQueryCollectionElementSemanticCapabilities.ExactEquality
        | CosmosRelationQueryCollectionElementSemanticCapabilities.ExactInequality;

    const CosmosRelationQueryStructuredCollectionAbsenceBehavior Prohibited =
        CosmosRelationQueryStructuredCollectionAbsenceBehavior.ProhibitedByIngestion;
}
