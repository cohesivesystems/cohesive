using System.Collections.Immutable;
using Cohesive.Adapters.Services.Infra;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Tests;

public sealed class ServiceInfrastructureAssociationTests
{
    static ExecutionDefinitionDocument Service() => ServiceDefinitionDocuments.Create(new("notes"), new("1"),
        new([new ServiceProcessOperation("publish", new(new("publish"), new("1"), new("sha256", "test/v1", new string('a', 64))))]),
        new(new("tests"), new("tests/placement"), DocumentOrigin.Generated));
    static InfrastructureDefinition Topology() => new(new("platform"), new("1"),
        workloads: [new(new("api")), new(new("worker"))],
        resources: [new(new("scheduler"), InfrastructureResourceLifecycle.Persistent)],
        bindings: [new(new("api-scheduler"), new("api"), new("scheduler"), new("process-client")),
            new(new("worker-scheduler"), new("worker"), new("scheduler"), new("process-worker"))]);

    [Fact]
    public void RetainsExactAuthoritiesAndCopiesOperationSelection()
    {
        var service = Service(); var infrastructure = Topology();
        var selections = new Dictionary<string, ImmutableArray<InfrastructureBindingId>> { ["publish"] = [new("api-scheduler")] };
        var association = ServiceInfrastructureAssociation.Create(service, infrastructure, new("api"), selections);
        selections.Clear();
        Assert.Equal(service.Metadata.Fingerprint, association.Service.Fingerprint);
        Assert.Equal(InfrastructureDefinitionDocument.FromDefinition(infrastructure).ToReference(), association.Infrastructure);
        Assert.Equal(new InfrastructureBindingId("api-scheduler"), Assert.Single(association.Operations["publish"]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("worker-scheduler")]
    public void RejectsMissingOrOtherWorkloadBinding(string binding) => Assert.Throws<ArgumentException>(() =>
        ServiceInfrastructureAssociation.Create(Service(), Topology(), new("api"),
            new Dictionary<string, ImmutableArray<InfrastructureBindingId>> { ["publish"] = [new(binding)] }));

    [Fact]
    public void RejectsIncompleteCoverageAndUnknownWorkload()
    {
        Assert.Throws<ArgumentException>(() => ServiceInfrastructureAssociation.Create(Service(), Topology(), new("api"),
            new Dictionary<string, ImmutableArray<InfrastructureBindingId>>()));
        Assert.Throws<ArgumentException>(() => ServiceInfrastructureAssociation.Create(Service(), Topology(), new("absent"),
            new Dictionary<string, ImmutableArray<InfrastructureBindingId>> { ["publish"] = [] }));
    }
}
