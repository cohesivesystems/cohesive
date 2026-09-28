using System.Collections.Immutable;
using Cohesive.Adapters.Services.Infra;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
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

    [Fact]
    public void SharedPrerequisitesDeriveCoverageAndMatchExplicitPlacement()
    {
        var first = Service();
        Assert.True(ServiceDefinitionDocuments.ValidateAndProject(first, out var definition).IsValid);
        var start = Assert.IsType<ServiceProcessOperation>(Assert.Single(definition!.Operations));
        var service = ServiceDefinitionDocuments.Create(new("notes"), new("2"),
            new([start, new ServiceProcessResultOperation("result", start.Process)]), first.Metadata.Provenance);
        var prerequisites = new List<InfrastructureBindingId> { new("api-scheduler") };
        var association = ServiceInfrastructureAssociation.CreateWithSharedPrerequisites(service, Topology(), new("api"), prerequisites);
        prerequisites.Clear();
        var explicitPlacement = ServiceInfrastructureAssociation.Create(service, Topology(), new("api"),
            new Dictionary<string, ImmutableArray<InfrastructureBindingId>>
            { ["publish"] = [new("api-scheduler")], ["result"] = [new("api-scheduler")] });
        Assert.Equal(explicitPlacement.Service, association.Service);
        Assert.Equal(explicitPlacement.Infrastructure, association.Infrastructure);
        Assert.Equal(2, association.Operations.Count);
        foreach (var operation in explicitPlacement.Operations)
            Assert.Equal(operation.Value.ToArray(), association.Operations[operation.Key].ToArray());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("worker-scheduler")]
    [InlineData("duplicate")]
    public void SharedPrerequisitesPreserveBindingAdmission(string scenario)
    {
        InfrastructureBindingId[] selected = scenario == "duplicate"
            ? [new("api-scheduler"), new("api-scheduler")] : [new(scenario)];
        Assert.Throws<ArgumentException>(() => ServiceInfrastructureAssociation.CreateWithSharedPrerequisites(
            Service(), Topology(), new("api"), selected));
    }

    [Fact]
    public void RejectsUnclosedCapabilitiesAndEvidenceFromAnotherDefinition()
    {
        var topology = Topology();
        var association = ServiceInfrastructureAssociation.Create(Service(), topology, new("api"),
            new Dictionary<string, ImmutableArray<InfrastructureBindingId>> { ["publish"] = [new("api-scheduler")] });
        var profile = new InfrastructureCapabilityProfile(InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new("test"), new("test"), [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [new InfrastructureCapabilityVariant(new("local"))]);
        var closure = InfrastructureCapabilityCompiler.Compile(InfrastructureDefinitionDocument.FromDefinition(topology), profile, new("local"));
        var validation = association.ValidateCapabilityClosure(closure);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == InfrastructureBindingElaborationDiagnosticCodes.ContractUnavailable);
        var other = new InfrastructureDefinition(new("other"), new("1"), workloads: [new(new("api"))]);
        var otherClosure = InfrastructureCapabilityCompiler.Compile(InfrastructureDefinitionDocument.FromDefinition(other), profile, new("local"));
        Assert.True(otherClosure.IsClosed);
        Assert.Equal("services.infra.definitionMismatch", Assert.Single(association.ValidateCapabilityClosure(otherClosure).Diagnostics).Code);
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
