using System.Reflection;

namespace Cohesive.Host.Cli;

/// <summary>
/// Immutable description of a registered CLI command.
/// </summary>
public sealed record CliCommandDescriptor(
    string Name,
    string Path,
    Type ConfigurationType,
    Type EffectiveContextType,
    MethodInfo? DynamicHandler,
    IReadOnlyList<MethodInfo> DynamicValidators,
    IReadOnlyList<CliCommandDescriptor> Subcommands
    );

/// <summary>
/// Describes a dynamic CLI handler parameter that could not be resolved.
/// </summary>
public sealed record CliDynamicHandlerResolutionError(
    string CommandPath,
    string? ParameterName,
    Type ParameterType,
    string Message
    );

sealed class CliValidationServicesScope(IServiceProvider services, IDisposable? lease = null) : IDisposable
{
    public IServiceProvider Services { get; } = Guard.RequireNotNull(services);

    public void Dispose() => lease?.Dispose();
}
