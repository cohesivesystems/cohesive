namespace Cohesive.Processes.Model;

/// <summary>
/// Strongly typed process definition wrapper that binds a runtime input type and output type to a lowered process definition.
/// </summary>
/// <typeparam name="TInput">Primary process input type.</typeparam>
/// <typeparam name="TOutput">Process result type.</typeparam>
public sealed class TypedProcessDefinition<TInput, TOutput>
{
    /// <summary>
    /// Creates a strongly typed process definition wrapper.
    /// </summary>
    /// <param name="definition">Lowered process definition to execute.</param>
    /// <param name="inputParameterName">Runtime parameter name used to supply the typed input value.</param>
    public TypedProcessDefinition(ProcessDefinition definition, string inputParameterName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputParameterName);

        Definition = definition;
        InputParameterName = inputParameterName;
    }

    /// <summary>
    /// Lowered process definition to execute.
    /// </summary>
    public ProcessDefinition Definition { get; }

    /// <summary>
    /// Runtime parameter name used to pass the typed input value into the process.
    /// </summary>
    public string InputParameterName { get; }
}
