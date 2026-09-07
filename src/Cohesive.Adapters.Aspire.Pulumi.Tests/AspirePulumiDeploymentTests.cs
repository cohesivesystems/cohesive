using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Cohesive.Adapters.Aspire.Pulumi;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cohesive.Adapters.Aspire.Pulumi.Tests;

public sealed class AspirePulumiDeploymentTests
{
    static readonly InfrastructureCapabilityId Https = new("test/workload/https");
    static readonly InfrastructureCapabilityId Storage = new("test/resource/storage");
    static readonly InfrastructureTargetFacilityId AppService = new("test/app-service");
    static readonly InfrastructureTargetFacilityId ObjectStore = new("test/object-store");
    static readonly InfrastructureCapabilityVariantId Variant = new("test/production");
    static readonly InfrastructureNodeId Api = new("workloads/api");
    static readonly InfrastructureNodeId State = new("resources/state");
    static readonly InfrastructureNodeId Logs = new("resources/logs");
    static readonly InfrastructurePhysicalResourceId ApiPhysical = new("test/app-service/sites/api");
    static readonly InfrastructurePhysicalResourceId StatePhysical = new("test/object-store/buckets/state");
    static readonly InfrastructurePhysicalResourceId LogsPhysical = new("test/object-store/buckets/logs");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("pulumi/test/production");
    static readonly InfrastructureTargetId Target = new("test/pulumi/1");
    static readonly SourceReference Source = SourceReference.Create("test-adapter", "pulumi-production");

