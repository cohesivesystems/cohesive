namespace Cohesive.Adapters.SQLite.Tests;

// The test runner loads this assembly normally. A recovery test launches its explicit executable entry point
// to kill a writer without managed disposal. SQL comes only from the parent test, never production input.
internal static class SqliteCrashWorker
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--process-retain-worker")
        {
            var fixture = Cohesive.Tests.ExecutionKernel.ProcessDurabilityTestFixture.Create();
            var store = SqliteProcessDurableStoreTests.Open(args[1]);
            var host = new SqliteProcessDurableStoreTests.Host(fixture.OperationResult);
            var runtime = SqliteProcessDurableStoreTests.Runtime(store, fixture, host);
            var initialized = await runtime.InitializeAsync(OperationContext.Create(), fixture.Plan, fixture.Start);
            var result = await runtime.ActivateAsync(OperationContext.Create(), fixture.Plan, initialized.Snapshot!.Checkpoint.ContinuationIdentity, fixture.Activation);
            if (result.Disposition != Cohesive.Storage.Processes.ProcessDurableRuntimeDisposition.Applied) return 4;
            Console.WriteLine("activated");
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 3;
        }
        if (args.Length == 3 && args[0] == "--ingestion-retain-worker")
        {
            var store = new SqliteIngestionWorkStore(new(new(args[1], durability: SqliteDurability.Full)));
            var count = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            foreach (var boundary in IngestionWorkFixture.Chain().Take(count))
                await store.RetainAsync(OperationContext.Create(), boundary.Document, boundary.Content);
            Console.WriteLine("retained");
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 3;
        }
        if (args.Length != 3 || args[0] != "--sqlite-crash-worker") return 2;
        var database = new SqliteDatabase(new(args[1]));
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = database.CreateCommand(connection, transaction, args[2]);
        command.ExecuteNonQuery();
        Console.WriteLine("uncommitted");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 3;
    }
}
