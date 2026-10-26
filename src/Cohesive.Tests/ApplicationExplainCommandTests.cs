using Cohesive.Host.Composition;
using Cohesive.Infra;
using Cohesive.Infra.Realization;

namespace Cohesive.Tests;

public sealed class ApplicationExplainCommandTests
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

    [Fact]
    public void Typed_report_can_be_explained_without_an_orchestrator()
    {
        var report = Report();
        var command = new ApplicationExplainCommand(["--explain"]);
        using var output = new StringWriter();
        Assert.True(command.TryWrite(output, () => report));
        Assert.Equal(report.ToJson() + output.NewLine, output.ToString());
    }

    [Fact]
    public void Normal_host_path_does_not_evaluate_report()
    {
        var command = new ApplicationExplainCommand(["--environment=local"]);
        Assert.Equal(["--environment=local"], command.Arguments);
        Assert.False(command.TryWrite(TextWriter.Null, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void Failed_projection_writes_no_partial_report()
    {
        using var output = new StringWriter();
        var command = new ApplicationExplainCommand(["--explain"]);
        Assert.Throws<ArgumentException>(() => command.TryWrite(output, () => throw new ArgumentException("invalid plan")));
        Assert.Equal("", output.ToString());
    }
}
