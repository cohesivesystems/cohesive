using System.Collections.Immutable;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;
using Cohesive.Model;
using Xunit;

namespace Cohesive.Tests.Integrations;

public sealed partial class IntegrationModelTests
{
    static readonly DateTimeOffset AdmissionTime = new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);
    public sealed record TestCapabilities(bool CanRead);
    public sealed record TestEvidence(string Source);
    static IntegrationCapabilityEvaluator<TestCapabilities> Evaluator => new("test/evaluator", "v1", "test/read/v1",
        (binding, capability, offer, atUtc, catalog) => [.. binding.Properties.Where(p => p.Use.Role == ContractRole.Requires).Select(p =>
            new IntegrationRequirementDecision(p.Use.Property, capability.CanRead ? IntegrationQualificationStatus.RequiresRuntimeEvidence : IntegrationQualificationStatus.Unsupported,
                capability.CanRead ? CapabilityRealizationKind.Composed : CapabilityRealizationKind.Unavailable,
                capability.CanRead ? "pending" : "cannot-read", capability.CanRead ? "Await retained observations and publication." : "Resource cannot read the requested data."))]);
    static IntegrationCapabilityOffer Offer(bool canRead = true, string connectionRevision = "v1", string schema = "test/read/v1") =>
        IntegrationCapabilityOffer.Declare("test/offer/" + connectionRevision, "v1", Profile(), Connection(connectionRevision), schema,
            new TestCapabilities(canRead), ContractPropertyDocuments.DeclareContext(new("test/evidence/" + connectionRevision), new("v1"),
                new TestEvidence("offline conformance fixture"), Profile().Document.Metadata.Provenance), AdmissionTime, AdmissionTime.AddDays(1));
    static IntegrationCompilation Candidate(string connectionRevision = "v1") =>
        Connected(Profile()).Compile(new ConnectionBindings().Bind(MarketData, Connection(connectionRevision)));

    [Fact]
    public void QualificationPinsAllInputsAndReopensWithoutClaimingRuntimeCoverage()
    {
        var result = IntegrationQualification.Qualify(Candidate(), Offer(), Evaluator, AdmissionTime);
        Assert.True(result.IsAdmissible);
        var compiled = result.Compilation!;
        Assert.Equal(Candidate().Binding!.Reference, compiled.Binding!.Reference);
        Assert.Equal(Candidate().Process!.Reference, compiled.Process!.Reference);
        var plan = compiled.Qualification!.GetDefinition<IntegrationQualificationPlan>();
        Assert.Equal(Offer().Document.Reference, plan.Offer);
        Assert.Equal(Evaluator.Definition.Reference, plan.Evaluator);
        Assert.Equal(IntegrationQualificationStatus.RequiresRuntimeEvidence, Assert.Single(plan.Decisions).Status);
        Assert.True(IntegrationQualification.Inspect(compiled).IsValid);
        var reopened = compiled with { Documents = [.. compiled.Documents.Select(d =>
        {
            Assert.True(ExecutionDefinitionJsonSerializer.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(d), out var value).IsValid);
            return value!;
        })] };
        Assert.True(IntegrationQualification.Inspect(reopened, AdmissionTime).IsValid);
        Assert.Equal(compiled.Qualification.Reference, IntegrationQualification.Qualify(Candidate(), Offer(), Evaluator, AdmissionTime).Compilation!.Qualification!.Reference);
        Assert.Contains(compiled.Validation.Diagnostics, d => d.Code == "integrations.ingestion.realization.unqualified");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void EvidenceOutsideItsWindowCannotAdmit(int hours)
    {
        var result = IntegrationQualification.Qualify(Candidate(), Offer(), Evaluator, AdmissionTime.AddHours(hours));
        Assert.False(result.IsAdmissible);
        Assert.Contains(result.Validation.Diagnostics, d => d.Code == "integrations.qualification.evidence.window");
    }

    [Fact]
    public void ExpiryBlocksNewDispatchButPreservesOriginalReconciliation()
    {
        var compiled = IntegrationQualification.Qualify(Candidate(), Offer(), Evaluator, AdmissionTime).Compilation!;
        var implementation = new object();
        var runtime = new IntegrationRuntime().Register(Profile(), Connection(), implementation);
        Assert.Same(implementation, runtime.ResolveQualifiedForDispatch<object>(compiled, AdmissionTime));
        Assert.Throws<InvalidOperationException>(() => runtime.ResolveQualifiedForDispatch<object>(compiled, AdmissionTime.AddDays(1)));
        Assert.Same(implementation, runtime.Resolve<object>(compiled));
    }

    [Fact]
    public void EvidenceCannotBeBorrowedFromAnotherConnectionOrSchema()
    {
        Assert.False(IntegrationQualification.Qualify(Candidate(), Offer(connectionRevision: "v2"), Evaluator, AdmissionTime).IsAdmissible);
        Assert.False(IntegrationQualification.Qualify(Candidate(), Offer(schema: "unknown/v1"), Evaluator, AdmissionTime).IsAdmissible);
    }

    [Fact]
    public void UnsupportedAndOmittedRequirementsCannotAdmit()
    {
        var rejected = IntegrationQualification.Qualify(Candidate(), Offer(canRead: false), Evaluator, AdmissionTime);
        Assert.False(rejected.IsAdmissible);
        Assert.Equal(IntegrationQualificationStatus.Unsupported, Assert.Single(rejected.Decisions).Status);
        var incomplete = new IntegrationCapabilityEvaluator<TestCapabilities>("test/incomplete", "v1", "test/read/v1", (_, _, _, _, _) => []);
        Assert.Contains(IntegrationQualification.Qualify(Candidate(), Offer(), incomplete, AdmissionTime).Validation.Diagnostics,
            d => d.Code == "integrations.qualification.requirements");
    }

    [Fact]
    public void RetainedPlanRejectsMissingEvidenceAndChangedProcess()
    {
        var compiled = IntegrationQualification.Qualify(Candidate(), Offer(), Evaluator, AdmissionTime).Compilation!;
        var evidence = Offer().Document.GetDefinition<IntegrationCapabilityOfferDefinition>().Evidence;
        Assert.False(IntegrationQualification.Inspect(compiled with { Documents = [.. compiled.Documents.Where(d => d.Reference != evidence)] }).IsValid);
        var changed = compiled with { Process = compiled.Binding };
        Assert.False(IntegrationQualification.Inspect(changed).IsValid);
        Assert.False(IntegrationQualification.Qualify(Candidate() with { Process = Candidate().Binding }, Offer(), Evaluator, AdmissionTime).IsAdmissible);
    }

    [Fact]
    public void SelectorsPreserveFailuresAndNeverSubstituteForAnExactChoice()
    {
        var good = IntegrationQualification.Qualify(Candidate(), Offer(), Evaluator, AdmissionTime);
        var other = IntegrationQualification.Qualify(Candidate("v2"), Offer(connectionRevision: "v2"), Evaluator, AdmissionTime);
        var bad = IntegrationQualification.Qualify(Candidate("v2"), Offer(canRead: false, connectionRevision: "v2"), Evaluator, AdmissionTime);
        Assert.False(IntegrationCandidateSelection.Resolve([], IntegrationCandidateSelector.ExactlyOne).Validation.IsValid);
        Assert.False(IntegrationCandidateSelection.Resolve([good, other], IntegrationCandidateSelector.ExactlyOne).Validation.IsValid);
        Assert.Single(IntegrationCandidateSelection.Resolve([bad, good], IntegrationCandidateSelector.ExactlyOne).Selected);
        var exact = IntegrationCandidateSelection.Resolve([bad, good], IntegrationCandidateSelector.Exactly, bad.Candidate.Binding!.Reference);
        Assert.False(exact.Validation.IsValid); Assert.Empty(exact.Selected);
        var all = IntegrationCandidateSelection.Resolve([bad, good], IntegrationCandidateSelector.All);
        Assert.False(all.Validation.IsValid); Assert.Equal(2, all.Candidates.Length); Assert.Empty(all.Selected);
        Assert.Equal(2, IntegrationCandidateSelection.Resolve([other, good], IntegrationCandidateSelector.All).Selected.Length);
    }
    [Fact]
    public void ScopedDispatchRequiresBothCurrentPolicyAndFreshQualification()
    {
        var scoped = ResolveScoped(SystemLayer().Document);
        var candidate = Connected(scoped.Profile!).Compile(scoped.Bindings!);
        var compiled = IntegrationQualification.Qualify(candidate, Offer(), Evaluator, AdmissionTime).Compilation!;
        var handler = new object();
        var runtime = new IntegrationRuntime().Register(Profile(), Connection(), handler);
        Assert.Throws<InvalidOperationException>(() => runtime.ResolveForDispatch<object>(compiled, [SystemLayer().Document], Grants));
        Assert.Same(handler, runtime.ResolveForDispatch<object>(compiled, [SystemLayer().Document], Grants, AdmissionTime));
        Assert.Throws<InvalidOperationException>(() => runtime.ResolveQualifiedForDispatch<object>(compiled, AdmissionTime));
        var revoked = Cohesive.Configuration.SelectionLayer<IntegrationChoice, IntegrationSelectionPolicy>
            .Define("config/system", "v2", "admin", new()).Enforce(new(AllowedProviders: [])).Document;
        Assert.Throws<InvalidOperationException>(() => runtime.ResolveForDispatch<object>(compiled, [revoked], Grants, AdmissionTime));
    }

    [Fact]
    public void DomainProjectionFailuresProduceDiagnosticsWithoutEchoingInputs()
    {
        var failing = new IntegrationCapabilityEvaluator<TestCapabilities>("test/invalid-context", "v1", "test/read/v1",
            (_, _, _, _, _) => throw new InvalidDataException("sensitive input must not escape"));
        var result = IntegrationQualification.Qualify(Candidate(), Offer(), failing, AdmissionTime);
        Assert.False(result.IsAdmissible);
        Assert.Contains(result.Validation.Diagnostics, d => d.Code == "integrations.qualification.interpretation");
        Assert.DoesNotContain(result.Validation.Diagnostics, d => d.Message.Contains("sensitive input"));
    }
}
