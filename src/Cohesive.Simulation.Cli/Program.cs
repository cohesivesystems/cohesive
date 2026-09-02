namespace Cohesive.Simulation.Cli;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            return await SimulationCliApplication.RunAsync(
                    args,
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput(),
                    Console.Error,
                    cancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
}
