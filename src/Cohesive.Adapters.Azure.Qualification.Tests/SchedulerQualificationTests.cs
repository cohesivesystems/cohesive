using Cohesive.Adapters.Azure.Qualification;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Converters;
using Moq;

namespace Cohesive.Adapters.Azure.Qualification.Tests;

public class SchedulerQualificationTests
{
    static RuntimeQualificationOptions Options() => new(Guid.NewGuid(), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
    [Fact]
    public async Task Existing_instance_is_never_replaced_terminated_or_purged()
    {
        var options = Options();
        var client = new Mock<DurableTaskClient>(MockBehavior.Strict, "probe");
        client.Setup(c => c.GetInstanceAsync(options.ObjectName, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrchestrationMetadata("existing", options.ObjectName));
        var result = await SchedulerRuntimeQualification.ExecuteAsync(client.Object, options);
        Assert.Equal(QualificationOutcome.Collision, result.Outcome);
        client.VerifyNoOtherCallsExceptGet();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_matching_completed_challenge_is_execution_evidence(bool matching)
    {
        var options = Options(); string? challenge = null;
        var client = new Mock<DurableTaskClient>(MockBehavior.Strict, "probe");
        client.Setup(c => c.GetInstanceAsync(options.ObjectName, false, It.IsAny<CancellationToken>())).ReturnsAsync((OrchestrationMetadata?)null);
        client.Setup(c => c.ScheduleNewOrchestrationInstanceAsync(new TaskName(SchedulerRuntimeQualification.OrchestrationName), It.IsAny<object>(), It.IsAny<StartOrchestrationOptions>(), It.IsAny<CancellationToken>()))
            .Callback<TaskName, object, StartOrchestrationOptions, CancellationToken>((_, input, start, _) => {
                challenge = Assert.IsType<string>(input); Assert.Equal(options.ObjectName, start.InstanceId);
                Assert.Equal(Enum.GetNames<OrchestrationRuntimeStatus>(), start.DedupeStatuses);
            }).ReturnsAsync(options.ObjectName);
        client.Setup(c => c.WaitForInstanceCompletionAsync(options.ObjectName, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OrchestrationMetadata(SchedulerRuntimeQualification.OrchestrationName, options.ObjectName) {
                RuntimeStatus = OrchestrationRuntimeStatus.Completed, DataConverter = JsonDataConverter.Default,
                SerializedInput = JsonDataConverter.Default.Serialize(challenge),
                SerializedOutput = JsonDataConverter.Default.Serialize(matching ? challenge : "other") });
        var result = await SchedulerRuntimeQualification.ExecuteAsync(client.Object, options);
        Assert.Equal(matching, result.Succeeded);
        Assert.Equal(matching ? QualificationCleanup.HistoryRetained : QualificationCleanup.Unresolved, result.Cleanup);
    }
    [Fact]
    public async Task Worker_orchestration_calls_exactly_one_pure_echo_activity()
    {
        var challenge = Guid.NewGuid().ToString("N");
        var context = new Mock<TaskOrchestrationContext>(MockBehavior.Strict);
        context.SetupGet(c => c.InstanceId).Returns(Options().ObjectName);
        context.Setup(c => c.CallActivityAsync<string>(new TaskName(SchedulerRuntimeQualification.ActivityName), challenge, null)).ReturnsAsync(challenge);
        Assert.Equal(challenge, await new SchedulerRuntimeQualification.EchoOrchestrator().RunAsync(context.Object, challenge));
        context.Verify(c => c.CallActivityAsync<string>(new TaskName(SchedulerRuntimeQualification.ActivityName), challenge, null), Times.Once);
    }

    [Fact]
    public async Task Scheduler_timeout_keeps_outcome_unresolved_without_termination_or_purge()
    {
        var options = Options();
        var client = new Mock<DurableTaskClient>(MockBehavior.Strict, "probe");
        client.Setup(c => c.GetInstanceAsync(options.ObjectName, false, It.IsAny<CancellationToken>())).ReturnsAsync((OrchestrationMetadata?)null);
        client.Setup(c => c.ScheduleNewOrchestrationInstanceAsync(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<StartOrchestrationOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(options.ObjectName);
        client.Setup(c => c.WaitForInstanceCompletionAsync(options.ObjectName, true, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException("private payload"));
        var result = await SchedulerRuntimeQualification.ExecuteAsync(client.Object, options);
        Assert.False(result.Succeeded);
        Assert.Equal(QualificationCleanup.Unresolved, result.Cleanup);
        Assert.Equal(3, client.Invocations.Count);
        Assert.DoesNotContain("private", result.ToString());
    }

    [Fact]
    public void Echo_contract_rejects_arbitrary_or_empty_payloads()
    {
        Assert.Throws<ArgumentException>(() => SchedulerRuntimeQualification.ValidateChallenge("not a challenge"));
        Assert.Throws<ArgumentException>(() => SchedulerRuntimeQualification.ValidateChallenge(Guid.Empty.ToString("N")));
        var input = Guid.NewGuid().ToString("N");
        Assert.Equal(input, SchedulerRuntimeQualification.ValidateChallenge(input));
    }
}
static class MockExtensions
{
    internal static void VerifyNoOtherCallsExceptGet(this Mock<DurableTaskClient> client)
    {
        Assert.Single(client.Invocations);
        Assert.Equal(nameof(DurableTaskClient.GetInstanceAsync), client.Invocations[0].Method.Name);
    }
}
