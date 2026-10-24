namespace Cohesive.Storage;

/// <summary>One offline entity placement supplying both repository access and query acquisition.</summary>
public interface IEntityStorageBinding
{
    /// <summary>Exact entity observation contract supplied by the repository.</summary>
    Cohesive.Model.ValueContract Observation { get; }
    /// <summary>Canonical acquisition contract used to prepare composite queries without clients.</summary>
    EntityRelationQuerySourceBinding Source { get; }

    /// <summary>Validates and binds the native resource borrowed from the host service provider.</summary>
    /// <param name="services">Host-owned native client registrations.</param>
    /// <param name="cancellationToken">Cancels metadata validation before traffic admission.</param>
    /// <returns>A shared repository and source; does not own native clients.</returns>
    ValueTask<EntityStorageRegistration> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default);
}
