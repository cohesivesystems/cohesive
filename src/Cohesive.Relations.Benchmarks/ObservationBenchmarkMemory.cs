namespace Cohesive.Relations.Benchmarks;

// Diagnostic-only full-GC snapshots. GetTotalMemory disagreed with last-GC live heap accounting
// in the installed runtime; see the field-storage investigation. Never invoke during timed work.
internal static class ObservationBenchmarkMemory
{
    internal static long CollectLiveBytes()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var heap = GC.GetGCMemoryInfo();
        return heap.HeapSizeBytes - heap.FragmentedBytes;
    }
}
