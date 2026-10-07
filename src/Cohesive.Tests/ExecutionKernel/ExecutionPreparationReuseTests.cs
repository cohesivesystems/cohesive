using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Model;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionPreparationReuseTests
{
    [Fact]
    public void ImmutableProjectionIsSharedThroughConcurrentFirstUse()
    {
        var document = Create(new ImmutablePayload("value"));
        ImmutablePayload.Reads = 0;
        var results = new ImmutablePayload[32];
        Parallel.For(0, results.Length, index => results[index] = document.GetDefinition<ImmutablePayload>());
        Assert.Equal(1, ImmutablePayload.Reads);
        Assert.All(results, value => Assert.Same(results[0], value));
        Assert.Same(results[0], ExecutionDefinitionJsonSerializer.DeserializeDefinition<ImmutablePayload>(document));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 128; index++) _ = document.GetDefinition<ImmutablePayload>();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var independent = new ExecutionDefinitionDocument(document.Kind, document.Metadata, document.Definition);
        Assert.NotSame(results[0], independent.GetDefinition<ImmutablePayload>());
    }

    [Fact]
    public async Task DifferentProjectionTypesDoNotBlockEachOther()
    {
        var document = Create(new MutablePayload(["original"]));
        BlockingPayload.Entered.Reset();
        BlockingPayload.Release.Reset();
        var first = Task.Run(() => document.GetDefinition<BlockingPayload>());
        try
        {
            Assert.True(BlockingPayload.Entered.Wait(TimeSpan.FromSeconds(5)));
            var second = Task.Run(() => document.GetDefinition<IndependentPayload>());
            var result = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("original", result.Values[0]);
            Assert.False(first.IsCompleted);
        }
        finally
        {
            BlockingPayload.Release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void ArbitraryMutableProjectionsRemainIndependent()
    {
        var document = Create(new MutablePayload(["original"]));
        var first = document.GetDefinition<MutablePayload>();
        first.Values[0] = "changed";
        Assert.Equal("original", document.GetDefinition<MutablePayload>().Values[0]);
    }

    [Fact]
    public void FailedImmutableDecodingRemainsRetryable()
    {
        var original = Create(new ImmutablePayload("value"));
        using var parsed = JsonDocument.Parse("{\"$types\":[],\"text\":1}");
        var document = new ExecutionDefinitionDocument(original.Kind, original.Metadata, parsed.RootElement);
        var first = Assert.Throws<JsonException>(() => document.GetDefinition<ImmutablePayload>());
        var second = Assert.Throws<JsonException>(() => document.GetDefinition<ImmutablePayload>());
        Assert.NotSame(first, second);
    }

    [Fact]
    public void HashingDoesNotAllocatePayloadTextAndEqualityStillChecksContent()
    {
        var document = Create(new ImmutablePayload(new string('x', 100_000)));
        using var parsed = JsonDocument.Parse("{\"$types\":[],\"text\":\"different\"}");
        var forged = new ExecutionDefinitionDocument(document.Kind, document.Metadata, parsed.RootElement);
        Assert.NotEqual(document, forged);
        Assert.Equal(document.GetHashCode(), forged.GetHashCode());
        for (var i = 0; i < 16; i++) _ = document.GetHashCode();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 128; i++) _ = document.GetHashCode();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.False(ExecutionDefinitionDocumentValidator.Validate(forged).IsValid);
    }

    [Fact]
    public void SuccessfulNestedTypeValidationDoesNotFormatPointers()
    {
        TypeRef type = new ObjectTypeRef([.. Enumerable.Range(0, 128).Select(index =>
            new ObjectFieldTypeDef($"field{index}", new ScalarTypeRef(ScalarTypeKind.String)))]);
        Assert.True(PortableExecutionValidator.Validate(type).IsValid);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = PortableExecutionValidator.Validate(type);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(result.IsValid);
        Assert.InRange(allocated, 0, 25_000);
    }

    [Fact]
    public void LazyPointersSnapshotEscapingAndSiblingPathsAtFailure()
    {
        var observation = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["a/b~c"] = ObservationValue.Undefined,
            ["second"] = ObservationValue.Undefined
        });
        var contract = new ValueContract(new ObjectTypeRef([]));
        var result = PortableExecutionValidator.Validate(PortableValue.Concrete(contract, observation));
        var paths = result.Diagnostics.Where(diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.UndefinedObservation)
            .Select(diagnostic => diagnostic.Location).ToArray();
        Assert.Equal(["/value/a~1b~0c", "/value/second"], paths);
    }

    static ExecutionDefinitionDocument Create<T>(T payload) => ExecutionDefinitionDocument.Create(
        new("test"), new("test/reuse"), new("1"), payload,
        new(new("tests", "1"), new("tests/reuse"), DocumentOrigin.Compiled));

    public sealed record MutablePayload(string[] Values);
    public sealed record ImmutablePayload : IImmutableExecutionDefinition
    {
        public static int Reads;
        public ImmutablePayload(string text) { Text = text; Interlocked.Increment(ref Reads); }
        public string Text { get; }
    }
    public sealed record BlockingPayload : IImmutableExecutionDefinition
    {
        public static readonly ManualResetEventSlim Entered = new();
        public static readonly ManualResetEventSlim Release = new();
        public BlockingPayload(System.Collections.Immutable.ImmutableArray<string> values)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test failed to release decoding.");
            Values = values;
        }
        public System.Collections.Immutable.ImmutableArray<string> Values { get; }
    }
    public sealed record IndependentPayload(System.Collections.Immutable.ImmutableArray<string> Values) : IImmutableExecutionDefinition;
}
