using Cohesive.Application;
using Cohesive.Infra;
using Cohesive.Infra.Realization;

namespace Cohesive.Tests;

internal static class ApplicationExplanationFixture
{
    public sealed class EmptyApplication : ApplicationModel { }

    internal static ApplicationExplanationDocument Report()
    {
        var definition = Infrastructure.Define(new("test/application"), new("1"), new("test/bindings"),
            model => model.Workload(new("worker")).Requires(new("execution")));
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities"), new("test/capabilities"),
            new("test/target"), new("test/variant"), [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target => target.Workload(new("process")).Provides(new(new("test/execution"), new("execution"),
                CapabilityRealizationKind.Native, sourceReferences: [new("test://evidence")])));
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment"), definition.Definition, facilities,
            target => target.Workload(new("worker"), new("process"), new("native/worker"), [new("test://worker")]));
        var deployment = InfrastructureTargetDeploymentCompiler.Compile(definition, manifest);
        return ApplicationExplanation.Create(ModelActivator.Create<EmptyApplication>(), definition, deployment, "test", new("test://deployment"));
    }

}
