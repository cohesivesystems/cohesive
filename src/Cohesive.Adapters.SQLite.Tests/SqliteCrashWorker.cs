namespace Cohesive.Adapters.SQLite.Tests;

// The test runner loads this assembly normally. A recovery test launches its explicit executable entry point
// to kill a writer without managed disposal. SQL comes only from the parent test, never production input.
internal static class SqliteCrashWorker
{
    static async Task<int> Main(string[] args)
    {
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
