using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.Aspire.Pulumi;

/// <summary>Runtime-only policy for an Aspire-to-Pulumi deployment bridge.</summary>
public sealed record AspirePulumiDeploymentOptions
{
    /// <summary>Creates runtime deployment policy.</summary>
    /// <param name="repositoryRoot">Absolute repository root used to resolve the portable Pulumi program path.</param>
    /// <param name="executor">Pulumi execution boundary, or <see langword="null"/> to use the official Automation API.</param>
    /// <exception cref="ArgumentException"><paramref name="repositoryRoot"/> is empty or not absolute.</exception>
    public AspirePulumiDeploymentOptions(
        string repositoryRoot,
        IAspirePulumiDeploymentExecutor? executor = null)
    {
        repositoryRoot = Guard.RequireNotNullOrWhiteSpace(repositoryRoot);
        if (!Path.IsPathFullyQualified(repositoryRoot))
            throw new ArgumentException("The Aspire-Pulumi repository root must be absolute.", nameof(repositoryRoot));
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        Executor = executor ?? new PulumiAutomationDeploymentExecutor();
    }

    /// <summary>Absolute repository root used to resolve the portable Pulumi program path.</summary>
    public string RepositoryRoot { get; }

    /// <summary>Runtime boundary that delegates reconciliation to Pulumi.</summary>
    public IAspirePulumiDeploymentExecutor Executor { get; }
}

/// <summary>
/// Aspire application-model resource contributing Cohesive handoff, Pulumi apply, and Pulumi destroy pipeline steps.
/// </summary>
public sealed class AspirePulumiDeploymentResource : Resource
{
    /// <summary>Stable filename of the portable handoff emitted by the publish/deploy pipeline.</summary>
    public const string HandoffFileName = "cohesive.infra.pulumi.json";

    /// <summary>Creates a deployment bridge resource.</summary>
    /// <param name="name">Aspire resource name.</param>
    /// <param name="handoff">Exact portable deployment handoff.</param>
    /// <param name="options">Runtime-only repository and executor policy.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white-space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="handoff"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public AspirePulumiDeploymentResource(
        string name,
        AspirePulumiDeploymentHandoff handoff,
        AspirePulumiDeploymentOptions options)
        : base(Guard.RequireNotNullOrWhiteSpace(name))
    {
        Handoff = Guard.RequireNotNull(handoff);
        Options = Guard.RequireNotNull(options);
        HandoffStepName = $"cohesive-pulumi-{Name}-handoff";
        PreviewStepName = $"cohesive-pulumi-{Name}-preview";
        ApplyStepName = $"cohesive-pulumi-{Name}-apply";
        DestroyStepName = $"cohesive-pulumi-{Name}-destroy";
    }

    /// <summary>Exact portable deployment handoff.</summary>
    public AspirePulumiDeploymentHandoff Handoff { get; }

    /// <summary>Runtime-only repository and executor policy.</summary>
    public AspirePulumiDeploymentOptions Options { get; }

    /// <summary>Stable Aspire pipeline step that materializes <see cref="Handoff"/>.</summary>
    public string HandoffStepName { get; }

    /// <summary>Stable Aspire pipeline step that delegates a non-mutating preview to Pulumi.</summary>
    public string PreviewStepName { get; }

    /// <summary>Stable Aspire pipeline step that delegates apply to Pulumi.</summary>
    public string ApplyStepName { get; }

    /// <summary>Stable Aspire pipeline step that delegates destroy to Pulumi.</summary>
    public string DestroyStepName { get; }
}

