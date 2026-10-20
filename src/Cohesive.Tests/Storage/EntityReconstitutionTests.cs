using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Storage;

namespace Cohesive.Tests.Storage;

public sealed class EntityReconstitutionTests
{
    [Fact]
    public void Replacement_actions_preserve_semantic_versions_independently_of_stream_positions()
    {
        var initial = RunControlFixture.Write(RunControlFixture.Initial()).Entity;
        var first = EntityReconstitution.Apply(RunControlFixture.Entity, null, new(1, initial));
        var noChange = EntityReconstitution.Apply(RunControlFixture.Entity, first, new(2, initial));
        Assert.Equal(2, noChange.Position);
        Assert.Equal(0, noChange.State.Version);
        Assert.Same(initial, noChange.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Initial_action_must_be_position_one(long position)
    {
        var initial = RunControlFixture.Write(RunControlFixture.Initial()).Entity;
        Assert.Throws<InvalidOperationException>(() => EntityReconstitution.Apply(RunControlFixture.Entity, null, new(position, initial)));
    }

    [Fact]
    public void Reordered_actions_and_changed_subjects_are_rejected()
    {
        var initial = RunControlFixture.Write(RunControlFixture.Initial()).Entity;
        var first = new EntityStateAction(1, initial);
        Assert.Throws<InvalidOperationException>(() => EntityReconstitution.Apply(RunControlFixture.Entity, first, first));
        Assert.Throws<InvalidOperationException>(() => EntityReconstitution.Apply(RunControlFixture.Entity, first,
            new(2, RunControlFixture.Write(RunControlFixture.Initial("other")).Entity)));
    }

    [Fact]
    public void Semantic_version_cannot_move_backwards()
    {
        var initial = RunControlFixture.Write(RunControlFixture.Initial(), 3).Entity;
        Assert.Throws<InvalidOperationException>(() => EntityReconstitution.Apply(RunControlFixture.Entity, new(1, initial),
            new(2, RunControlFixture.Write(RunControlFixture.Initial(), 2).Entity)));
    }
}
