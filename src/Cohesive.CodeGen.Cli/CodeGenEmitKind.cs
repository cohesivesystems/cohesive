namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Artifact kinds supported by the codegen CLI.
/// </summary>
public enum CodeGenEmitKind
{
    Shapes = 0,
    Apis = 1,
    OpenApi = 2,
    GraphQL = 3,
    Constants = 4,
    ApiPlaywright = 5,
    Transitions = 6,
    Processes = 7,
    Invariants = 8
}
