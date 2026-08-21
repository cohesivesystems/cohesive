using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Materialize;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationConformanceRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutesAnOpenReplicaCatalogInCanonicalOrder()
    {
        List<string> executionOrder = [];
        var replicas = new IMaterializationConformanceReplica<TestResult>[]
        {
            Replica("third", executionOrder),
            Replica("postgres", executionOrder),
            Replica("cosmos", executionOrder)
        };
        var runner = new MaterializationConformanceRunner<TestResult>(
            expectedDefinitionFingerprint: "definition-v1",
            replicas: replicas);

        var results = await runner.RunAsync(OperationContext.Create());

        Assert.Equal(["cosmos", "postgres", "third"], executionOrder);
        Assert.Equal(["cosmos", "postgres", "third"], results.Select(static result => result.Replica));
        Assert.All(results, static result => Assert.Equal<string>(["document-a", "document-b"], result.Documents));
    }

    [Fact]
    public void Constructor_RejectsDuplicateReplicaIdentityBeforeExecution()
    {
        List<string> executionOrder = [];

        var exception = Assert.Throws<ArgumentException>(() =>
            new MaterializationConformanceRunner<TestResult>(
                expectedDefinitionFingerprint: "definition-v1",
                replicas:
                [
                    Replica("postgres", executionOrder),
                    Replica("postgres", executionOrder)
                ]));

        Assert.Contains("repeat a replica identity", exception.Message, StringComparison.Ordinal);
        Assert.Empty(executionOrder);
    }

    [Fact]
    public async Task RunAsync_FailsClosedOnDefinitionDrift()
    {
        var runner = new MaterializationConformanceRunner<TestResult>(
            expectedDefinitionFingerprint: "definition-v1",
            replicas:
            [
                new TestReplica(
                    replica: "postgres",
                    execute: static _ => ValueTask.FromResult(new TestResult(
                        Replica: "postgres",
                        DefinitionFingerprint: "definition-v2",
                        Documents: ["document-a"])))
            ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunAsync(OperationContext.Create()));

        Assert.Contains("another materialization definition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FailsClosedOnCanonicalDocumentDrift()
    {
        var runner = new MaterializationConformanceRunner<TestResult>(
            expectedDefinitionFingerprint: "definition-v1",
            replicas:
            [
                new TestReplica(
                    replica: "cosmos",
                    execute: static _ => ValueTask.FromResult(new TestResult(
                        Replica: "cosmos",
                        DefinitionFingerprint: "definition-v1",
                        Documents: ["document-a"]))),
                new TestReplica(
                    replica: "postgres",
                    execute: static _ => ValueTask.FromResult(new TestResult(
                        Replica: "postgres",
                        DefinitionFingerprint: "definition-v1",
                        Documents: ["document-b"])))
            ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunAsync(OperationContext.Create()));

        Assert.Contains("different canonical materialized documents", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FailsClosedOnNonCanonicalReplicaOrdering()
    {
        var runner = new MaterializationConformanceRunner<TestResult>(
            expectedDefinitionFingerprint: "definition-v1",
            replicas:
            [
                new TestReplica(
                    replica: "postgres",
                    execute: static _ => ValueTask.FromResult(new TestResult(
                        Replica: "postgres",
                        DefinitionFingerprint: "definition-v1",
                        Documents: ["document-b", "document-a"])))
            ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunAsync(OperationContext.Create()));

        Assert.Contains("strict canonical order", exception.Message, StringComparison.Ordinal);
    }

    static TestReplica Replica(string replica, List<string> executionOrder) => new(
        replica: replica,
        execute: context =>
        {
            context.ThrowIfCancellationRequested();
            executionOrder.Add(replica);
            return ValueTask.FromResult(new TestResult(
                Replica: replica,
                DefinitionFingerprint: "definition-v1",
                Documents: ["document-a", "document-b"]));
        });

    sealed record TestResult(
        string Replica,
        string DefinitionFingerprint,
        ImmutableArray<string> Documents) : IMaterializationConformanceResult;

    sealed class TestReplica : IMaterializationConformanceReplica<TestResult>
    {
        readonly Func<OperationContext, ValueTask<TestResult>> execute;

        internal TestReplica(
            string replica,
            Func<OperationContext, ValueTask<TestResult>> execute)
        {
            Replica = replica;
            this.execute = execute;
        }

        public string Replica { get; }

        public ValueTask<TestResult> ExecuteAsync(OperationContext context) => execute(context);
    }
}
