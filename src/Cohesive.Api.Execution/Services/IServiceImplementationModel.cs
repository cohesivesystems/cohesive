namespace Cohesive.Api.Execution.Services;

/// <summary>Portable implementation contribution exposed by an activated application component.</summary>
public interface IServiceImplementationModel
{
    /// <summary>Offline endpoint coverage and selected implementation authorities.</summary>
    ServiceImplementationPlan Plan { get; }
}
