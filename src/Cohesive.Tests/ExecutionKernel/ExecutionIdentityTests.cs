using System.Text.Json;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionIdentityTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Identities_HaveExplicitValueSemanticsAndFlatScalarJson()
    {
        object[] identities =
        [
            new ExecutionDefinitionId("definition/order-fulfillment"),
            new ExecutionRevisionId("revision/42"),
            new ExecutionNodeId("node/reserve-inventory"),
            new ProcessInstanceId("process/order-123"),
            new ProcessAttemptId("attempt/process-2"),
            new ActivationId("activation/reserve-1"),
            new TokenId("token/branch-3"),
            new OperationAttemptId("operation-attempt/charge-1")
        ];

        foreach (var identity in identities)
        {
            var identityType = identity.GetType();
            var json = JsonSerializer.Serialize(identity, identityType, JsonOptions);
            var roundTrip = JsonSerializer.Deserialize(json, identityType, JsonOptions);

            Assert.Equal($"\"{identity}\"", json);
            Assert.Equal(identity, roundTrip);
            Assert.Equal(identity.GetHashCode(), roundTrip?.GetHashCode());
        }
    }

    [Fact]
    public void Identities_RequireExplicitNonEmptyValues()
    {
        Func<string, object>[] factories =
        [
            static value => new ExecutionDefinitionId(value),
            static value => new ExecutionRevisionId(value),
            static value => new ExecutionNodeId(value),
            static value => new ProcessInstanceId(value),
            static value => new ProcessAttemptId(value),
            static value => new ActivationId(value),
            static value => new TokenId(value),
            static value => new OperationAttemptId(value)
        ];

        foreach (var factory in factories)
        {
            Assert.Throws<ArgumentNullException>(() => factory(null!));
            Assert.Throws<ArgumentException>(() => factory("  "));
        }
    }

    [Fact]
    public void IdentityKinds_RemainNominallyDistinct()
    {
        const string value = "shared-text";

        Assert.NotEqual<object>(new ExecutionDefinitionId(value), new ExecutionRevisionId(value));
        Assert.NotEqual<object>(new ProcessInstanceId(value), new ProcessAttemptId(value));
        Assert.NotEqual<object>(new ActivationId(value), new TokenId(value));
        Assert.NotEqual<object>(new TokenId(value), new OperationAttemptId(value));
    }

    [Fact]
    public void ProcessContinuationIdentity_OwnsAndRoundTripsTheInstanceAttemptPair()
    {
        var continuation = new ProcessContinuationIdentity(
            processInstanceId: new("process/order-123"),
            processAttemptId: new("attempt/process-2"));

        var json = JsonSerializer.Serialize(continuation, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ProcessContinuationIdentity>(json, JsonOptions);

        Assert.Equal(continuation, roundTrip);
        Assert.Equal(new ProcessInstanceId("process/order-123"), roundTrip?.ProcessInstanceId);
        Assert.Equal(new ProcessAttemptId("attempt/process-2"), roundTrip?.ProcessAttemptId);
    }

    [Fact]
    public void ProcessContinuationIdentity_RejectsDefaultComponents()
    {
        var processInstanceId = new ProcessInstanceId("process/order-123");
        var processAttemptId = new ProcessAttemptId("attempt/process-2");

        Assert.Throws<ArgumentException>(() => new ProcessContinuationIdentity(default, processAttemptId));
        Assert.Throws<ArgumentException>(() => new ProcessContinuationIdentity(processInstanceId, default));
    }
}
