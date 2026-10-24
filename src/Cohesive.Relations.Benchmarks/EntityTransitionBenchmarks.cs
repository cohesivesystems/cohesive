using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm latency and allocation of one entity API transition, request bytes to response bytes.</summary>
[MemoryDiagnoser]
public class EntityTransitionEndToEndBenchmarks
{
    EntityTransitionEndToEnd fixture = null!;

    /// <summary>Repository behind the endpoint.</summary>
    [Params(EntityTransitionStore.InMemory, EntityTransitionStore.Sqlite)]
    public EntityTransitionStore Store { get; set; }

    /// <summary>Response projection used by the endpoint.</summary>
    [Params(EntityTransitionResponse.ClrResource, EntityTransitionResponse.Observation)]
    public EntityTransitionResponse Response { get; set; }

    /// <summary>Builds the host, seeds one entity and verifies one complete transition outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        fixture = EntityTransitionEndToEnd.Create(Store, Response);
        fixture.InvokeAsync().GetAwaiter().GetResult();
    }

    /// <summary>Releases the host and any database file.</summary>
    [GlobalCleanup]
    public void Cleanup() => fixture.Dispose();

    /// <summary>Executes one committed carrier assignment through the mapped endpoint.</summary>
    /// <returns>The response byte count.</returns>
    [Benchmark]
    public Task<int> Transition() => fixture.InvokeAsync();
}

// Opt-in allocation qualification: whole-endpoint medians plus a stage partition of the same work.
// Stages call the public operations the binding composes; they are attribution aids, not a second pipeline.
internal static class EntityTransitionAllocationBenchmark
{
    internal static int Run(string[] args)
    {
        var count = AllocationMeasurement.Iterations(args);
        Console.WriteLine("store,response,workload,median_bytes_per_operation,median_microseconds_per_operation");
        foreach (var store in Enum.GetValues<EntityTransitionStore>())
        {
            foreach (var response in Enum.GetValues<EntityTransitionResponse>())
            {
                using var fixture = EntityTransitionEndToEnd.Create(store, response);
                Measure(store, response, "endpoint", () => fixture.InvokeAsync().GetAwaiter().GetResult(), count);
                if (response == EntityTransitionResponse.ClrResource)
                    MeasureStages(store, fixture, count);
            }
        }
        return 0;
    }

    static void MeasureStages(EntityTransitionStore store, EntityTransitionEndToEnd fixture, int count)
    {
        var context = fixture.Context;
        var entity = EntityTransitionEndToEnd.Entity;
        var snapshot = fixture.Repository.TryGet(context, "shipment-1", EntityReadOptions.Full).GetAwaiter().GetResult()!;
        var input = fixture.NextInput();
        var activation = new ActivationId("benchmark/activation");
        TransitionDecision Decide() => TransitionReferenceInterpreter.DecideFullState(fixture.Plan, activation, input,
            PortableValue.Concrete(fixture.Plan.Definition.Observation,
                ObservationValue.FromObject(entity.CreateState(snapshot.Entity).Fields)));
        var decision = Decide();
        var candidate = TransitionStateProjector.ApplyToEntity(entity, "shipment-1", decision, snapshot.Entity);
        var committed = fixture.Repository.Upsert(context, new(candidate.Snapshot)).GetAwaiter().GetResult();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = null };

        var none = EntityTransitionResponse.ClrResource;
        Measure(store, none, "stage:read", () => fixture.Repository.TryGet(context, "shipment-1", EntityReadOptions.Full).GetAwaiter().GetResult(), count);
        Measure(store, none, "stage:decide", Decide, count);
        var loadedState = PortableValue.Concrete(fixture.Plan.Definition.Observation,
            ObservationValue.FromObject(entity.CreateState(snapshot.Entity).Fields));
        Measure(store, none, "stage:decide/validate-loaded", () => entity.CreateState(snapshot.Entity), count);
        Measure(store, none, "stage:decide/interpret", () => TransitionReferenceInterpreter.DecideFullState(
            fixture.Plan, activation, input, loadedState), count);
        Measure(store, none, "stage:apply", () => TransitionStateProjector.ApplyToEntity(entity, "shipment-1", decision, snapshot.Entity), count);
        Measure(store, none, "stage:commit-unconditional", () => fixture.Repository.Upsert(context, new(candidate.Snapshot)).GetAwaiter().GetResult(), count);
        Measure(store, none, "stage:respond-clr", () => JsonSerializer.SerializeToUtf8Bytes(EntityTransitionEndToEnd.ToResource(committed), options), count);
        Measure(store, EntityTransitionResponse.Observation, "stage:respond-observation",
            () => JsonSerializer.SerializeToUtf8Bytes(committed.Entity.Observation.Value), count);
    }

    // Precise process-wide counters: endpoint continuations may resume on another thread.
    static void Measure(EntityTransitionStore store, EntityTransitionResponse response, string workload, Func<object?> operation, int count)
    {
        for (var warm = 0; warm < 50; warm++) GC.KeepAlive(operation());
        var bytes = new double[7];
        var micros = new double[7];
        for (var sample = 0; sample < bytes.Length; sample++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var iteration = 0; iteration < count; iteration++) GC.KeepAlive(operation());
            micros[sample] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMicroseconds / count;
            bytes[sample] = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)count;
        }
        Array.Sort(bytes);
        Array.Sort(micros);
        Console.WriteLine(string.Join(',', store, response, workload,
            bytes[3].ToString("F0", CultureInfo.InvariantCulture), micros[3].ToString("F1", CultureInfo.InvariantCulture)));
    }
}
