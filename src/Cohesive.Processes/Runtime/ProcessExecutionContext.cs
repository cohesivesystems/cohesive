namespace Cohesive.Processes.Runtime;

/// <summary>
/// Mutable process execution context available to process node expressions.
/// </summary>
public sealed class ProcessExecutionContext
{
    readonly Dictionary<string, object?> variables = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an execution context.
    /// </summary>
    public ProcessExecutionContext(
        string processId,
        string processName,
        IReadOnlyDictionary<string, object?> parameters,
        string currentPlace
        )
    {
        ProcessId = Guard.RequireNotNullOrWhiteSpace(processId);
        ProcessName = Guard.RequireNotNullOrWhiteSpace(processName);
        Parameters = new Dictionary<string, object?>(
            Guard.RequireNotNull(parameters),
            StringComparer.Ordinal);
        CurrentPlace = Guard.RequireNotNullOrWhiteSpace(currentPlace);
    }

    /// <summary>
    /// Process id.
    /// </summary>
    public string ProcessId { get; }

    /// <summary>
    /// Process definition name.
    /// </summary>
    public string ProcessName { get; }

    /// <summary>
    /// Immutable process parameters.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>
    /// Mutable process variable map.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Variables => variables;

    /// <summary>
    /// Current execution place.
    /// </summary>
    public string CurrentPlace { get; internal set; }

    /// <summary>
    /// Writes a variable.
    /// </summary>
    public void SetVariable(string name, object? value) => variables[Guard.RequireNotNullOrWhiteSpace(name)] = value;

    /// <summary>
    /// Returns true when the variable exists.
    /// </summary>
    public bool ContainsVariable(string name) => variables.ContainsKey(Guard.RequireNotNullOrWhiteSpace(name));

    /// <summary>
    /// Returns variable or null.
    /// </summary>
    public object? GetVariable(string name)
    {
        variables.TryGetValue(Guard.RequireNotNullOrWhiteSpace(name), out var value);
        return value;
    }

    /// <summary>
    /// Gets variable with type conversion checks.
    /// </summary>
    public T? GetVariable<T>(string name)
    {
        if (!variables.TryGetValue(Guard.RequireNotNullOrWhiteSpace(name), out var value))
            return default;

        if (value is null)
            return default;

        if (value is not T typed)
            throw new SemanticRuleViolationException($"Process variable '{name}' expected type '{typeof(T).FullName}' but was '{value.GetType().FullName}'.");

        return typed;
    }

    /// <summary>
    /// Requires a variable value.
    /// </summary>
    public T RequireVariable<T>(string name)
    {
        if (!variables.TryGetValue(Guard.RequireNotNullOrWhiteSpace(name), out var value) || value is null)
            throw new SemanticRuleViolationException($"Process variable '{name}' is required but was not found.");

        if (value is not T typed)
            throw new SemanticRuleViolationException($"Process variable '{name}' expected type '{typeof(T).FullName}' but was '{value.GetType().FullName}'.");

        return typed;
    }

    /// <summary>
    /// Returns parameter or null.
    /// </summary>
    public object? GetParameter(string name)
    {
        Parameters.TryGetValue(Guard.RequireNotNullOrWhiteSpace(name), out var value);
        return value;
    }

    /// <summary>
    /// Requires a typed parameter.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException"></exception>
    public T RequireParameter<T>(string name)
    {
        if (!Parameters.TryGetValue(Guard.RequireNotNullOrWhiteSpace(name), out var value) || value is null)
            throw new SemanticRuleViolationException($"Process parameter '{name}' is required but was not found.");

        if (value is not T typed)
            throw new SemanticRuleViolationException($"Process parameter '{name}' expected type '{typeof(T).FullName}' but was '{value.GetType().FullName}'.");

        return typed;
    }

    internal Dictionary<string, object?> CloneVariables() => 
        new(variables, StringComparer.Ordinal);

    internal void RestoreVariables(Dictionary<string, object?> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        variables.Clear();
        foreach (var (name, value) in snapshot)
            variables[name] = value;
    }
}