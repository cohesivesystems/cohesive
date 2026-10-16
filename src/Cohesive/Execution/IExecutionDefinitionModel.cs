namespace Cohesive.Execution;

/// <summary>A declaration contributing one canonical document to an application's exact-reference catalog.</summary>
public interface IExecutionDefinitionModel
{
    /// <summary>The authoritative document; preparation is cached by the model.</summary>
    ExecutionDefinitionDocument Document { get; }
}
