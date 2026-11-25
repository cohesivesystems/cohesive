using Cohesive.Machines;
using Cohesive.Model.Authoring;
using Cohesive.Transitions.Assurance;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Machines;

namespace Cohesive.Assurance.Tests;

public sealed class TransitionCaseSelectionTests
{
    public sealed record Gauge(string Id, int Mode, bool Locked, int Limit);
    public sealed record Reading(int Level);

    static TransitionProbe<Gauge, Reading, bool> Calibrate()
    {
        var machine = Machine.Define("calibration", "1");
        var ready = machine.State("ready").Initial();
        var calibrated = machine.State("calibrated").Final();
        var finish = machine.Edge("finish").From(ready).To(calibrated)
            .When((Gauge gauge, Reading reading) => reading.Level == gauge.Limit);
        var entity = new MachineDiscriminatorBinding<Gauge, int>(machine.Build(), g => g.Mode)
            .Map(ready, 0).Map(calibrated, 1).Bind(ObjectEntityDefinition.For<Gauge>());
        var transition = TransitionAuthoring.Create<Gauge, Reading, bool>(entity.Shape, new("calibrate"), new("1"),
            t => t.Requires(new("unlocked"), (g, r) => !g.Locked, (_, _) => false)
                .Requires(new("minimum"), (g, r) => r.Level > 3, (_, _) => false)
                .Move(finish, (_, _) => false).Return(true));
        return TransitionAssurance.Prepare(transition);
    }

    static CaseSelectionOptions<Gauge, Reading> Options(int cases, int candidates) =>
        new CaseSelectionOptions<Gauge, Reading> { MaxCases = cases, MaxCandidates = candidates }
            .StateRange(g => g.Limit, 3, 6).InputRange(r => r.Level, 2, 7);

    static readonly Gauge Template = new("preserve", 0, false, 5);

    [Fact]
    public void Selects_the_intersection_on_the_actual_edge_and_explains_it()
    {
        var suite = Calibrate().SelectCases(Template, new(4), Options(8, 512));
        var atIntersection = Assert.Single(suite.Cases.Where(c => c.State.Limit == 4 && c.Input.Level == 4
            && c.State.Mode == 0 && !c.State.Locked));
        Assert.Contains(atIntersection.CoveredGoals, goal => goal.First.Node == "minimum"
            && goal.Second is { Kind: "edge-boundary", Node: "finish" });
        Assert.All(suite.Cases, c =>
        {
            Assert.Equal("preserve", c.State.Id);
            Assert.InRange(c.Input.Level, 2, 7);
            if (c.State.Locked || c.State.Mode != 0)
                Assert.DoesNotContain(c.CoveredGoals, goal => goal.First.Kind == "edge" || goal.Second?.Kind == "edge-boundary");
        });
    }

    [Fact]
    public void Selection_is_repeatable_and_reports_the_observed_goals_left_out()
    {
        var probe = Calibrate();
        var tiny = probe.SelectCases(Template, new(4), Options(1, 512));
        var again = probe.SelectCases(Template, new(4), Options(1, 512));
        var first = Assert.Single(tiny.Cases);
        Assert.Equal((first.State, first.Input), (again.Cases[0].State, again.Cases[0].Input));
        Assert.Equal(tiny.UncoveredGoals.ToArray(), again.UncoveredGoals.ToArray());
        Assert.NotEmpty(tiny.UncoveredGoals);
        Assert.Empty(first.CoveredGoals.Intersect(tiny.UncoveredGoals));
        Assert.True(tiny.CandidateCount > tiny.Cases.Length);
        Assert.Throws<InvalidOperationException>(() => probe.SelectCases(Template, new(4), Options(1, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.SelectCases(Template, new(8), Options(2, 512)));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.SelectCases(Template, new(4), Options(0, 512)));
    }
}
