namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Local registry for code-defined process definitions used by the Durable Task adapter.
/// </summary>
public sealed class DurableTaskProcessDefinitionRegistry
{
    readonly Lock gate = new();
    readonly Dictionary<string, ProcessDefinition> definitionsByName = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a process definition by name.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public DurableTaskProcessDefinitionRegistry Register(ProcessDefinition process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (gate)
        {
            if (definitionsByName.TryGetValue(process.Name, out var existing))
            {
                if (ReferenceEquals(existing, process))
                    return this;

                throw new InvalidOperationException(
                    $"A durable process definition named '{process.Name}' is already registered. " +
                    "Use a unique process name or reuse the same definition instance."
                    );
            }
            definitionsByName.Add(process.Name, process);
            return this;
        }
    }

    /// <summary>
    /// Gets a registered process definition.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public ProcessDefinition Get(string processName)
    {
        if (!TryGet(processName, out var process))
            throw new ArgumentException($"No durable process definition named '{processName}' is registered.");

        return process;
    }

    /// <summary>
    /// Tries to get a registered process definition.
    /// </summary>
    public bool TryGet(string processName, out ProcessDefinition process)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        lock (gate)
            return definitionsByName.TryGetValue(processName, out process!);
    }
}
