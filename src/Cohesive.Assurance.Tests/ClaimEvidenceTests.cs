using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Authoring;
using Cohesive.Model;

namespace Cohesive.Assurance.Tests;

public sealed class ClaimEvidenceTests
{
    public sealed record Stock(int OnHand, int Reserved);
    static ClaimReference R(string name) => new(new("test://" + name), "1", name);
    static readonly ClaimReference Method = R("verifier/v1"), Configuration = R("verifier/config");
    static readonly EvidenceSubject Subject = new(EvidenceTargetKind.Implementation, R("candidate"),
        R("candidate/config"), R("all-orders"), ProofObligation.Realization);
    static ClaimDocument Claim(string revision = "1")
    {
        var stock = new ExpressionAuthoring().Input<Stock>("stock");
        return Assurance.Claim.Define("stock", revision, R("stock"), R("mapping"), R("source"), R("authoring/config"))
            .AtEveryCommittedState(R("stock-selection"), InitialStateSelection.Include)
            .Require(stock, ClaimInputRole.State, s => s.Reserved <= s.OnHand).Build();
    }
    static ClaimEvidence Report(EvidenceKind kind = EvidenceKind.FormalProof,
        ClaimVerdict verdict = ClaimVerdict.Satisfied, EvidenceCoverage coverage = EvidenceCoverage.FullScope,
        AssuranceAttemptStatus status = AssuranceAttemptStatus.Completed,
        InterpretationSupport support = InterpretationSupport.Supported, bool complete = true,
        EvidenceSubject? subject = null, ImmutableArray<ClaimReference> dependencies = default,
        ImmutableArray<ClaimReference> residuals = default, ImmutableArray<ClaimReference> shared = default) =>
        new(Claim(), subject ?? Subject, kind, Method, Configuration, support, status, verdict, coverage,
            complete, dependencies, residuals, shared, [R("tool-report")]);
    static EvidenceAcceptance Accept(ClaimEvidence evidence) => EvidenceAcceptance.Of(evidence);
    static ClaimAssessment Assess(ClaimEvidence report, ImmutableArray<ClaimReference> unavailable = default,
        ClaimDisposition? disposition = null) => ClaimAssessment.Evaluate(report, R("policy"),
            [Accept(report)], DateTimeOffset.UnixEpoch, unavailable, disposition);

    [Theory]
    [InlineData(EvidenceKind.FormalProof)]
    [InlineData(EvidenceKind.ExhaustiveCheck)]
    [InlineData(EvidenceKind.ProviderDeclaration)]
    public void AcceptedCompleteReportsEstablishOnlyTheirDeclaredScope(EvidenceKind kind)
    {
        var full = Report(kind);
        Assert.Equal(ProofState.Proven, Assess(full).State);
        var restricted = Report(kind, coverage: EvidenceCoverage.RestrictedScope, residuals: [R("orders<=4")]);
        Assert.Equal(ProofState.Conditional, Assess(restricted).State);
        Assert.Equal(R("orders<=4"), Assert.Single(restricted.Residuals));
        Assert.Equal(ProofState.Unknown, Assess(Report(kind, coverage: EvidenceCoverage.Sampled)).State);
        Assert.Equal(kind, ClaimEvidence.Deserialize(full.Serialize()).Kind);
    }

    [Theory]
    [InlineData(AssuranceAttemptStatus.InvalidObservations)]
    [InlineData(AssuranceAttemptStatus.Unsupported)]
    [InlineData(AssuranceAttemptStatus.BudgetExceeded)]
    [InlineData(AssuranceAttemptStatus.TimedOut)]
    [InlineData(AssuranceAttemptStatus.Cancelled)]
    [InlineData(AssuranceAttemptStatus.Unknown)]
    [InlineData(AssuranceAttemptStatus.Incomplete)]
    public void NoncompletedAttemptsCannotBecomeProof(AssuranceAttemptStatus status)
    {
        var report = Report(status: status, verdict: ClaimVerdict.Inconclusive);
        var restored = ClaimEvidence.Deserialize(report.Serialize());
        Assert.Equal(status, restored.Status);
        Assert.Equal(ProofState.Unknown, Assess(restored).State);
        Assert.Throws<ArgumentException>(() => Report(status: status));
        Assert.Throws<ArgumentException>(() => Report(status: status, verdict: ClaimVerdict.Violated));
    }

    [Theory]
    [InlineData(InterpretationSupport.Unsupported)]
    [InlineData(InterpretationSupport.Undetermined)]
    public void InterpretationSupportMustBeEstablished(InterpretationSupport support)
    {
        Assert.Equal(ProofState.Unknown, Assess(Report(support: support, verdict: ClaimVerdict.Inconclusive)).State);
        Assert.Throws<ArgumentException>(() => Report(support: support));
    }

