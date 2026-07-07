namespace Cohesive.Relations.Execution;

/// <summary>
/// Explainability callback for projection execution.
/// </summary>
public interface IRelationExecutionListener
{
    /// <summary>
    /// Called after every field assignment.
    /// </summary>
    void OnAssignment(RelationAssignmentTrace trace);
}