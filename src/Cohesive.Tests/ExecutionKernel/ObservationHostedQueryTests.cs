using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Xunit.Abstractions;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ObservationHostedQueryTests(ITestOutputHelper output)
{
    static readonly FieldPath Name = FieldPath.Capture<SchemaOnlyRow>(row => row.Name);

    [Fact]
    public async Task ObservationViewRetainsExactQueryAndDoesNotConstructItsClrSchema()
    {
        var query = Query<SchemaOnlyRow>();
        var view = query.AsObservation();
        Assert.Same(query.Document, view.Document);
        Assert.Same(query.Validation, view.Validation);
        Assert.Equal(query.Reference, view.Reference);
        Assert.Equal(query.InputContract, view.InputContract);
        Assert.Equal(query.ResultContract, view.ResultContract);
        var observed = Value(128);
        var catalog = new ProcessRelationHandlerCatalog([
            ProcessRelationHandlerRegistration.CreateDeterministic(view, query.Implementation, (value, _, _) => value)]);
        var evaluation = ProcessRelationHandlerCatalogTests.Evaluation(query.Reference, query.InputContract, observed);
        var result = await catalog.EvaluateAsync(OperationContext.Create(), evaluation);
        Assert.True(result.IsSuccessful);
        Assert.Same(observed.Fields, result.Value!.Value!.Value.Fields);
    }

    [Fact]
    public void SparseReadAndWriteShareUntouchedStorageAcrossIndependentInvocations()
    {
        var query = Query<SchemaOnlyRow>();
        var source = Value(128);
        var binding = DeterministicHostedQueryBinding.Create(query.AsObservation(), query.Implementation,
            (value, _, _) =>
            {
                Assert.True(value.TryGetField(Name, out var name));
                return value.WithField(Name, ObservationValue.FromString(name.GetString() + "/updated"));
            });
        var input = PortableValue.Concrete(query.InputContract, source);
        var first = binding.Evaluate(input).Success!.Value!.Value;
        var second = binding.Evaluate(input).Success!.Value!.Value;
        Assert.NotSame(first.Fields, second.Fields);
        Assert.Equal("source", source.Fields![nameof(SchemaOnlyRow.Name)].GetString());
        Assert.Equal("source/updated", first.Fields![nameof(SchemaOnlyRow.Name)].GetString());
        Assert.True(source.Fields[nameof(SchemaOnlyRow.Payload)].Array.Equals(first.Fields[nameof(SchemaOnlyRow.Payload)].Array));
    }

    [Fact]
    public void ObservationViewKeepsAdmissionResultValidationAndCancellation()
    {
        var query = Query<SchemaOnlyRow>();
        var calls = 0;
        var binding = DeterministicHostedQueryBinding.Create(query.AsObservation(), query.Implementation,
            (value, _, _) => { calls++; return ObservationValue.FromString("wrong result"); });
        var missing = binding.Evaluate(PortableValue.Missing(query.InputContract));
        Assert.Equal(HostedQueryValueDiagnosticCodes.InputValueInvalid, missing.Failure!.Code);
        var invalid = binding.Evaluate(PortableValue.Concrete(query.InputContract,
            Value(1).WithField(Name, ObservationValue.FromInt64(42))));
        Assert.Equal(ResultType.Failure, invalid.Type);
        Assert.Equal(0, calls);
        var result = binding.Evaluate(PortableValue.Concrete(query.InputContract, Value(1)));
        Assert.Equal(HostedQueryValueDiagnosticCodes.ResultValueInvalid, result.Failure!.Code);
        Assert.Equal(1, calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => binding.Evaluate(
            PortableValue.Concrete(query.InputContract, Value(1)), cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void PersistedValuesAreReadmittedWithoutMaterializingTheClrSchema()
    {
        var query = Query<SchemaOnlyRow>();
        var input = PortableValue.Concrete(query.InputContract, Value(8));
        var restored = JsonSerializer.Deserialize<PortableValue>(JsonSerializer.Serialize(input))!;
        var binding = DeterministicHostedQueryBinding.Create(query.AsObservation(), query.Implementation,
            (value, _, _) => value);
        var result = binding.Evaluate(restored);
        Assert.Equal(ResultType.Success, result.Type);
        Assert.Same(restored.Value!.Value.Fields, result.Success!.Value!.Value.Fields);
        Assert.Equal(input, result.Success);
    }

    [Fact]
    public void WarmForwardingDoesNotAllocateWithPayloadSize()
    {
        var query = Query<MaterializedRow>();
        var observation = DeterministicHostedQueryBinding.Create(query.AsObservation(), query.Implementation,
            (value, _, _) => value);
        var materialized = DeterministicHostedQueryBinding.Create(query, query.Implementation,
            (value, _, _) => value);
        var input = PortableValue.Concrete(query.InputContract, Value(1024));
        var baseline = Measure(materialized, input);
        var direct = Measure(observation, input);
        var large = Measure(observation, PortableValue.Concrete(query.InputContract, Value(8192)));
        output.WriteLine($"Typed forwarding: {baseline}; observation forwarding: {direct}; large observation forwarding: {large} B.");
        Assert.InRange(direct, 0, 4096);
        Assert.InRange(large, 0, 4096);
        Assert.True(baseline > direct + 128 * 1024, $"Typed {baseline}, direct {direct}.");
    }

    static long Measure(DeterministicHostedQueryBinding binding, PortableValue input)
    {
        for (var i = 0; i < 4; i++) binding.Evaluate(input);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = binding.Evaluate(input);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(ResultType.Success, result.Type);
        }
        Array.Sort(samples);
        return samples[3];
    }

    static ObservationValue Value(int count) => ObservationValue.FromObject(new
    {
        Name = "source",
        Payload = Enumerable.Repeat(new string('x', 128), count).ToArray()
    });

    static HostedQuery<TRow, TRow> Query<TRow>() where TRow : notnull => HostedQuery<TRow, TRow>.Create(
        new("query/tests/schema-only"), new("1"), new("tests.schema-only", "1"), "configuration",
        ProcessRelationHandlerCatalogTests.Query().Document.Metadata.Provenance,
        evaluationSemantics: HostedQueryEvaluationSemantics.DeterministicComputation);

    public sealed class SchemaOnlyRow
    {
        public SchemaOnlyRow(string name, string[] payload) => throw new InvalidOperationException("Schema must not be materialized.");
        public string Name { get; } = "";
        public string[] Payload { get; } = [];
    }
    public sealed record MaterializedRow(string Name, string[] Payload);
}