    [Fact]
    public void RefutingProofDoesNotRequireAnExecutionWitness()
    {
        var report = Report(verdict: ClaimVerdict.Violated);
        Assert.Null(report.Trace);
        Assert.Equal(R("tool-report"), Assert.Single(report.Artifacts));
        Assert.Equal(ProofState.Disproven, Assess(report).State);
    }

    [Fact]
    public void AcceptancePinsKindMethodAndConfiguration()
    {
        var declaration = Report(EvidenceKind.ProviderDeclaration);
        var acceptedProof = new EvidenceAcceptance(EvidenceKind.FormalProof, Method, Configuration);
        foreach (var acceptance in new[] { acceptedProof, Accept(declaration) with { Method = R("other") },
                     Accept(declaration) with { Configuration = R("other-config") } })
        {
            var assessment = ClaimAssessment.Evaluate(Claim(), Subject, R("policy"), [acceptance], [declaration], DateTimeOffset.UnixEpoch);
            Assert.Equal(ProofState.Unknown, assessment.State);
            Assert.Equal("evidence.method-not-accepted", Assert.Single(assessment.Contributions).Reason);
        }
    }

    [Fact]
    public void ModelEncodingAndRealizationRemainSeparateAndExactTargetsMatter()
    {
        EvidenceSubject[] subjects = [Subject with { Obligation = ProofObligation.Model },
            Subject with { Obligation = ProofObligation.Encoding }, Subject with { Target = R("another-candidate") },
            Subject with { Configuration = R("another-config") }, Subject with { Scope = R("another-scope") },
            Subject with { Kind = EvidenceTargetKind.Deployment }];
        foreach (var subject in subjects)
        {
            var report = Report(subject: subject);
            Assert.Throws<ArgumentException>(() => ClaimAssessment.Evaluate(Claim(), Subject, R("policy"),
                [Accept(report)], [report], DateTimeOffset.UnixEpoch));
        }
    }

