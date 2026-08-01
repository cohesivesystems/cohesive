using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationBackendPoolTests
{
    [Fact]
    public void Definition_NormalizesMembersAndFingerprintsSetLikeOrderDeterministically()
    {
        var first = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var second = MaterializationBackendPoolTestFixture.Descriptor("target/b");
        var forward = MaterializationBackendPoolTestFixture.Definition(
            [first, second],
            defaultTarget: first.Id);
        var reverse = MaterializationBackendPoolTestFixture.Definition(
            [second, first],
            defaultTarget: first.Id);

        var forwardDocument = MaterializationBackendPoolDocument.FromDefinition(forward);
        var reverseDocument = MaterializationBackendPoolDocument.FromDefinition(reverse);

        Assert.Equal([first.Id, second.Id], forward.Members.Select(static member => member.Id));
        Assert.Equal(forward, reverse);
        Assert.Equal(forward.GetHashCode(), reverse.GetHashCode());
        Assert.Equal(forwardDocument.DefinitionFingerprint, reverseDocument.DefinitionFingerprint);
        Assert.Equal(
            MaterializationBackendPoolJsonSerializer.GetCanonicalBytes(forwardDocument),
            MaterializationBackendPoolJsonSerializer.GetCanonicalBytes(reverseDocument));
    }

    [Fact]
    public void Definition_EqualitySurvivesCanonicalReprojectionWithIndependentCapabilityCollections()
    {
        var member = MaterializationBackendPoolTestFixture.Descriptor(
            "target/a",
            includeEvidence: true);
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [member],
            defaultTarget: member.Id);
        var json = MaterializationBackendPoolJsonSerializer.Serialize(
            MaterializationBackendPoolDocument.FromDefinition(definition),
            Cohesive.Model.Serialization.PortableDocumentJsonFormatting.Compact);

        var restored = MaterializationBackendPoolJsonSerializer.Deserialize(json).Definition;

        Assert.Equal(definition, restored);
        Assert.Equal(definition.GetHashCode(), restored.GetHashCode());
    }

    [Fact]
    public void Definition_RejectsInvalidMembershipAndUnsafeDefaultDeclarations()
    {
        var member = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var duplicate = MaterializationBackendPoolTestFixture.Descriptor(
            "target/a",
            profileId: "profile/duplicate");
        var foreign = MaterializationBackendPoolTestFixture.Descriptor(
            "target/foreign",
            materialization: new("materialization/foreign"));

        Assert.Throws<ArgumentException>(() => MaterializationBackendPoolTestFixture.Definition(
            [],
            defaultTarget: null));
        Assert.Throws<ArgumentException>(() => MaterializationBackendPoolTestFixture.Definition(
            [member, null!],
            defaultTarget: member.Id));
        Assert.Throws<ArgumentException>(() => MaterializationBackendPoolTestFixture.Definition(
            [member, duplicate],
            defaultTarget: member.Id));
        Assert.Throws<ArgumentException>(() => MaterializationBackendPoolTestFixture.Definition(
            [member, foreign],
            defaultTarget: member.Id));
        Assert.Throws<ArgumentException>(() => MaterializationBackendPoolTestFixture.Definition(
            [member],
            defaultTarget: new("target/undeclared")));
        Assert.Throws<ArgumentException>(() => MaterializationBackendPoolTestFixture.Definition(
            [member],
            defaultTarget: default(MaterializationTargetId)));
    }

    [Fact]
    public void Fingerprint_CoversMembershipDefaultDefinitionAndProvenance()
    {
        var first = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var second = MaterializationBackendPoolTestFixture.Descriptor("target/b");
        var baseline = MaterializationBackendPoolTestFixture.Definition(
            [first, second],
            defaultTarget: first.Id);
        var differentMember = MaterializationBackendPoolTestFixture.Definition(
            [first, MaterializationBackendPoolTestFixture.Descriptor("target/c")],
            defaultTarget: first.Id);
        var differentDefault = MaterializationBackendPoolTestFixture.Definition(
            [first, second],
            defaultTarget: second.Id);
        var differentDefinition = MaterializationBackendPoolTestFixture.Definition(
            [first, second],
            defaultTarget: first.Id,
            definitionFingerprint: new(
                MaterializationDefinitionFingerprinter.Algorithm,
                MaterializationDefinitionFingerprinter.Canonicalization,
                "different-definition"));
        var differentProvenance = MaterializationBackendPoolTestFixture.Definition(
            [first, second],
            defaultTarget: first.Id,
            provenanceReference: "tests/backend-pool/other-source");

        var fingerprints = new[]
        {
            baseline,
            differentMember,
            differentDefault,
            differentDefinition,
            differentProvenance
        }.Select(MaterializationBackendPoolFingerprinter.Compute).ToArray();

        Assert.Equal(fingerprints.Length, fingerprints.Distinct().Count());
    }

    [Fact]
    public void InMemoryPool_ResolvesOnlyExactDeclaredDependencies()
    {
        var firstDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var secondDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/b");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [firstDescriptor, secondDescriptor],
            defaultTarget: firstDescriptor.Id);
        IMaterializationTarget first = new InMemoryMaterializationTarget(firstDescriptor);
        IMaterializationTarget second = new InMemoryMaterializationTarget(secondDescriptor);

        IMaterializationTargetPool pool = new InMemoryMaterializationTargetPool(
            definition,
            [second, first]);

        Assert.Same(first, pool.Resolve(firstDescriptor.Id));
        Assert.Same(second, pool.Resolve(secondDescriptor.Id));
        Assert.Throws<KeyNotFoundException>(() => pool.Resolve(new("target/missing")));
        Assert.Throws<ArgumentException>(() => pool.Resolve(default));
    }

    [Fact]
    public void InMemoryPool_RejectsMissingExtraDuplicateAndDescriptorDrift()
    {
        var firstDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/a");
        var secondDescriptor = MaterializationBackendPoolTestFixture.Descriptor("target/b");
        var definition = MaterializationBackendPoolTestFixture.Definition(
            [firstDescriptor, secondDescriptor],
            defaultTarget: firstDescriptor.Id);
        IMaterializationTarget first = new InMemoryMaterializationTarget(firstDescriptor);
        IMaterializationTarget second = new InMemoryMaterializationTarget(secondDescriptor);
        IMaterializationTarget extra = new InMemoryMaterializationTarget(
            MaterializationBackendPoolTestFixture.Descriptor("target/extra"));
        IMaterializationTarget drifted = new InMemoryMaterializationTarget(
            MaterializationBackendPoolTestFixture.Descriptor(
                firstDescriptor.Id.Value,
                profileId: "profile/drifted"));

        Assert.Throws<ArgumentException>(() => new InMemoryMaterializationTargetPool(
            definition,
            [first]));
        Assert.Throws<ArgumentException>(() => new InMemoryMaterializationTargetPool(
            definition,
            [first, second, extra]));
        Assert.Throws<ArgumentException>(() => new InMemoryMaterializationTargetPool(
            definition,
            [first, first, second]));
        Assert.Throws<ArgumentException>(() => new InMemoryMaterializationTargetPool(
            definition,
            [drifted, second]));
    }
}

