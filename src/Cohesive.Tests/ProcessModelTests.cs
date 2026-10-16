using Cohesive.Processes.Authoring;

namespace Cohesive.Tests;

public sealed class ProcessModelTests
{
    [Fact]
    public void Each_instance_materializes_its_own_declaration_values()
    {
        var first = new InstanceConstantProcess("first");
        var second = new InstanceConstantProcess("second");
        Assert.True(first.Definition.IsValid);
        Assert.True(second.Definition.IsValid);
        Assert.NotEqual(first.Definition.Reference, second.Definition.Reference);
        Assert.Same(first.Definition, first.Definition);
    }
}

[Process(id: "test/instance-constant", revision: "1")]
public sealed partial class InstanceConstantProcess : ProcessModel<string, string>
{
    readonly string value;
    public InstanceConstantProcess(string value) => this.value = value;
    protected override async ProcessTask<string> Execute(ProcessContext process, string input)
    {
        return process.Constant(value);
    }
}
