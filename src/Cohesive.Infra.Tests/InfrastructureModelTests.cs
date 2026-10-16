using Cohesive.Prelude;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureModelTests
{
    sealed class Model : InfrastructureModel
    {
        public int Preparations { get; private set; }
        public Model() => Define(new("test/model"), new("1"), new("test/bindings"), graph =>
        {
            Preparations++;
            graph.Resource(new("store")).Persistent();
        });
        public void Redefine() => Define(new("other"), new("1"), new("other/bindings"), _ => { });
    }

    [Fact]
    public void Instance_authoring_matches_direct_builder_and_prepares_once()
    {
        var model = new Model();
        Assert.Equal(0, model.Preparations);
        ((IAuthoringModel)model).Complete();
        var direct = Infrastructure.Define(new("test/model"), new("1"), new("test/bindings"),
            graph => graph.Resource(new("store")).Persistent());
        Assert.Equal(direct.Definition.Fingerprint, model.Definition.Definition.Fingerprint);
        Assert.Same(model.Definition, model.Definition);
        Assert.Equal(1, model.Preparations);
        Assert.Throws<InvalidOperationException>(model.Redefine);
    }
}
