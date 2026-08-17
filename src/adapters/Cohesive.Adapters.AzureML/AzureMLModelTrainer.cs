using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.MachineLearning;
using Azure.ResourceManager.MachineLearning.Models;
using Cohesive.AI.Training;

namespace Cohesive.Adapters.AzureML;

/// <summary>
/// Azure Machine Learning command-job implementation of <see cref="IModelTrainer"/>.
/// </summary>
public sealed class AzureMLModelTrainer : IModelTrainer
{
    const string DefaultOutputName = "trained_model";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly ArmClient armClient;
    readonly AzureMLModelTrainerOptions options;

    /// <summary>Initializes a new instance of the azure ml model trainer type.</summary>
    public AzureMLModelTrainer(TokenCredential credential, AzureMLModelTrainerOptions options, ArmClientOptions? armClientOptions = null)
    {
        this.armClient = new(credential, defaultSubscriptionId: options.SubscriptionId, armClientOptions);
        this.options = options;
        this.options.Validate();
    }
    
    /// <summary>Starts an Azure ML training job.</summary>
    public async ValueTask<TrainingJobReference> StartAsync(TrainingRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var configuration = AzureMLTrainingConfiguration.Parse(request.ConfigJson);
        var workspace = GetWorkspaceResource();
        var jobs = workspace.GetMachineLearningJobs();
        var jobId = CreateJobId(request.OutputModelName);
        var job = CreateJob(jobId, request, configuration);
        var operation = await jobs
            .CreateOrUpdateAsync(WaitUntil.Started, jobId, new(job), ct)
            .ConfigureAwait(false);

        return new(jobId, MapStatus(operation.Value.Data.Properties.Status));
    }

    /// <summary>Gets status asynchronously.</summary>
    public async ValueTask<TrainingJobState> GetStatusAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ct.ThrowIfCancellationRequested();

        var workspace = GetWorkspaceResource();
        var response = await workspace.GetMachineLearningJobAsync(jobId, ct).ConfigureAwait(false);
        var job = response.Value.Data.Properties;
        var status = MapStatus(job.Status);

