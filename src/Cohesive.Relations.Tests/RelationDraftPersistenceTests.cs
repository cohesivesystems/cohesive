using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Tests the portable relation-draft model, strict persistence contract, and semantic fingerprint.
/// </summary>
public sealed class RelationDraftPersistenceTests
{
    static readonly GraphId DomainGraphId = new("domain/v1");
    static readonly QualifiedShapeId LoadShape = new(DomainGraphId, new("Load"));
    static readonly QualifiedShapeId SearchShape = new(DomainGraphId, new("LoadSearchDto"));
    static readonly ValueBindingId LoadBinding = new("load");
    static readonly ValueBindingId ResultBinding = new("loadSearch");

    [Fact]
    public void Document_RoundTrip_PreservesEveryClosedResolutionVariant()
    {
        var document = RelationDraftDocument.FromDraft(CreateDraft());

        var json = RelationDraftJsonSerializer.Serialize(document, indented: false);
        var roundTripped = RelationDraftJsonSerializer.Deserialize(json);
        var roundTrippedJson = RelationDraftJsonSerializer.Serialize(roundTripped, indented: false);

        var resolutions = roundTripped.Draft.Projection.Assignments
            .Select(static assignment => assignment.Resolution)
            .ToArray();
        Assert.Single(resolutions.OfType<SelectedRelationDraftAssignmentResolution>());
        Assert.Single(resolutions.OfType<OmittedRelationDraftAssignmentResolution>());
        Assert.Single(resolutions.OfType<UnresolvedRelationDraftAssignmentResolution>());
        Assert.Single(resolutions.OfType<AmbiguousRelationDraftAssignmentResolution>());
        Assert.Contains("\"$resolution\":\"selected\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$resolution\":\"omitted\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$resolution\":\"unresolved\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$resolution\":\"ambiguous\"", json, StringComparison.Ordinal);
        Assert.Equal(document.DraftFingerprint, roundTripped.DraftFingerprint);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(roundTrippedJson)));
    }

    [Fact]
    public void UnresolvedAndAmbiguousAssignments_AreValidPersistedDraftState()
    {
        var draft = CreateDraft();

        var draftValidation = RelationDraftValidator.Validate(draft);
        var document = RelationDraftDocument.FromDraft(draft);
        var documentValidation = RelationDraftDocumentSemanticValidator.Validate(document);
        var roundTripped = RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document, indented: false));

        Assert.True(draftValidation.IsValid);
        Assert.True(documentValidation.IsValid);
        Assert.Contains(
            roundTripped.Draft.Projection.Assignments,
            static assignment => assignment.Resolution is UnresolvedRelationDraftAssignmentResolution
            );
        Assert.Contains(
            roundTripped.Draft.Projection.Assignments,
            static assignment => assignment.Resolution is AmbiguousRelationDraftAssignmentResolution
            );
    }

    [Fact]
    public void Deserialize_RejectsUnknownPropertiesAndDuplicateProperties()
    {
        var json = SerializeCurrentDocument();

        var unknownRoot = JsonNode.Parse(json)!.AsObject();
        unknownRoot["unexpected"] = true;
        Assert.Throws<JsonException>(() => RelationDraftJsonSerializer.Deserialize(unknownRoot.ToJsonString()));

        var unknownResolutionProperty = JsonNode.Parse(json)!.AsObject();
        FindResolution(unknownResolutionProperty, "selected")["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            RelationDraftJsonSerializer.Deserialize(unknownResolutionProperty.ToJsonString()));

        var duplicateSchemaVersion = json.Replace(
            "\"schemaVersion\":\"relation-draft/v1\"",
            "\"schemaVersion\":\"relation-draft/v1\",\"schemaVersion\":\"relation-draft/v1\"",
            StringComparison.Ordinal);
        Assert.NotEqual(json, duplicateSchemaVersion);
        Assert.Throws<JsonException>(() =>
            RelationDraftJsonSerializer.Deserialize(duplicateSchemaVersion));
    }

    [Fact]
    public void Deserialize_RejectsMissingAndUnknownResolutionDiscriminators()
    {
        var json = SerializeCurrentDocument();

        var missingDiscriminator = JsonNode.Parse(json)!.AsObject();
        FindResolution(missingDiscriminator, "selected").Remove("$resolution");
        Assert.Throws<JsonException>(() =>
            RelationDraftJsonSerializer.Deserialize(missingDiscriminator.ToJsonString()));

        var unknownDiscriminator = JsonNode.Parse(json)!.AsObject();
        FindResolution(unknownDiscriminator, "selected")["$resolution"] = "ranked";
        Assert.Throws<JsonException>(() =>
            RelationDraftJsonSerializer.Deserialize(unknownDiscriminator.ToJsonString()));
    }

    [Fact]
    public void Deserialize_RejectsMissingRequiredDraftMembers()
    {
        AssertMutationRejected(document => document["draft"]!.AsObject().Remove("outputMode"));
        AssertMutationRejected(document =>
            document["draft"]!["projection"]!.AsObject().Remove("assignments"));
        AssertMutationRejected(document =>
            document["draft"]!["projection"]!["assignments"]![0]!
                .AsObject()
                .Remove("candidates"));
        AssertMutationRejected(document =>
            FindResolution(document, RelationDraftWireNames.UnresolvedResolution)
                .Remove("reasons"));
        AssertMutationRejected(document =>
            document["draft"]!["projection"]!["assignments"]![0]!["target"]!["segments"]![0]!
                .AsObject()
                .Remove("kind"));
    }

    [Fact]
    public void Deserialize_RejectsWrongCaseAndNumericEnumValues()
    {
        AssertMutationRejected(document =>
            document["draft"]!["outputMode"] = "onePerRoot");
        AssertMutationRejected(document =>
            document["draft"]!["outputMode"] = 0);
        AssertMutationRejected(document =>
            FindResolution(document, RelationDraftWireNames.UnresolvedResolution)["reasons"]![0] =
                "noCandidate");
        AssertMutationRejected(document =>
            document["draft"]!["projection"]!["assignments"]![0]!["target"]!["segments"]![0]!["kind"] =
                "999");
    }

    [Fact]
    public void TryDeserialize_ReportsNullLogicalEntriesAsStructuredInputErrors()
    {
        var nullNode = JsonNode.Parse(SerializeCurrentDocument())!.AsObject();
        nullNode["draft"]!["input"]!["nodes"]![0] = null;
        var nodeValidation = RelationDraftJsonSerializer.TryDeserialize(
            nullNode.ToJsonString(),
            out var nodeDocument);

        var nullParameter = JsonNode.Parse(SerializeCurrentDocument())!.AsObject();
        nullParameter["draft"]!["input"]!["parameters"]!.AsArray().Add(null);
        var parameterValidation = RelationDraftJsonSerializer.TryDeserialize(
            nullParameter.ToJsonString(),
            out var parameterDocument);

        Assert.False(nodeValidation.IsValid);
        Assert.Null(nodeDocument);
        AssertDiagnostic(nodeValidation, "relationDraft.deserialize.invalid");
        Assert.False(parameterValidation.IsValid);
        Assert.Null(parameterDocument);
        AssertDiagnostic(parameterValidation, "relationDraft.deserialize.invalid");
    }

    [Fact]
    public void Deserialize_RejectsMissingTraversalJoinSemantics()
    {
        var json = RelationDraftJsonSerializer.Serialize(
            RelationDraftDocument.FromDraft(CreateTraversalDraft()),
            indented: false);

        foreach (var property in new[] { "joinKind", "requirement" })
        {
            var document = JsonNode.Parse(json)!.AsObject();
            var traversal = document["draft"]!["input"]!["nodes"]!
                .AsArray()
                .Select(static node => node!.AsObject())
                .Single(static node => string.Equals(
                    node["$node"]!.GetValue<string>(),
                    "traverseRelationship",
                    StringComparison.Ordinal));
            Assert.True(traversal.Remove(property));
            Assert.Throws<JsonException>(() =>
                RelationDraftJsonSerializer.Deserialize(document.ToJsonString()));
        }
    }

    [Fact]
    public void Fingerprint_ExcludesLifecycleIdentityAndMetadata_ButIncludesSemantics()
    {
        var draft = CreateDraft();
        var idSlot = GetAssignment(draft, "assign-id");
        var idCandidate = Assert.Single(idSlot.Candidates);
        var lifecycleRevision = draft with { Id = new RelationDraftId("draft/load-search/revision-2") };
        var first = RelationDraftDocument.FromDraft(
            draft,
            new RelationDraftDocumentMetadata(
                origin: DocumentOrigin.Generated,
                name: "Convention draft",
                producer: "cohesive-relations",
                createdAtUtc: new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero),
                producerArtifacts:
                [
                    new("ari/relation-proposal", "proposal-42")
                ],
                conventionDecisions:
                [
                    new(
                        DirectFieldRelationDraftConventionMatcher.ExactOrdinalRuleId,
                        idSlot.Id,
                        idCandidate.Id,
                        LoadBinding,
                        FieldPath.FromField("Id"),
                        idSlot.Target)
                ]));
        var second = RelationDraftDocument.FromDraft(
            lifecycleRevision,
            new RelationDraftDocumentMetadata(
                origin: DocumentOrigin.Imported,
                name: "Ari workbench draft",
                producer: "ari",
                updatedAtUtc: new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero)));
        var semanticChange = draft with { OutputMode = RelationOutputMode.ZeroOrOnePerRoot };

        Assert.NotEqual(
            RelationDraftJsonSerializer.Serialize(first, indented: false),
            RelationDraftJsonSerializer.Serialize(second, indented: false));
        Assert.Equal(first.DraftFingerprint, second.DraftFingerprint);
        Assert.Equal("relation-draft/v1-c14n/v2", first.DraftFingerprint.Canonicalization);
        Assert.NotEqual(
            first.DraftFingerprint,
            RelationDraftFingerprinter.Compute(semanticChange));
        Assert.Single(first.Metadata.ProducerArtifacts);
        Assert.Single(first.Metadata.ConventionDecisions);
    }

    [Fact]
    public void AriProposalFixture_KeepsInferenceEvidenceOutsidePortableDraftSemantics()
    {
        var draft = CreateDraft();
        var slot = GetAssignment(draft, "assign-id");
        var candidate = Assert.Single(slot.Candidates);
        var document = RelationDraftDocument.FromDraft(
            draft,
            new RelationDraftDocumentMetadata(
                origin: DocumentOrigin.Generated,
                producer: "ari",
                producerVersion: "fixture/v1",
                producerArtifacts:
                [
                    new("ari/relation-proposal", "ari-proposal-42")
                ]));
        var first = new AriRelationProposalFixture(
            "ari-proposal-42",
            document,
            ImmutableDictionary<RelationDraftCandidateId, AriCandidateEvidence>.Empty.Add(
                candidate.Id,
                new(0.72, "edi-domain-matcher/v4", ["name", "context"])));
        var reranked = first with
        {
            CandidateEvidence = first.CandidateEvidence.SetItem(
                candidate.Id,
                new(0.94, "edi-domain-matcher/v5", ["name", "context", "review"]))
        };

        Assert.Equal(first.PortableDraft.DraftFingerprint, reranked.PortableDraft.DraftFingerprint);
        Assert.Equal(slot.Id, Assert.Single(
            reranked.PortableDraft.Draft.Projection.Assignments,
            assignment => assignment.Candidates.Any(item => item.Id == candidate.Id)).Id);
        Assert.DoesNotContain(
            "confidence",
            RelationDraftJsonSerializer.Serialize(reranked.PortableDraft, indented: false),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FingerprintAndSerialization_AreInvariantToSetLikeCollectionOrder()
    {
        var first = RelationDraftDocument.FromDraft(CreateDraft(reverseCollections: false));
        var second = RelationDraftDocument.FromDraft(CreateDraft(reverseCollections: true));

        Assert.Equal(first.DraftFingerprint, second.DraftFingerprint);
        Assert.Equal(
            RelationDraftJsonSerializer.Serialize(first, indented: false),
            RelationDraftJsonSerializer.Serialize(second, indented: false));
    }

    [Fact]
    public void Fingerprint_IsInvariantToUnresolvedReasonSetOrder()
    {
        var draft = CreateDraft();
        var unresolved = GetAssignment(draft, "assign-unresolved");
        var firstResolution = new UnresolvedRelationDraftAssignmentResolution(
            [
                RelationDraftUnresolvedReason.NoCandidate,
                RelationDraftUnresolvedReason.UnsupportedStructure
            ]);
        var reversedResolution = firstResolution with
        {
            Reasons =
            [
                RelationDraftUnresolvedReason.UnsupportedStructure,
                RelationDraftUnresolvedReason.NoCandidate
            ]
        };
        var first = ReplaceResolution(draft, unresolved.Id.Value, firstResolution);
        var reversed = ReplaceResolution(draft, unresolved.Id.Value, reversedResolution);

        Assert.True(RelationDraftValidator.Validate(first).IsValid);
        Assert.True(RelationDraftValidator.Validate(reversed).IsValid);
        Assert.Equal(
            RelationDraftFingerprinter.Compute(first),
            RelationDraftFingerprinter.Compute(reversed));
    }

    [Fact]
    public void Validator_RejectsSelectedAndAmbiguousReferencesOutsideTheirSlot()
    {
        var draft = CreateDraft();
        var ambiguousCandidate = GetAssignment(draft, "assign-ambiguous").Candidates[0].Id;
        var invalidSelected = ReplaceResolution(
            draft,
            assignmentId: "assign-id",
            new SelectedRelationDraftAssignmentResolution(new("candidate-not-declared")));
        var invalidAmbiguous = ReplaceResolution(
            draft,
            assignmentId: "assign-ambiguous",
            new AmbiguousRelationDraftAssignmentResolution(
                [ambiguousCandidate, new("candidate-not-declared")]));

        var selectedValidation = RelationDraftValidator.Validate(invalidSelected);
        var ambiguousValidation = RelationDraftValidator.Validate(invalidAmbiguous);

        AssertDiagnostic(selectedValidation, "relationDraft.resolution.selectedCandidateUnknown");
        AssertDiagnostic(ambiguousValidation, "relationDraft.resolution.ambiguousCandidateUnknown");
        Assert.Throws<ArgumentException>(() => RelationDraftDocument.FromDraft(invalidSelected));
        Assert.Throws<ArgumentException>(() => RelationDraftDocument.FromDraft(invalidAmbiguous));
    }

    [Fact]
    public void Validator_RejectsUnderspecifiedAndDuplicateAmbiguousReferences()
    {
        var draft = CreateDraft();
        var ambiguousCandidate = GetAssignment(draft, "assign-ambiguous").Candidates[0].Id;
        var tooFew = ReplaceResolution(
            draft,
            assignmentId: "assign-ambiguous",
            new AmbiguousRelationDraftAssignmentResolution([ambiguousCandidate]));
        var duplicate = ReplaceResolution(
            draft,
            assignmentId: "assign-ambiguous",
            new AmbiguousRelationDraftAssignmentResolution(
                [ambiguousCandidate, ambiguousCandidate]));

        AssertDiagnostic(
            RelationDraftValidator.Validate(tooFew),
            "relationDraft.resolution.ambiguousCandidateCountInvalid");
        AssertDiagnostic(
            RelationDraftValidator.Validate(duplicate),
            "relationDraft.resolution.ambiguousCandidateDuplicate");
    }

    [Fact]
    public void Validator_RejectsCandidateIdentityThatDoesNotMatchItsSlotAndExpression()
    {
        var draft = CreateDraft();
        var slot = GetAssignment(draft, "assign-id");
        var staleCandidate = Assert.Single(slot.Candidates) with
        {
            Id = new RelationDraftCandidateId("stale-candidate-id")
        };
        var stale = draft with
        {
            Projection = draft.Projection with
            {
                Assignments =
                [
                    .. draft.Projection.Assignments.Select(assignment =>
                        assignment.Id == slot.Id
                            ? assignment with { Candidates = [staleCandidate] }
                            : assignment)
                ]
            }
        };

        AssertDiagnostic(
            RelationDraftValidator.Validate(stale),
            "relationDraft.candidate.idMismatch");
    }

    [Fact]
    public void CandidateIdentityV2_PreservesTheAssignmentSlotV1Vector()
    {
        var slot = RelationDraftIdentityConvention.CreateAssignmentSlotId(
            new QualifiedShapeId(new GraphId("test"), new ShapeId("Load")),
            FieldPath.FromField("Id"));
        var candidate = RelationDraftIdentityConvention.CreateCandidateId(
            slot,
            Expr.Const(1.0000000000000001e18));

        Assert.Equal(
            "relation-draft-slot:v1:sha256:0a5c1330246e8a41ceab8007ca8eda903a37e9af26f25647fb0739e4cd9cfdc4",
            slot.Value);
        Assert.StartsWith("relation-draft-candidate:v2:sha256:", candidate.Value, StringComparison.Ordinal);
        Assert.Equal("relation-draft-candidate-identity/v2", RelationDraftIdentityConvention.CandidateVersion);
    }

    [Fact]
    public void DocumentValidator_RejectsDanglingConventionAttribution()
    {
        var draft = CreateDraft();
        var document = RelationDraftDocument.FromDraft(
            draft,
            new RelationDraftDocumentMetadata(
                producer: "convention-matcher",
                conventionDecisions:
                [
                    new(
                        DirectFieldRelationDraftConventionMatcher.ExactOrdinalRuleId,
                        new QueryAssignmentId("unknown-slot"),
                        candidateId: null,
                        sourceBinding: LoadBinding,
                        source: null,
                        target: FieldPath.FromField("Id"))
                ]));

        var validation = RelationDraftDocumentSemanticValidator.Validate(document);

        AssertDiagnostic(validation, "relationDraft.metadata.conventionDecisionSlotUnknown");
        Assert.Throws<JsonException>(() => RelationDraftJsonSerializer.Deserialize(RelationDraftJsonSerializer.Serialize(document, indented: false)));
    }

    [Fact]
    public void DocumentValidator_RejectsMalformedConventionAttributionMetadata()
    {
        var draft = CreateDraft();
        var slot = GetAssignment(draft, "assign-id");
        var candidate = Assert.Single(slot.Candidates);
        var malformedDecision = new RelationDraftConventionDecision(
            DirectFieldRelationDraftConventionMatcher.ExactOrdinalRuleId,
            slot.Id,
            candidate.Id,
            LoadBinding,
            FieldPath.FromField("Id"),
            slot.Target) with
        {
            RuleId = " ",
            SourceBinding = default
        };
        var document = RelationDraftDocument.FromDraft(
            draft,
            new RelationDraftDocumentMetadata(
                producer: "convention-matcher",
                conventionDecisions: [malformedDecision]));

        var validation = RelationDraftDocumentSemanticValidator.Validate(document);

        AssertDiagnostic(
            validation,
            "relationDraft.metadata.conventionDecisionRuleIdMissing");
        AssertDiagnostic(
            validation,
            "relationDraft.metadata.conventionDecisionSourceBindingMissing");
    }

    static RelationDraft CreateDraft(bool reverseCollections = false)
    {
        var source = new SourceQueryNode(new("source-load"), LoadBinding, LoadShape);
        var filter = new FilterQueryNode(new("filter-load"), source.Id, Expr.Const(true));
        ImmutableArray<LogicalQueryNode> nodes = [source, filter];

        QueryAssignmentId idSlotId = new("assign-id");
        QueryAssignmentId ambiguousSlotId = new("assign-ambiguous");
        var selectedValue = Expr.Field(LoadBinding, "Id");
        var selectedCandidate = new RelationDraftCandidate(
            RelationDraftIdentityConvention.CreateCandidateId(idSlotId, selectedValue),
            selectedValue
            );
        var customerIdValue = Expr.Field(LoadBinding, "CustomerId");
        var customerIdCandidate = new RelationDraftCandidate(
            RelationDraftIdentityConvention.CreateCandidateId(ambiguousSlotId, customerIdValue),
            customerIdValue
            );
        var alternateCustomerValue = Expr.Field(LoadBinding, "AlternateCustomerId");
        var alternateCustomerCandidate = new RelationDraftCandidate(
            RelationDraftIdentityConvention.CreateCandidateId(ambiguousSlotId, alternateCustomerValue),
            alternateCustomerValue
            );
        ImmutableArray<RelationDraftCandidate> ambiguousCandidates = [customerIdCandidate, alternateCustomerCandidate];
        ImmutableArray<RelationDraftCandidateId> ambiguousCandidateIds = [customerIdCandidate.Id, alternateCustomerCandidate.Id];

        ImmutableArray<RelationDraftAssignmentSlot> assignments =
        [
            new(
                idSlotId,
                FieldPath.FromField("Id"),
                [selectedCandidate],
                new SelectedRelationDraftAssignmentResolution(selectedCandidate.Id)),
            new(
                new("assign-optional"),
                FieldPath.FromField("OptionalText"),
                candidates: [],
                OmittedRelationDraftAssignmentResolution.Instance),
            new(
                new("assign-unresolved"),
                FieldPath.FromField("CustomerName"),
                candidates: [],
                new UnresolvedRelationDraftAssignmentResolution(
                    [RelationDraftUnresolvedReason.NoCandidate])),
            new(
                ambiguousSlotId,
                FieldPath.FromField("CustomerId"),
                ambiguousCandidates,
                new AmbiguousRelationDraftAssignmentResolution(ambiguousCandidateIds))
        ];
        
        ImmutableArray<InvariantDefinition> invariants =
        [
            new("output-has-id", Expr.Const(true)),
            new("output-is-valid", Expr.Const(true))
        ];

        if (reverseCollections)
        {
            nodes = [.. nodes.Reverse()];
            ambiguousCandidates = [.. ambiguousCandidates.Reverse()];
            ambiguousCandidateIds = [.. ambiguousCandidateIds.Reverse()];
            assignments =
            [
                .. assignments
                    .Select(assignment => assignment.Id.Value == "assign-ambiguous"
                        ? new RelationDraftAssignmentSlot(
                            assignment.Id,
                            assignment.Target,
                            ambiguousCandidates,
                            new AmbiguousRelationDraftAssignmentResolution(ambiguousCandidateIds))
                        : assignment)
                    .Reverse()
            ];
            invariants = [.. invariants.Reverse()];
        }

        return new(
            id: new("draft/load-search"),
            relationId: new("load-search"),
            name: new("Load search projection"),
            input: new(nodes),
            rootBinding: LoadBinding,
            projection: new(
                id: new("project-load-search"),
                input: filter.Id,
                resultBinding: ResultBinding,
                resultShape: SearchShape,
                assignments
                ),
            outputMode: RelationOutputMode.OnePerRoot,
            outputKey: Expr.Field(ResultBinding, "Id"),
            invariants
            );
    }

    static RelationDraft CreateTraversalDraft()
    {
        var source = new SourceQueryNode(new("source-load"), LoadBinding, LoadShape);
        var traversal = new TraverseRelationshipQueryNode(
            new QueryNodeId("traverse-customer"),
            source.Id,
            LoadBinding,
            new RelationshipId("load-customer"),
            RelationshipTraversalDirection.Forward,
            new ValueBindingId("customer"),
            JoinKind.Left,
            QueryInputRequirement.Required);
        QueryAssignmentId slotId = new("assign-id");
        var value = Expr.Field(LoadBinding, "Id");
        var candidate = new RelationDraftCandidate(
            RelationDraftIdentityConvention.CreateCandidateId(slotId, value),
            value);
        return new(
            new RelationDraftId("draft/traversal"),
            new RelationId("traversal-relation"),
            new RelationName("Traversal relation"),
            new LogicalQueryDefinition([source, traversal]),
            LoadBinding,
            new RelationDraftProjection(
                new QueryNodeId("project-traversal"),
                traversal.Id,
                ResultBinding,
                SearchShape,
                [
                    new(
                        slotId,
                        FieldPath.FromField("Id"),
                        [candidate],
                        new SelectedRelationDraftAssignmentResolution(candidate.Id))
                ]));
    }

    static RelationDraft ReplaceResolution(
        RelationDraft draft,
        string assignmentId,
        RelationDraftAssignmentResolution resolution)
    {
        var assignments = draft.Projection.Assignments
            .Select(assignment => string.Equals(assignment.Id.Value, assignmentId, StringComparison.Ordinal)
                ? assignment with { Resolution = resolution }
                : assignment)
            .ToImmutableArray();
        return draft with
        {
            Projection = draft.Projection with { Assignments = assignments }
        };
    }

    static RelationDraftAssignmentSlot GetAssignment(RelationDraft draft, string assignmentId) =>
        Assert.Single(draft.Projection.Assignments, assignment =>
            string.Equals(assignment.Id.Value, assignmentId, StringComparison.Ordinal));

    static JsonObject FindResolution(JsonObject document, string discriminator) =>
        document["draft"]!["projection"]!["assignments"]!
            .AsArray()
            .Select(static assignment => assignment!["resolution"]!.AsObject())
            .Single(resolution => string.Equals(
                resolution["$resolution"]!.GetValue<string>(),
                discriminator,
                StringComparison.Ordinal));

    static void AssertMutationRejected(Action<JsonObject> mutate)
    {
        var document = JsonNode.Parse(SerializeCurrentDocument())!.AsObject();
        mutate(document);
        Assert.Throws<JsonException>(() =>
            RelationDraftJsonSerializer.Deserialize(document.ToJsonString()));
    }

    static string SerializeCurrentDocument() =>
        RelationDraftJsonSerializer.Serialize(
            RelationDraftDocument.FromDraft(CreateDraft()),
            indented: false);

    static void AssertDiagnostic(DocumentValidationResult result, string code) =>
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);

    sealed record AriRelationProposalFixture(
        string ProposalId,
        RelationDraftDocument PortableDraft,
        ImmutableDictionary<RelationDraftCandidateId, AriCandidateEvidence> CandidateEvidence);

    sealed record AriCandidateEvidence(
        double Confidence,
        string Model,
        ImmutableArray<string> Signals);
}