    [Fact]
    public void EmptyAssessmentAndUnacceptedEvidenceAreUnknownButChangedClaimsFail()
    {
        var report = Report();
        var empty = ClaimAssessment.Evaluate(Claim(), Subject, R("policy"), [], [], DateTimeOffset.UnixEpoch);
        Assert.Equal(ProofState.Unknown, empty.State);
        Assert.Empty(empty.Contributions);
        var unaccepted = ClaimAssessment.Evaluate(Claim(), Subject, R("policy"), [], [report], DateTimeOffset.UnixEpoch);
        Assert.Equal(ProofState.Unknown, unaccepted.State);
        Assert.Throws<ArgumentException>(() => ClaimAssessment.Evaluate(Claim("2"), Subject, R("policy"),
            [Accept(report)], [report], DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void DependencyInvalidationCreatesANewAssessmentAndPreservesPriorEvidence()
    {
        var assumption = R("serializable-provider-declaration");
        var report = Report(dependencies: [assumption], shared: [R("shared-policy")]);
        var original = Assess(report);
        var retained = original.Serialize();
        var changed = Assess(report, unavailable: [assumption]);
        Assert.Equal(ProofState.Unknown, changed.State);
        Assert.Equal("evidence.dependency-unavailable", Assert.Single(changed.Contributions).Reason);
        Assert.Equal(ProofState.Proven, ClaimAssessment.Reassess(retained).State);
        Assert.Equal(retained, original.Serialize());
        Assert.Equal(report.Serialize(), Assert.Single(changed.Evidence).Serialize());
        Assert.Contains(R("shared-policy"), report.Dependencies);
        Assert.Equal(ProofState.Unknown, Assess(report, unavailable: [R("tool-report")]).State);
        Assert.Equal(ProofState.Unknown, Assess(Report(complete: false)).State);
        Assert.Equal(ProofState.Unknown, Assess(report, unavailable: [Claim().Definition.Selection]).State);
    }

    [Fact]
    public void WaiverExpiryAndRequirementRevisionNeverAlterProofState()
    {
        var refutation = Report(verdict: ClaimVerdict.Violated);
        var waiver = new ClaimDisposition(ClaimDispositionKind.Waived, R("authority"), "accepted risk",
            DateTimeOffset.UnixEpoch.AddDays(1));
        var waived = Assess(refutation, disposition: waiver);
        Assert.Equal(ProofState.Disproven, waived.State);
        Assert.True(waived.DispositionEffective);
        var expired = ClaimAssessment.Evaluate(Claim(), Subject, R("policy"), [Accept(refutation)], [refutation],
            waiver.ExpiresAt!.Value, disposition: waiver);
        Assert.Equal(ProofState.Disproven, expired.State);
        Assert.False(expired.DispositionEffective);
        var revised = Assess(refutation, disposition: new(ClaimDispositionKind.RequirementRevised,
            R("authority"), "accepted policy change", Revision: R("accepted-semantic-diff")));
        Assert.Equal(ProofState.Disproven, revised.State);
        Assert.True(revised.DispositionEffective);
        Assert.Throws<ArgumentException>(() => Assess(refutation, disposition: waiver with { ExpiresAt = null }));
    }

    [Fact]
    public void RefutationWinsOverContradictoryProofWithoutDiscardingEither()
    {
        var proof = Report(); var refutation = Report(verdict: ClaimVerdict.Violated);
        var assessment = ClaimAssessment.Evaluate(Claim(), Subject, R("policy"), [Accept(proof)],
            [proof, refutation, proof], DateTimeOffset.UnixEpoch);
        Assert.Equal(ProofState.Disproven, assessment.State);
        Assert.True(assessment.ConflictingEvidence);
        Assert.Equal(2, assessment.Evidence.Length);
        var reordered = ClaimAssessment.Evaluate(Claim(), Subject, R("policy"), [Accept(proof), Accept(proof)],
            [refutation, proof], DateTimeOffset.UnixEpoch);
        Assert.Equal(assessment.Serialize(), reordered.Serialize());
    }

    [Fact]
    public void RestrictedProofDoesNotConflictWithoutEstablishedCounterexampleMembership()
    {
        var bounded = Report(coverage: EvidenceCoverage.RestrictedScope, residuals: [R("orders<=4")]);
        var refutation = Report(verdict: ClaimVerdict.Violated);
        var assessment = ClaimAssessment.Evaluate(Claim(), Subject, R("policy"), [Accept(bounded)],
            [bounded, refutation], DateTimeOffset.UnixEpoch);
        Assert.Equal(ProofState.Disproven, assessment.State);
        Assert.False(assessment.ConflictingEvidence);
        Assert.Contains(assessment.Contributions, x => x.State == ProofState.Conditional);
    }

    [Fact]
    public void MixedMatchingAndMismatchedReportsFailRatherThanHideWiringErrors()
    {
        var correct = Report();
        var wrong = Report(subject: Subject with { Scope = R("different-scope") });
        Assert.Throws<ArgumentException>(() => ClaimAssessment.Evaluate(Claim(), Subject, R("policy"),
            [Accept(correct)], [correct, wrong], DateTimeOffset.UnixEpoch));
        var json = JsonNode.Parse(Assess(correct).Serialize())!;
        json["subject"]!["scope"]!["revision"] = "2";
        Assert.Throws<JsonException>(() => ClaimAssessment.Reassess(json.ToJsonString()));
    }

    [Fact]
    public void CanonicalEvidenceAndAssessmentRejectTamperedDerivedFields()
    {
        var report = Report(dependencies: [R("b"), R("a"), R("b")]);
        var reordered = Report(dependencies: [R("a"), R("b")]);
        Assert.Equal(report.Fingerprint(), reordered.Fingerprint());
        Assert.Equal(report.Serialize(), ClaimEvidence.Deserialize(report.Serialize()).Serialize());
        var assessment = Assess(report);
        Assert.Equal(assessment.Serialize(), ClaimAssessment.Reassess(assessment.Serialize()).Serialize());
        var changed = JsonNode.Parse(assessment.Serialize())!;
        changed["state"] = "Disproven";
        Assert.Throws<JsonException>(() => ClaimAssessment.Reassess(changed.ToJsonString()));
        var incompatible = JsonNode.Parse(report.Serialize())!;
        incompatible["schema"] = "future";
        Assert.Throws<JsonException>(() => ClaimEvidence.Deserialize(incompatible.ToJsonString()));
        Assert.Throws<JsonException>(() => ClaimEvidence.Deserialize("{\"schema\":\"duplicate\"," + report.Serialize()[1..]));
    }

    [Fact]
    public void RestrictedCoverageMustNameResidualsAndCannotMasqueradeAsFullScope()
    {
        Assert.Throws<ArgumentException>(() => Report(coverage: EvidenceCoverage.RestrictedScope));
        Assert.Throws<ArgumentException>(() => Report(residuals: [R("hidden-assumption")]));
    }

    static TraceEvaluation Trace(int reserved = 1, int count = 1)
    {
        var claim = Claim();
        var observation = new SemanticObservation("state", "p", 0, 0, claim.Definition.Selection,
            SemanticObservationKind.InitialState,
            ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(ClaimInputRole.State,
                ObservationValue.FromObject(new Stock(2, reserved))));
        var batch = new TraceObservationBatch(new(claim.Definition.Observation, R("atomic"), [new(claim.Definition.Selection, ["p"])]),
            Subject.Target, R("epoch"), [new(R("alternative"), [.. Enumerable.Range(0, count).Select(i => observation with { Occurrence = "s" + i, Step = i, Ticks = i })],
                [new(claim.Definition.Selection, "p", count, count, ObservationClosure.Closed, R("closure"))])],
            EnumerationCompleteness.Complete, R("enumeration"));
        return TraceClaimEvaluator.Evaluate(claim, batch);
    }

    [Fact]
    public void TrustedCaptureAllocationDoesNotScaleWithObservationCount()
    {
        var small = Trace(count: 1);
        var large = Trace(count: 2000);
        ClaimEvidence Capture(TraceEvaluation value) => ClaimEvidence.FromTrace(value, Subject, Configuration, dependenciesComplete: true);
        for (var i = 0; i < 64; i++) { Capture(small); Capture(large); }
        long Allocated(TraceEvaluation value)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = Capture(value);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
            return allocated;
        }
        Assert.InRange(Allocated(large), 0, Allocated(small) + 256);
    }

    [Theory]
    [InlineData(1, ProofState.Unknown)]
    [InlineData(3, ProofState.Disproven)]
    public void RealTraceEvidenceCanRefuteButCannotProve(int reserved, ProofState expected)
    {
        var evaluation = Trace(reserved);
        var claim = evaluation.Claim;
        var batch = evaluation.Batch;
        var report = ClaimEvidence.FromTrace(evaluation, Subject, Configuration, dependenciesComplete: true);
        var restored = ClaimEvidence.Deserialize(report.Serialize());
        Assert.Equal(expected, Assess(restored).State);
        Assert.Equal(evaluation.Serialize(), restored.Trace!.Serialize());
        Assert.Same(evaluation, report.Trace); // Trusted reference-produced results are reused, not re-evaluated.
        var copied = evaluation with { };
        Assert.NotSame(copied, ClaimEvidence.FromTrace(copied, Subject, Configuration, dependenciesComplete: true).Trace);
        Assert.IsType<JsonObject>(JsonNode.Parse(report.Serialize())!["trace"]);
        Assert.Contains(R("atomic"), report.Dependencies);
        Assert.Contains(R("closure"), report.Dependencies);
        Assert.Equal(ProofState.Unknown, Assess(report, unavailable: [R("enumeration")]).State);
        var invalid = TraceClaimEvaluator.Evaluate(claim, batch with { Contract = null! });
        var invalidEvidence = ClaimEvidence.FromTrace(invalid, Subject, Configuration, dependenciesComplete: true);
        Assert.Equal(AssuranceAttemptStatus.InvalidObservations, invalidEvidence.Status);
        Assert.False(invalidEvidence.DependenciesComplete);
        Assert.Equal(ProofState.Unknown, Assess(ClaimEvidence.Deserialize(invalidEvidence.Serialize())).State);
        foreach (var invalidSubject in new[] { Subject with { Obligation = ProofObligation.Model },
                     Subject with { Obligation = ProofObligation.Encoding }, Subject with { Kind = EvidenceTargetKind.Model } })
        {
            Assert.Throws<ArgumentException>(() => ClaimEvidence.FromTrace(evaluation, invalidSubject, Configuration, dependenciesComplete: true));
            var altered = JsonNode.Parse(report.Serialize())!;
            altered["subject"]!["kind"] = invalidSubject.Kind.ToString();
            altered["subject"]!["obligation"] = invalidSubject.Obligation.ToString();
            Assert.Throws<JsonException>(() => ClaimEvidence.Deserialize(altered.ToJsonString()));
        }
        var deployment = ClaimEvidence.FromTrace(evaluation, Subject with { Kind = EvidenceTargetKind.Deployment },
            Configuration, dependenciesComplete: true);
        Assert.Equal(EvidenceTargetKind.Deployment, deployment.Subject.Kind);
        var forged = JsonNode.Parse(report.Serialize())!;
        forged["trace"]!["verdict"] = "Inconclusive";
        Assert.Throws<JsonException>(() => ClaimEvidence.Deserialize(forged.ToJsonString()));
        var savedAssessment = JsonNode.Parse(Assess(report).Serialize())!;
        savedAssessment["evidence"]![0]!["trace"]!["verdict"] = "Inconclusive";
        Assert.Throws<JsonException>(() => ClaimAssessment.Reassess(savedAssessment.ToJsonString()));
        var corrupted = evaluation with { Verdict = ClaimVerdict.Inconclusive };
        Assert.Throws<JsonException>(() => ClaimEvidence.FromTrace(corrupted, Subject, Configuration, dependenciesComplete: true));
    }
}