internal static class MaterializationBackendPoolTestFixture
{
    public static readonly MaterializationId Materialization = new("materialization/backend-pool");

    public static readonly ExecutionDefinitionFingerprint DefinitionFingerprint = new(
        MaterializationDefinitionFingerprinter.Algorithm,
        MaterializationDefinitionFingerprinter.Canonicalization,
        "0123456789abcdef");

    public static MaterializationBackendPoolDefinition Definition(
        ImmutableArray<MaterializationTargetDescriptor> members,
        MaterializationTargetId? defaultTarget,
        string poolId = "pool/search",
        ExecutionDefinitionFingerprint? definitionFingerprint = null,
        string provenanceReference = "tests/backend-pool") =>
        new(
            new(poolId),
            Materialization,
            definitionFingerprint ?? DefinitionFingerprint,
            members,
            defaultTarget,
            Provenance(provenanceReference));

    public static MaterializationTargetDescriptor Descriptor(
        string id,
        MaterializationId? materialization = null,
        string? profileId = null,
        bool includeEvidence = false)
    {
        MaterializationTargetId targetId = new(id);
        ImmutableArray<MaterializationCapabilityEvidence> evidence = includeEvidence
            ?
            [
                new(
                    new("evidence/seal"),
                    MaterializationCapabilityKind.TargetSeal,
                    CapabilityRealizationKind.Native,
                    [MaterializationGuaranteeKind.FencedMutation],
                    [],
                    ["tests/backend-pool"])
            ]
            : [];
        return new(
            targetId,
            materialization ?? Materialization,
            new(
                new(profileId ?? $"profile/{id}"),
                MaterializationEndpointRole.Target,
                targetId.Value,
                evidence));
    }

    public static ExecutionProvenance Provenance(string reference) =>
        new(
            new("cohesive-tests", "1"),
            new(reference),
            DocumentOrigin.Generated);
}
