namespace Cohesive.CodeGen.Cli;

/// <summary>
/// Artifact kinds supported by the codegen CLI.
/// </summary>
public enum CodeGenEmitKind
{
    /// <summary>Represents the shapes option.</summary>
    Shapes = 0,
    /// <summary>Represents the apis option.</summary>
    Apis = 1,
    /// <summary>Represents the open api option.</summary>
    OpenApi = 2,
    /// <summary>Represents the graph ql option.</summary>
    GraphQL = 3,
    /// <summary>Represents the constants option.</summary>
    Constants = 4,
    /// <summary>Represents the api playwright option.</summary>
    ApiPlaywright = 5,
    /// <summary>Represents the transitions option.</summary>
    Transitions = 6,
    /// <summary>Represents the processes option.</summary>
    Processes = 7,
    /// <summary>Represents the invariants option.</summary>
    Invariants = 8
}
