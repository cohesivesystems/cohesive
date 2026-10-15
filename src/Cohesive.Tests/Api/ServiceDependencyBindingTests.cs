using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Storage;

namespace Cohesive.Tests.Api;

public sealed class ServiceDependencyBindingTests
{
    static Process<StartRun, string> Process => ProcessAuthoring.Project<StartRun, string>(RunControlFixture.ProcessDocument);
    static ServiceDependencies Dependencies => new ServiceDependencies().Transition(RunControlFixture.Start);
    static ServiceBuilder Declaration(ServiceDependencies? dependencies = null) => Service.Define(
        new("test/service"), new("1"), RunControlFixture.Provenance)
        .Use(dependencies ?? Dependencies).Require(new("run"))
        .Operation("run").Run(Process).ExecuteEphemerally(TimeSpan.FromSeconds(3));
    static IServiceInvocationAuthorization Authorization => new IdentityServiceInvocationAuthorization("tenant", new(nameof(RunControl.Tenant)));
    static IEntityRepository Repository() => new InMemoryEntityOutboxRepository(RunControlFixture.Entity,
        EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));

    [Fact]
    public void Inline_and_separate_service_documents_share_binding_and_preserve_authority()
    {
        var service = Declaration();
        var document = service.Build();
        var repository = Repository();
        var inline = service.Bind(Process, _ => repository, RunControlFixture.Contracts()).Build("test", Authorization);
        var separate = Service.Bind(document, Process, service.Dependencies, _ => repository, RunControlFixture.Contracts()).Build("test", Authorization);
        Assert.Equal(document.Metadata.Fingerprint, inline.Declaration.Metadata.Fingerprint);
        Assert.Same(document, separate.Declaration);
        Assert.Equal(inline.Declaration.Metadata.Fingerprint, separate.Declaration.Metadata.Fingerprint);
        Assert.Equal("run", separate.OperationId);
    }

    [Fact]
    public void Binding_cannot_override_separately_declared_timeout()
    {
        var binding = Declaration().Bind(Process, _ => Repository(), RunControlFixture.Contracts());
        var error = Assert.Throws<ServiceBindingValidationException>(() => binding.Build("test", TimeSpan.FromSeconds(20), Authorization));
        Assert.Equal("services.binding.executionMismatch", Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Missing_exact_transition_fails_before_repository_resolution()
    {
        var resolutions = 0;
        var error = Assert.Throws<ServiceBindingValidationException>(() => Declaration(new()).Bind(Process,
            _ => { resolutions++; return Repository(); }));
        Assert.Equal("services.binding.transitionMissing", Assert.Single(error.Validation.Diagnostics).Code);
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public void Missing_and_ambiguous_repository_authorities_are_setup_errors()
    {
        var missing = Assert.Throws<ServiceBindingValidationException>(() => Declaration().Bind(Process, Array.Empty<IEntityRepository>()));
        Assert.Equal("services.binding.repositoryMissing", Assert.Single(missing.Validation.Diagnostics).Code);
        var repository = Repository();
        var ambiguous = Assert.Throws<ServiceBindingValidationException>(() => Declaration().Bind(Process, repository, repository));
        Assert.Equal("services.binding.repositoryAmbiguous", Assert.Single(ambiguous.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Unused_registered_transition_does_not_prepare_repository_dependencies()
    {
        var process = ProcessAuthoring.Create<string, string>(new(new("echo"), new("1"), new("return"),
            ProcessRecoveryPolicy.ContinueAttempt, RunControlFixture.Provenance), builder => builder.Return(new("return"), builder.Input.Value));
        var service = Service.Define(new("echo-service"), new("1"), RunControlFixture.Provenance).Use(Dependencies)
            .Operation("echo").Run(process).ExecuteEphemerally(TimeSpan.FromSeconds(1));
        var hosted = service.Bind(process, _ => throw new InvalidOperationException("Unused dependency resolved"))
            .Build("test", Authorization);
        Assert.Equal("echo", hosted.OperationId);
    }

    [Fact]
    public void Immutable_authoring_branches_preserve_their_own_dependency_sets()
    {
        var original = Service.Define(new("service"), new("1"), RunControlFixture.Provenance);
        var selected = original.Use(Dependencies);
        Assert.NotSame(original.Dependencies, selected.Dependencies);
        Assert.Same(selected.Dependencies, selected.Require(new("run")).Dependencies);
        Assert.Same(selected.Dependencies, selected.Operation("run").Run(Process).ExecuteEphemerally(TimeSpan.FromSeconds(1)).Dependencies);
    }
}
