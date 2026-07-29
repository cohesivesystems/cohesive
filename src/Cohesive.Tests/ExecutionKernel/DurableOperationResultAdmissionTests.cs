using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableOperationResultAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EligibleAcknowledgement_ProducesAClosedIntentForBothCanonicalTargetKinds(
        bool transitionTarget)
    {
        var fixture = DurableOperationTestFixture.Create();
        InteractionTarget target = transitionTarget
            ? DurableOperationTestFixture.TransitionTarget()
            : DurableOperationTestFixture.ProcessTarget();
        var acknowledged = DurableOperationContractTests.Acknowledge(
            fixture,
            fixture.CreateState(target: target));

        var result = fixture.Executor.AdmitResult(
            acknowledged,
            new(target, DurableOperationResultArrival.Eligible));

        Assert.Equal(DurableOperationAdmissionResultKind.Dispositioned, result.Kind);
        var admission = Assert.IsType<DurableOperationAdmission>(result.Admission);
        Assert.Equal(target, admission.Target);
        Assert.Equal(DurableOperationResultArrival.Eligible, admission.Arrival);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, admission.Disposition);
        Assert.True(admission.AdvancesTarget);
        Assert.Equal(DurableOperationStatus.Dispositioned, result.State.Status);
        if (transitionTarget)
            Assert.IsType<TransitionInteractionTarget>(admission.Target);
        else
            Assert.IsType<ProcessTokenInteractionTarget>(admission.Target);
    }

    [Theory]
    [InlineData(
        DurableOperationResultArrival.Late,
        DurableOperationAdmissionDisposition.Observed,
        null)]
    [InlineData(
        DurableOperationResultArrival.Stale,
        DurableOperationAdmissionDisposition.Rejected,
        null)]
    [InlineData(
        DurableOperationResultArrival.Duplicate,
        DurableOperationAdmissionDisposition.ReusedPriorDisposition,
        DurableOperationAdmissionDisposition.Accepted)]
    public void LateStaleAndDuplicateResults_HaveExplicitNonAdvancingDispositions(
        DurableOperationResultArrival arrival,
        DurableOperationAdmissionDisposition expected,
        DurableOperationAdmissionDisposition? prior)
    {
        var fixture = DurableOperationTestFixture.Create();
        var acknowledged = DurableOperationContractTests.Acknowledge(
            fixture,
            fixture.CreateState());

        var result = fixture.Executor.AdmitResult(
            acknowledged,
            new(acknowledged.Request.ResponseTarget, arrival, prior));

        Assert.Equal(DurableOperationAdmissionResultKind.Dispositioned, result.Kind);
        var admission = Assert.IsType<DurableOperationAdmission>(result.Admission);
        Assert.Equal(arrival, admission.Arrival);
        Assert.Equal(expected, admission.Disposition);
        Assert.Equal(prior, admission.PriorDisposition);
        Assert.False(admission.AdvancesTarget);
        Assert.Equal(admission, result.State.Admission);
    }

    [Theory]
    [InlineData(RequestResultDisposition.Reject, DurableOperationAdmissionDisposition.Rejected)]
    [InlineData(RequestResultDisposition.Observe, DurableOperationAdmissionDisposition.Observed)]
    public void DuplicateRejectOrObservePolicy_DoesNotRequireAPriorDisposition(
        RequestResultDisposition policy,
        DurableOperationAdmissionDisposition expected)
    {
        var fixture = DurableOperationTestFixture.Create(duplicateResult: policy);
        var acknowledged = DurableOperationContractTests.Acknowledge(
            fixture,
            fixture.CreateState());

        var result = fixture.Executor.AdmitResult(
            acknowledged,
            new(acknowledged.Request.ResponseTarget, DurableOperationResultArrival.Duplicate));

        var admission = Assert.IsType<DurableOperationAdmission>(result.Admission);
        Assert.Equal(DurableOperationAdmissionResultKind.Dispositioned, result.Kind);
        Assert.Equal(expected, admission.Disposition);
        Assert.Null(admission.PriorDisposition);
        Assert.False(admission.AdvancesTarget);
    }

    [Fact]
    public void PreviouslyDispositionedResult_ReusesItsReceiptEvenWhenTheTargetIsNowLate()
    {
        var fixture = DurableOperationTestFixture.Create();
        var acknowledged = DurableOperationContractTests.Acknowledge(
            fixture,
            fixture.CreateState());
        var accepted = fixture.Executor.AdmitResult(
            acknowledged,
            new(acknowledged.Request.ResponseTarget, DurableOperationResultArrival.Eligible));

        var duplicateAfterCompletion = fixture.Executor.AdmitResult(
            accepted.State,
            new(acknowledged.Request.ResponseTarget, DurableOperationResultArrival.Late));

        Assert.Equal(DurableOperationAdmissionResultKind.Duplicate, duplicateAfterCompletion.Kind);
        Assert.Same(accepted.State, duplicateAfterCompletion.State);
        Assert.Equal(accepted.Admission, duplicateAfterCompletion.Admission);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, duplicateAfterCompletion.Admission?.Disposition);
    }

    [Fact]
    public void Admission_RejectsAReportedTargetThatIsNotTheExactRequestContinuation()
    {
        var fixture = DurableOperationTestFixture.Create();
        var acknowledged = DurableOperationContractTests.Acknowledge(
            fixture,
            fixture.CreateState(target: DurableOperationTestFixture.ProcessTarget()));

        var mismatch = fixture.Executor.AdmitResult(
            acknowledged,
            new(
                DurableOperationTestFixture.TransitionTarget(),
                DurableOperationResultArrival.Eligible));

        Assert.Equal(DurableOperationAdmissionResultKind.TargetMismatch, mismatch.Kind);
        Assert.Same(acknowledged, mismatch.State);
        Assert.Null(mismatch.Admission);
        Assert.Null(mismatch.State.Admission);
    }

    [Fact]
    public async Task AdapterExecution_ReturnsEvidenceWithoutChangingDurableOrTargetState()
    {
        var fixture = DurableOperationTestFixture.Create();
        var initial = fixture.CreateState(target: DurableOperationTestFixture.TransitionTarget());
        var claimed = fixture.Executor.Claim(
            initial,
            new("operation-attempt/1"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var invocation = Assert.IsType<DurableOperationInvocation>(dispatched.Invocation);
        var adapter = new DurableOperationFakeAdapter(fixture.RequestContract)
            .Script(initial.OperationId, fixture.Success());

        var observation = await DurableOperationReferenceExecutor.ExecuteAsync(
            DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1)),
            invocation,
            adapter);

        Assert.IsType<DurableOperationOutcomeObservation>(observation);
        Assert.Equal(DurableOperationStatus.Dispatched, dispatched.State.Status);
        Assert.Null(dispatched.State.Acknowledgement);
        Assert.Null(dispatched.State.Admission);
        Assert.Single(adapter.Invocations);
        Assert.Equal(initial.Request, adapter.Invocations[0].Request);

        var exposedTypes = typeof(DurableOperationInvocation)
            .GetProperties()
            .Select(static property => property.PropertyType)
            .ToArray();
        Assert.DoesNotContain(exposedTypes, static type => typeof(Delegate).IsAssignableFrom(type));
        Assert.DoesNotContain(
            exposedTypes,
            static type => type.Namespace?.StartsWith("Cohesive.Processes", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            exposedTypes,
            static type => type.Namespace?.StartsWith("Cohesive.Transitions", StringComparison.Ordinal) == true);
    }
}
