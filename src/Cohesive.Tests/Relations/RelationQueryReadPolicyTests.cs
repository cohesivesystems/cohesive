using Cohesive.Relations.Execution;

namespace Cohesive.Tests.Relations;

public sealed class RelationQueryReadPolicyTests
{
    [Theory]
    [InlineData(RelationQueryConsistency.SingleSnapshot)]
    [InlineData(RelationQueryConsistency.IndependentSources)]
    [InlineData(RelationQueryConsistency.Eventual)]
    public void Live_preserves_the_callers_explicit_requirement(RelationQueryConsistency requirement) =>
        Assert.Equal(requirement, new RelationQueryReadPolicy().ConsistencyFor(requirement));

    [Fact]
    public void Materialized_requires_explicit_eventual_consent()
    {
        var policy = new RelationQueryReadPolicy { Source = RelationQueryReadSource.Materialized };
        Assert.Throws<ArgumentException>(policy.Validate);
        Assert.Throws<ArgumentException>(() => policy.ConsistencyFor(RelationQueryConsistency.SingleSnapshot));
        Assert.Equal(RelationQueryConsistency.Eventual,
            (policy with { AcceptEventualConsistency = true }).ConsistencyFor(RelationQueryConsistency.SingleSnapshot));
    }

    [Fact]
    public void Unknown_source_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new RelationQueryReadPolicy { Source = (RelationQueryReadSource)99 }.Validate());

    [Fact]
    public void Unknown_live_requirement_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new RelationQueryReadPolicy().ConsistencyFor((RelationQueryConsistency)99));
}
