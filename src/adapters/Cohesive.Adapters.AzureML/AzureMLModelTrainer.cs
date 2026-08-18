using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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
    
    /// <summary>
    /// Submits an Azure ML training job under a deterministic provider job identity.
    /// </summary>
    /// <param name="submission">Stable logical submission identity and exact request content.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The Azure ML job accepted for the exact logical submission.</returns>
    /// <exception cref="TrainingJobSubmissionConflictException">
    /// Thrown when the deterministic Azure ML job identity contains different submission evidence.
    /// </exception>
    public async ValueTask<TrainingJobReference> SubmitAsync(
        TrainingJobSubmission submission,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ct.ThrowIfCancellationRequested();

        var request = submission.Request;
        var workspace = GetWorkspaceResource();
        var jobs = workspace.GetMachineLearningJobs();
        var jobId = CreateJobId(submission.SubmissionId);
        var existing = await jobs.GetIfExistsAsync(jobId, ct).ConfigureAwait(false);
        if (existing.HasValue)
            return BindAcceptedJob(submission, existing.Value!.Data.Properties);

        var configuration = AzureMLTrainingConfiguration.Parse(request.ConfigJson);
        var job = CreateJob(jobId, submission, configuration);
        var operation = await jobs
            .CreateOrUpdateAsync(WaitUntil.Started, jobId, new(job), ct)
            .ConfigureAwait(false);

        return BindAcceptedJob(submission, operation.Value.Data.Properties);
    }

    /// <summary>
    /// Reconciles an exact logical submission against deterministic Azure ML job evidence.
    /// </summary>
    /// <param name="submission">Stable logical submission identity and exact request content.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>Accepted, authoritatively absent, or unresolved submission evidence.</returns>
    /// <exception cref="TrainingJobSubmissionConflictException">
    /// Thrown when the deterministic Azure ML job identity contains different submission evidence.
    /// </exception>
    public async ValueTask<TrainingJobSubmissionResolution> ReconcileSubmissionAsync(
        TrainingJobSubmission submission,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ct.ThrowIfCancellationRequested();

        try
        {
            var workspace = GetWorkspaceResource();
            var jobs = workspace.GetMachineLearningJobs();
            var jobId = CreateJobId(submission.SubmissionId);
            var existing = await jobs.GetIfExistsAsync(jobId, ct).ConfigureAwait(false);
            if (!existing.HasValue)
                return new TrainingJobSubmissionResolution.ConfirmedAbsent();

            return new TrainingJobSubmissionResolution.Accepted(
                BindAcceptedJob(submission, existing.Value!.Data.Properties));
        }
        catch (RequestFailedException error)
        {
            ct.ThrowIfCancellationRequested();
            return new TrainingJobSubmissionResolution.Unresolved(
                ErrorType: $"AzureML.{error.ErrorCode ?? "RequestFailed"}",
                ErrorMessage: error.Message,
                IsTransient: IsTransient(error.Status));
        }
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

    MachineLearningCommandJob CreateJob(
        string jobId,
        TrainingJobSubmission submission,
        AzureMLTrainingConfiguration configuration)
    {
        var request = submission.Request;
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
        AzureMLTrainingSubmissionEvidence.WriteTo(job.Properties, submission);
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
            ArtifactLocation: artifactUri.ToString(),
            Metrics: Array.Empty<TrainingMetric>()
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

    internal static string CreateJobId(string submissionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);

        var prefix = new StringBuilder(capacity: Math.Min(submissionId.Length, 32));
        var pendingSeparator = false;
        foreach (var character in submissionId)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingSeparator && prefix.Length > 0)
                {
                    if (prefix.Length >= 31)
                        break;
                    prefix.Append('-');
                }
                if (prefix.Length >= 32)
                    break;
                prefix.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        if (prefix.Length is 0)
            prefix.Append("submission");

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(submissionId));
        return $"train-{prefix}-{Convert.ToHexStringLower(digest)}";
    }

    static TrainingJobReference BindAcceptedJob(
        TrainingJobSubmission submission,
        MachineLearningJobProperties job)
    {
        AzureMLTrainingSubmissionEvidence.EnsureMatches(job.Properties, submission);

        var jobId = CreateJobId(submission.SubmissionId);
        return new(jobId, MapStatus(job.Status));
    }

    static bool IsTransient(int status) => status is 408 or 409 or 429 or >= 500;

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
