namespace Cohesive.AI.Training;

/// <summary>
/// Registers materialized training datasets with a provider-specific asset registry.
/// </summary>
public interface ITrainingDatasetRegistry
{
    /// <summary>
    /// Registers one materialized dataset asset.
    /// </summary>
    ValueTask<TrainingDatasetRegistration> RegisterAsync(TrainingDatasetRegistrationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Registration request for one materialized dataset asset.
/// </summary>
public sealed record TrainingDatasetRegistrationRequest(
    string AssetName,
    string AssetVersion,
    Uri Location,
    bool IsFolder,
    IReadOnlyDictionary<string, string>? Tags = null
    );

/// <summary>
/// Provider response describing one registered dataset asset.
/// </summary>
public sealed record TrainingDatasetRegistration(
    string AssetName,
    string AssetVersion,
    Uri AssetUri
    );


/// <summary>Represents a passthrough training dataset registry.</summary>
public sealed class PassthroughTrainingDatasetRegistry : ITrainingDatasetRegistry
{
    /// <summary>Registers the value asynchronously.</summary>
    public ValueTask<TrainingDatasetRegistration> RegisterAsync(TrainingDatasetRegistrationRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new TrainingDatasetRegistration(AssetName: request.AssetName, AssetVersion: request.AssetVersion, AssetUri: request.Location));
    }
}
