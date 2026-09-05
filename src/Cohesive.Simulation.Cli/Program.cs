namespace Cohesive.Simulation.Cli;

static class Program
{
    static Task<int> Main(string[] args) => SimulationCliApplication.RunAsync(args);
}