/// <summary>Adds exact Cohesive-to-Pulumi handoffs to Aspire deployment pipelines.</summary>
public static class AspirePulumiDeploymentBuilderExtensions
{
    /// <summary>
    /// Adds an optional Pulumi deployment bridge without introducing Pulumi into Cohesive.Infra or the base Aspire adapter.
    /// </summary>
    /// <param name="builder">Aspire distributed application builder.</param>
    /// <param name="name">Stable Aspire resource name for this deployment target.</param>
    /// <param name="plan">Complete exact Cohesive target-deployment plan.</param>
    /// <param name="environmentName">Cohesive/Aspire environment profile selected for the deployment.</param>
    /// <param name="pulumiProjectName">Expected project name from the existing Pulumi program.</param>
    /// <param name="pulumiStackName">Pulumi stack name or fully qualified stack identity.</param>
    /// <param name="programDirectory">Repository-relative directory containing the existing Pulumi program.</param>
    /// <param name="options">Runtime-only repository and executor policy.</param>
    /// <returns>
    /// An Aspire resource contributing a portable handoff to <c>publish</c>, Pulumi reconciliation to <c>deploy</c>,
    /// and Pulumi destruction to <c>destroy</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The plan or another handoff input is invalid.</exception>
    public static IResourceBuilder<AspirePulumiDeploymentResource> AddCohesivePulumiDeployment(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        InfrastructureTargetDeploymentPlan plan,
        string environmentName,
        string pulumiProjectName,
        string pulumiStackName,
        RepositoryPath programDirectory,
        AspirePulumiDeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        var handoff = AspirePulumiDeploymentHandoff.Create(
            plan,
            environmentName,
            pulumiProjectName,
            pulumiStackName,
            programDirectory);
        var resource = new AspirePulumiDeploymentResource(name, handoff, options);
        var resourceBuilder = builder.AddResource(resource);

        resourceBuilder.WithPipelineStepFactory(
            stepName: resource.HandoffStepName,
            callback: context => MaterializeHandoffAsync(resource, context),
            dependsOn: [],
            requiredBy:
            [
                WellKnownPipelineSteps.Publish,
                WellKnownPipelineSteps.Deploy,
                WellKnownPipelineSteps.Destroy
            ],
            tags: [],
            description: "Persist the exact Cohesive infrastructure realization for Pulumi and external tooling.");
        resourceBuilder.WithPipelineStepFactory(
            stepName: resource.PreviewStepName,
            callback: context => ExecuteAsync(resource, context, AspirePulumiDeploymentOperation.Preview),
            dependsOn: [resource.HandoffStepName],
            requiredBy: [],
            tags: [],
            description: "Preview Pulumi changes without reconciling physical resources.");
        resourceBuilder.WithPipelineStepFactory(
            stepName: resource.ApplyStepName,
            callback: context => ExecuteAsync(resource, context, AspirePulumiDeploymentOperation.Apply),
            dependsOn: [resource.HandoffStepName],
            requiredBy: [WellKnownPipelineSteps.Deploy],
            tags: [WellKnownPipelineTags.ProvisionInfrastructure],
            description: "Delegate physical reconciliation to the existing Pulumi program.");
        resourceBuilder.WithPipelineStepFactory(
            stepName: resource.DestroyStepName,
            callback: context => ExecuteAsync(resource, context, AspirePulumiDeploymentOperation.Destroy),
            dependsOn: [resource.HandoffStepName],
            requiredBy: [WellKnownPipelineSteps.Destroy],
            tags: [WellKnownPipelineTags.ProvisionInfrastructure],
            description: "Delegate destruction to the Pulumi stack that owns physical state.");

        return resourceBuilder;
    }

    static async Task MaterializeHandoffAsync(
        AspirePulumiDeploymentResource resource,
        PipelineStepContext context)
    {
        var path = ResolveHandoffPath(resource, context);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(
            path,
            resource.Handoff.ToCanonicalBytes(),
            context.CancellationToken).ConfigureAwait(false);
        context.Summary.Add("Cohesive realization", resource.Handoff.Realization.Fingerprint.Value);
        context.Summary.Add("Cohesive handoff", path);
        await context.ReportingStep.SucceedAsync(
            $"Wrote exact Cohesive handoff {resource.Handoff.Fingerprint.Value}.",
            context.CancellationToken).ConfigureAwait(false);
    }

    static async Task ExecuteAsync(
        AspirePulumiDeploymentResource resource,
        PipelineStepContext context,
        AspirePulumiDeploymentOperation operation)
    {
        var request = new AspirePulumiDeploymentRequest(
            resource.Handoff,
            resource.Options.RepositoryRoot,
            ResolveHandoffPath(resource, context),
            operation,
            progress => context.ReportingStep.Log(
                progress.Stream == AspirePulumiOutputStream.StandardError
                    ? LogLevel.Warning
                    : LogLevel.Information,
                progress.Message));
        var result = await resource.Options.Executor.ExecuteAsync(request, context.CancellationToken).ConfigureAwait(false);
        context.Summary.Add("Pulumi stack", resource.Handoff.PulumiStackName);
        context.Summary.Add("Pulumi outcome", result.Outcome);
        await context.ReportingStep.SucceedAsync(
            $"Pulumi {operation.ToString().ToLowerInvariant()} completed with outcome '{result.Outcome}'.",
            context.CancellationToken).ConfigureAwait(false);
    }

    static string ResolveHandoffPath(
        AspirePulumiDeploymentResource resource,
        PipelineStepContext context)
    {
        var output = context.Services.GetRequiredService<IPipelineOutputService>();
        return Path.GetFullPath(Path.Combine(output.GetOutputDirectory(resource), AspirePulumiDeploymentResource.HandoffFileName));
    }
}
