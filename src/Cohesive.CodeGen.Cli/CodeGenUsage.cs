namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Usage text for the codegen CLI.
/// </summary>
public static class CodeGenUsage
{
    /// <summary>
    /// Writes usage information to the supplied text writer.
    /// </summary>
    public static void WriteTo(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  cohesive-codegen --contracts <contracts.dll> --out <dir> --emit <kinds> --module <name>");
        writer.WriteLine();
        writer.WriteLine("Example:");
        writer.WriteLine("  cohesive-codegen \\");
        writer.WriteLine("    --contracts path/to/MyApp.Api.Contracts.dll \\");
        writer.WriteLine("    --out path/to/react-app/src/generated \\");
        writer.WriteLine("    --emit shapes,apis \\");
        writer.WriteLine("    --module myapp");
        writer.WriteLine();
        writer.WriteLine("Optional:");
        writer.WriteLine("  --shape-projection <clr|canonical-json>");
        writer.WriteLine("    Project CLR semantics (default) or canonical JSON wire names and values.");
        writer.WriteLine("  --external-shapes <clr-namespace-prefix>=<typescript-import-path>");
        writer.WriteLine("    Treat matching CLR namespace shapes as owned by another generated TypeScript module.");
        writer.WriteLine("  --union-catalog <generated-union-type>=<typescript-export-name>");
        writer.WriteLine("    Emit a readonly runtime catalog derived from a closed union's discriminator cases.");
        writer.WriteLine();
        writer.WriteLine("Supported emit kinds:");
        writer.WriteLine("  shapes");
        writer.WriteLine("  apis");
        writer.WriteLine("  openapi");
        writer.WriteLine("  graphql");
        writer.WriteLine("  constants");
        writer.WriteLine("  transitions (reserved)");
        writer.WriteLine("  processes (reserved)");
        writer.WriteLine("  invariants (reserved)");
    }
}
