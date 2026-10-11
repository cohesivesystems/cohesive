using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

internal static class PortableFieldConcurrencyBenchmark
{
    internal static int Run(string[] args)
    {
        var iterations = args.Length == 0 ? 2_000 : int.Parse(args[0], CultureInfo.InvariantCulture);
        if (iterations is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(args));
        Console.WriteLine("kind,shape,workers,sample,iterations,elapsed_ms,allocated_bytes,bytes_per_operation,validation_bytes,writer_bytes");
        foreach (var shape in new[] { "flat", "nested", "deep" }) Measure(shape, iterations);
        return 0;
    }

    static void Measure(string shape, int iterations)
    {
        var observation = ObservationValue.FromObject(Enumerable.Range(0, 128).Reverse()
            .ToDictionary(index => $"field{index:D3}", _ => ObservationValue.FromBool(true)));
        if (shape == "nested")
            observation = ObservationValue.FromObject(Enumerable.Range(0, 128).Reverse().ToDictionary(
                index => $"field{index:D3}", _ => ObservationValue.FromObject(new Dictionary<string, ObservationValue>
                {
                    ["value"] = ObservationValue.FromBool(true)
                })));
        if (shape == "deep")
            for (var depth = 0; depth < 384; depth++)
                observation = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["child"] = observation });
        var portable = PortableValue.Concrete(new ValueContract(new JsonTypeRef(JsonTypeKind.Object)), observation);
        var expected = new ArrayBufferWriter<byte>();
        CanonicalJsonWriter.WriteCanonicalObservationValue(expected, observation);
        foreach (var workers in new[] { 1, 2, 4, 8 })
        {
            for (var sample = 0; sample < 3; sample++)
            {
                using var ready = new Barrier(workers + 1);
                void WaitForWorkers()
                {
                    if (!ready.SignalAndWait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("A parallel field worker failed to reach the measurement gate.");
                }
                var tasks = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(() =>
                {
                    var buffer = new ArrayBufferWriter<byte>();
                    void Run()
                    {
                        if (!PortableExecutionValidator.Validate(portable).IsValid)
                            throw new InvalidOperationException("Parallel validation failed.");
                        buffer.Clear();
                        CanonicalJsonWriter.WriteCanonicalObservationValue(buffer, observation);
                    }
                    for (var warm = 0; warm < 256; warm++) Run();
                    var allocationStart = GC.GetAllocatedBytesForCurrentThread();
                    if (!PortableExecutionValidator.Validate(portable).IsValid) throw new InvalidOperationException();
                    var validationBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                    buffer.Clear();
                    allocationStart = GC.GetAllocatedBytesForCurrentThread();
                    CanonicalJsonWriter.WriteCanonicalObservationValue(buffer, observation);
                    var writerBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                    WaitForWorkers();
                    WaitForWorkers();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var iteration = 0; iteration < iterations; iteration++) Run();
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (!buffer.WrittenSpan.SequenceEqual(expected.WrittenSpan))
                        throw new InvalidOperationException("Parallel canonical bytes changed.");
                    return (Bytes: bytes, ValidationBytes: validationBytes, WriterBytes: writerBytes);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                WaitForWorkers();
                // Exclude worker creation and warmup; include only the start gate and measured work.
                var timer = Stopwatch.StartNew();
                WaitForWorkers();
                Task.WaitAll(tasks);
                timer.Stop();
                var bytes = tasks.Sum(task => task.Result.Bytes);
                var operations = workers * iterations;
                Console.WriteLine($"field-benchmark,{shape},{workers},{sample},{iterations},{timer.Elapsed.TotalMilliseconds:F3},{bytes},{bytes / (double)operations:F1},{tasks.Average(task => task.Result.ValidationBytes):F1},{tasks.Average(task => task.Result.WriterBytes):F1}");

            }
        }
    }
}