        return new(
            JobId: jobId,
            Status: status,
            Result: status is TrainingJobStatus.Completed ? TryBuildResult(jobId, job) : null,
            Failure: status is TrainingJobStatus.Failed or TrainingJobStatus.Cancelled
                ? new TrainingJobFailure(
                    ErrorType: $"AzureML.{job.Status?.ToString() ?? nameof(TrainingJobStatus.Unknown)}",
                    ErrorMessage: $"AzureML job '{jobId}' ended with status '{job.Status?.ToString() ?? "Unknown"}'.",
                    IsTransient: false
                    )
                : null
            );
    }

    MachineLearningWorkspaceResource GetWorkspaceResource()
    {
        var id = MachineLearningWorkspaceResource.CreateResourceIdentifier(
            options.SubscriptionId,
            options.ResourceGroupName,
            options.WorkspaceName);
        return armClient.GetMachineLearningWorkspaceResource(id);
    }

    MachineLearningCommandJob CreateJob(string jobId, TrainingRequest request, AzureMLTrainingConfiguration configuration)
    {
        var job = new MachineLearningCommandJob(command: configuration.Command, environmentId: new(resourceId: configuration.EnvironmentId))
        {
            DisplayName = request.OutputModelName,
            ExperimentName = request.ExperimentName,
            Distribution = null,
        };

        if (!string.IsNullOrWhiteSpace(request.ComputeTarget))
            job.ComputeId = ResolveComputeId(request.ComputeTarget);

        if (request.Code is { } codeArtifact)
        {
            var codeInputName = string.IsNullOrWhiteSpace(configuration.CodeInputName)
                ? "code"
                : configuration.CodeInputName;

            job.Inputs[codeInputName] = new MachineLearningUriFileJobInput(new Uri(codeArtifact.BlobUri, UriKind.Absolute));
            job.Properties["cohesive.codeBlobUri"] = codeArtifact.BlobUri;
            job.Properties["cohesive.codeVersion"] = codeArtifact.Version;
            job.EnvironmentVariables["COHESIVE_CODE_BLOB_URI"] = codeArtifact.BlobUri;
            job.EnvironmentVariables["COHESIVE_CODE_VERSION"] = codeArtifact.Version;
        }
        else if (!string.IsNullOrWhiteSpace(configuration.CodeId))
        {
            job.CodeId = new(resourceId: configuration.CodeId);
        }
        else if (!string.IsNullOrWhiteSpace(configuration.CodeInputName))
        {
            throw new InvalidOperationException(
                $"AzureML training configuration requested code input '{configuration.CodeInputName}', but the training request did not include a packaged code artifact.");
        }

        foreach (var dataset in request.Datasets)
            job.Inputs[dataset.Name] = CreateInput(dataset);

        job.Outputs[configuration.OutputName ?? DefaultOutputName] = CreateOutput(configuration.OutputUri);

        job.Properties["cohesive.jobId"] = jobId;
        job.Properties["cohesive.modelName"] = request.ModelName;
        job.Properties["cohesive.outputModelName"] = request.OutputModelName;

        if (!string.IsNullOrWhiteSpace(request.BaseVersion))
            job.Properties["cohesive.baseVersion"] = request.BaseVersion;

        job.EnvironmentVariables["COHESIVE_MODEL_NAME"] = request.ModelName;
        job.EnvironmentVariables["COHESIVE_OUTPUT_MODEL_NAME"] = request.OutputModelName;

        if (!string.IsNullOrWhiteSpace(request.BaseVersion))
            job.EnvironmentVariables["COHESIVE_BASE_VERSION"] = request.BaseVersion;

        foreach (var variable in configuration.EnvironmentVariables)
            job.EnvironmentVariables[variable.Key] = variable.Value;

        return job;
    }

    ResourceIdentifier ResolveComputeId(string computeTarget) =>
        computeTarget.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            ? new(resourceId: computeTarget)
            : new(resourceId: $"/subscriptions/{options.SubscriptionId}/resourceGroups/{options.ResourceGroupName}/providers/Microsoft.MachineLearningServices/workspaces/{options.WorkspaceName}/computes/{computeTarget}");

    static MachineLearningJobInput CreateInput(TrainingDatasetArtifact dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (!Uri.TryCreate(dataset.Location, UriKind.Absolute, out var location))
        {
            throw new ArgumentException(
                $"Training dataset '{dataset.Name}' location must be an absolute URI.",
                nameof(dataset));
        }

        return dataset.Kind switch
        {
            TrainingDatasetArtifactKind.Folder => new MachineLearningUriFolderJobInput(location),
            _ => new MachineLearningUriFileJobInput(uri: location)
        };
    }

    static MachineLearningJobOutput CreateOutput(string? outputUri)
    {
        var output = new MachineLearningCustomModelJobOutput();
        if (!string.IsNullOrWhiteSpace(outputUri))
            output.Uri = new Uri(outputUri, UriKind.Absolute);

        return output;
    }

    static TrainingResult? TryBuildResult(string jobId, MachineLearningJobProperties job)
    {
        if (job is not MachineLearningCommandJob commandJob)
            return null;

        var artifactUri = commandJob.Outputs.Values
            .Select(TryResolveOutputUri)
            .FirstOrDefault(static uri => uri is not null);
        if (artifactUri is null)
            return null;

        var modelName = job.Properties.TryGetValue("cohesive.outputModelName", out var outputModelName)
            && !string.IsNullOrWhiteSpace(outputModelName)
            ? outputModelName
            : job.DisplayName ?? jobId;

        return new(
            ModelName: modelName,
            Version: jobId,
            ArtifactLocation: artifactUri,
            Metrics: new Dictionary<string, float>(StringComparer.Ordinal)
            );
    }

    static Uri? TryResolveOutputUri(MachineLearningJobOutput output) => output switch
    {
        MachineLearningCustomModelJobOutput customModel when customModel.Uri is not null => customModel.Uri,
        MachineLearningUriFolderJobOutput folder when folder.Uri is not null => folder.Uri,
        MachineLearningUriFileJobOutput file when file.Uri is not null => file.Uri,
        _ => null
    };

    static TrainingJobStatus MapStatus(MachineLearningJobStatus? status)
    {
        if (status is null)
            return TrainingJobStatus.Unknown;

        return status.Value switch
        {
            var s when s == MachineLearningJobStatus.NotStarted
                       || s == MachineLearningJobStatus.Starting
                       || s == MachineLearningJobStatus.Provisioning
                       || s == MachineLearningJobStatus.Preparing
                       || s == MachineLearningJobStatus.Queued => TrainingJobStatus.Pending,
            var s when s == MachineLearningJobStatus.Running
                       || s == MachineLearningJobStatus.Finalizing
                       || s == MachineLearningJobStatus.CancelRequested
                       || s == MachineLearningJobStatus.NotResponding
                       || s == MachineLearningJobStatus.Paused => TrainingJobStatus.Running,
            var s when s == MachineLearningJobStatus.Completed => TrainingJobStatus.Completed,
            var s when s == MachineLearningJobStatus.Failed => TrainingJobStatus.Failed,
            var s when s == MachineLearningJobStatus.Canceled => TrainingJobStatus.Cancelled,
            _ => TrainingJobStatus.Unknown
        };
    }

    static string CreateJobId(string outputModelName)
    {
        var trimmed = outputModelName.ToLettersOrDigitsWithSeparator(separator: '-');
        if (string.IsNullOrWhiteSpace(trimmed))
            trimmed = "model";

        if (trimmed.Length > 32)
            trimmed = trimmed[..32];

        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"train-{trimmed}-{suffix}";
    }

    sealed record AzureMLTrainingConfiguration(
        string Command,
        string EnvironmentId,
        string? CodeId,
        string? CodeInputName,
        string? OutputName,
        string? OutputUri,
        IReadOnlyDictionary<string, string> EnvironmentVariables
        )
    {
        public static AzureMLTrainingConfiguration Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                json = "{}";

            var model = JsonSerializer.Deserialize<AzureMLTrainingConfigurationModel>(json, JsonOptions)
                ?? new();

            if (string.IsNullOrWhiteSpace(model.Command))
                throw new InvalidOperationException("AzureML training configuration must include a non-empty 'command'.");

            if (string.IsNullOrWhiteSpace(model.EnvironmentId))
            {
                throw new InvalidOperationException(
                    "AzureML training configuration must include a non-empty 'environmentId'.");
            }

            return new(
                Command: model.Command,
                EnvironmentId: model.EnvironmentId,
                CodeId: model.CodeId,
                CodeInputName: model.CodeInputName,
                OutputName: string.IsNullOrWhiteSpace(model.OutputName) ? DefaultOutputName : model.OutputName,
                OutputUri: model.OutputUri,
                EnvironmentVariables: model.EnvironmentVariables ?? new Dictionary<string, string>(StringComparer.Ordinal)
                );
        }
    }

    sealed class AzureMLTrainingConfigurationModel
    {
        public string Command { get; init; } = string.Empty;

        public string EnvironmentId { get; init; } = string.Empty;

        public string? CodeId { get; init; }

        public string? CodeInputName { get; init; }

        public string? OutputName { get; init; }

        public string? OutputUri { get; init; }

        public Dictionary<string, string>? EnvironmentVariables { get; init; }
    }
}
