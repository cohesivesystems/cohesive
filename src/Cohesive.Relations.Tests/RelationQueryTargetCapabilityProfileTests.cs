using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryTargetCapabilityProfileTests
{
    static readonly RelationQueryOperatingBoundaryId PageBoundary = new("boundary/page");
    static readonly RelationQueryOperatingBoundaryId ProviderBoundary = new("boundary/provider");

    [Fact]
    public void HasSameSemantics_NormalizesDeclarationsAndIgnoresDescriptions()
    {
        var first = CreateProfile(metadata: "first", reverseDeclarations: false);
        var second = CreateProfile(metadata: "second", reverseDeclarations: true);

        Assert.True(first.HasSameSemantics(second));
        Assert.True(second.HasSameSemantics(first));
    }

    [Fact]
    public void HasSameSemantics_DetectsEverySemanticContractCategory()
    {
        var baseline = CreateProfile(metadata: "baseline", reverseDeclarations: false);
        var pageBoundary = baseline.OperatingBoundaries.Single(boundary => boundary.Id == PageBoundary);
        var filterEvidence = baseline.Capabilities.Single(evidence =>
            evidence.Id == new RelationQueryTargetCapabilityEvidenceId("evidence/filter"));

        RelationQueryTargetCapabilityProfile[] changed =
        [
            Copy(baseline, target: new("target/other")),
            Copy(baseline, id: new("target/profile/v2")),
            Copy(baseline, definitionVersions: ["schema/v1"]),
            Copy(baseline, compilerProfiles: ["compiler/v1"]),
            Copy(
                baseline,
                boundaries:
                [
                    .. baseline.OperatingBoundaries.Where(boundary => boundary.Id != PageBoundary),
                    new(pageBoundary.Id, pageBoundary.Kind, pageBoundary.Limit + 1)
                ]),
            Copy(
                baseline,
                capabilities:
                [
                    .. baseline.Capabilities.Where(evidence => evidence.Id != filterEvidence.Id),
                    new(
                        filterEvidence.Id,
                        new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Projection),
                        filterEvidence.OperatingBoundaries)
                ]),
            Copy(
                baseline,
                capabilities:
                [
                    .. baseline.Capabilities.Where(evidence => evidence.Id != filterEvidence.Id),
                    new(filterEvidence.Id, filterEvidence.Capability, [PageBoundary])
                ])
        ];

        Assert.All(changed, profile => Assert.False(baseline.HasSameSemantics(profile)));
        Assert.False(baseline.HasSameSemantics(null));
    }

    static RelationQueryTargetCapabilityProfile CreateProfile(string metadata, bool reverseDeclarations)
    {
        ImmutableArray<string> definitionVersions = ["schema/v2", "schema/v1"];
        ImmutableArray<string> compilerProfiles = ["compiler/v2", "compiler/v1"];
        ImmutableArray<RelationQueryOperatingBoundary> boundaries =
        [
            new(PageBoundary, RelationQueryOperatingBoundaryKind.MaximumPageSize, 100, $"page {metadata}"),
            new(
                ProviderBoundary,
                RelationQueryOperatingBoundaryKind.DeterministicProvider,
                description: $"provider {metadata}")
        ];
        ImmutableArray<RelationQueryTargetCapabilityEvidence> capabilities =
        [
            new(
                new("evidence/order"),
                new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Ordering),
                [ProviderBoundary, PageBoundary],
                $"ordering {metadata}"),
            new(
                new("evidence/filter"),
                new LogicalRelationQueryCapability(RelationQueryLogicalCapabilityKind.Filter),
                [ProviderBoundary],
                $"filter {metadata}"),
            new(
                new("evidence/equality"),
                new ExpressionRelationQueryCapability(
                    ExprCapabilities.ForBinary(BinaryOperator.Eq),
                    ExprCapabilityRequirementKind.Operation),
                description: $"equality {metadata}")
        ];

        if (reverseDeclarations)
        {
            definitionVersions = [.. definitionVersions.Reverse()];
            compilerProfiles = [.. compilerProfiles.Reverse()];
            boundaries = [.. boundaries.Reverse()];
            capabilities = [.. capabilities.Reverse()];
        }

        return new(
            new("target/test"),
            new("target/profile/v1"),
            definitionVersions,
            compilerProfiles,
            capabilities,
            boundaries,
            $"profile {metadata}");
    }

    static RelationQueryTargetCapabilityProfile Copy(
        RelationQueryTargetCapabilityProfile source,
        RelationQueryTargetId? target = null,
        RelationQueryTargetProfileId? id = null,
        ImmutableArray<string>? definitionVersions = null,
        ImmutableArray<string>? compilerProfiles = null,
        ImmutableArray<RelationQueryTargetCapabilityEvidence>? capabilities = null,
        ImmutableArray<RelationQueryOperatingBoundary>? boundaries = null) =>
        new(
            target ?? source.Target,
            id ?? source.Id,
            definitionVersions ?? source.SupportedDefinitionSchemaVersions,
            compilerProfiles ?? source.SupportedCompilerProfiles,
            capabilities ?? source.Capabilities,
            boundaries ?? source.OperatingBoundaries,
            source.Description);
}
