using Cohesive.Relations.Model;
using Cohesive.Model;

namespace Cohesive.Relations.Execution;

/// <summary>
/// Assignment-level execution trace emitted by the projection runner.
/// </summary>
public sealed record RelationAssignmentTrace(
    string RuleId,
    string TargetField,
    IReadOnlyList<FieldPath> SourcePaths,
    Expr Expression,
    string ObservationKey
);
