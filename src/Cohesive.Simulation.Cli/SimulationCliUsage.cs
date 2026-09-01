using Cohesive.Simulation.Provisioning;

namespace Cohesive.Simulation.Cli;

static class SimulationCliUsage
{
    public static void WriteTo(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine("Usage:");
        writer.WriteLine("  cohesive-sim provision --world <path|-> --seed <int64> --target <id> [options]");
        writer.WriteLine();
        writer.WriteLine("Required:");
        writer.WriteLine("  --world <path|->       Portable world-definition JSON path, or '-' for standard input.");
        writer.WriteLine("  --seed <int64>         Deterministic signed 64-bit root seed.");
        writer.WriteLine("  --target <id>          Stable logical identity of this JSON Lines destination.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --out <path|->         JSON Lines output path, or '-' for standard output (default).");
        writer.WriteLine($"  --batch-size <count>   Positive provisioning batch size (default: {WorldProvisioningOptions.DefaultBatchSize}).");
        writer.WriteLine("  --help, -h             Show this help.");
        writer.WriteLine();
        writer.WriteLine("Example:");
        writer.WriteLine("  cohesive-sim provision --world demo.world.json --seed 42 \\");
        writer.WriteLine("    --target playwright/global-setup --out test-results/demo-world.jsonl");
    }
}