    [Fact]
    public void Complete_plan_materializes_one_deterministic_portable_handoff()
    {
        var plan = CompletePlan();

        var first = CreateHandoff(plan);
        var second = CreateHandoff(plan);
        var json = first.ToJson();
        var restored = Assert.IsType<AspirePulumiDeploymentHandoff>(
            JsonSerializer.Deserialize<AspirePulumiDeploymentHandoff>(
                json,
                StrictDocumentJson.CreateOptions()));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.ToCanonicalBytes(), second.ToCanonicalBytes());
        Assert.Equal(first, restored);
        Assert.Equal(plan.Manifest, restored.Manifest);
        Assert.Equal(plan.Realization, restored.Realization);
        Assert.Equal(Target, restored.Realization.ToReference().Target);
        Assert.Equal(Authority, restored.LifecycleAuthority);
        Assert.DoesNotContain("/private/", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Handoff_rejects_lifecycle_authority_drift()
    {
        var plan = CompletePlan();

        var valid = CreateHandoff(plan);
        var exception = Assert.Throws<ArgumentException>(() => new AspirePulumiDeploymentHandoff(
            valid.SchemaVersion,
            valid.EnvironmentName,
            valid.PulumiProjectName,
            valid.PulumiStackName,
            valid.ProgramDirectory,
            new("pulumi/test/other"),
            valid.Manifest,
            valid.Realization,
            valid.Diagnostics));

        Assert.Equal("lifecycleAuthority", exception.ParamName);
        Assert.Contains("manages resource", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Authority.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Handoff_rejects_more_than_one_managed_lifecycle_authority()
    {
        InfrastructureLifecycleAuthorityId secondAuthority = new("pulumi/test/other-stack");

        var exception = Assert.Throws<ArgumentException>(() => CreateHandoff(CompletePlan(secondAuthority)));

        Assert.Equal("plan", exception.ParamName);
        Assert.Contains(Authority.Value, exception.Message, StringComparison.Ordinal);
        Assert.Contains(secondAuthority.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Handoff_surfaces_actionable_capability_mismatch_diagnostics_from_incomplete_plans()
    {
        InfrastructureCapabilityId unsupported = new("test/workload/unsupported");
        var semantic = Infrastructure.Define(
            new("test/aspire-pulumi-incomplete"),
            new("1"),
            new("test/aspire-pulumi-incomplete/bindings/v1"),
            infrastructure => infrastructure.Workload(Api).Requires(unsupported));
        var facilities = InfrastructureTargetFacilities.Define(
            new("test/aspire-pulumi-incomplete/facilities/v1"),
            new("test/aspire-pulumi-incomplete/capabilities/v1"),
            Target,
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target => target.Workload(AppService).Provides(Native("test/evidence/https", Https)));
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/aspire-pulumi-incomplete/deployment/v1"),
            semantic.Definition,
            facilities,
            deployment => deployment.Workload(Api, AppService, ApiPhysical, [Source]));
        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
        var diagnostic = Assert.Single(
            plan.Diagnostics,
            static item => item.Code == InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable);

        var exception = Assert.Throws<ArgumentException>(() => CreateHandoff(plan));

        Assert.False(plan.IsComplete);
        Assert.Equal("plan", exception.ParamName);
        Assert.Contains(diagnostic.Code, exception.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execution_request_requires_absolute_runtime_paths()
    {
        var handoff = CreateHandoff(CompletePlan());

        Assert.Throws<ArgumentException>(() => new AspirePulumiDeploymentRequest(
            handoff,
            repositoryRoot: "relative/repository",
            handoffPath: Path.GetFullPath("handoff.json"),
            AspirePulumiDeploymentOperation.Apply));
        Assert.Throws<ArgumentException>(() => new AspirePulumiDeploymentRequest(
            handoff,
            repositoryRoot: Path.GetFullPath("repository"),
            handoffPath: "relative/handoff.json",
            AspirePulumiDeploymentOperation.Apply));
    }

    [Fact]
    public void Pulumi_program_loads_the_exact_handoff_from_the_shared_environment_contract()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cohesive-pulumi-handoff-{Guid.NewGuid():N}.json");
        try
        {
            var handoff = CreateHandoff(CompletePlan());
            File.WriteAllBytes(path, handoff.ToCanonicalBytes());
            var environment = new Dictionary<string, string?>
            {
                [CohesivePulumiEnvironmentVariables.HandoffPath] = path,
                [CohesivePulumiEnvironmentVariables.HandoffFingerprint] = handoff.Fingerprint.Value,
                [CohesivePulumiEnvironmentVariables.EnvironmentName] = handoff.EnvironmentName
            };

            var restored = AspirePulumiDeploymentHandoff.TryLoadFromEnvironment(environment);

            Assert.Equal(handoff, restored);
            Assert.Null(AspirePulumiDeploymentHandoff.TryLoadFromEnvironment(
                new Dictionary<string, string?>()));
            Assert.Throws<InvalidOperationException>(() =>
                AspirePulumiDeploymentHandoff.TryLoadFromEnvironment(
                    new Dictionary<string, string?>
                    {
                        [CohesivePulumiEnvironmentVariables.HandoffPath] = path
                    }));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Pulumi_program_rejects_a_recompiled_plan_that_differs_from_the_handoff()
    {
        var handoff = CreateHandoff(CompletePlan());
        var changed = CompletePlan(statePhysical: new("test/object-store/buckets/changed-state"));

        var exception = Assert.Throws<InvalidOperationException>(() => handoff.RequireExactPlan(changed));

        Assert.Contains(handoff.Fingerprint.Value, exception.Message, StringComparison.Ordinal);
        Assert.Contains(handoff.Manifest.Fingerprint.Value, exception.Message, StringComparison.Ordinal);
        Assert.Contains(changed.Manifest.Fingerprint.Value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aspire_pipeline_materializes_handoff_and_delegates_apply_and_destroy_to_executor()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cohesive-aspire-pulumi-{Guid.NewGuid():N}");
        var executor = new RecordingExecutor();
        try
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = [],
                AssemblyName = typeof(AspirePulumiDeploymentTests).Assembly.GetName().Name,
                ProjectDirectory = FindRepositoryRoot(),
                DisableDashboard = true
            });
            var resource = builder.AddCohesivePulumiDeployment(
                name: "production-infrastructure",
                plan: CompletePlan(),
                environmentName: "production",
                pulumiProjectName: "test-infrastructure",
                pulumiStackName: "organization/test-infrastructure/production",
                programDirectory: new("infra/test"),
                options: new(FindRepositoryRoot(), executor));
            using var services = new ServiceCollection()
                .AddSingleton<IPipelineOutputService>(new FixedOutputService(outputDirectory))
                .BuildServiceProvider();
            var pipelineContext = new PipelineContext(
                new DistributedApplicationModel(builder.Resources),
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                services,
                NullLogger.Instance,
                CancellationToken.None);
            var factoryContext = new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = resource.Resource
            };
            var steps = new List<PipelineStep>();
            foreach (var annotation in resource.Resource.Annotations.OfType<PipelineStepAnnotation>())
                steps.AddRange(await annotation.CreateStepsAsync(factoryContext));

            Assert.Equal(4, steps.Count);
            var handoffStep = Assert.Single(steps, step => step.Name == resource.Resource.HandoffStepName);
            Assert.Contains(WellKnownPipelineSteps.Publish, handoffStep.RequiredBySteps);
            Assert.Contains(WellKnownPipelineSteps.Deploy, handoffStep.RequiredBySteps);
            Assert.Contains(WellKnownPipelineSteps.Destroy, handoffStep.RequiredBySteps);
            var previewStep = Assert.Single(steps, step => step.Name == resource.Resource.PreviewStepName);
            Assert.Contains(resource.Resource.HandoffStepName, previewStep.DependsOnSteps);
            Assert.Empty(previewStep.RequiredBySteps);
            var applyStep = Assert.Single(steps, step => step.Name == resource.Resource.ApplyStepName);
            Assert.Contains(resource.Resource.HandoffStepName, applyStep.DependsOnSteps);
            Assert.Contains(WellKnownPipelineSteps.Deploy, applyStep.RequiredBySteps);
            Assert.Contains(WellKnownPipelineTags.ProvisionInfrastructure, applyStep.Tags);
            var destroyStep = Assert.Single(steps, step => step.Name == resource.Resource.DestroyStepName);
            Assert.Contains(resource.Resource.HandoffStepName, destroyStep.DependsOnSteps);
            Assert.Contains(WellKnownPipelineSteps.Destroy, destroyStep.RequiredBySteps);

            var reporting = new RecordingReportingStep();
            var stepContext = new PipelineStepContext
            {
                PipelineContext = pipelineContext,
                ReportingStep = reporting
            };
            await handoffStep.Action(stepContext);
            var handoffPath = Path.Combine(outputDirectory, AspirePulumiDeploymentResource.HandoffFileName);
            Assert.True(File.Exists(handoffPath));
            var restored = Assert.IsType<AspirePulumiDeploymentHandoff>(
                JsonSerializer.Deserialize<AspirePulumiDeploymentHandoff>(
                    await File.ReadAllTextAsync(handoffPath),
                    StrictDocumentJson.CreateOptions()));
            Assert.Equal(resource.Resource.Handoff, restored);

            await previewStep.Action(stepContext);
            await applyStep.Action(stepContext);
            await destroyStep.Action(stepContext);

            Assert.Equal(3, executor.Requests.Count);
            Assert.Equal(AspirePulumiDeploymentOperation.Preview, executor.Requests[0].Operation);
            Assert.Equal(AspirePulumiDeploymentOperation.Apply, executor.Requests[1].Operation);
            Assert.Equal(AspirePulumiDeploymentOperation.Destroy, executor.Requests[2].Operation);
            Assert.All(executor.Requests, request =>
            {
                Assert.Equal(resource.Resource.Handoff, request.Handoff);
                Assert.Equal(handoffPath, request.HandoffPath);
                Assert.Equal(FindRepositoryRoot(), request.RepositoryRoot);
            });
            Assert.Contains(reporting.Messages, static message => message.Contains("pulumi-output", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Automation_executor_rejects_a_handoff_file_that_does_not_match_before_running_pulumi()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"cohesive-pulumi-fence-{Guid.NewGuid():N}");
        var programDirectory = Path.Combine(repositoryRoot, "infra", "test");
        var handoffPath = Path.Combine(repositoryRoot, AspirePulumiDeploymentResource.HandoffFileName);
        try
        {
            Directory.CreateDirectory(programDirectory);
            await File.WriteAllTextAsync(Path.Combine(programDirectory, "Pulumi.yaml"), "name: test-infrastructure\nruntime: dotnet\n");
            await File.WriteAllTextAsync(handoffPath, "{}");
            var handoff = CreateHandoff(CompletePlan());
            var request = new AspirePulumiDeploymentRequest(
                handoff,
                repositoryRoot,
                handoffPath,
                AspirePulumiDeploymentOperation.Apply);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new PulumiAutomationDeploymentExecutor().ExecuteAsync(request, CancellationToken.None));

            Assert.Contains(handoff.Fingerprint.Value, exception.Message, StringComparison.Ordinal);
            Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(repositoryRoot))
                Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    static AspirePulumiDeploymentHandoff CreateHandoff(InfrastructureTargetDeploymentPlan plan) =>
        AspirePulumiDeploymentHandoff.Create(
            plan,
            environmentName: "production",
            pulumiProjectName: "test-infrastructure",
            pulumiStackName: "organization/test-infrastructure/production",
            programDirectory: new("infra/test"));

    static InfrastructureTargetDeploymentPlan CompletePlan(
        InfrastructureLifecycleAuthorityId? logsAuthority = null,
        InfrastructurePhysicalResourceId? statePhysical = null)
    {
        var semantic = Infrastructure.Define(
            new("test/aspire-pulumi"),
            new("1"),
            new("test/aspire-pulumi/bindings/v1"),
            infrastructure =>
            {
                infrastructure.Workload(Api).Requires(Https);
                infrastructure.Resource(State).Persistent().Requires(Storage);
                infrastructure.Resource(Logs).Persistent().Requires(Storage);
            });
        var facilities = InfrastructureTargetFacilities.Define(
            new("test/aspire-pulumi/facilities/v1"),
            new("test/aspire-pulumi/capabilities/v1"),
            Target,
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Workload(AppService).Provides(Native("test/evidence/https", Https));
                target.Resource(ObjectStore).Provides(Native("test/evidence/storage", Storage));
            });
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/aspire-pulumi/deployment/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(Api, AppService, ApiPhysical, [Source]);
                deployment.Resource(State, ObjectStore, statePhysical ?? StatePhysical, Authority, [Source]);
                deployment.Resource(Logs, ObjectStore, LogsPhysical, logsAuthority ?? Authority, [Source]);
            });
        return InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
    }

    static InfrastructureCapabilityEvidence Native(string id, InfrastructureCapabilityId capability) => new(
        new(id),
        capability,
        CapabilityRealizationKind.Native,
        sourceReferences: [Source]);

    static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cohesive.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Cohesive repository root.");
    }

    sealed class RecordingExecutor : IAspirePulumiDeploymentExecutor
    {
        internal List<AspirePulumiDeploymentRequest> Requests { get; } = [];

        public Task<AspirePulumiDeploymentResult> ExecuteAsync(
            AspirePulumiDeploymentRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            request.ReportProgress?.Invoke(new(AspirePulumiOutputStream.StandardOutput, "pulumi-output"));
            return Task.FromResult(new AspirePulumiDeploymentResult(request.Operation, "Succeeded"));
        }
    }

    sealed class FixedOutputService(string outputDirectory) : IPipelineOutputService
    {
        public string GetOutputDirectory() => outputDirectory;

        public string GetOutputDirectory(IResource resource) => outputDirectory;

        public string GetTempDirectory() => outputDirectory;

        public string GetTempDirectory(IResource resource) => outputDirectory;
    }

    sealed class RecordingReportingStep : IReportingStep
    {
        internal List<string> Messages { get; } = [];

        public Task<IReportingTask> CreateTaskAsync(string statusText, CancellationToken cancellationToken) =>
            Task.FromResult<IReportingTask>(new RecordingReportingTask());

        public Task<IReportingTask> CreateTaskAsync(MarkdownString statusText, CancellationToken cancellationToken) =>
            Task.FromResult<IReportingTask>(new RecordingReportingTask());

        public void Log(LogLevel logLevel, string message, bool isErrorMessage) => Messages.Add(message);

        public void Log(LogLevel logLevel, string message) => Messages.Add(message);

        public void Log(LogLevel logLevel, MarkdownString message) => Messages.Add(message.ToString());

        public Task CompleteAsync(string completionMessage, CompletionState completionState, CancellationToken cancellationToken)
        {
            Messages.Add(completionMessage);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(MarkdownString completionMessage, CompletionState completionState, CancellationToken cancellationToken)
        {
            Messages.Add(completionMessage.ToString());
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class RecordingReportingTask : IReportingTask
    {
        public Task UpdateAsync(string statusText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(MarkdownString statusText, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteAsync(string? completionMessage, CompletionState completionState, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAsync(MarkdownString completionMessage, CompletionState completionState, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
