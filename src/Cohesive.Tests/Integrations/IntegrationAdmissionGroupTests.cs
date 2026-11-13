using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;

namespace Cohesive.Tests.Integrations;

public sealed partial class IntegrationModelTests
{
    static readonly IngestionLedgerAddress GroupForward = new("forward", "source", "store", "SPY");
    static readonly IngestionLedgerAddress GroupBackfill = GroupForward with { Flow = "backfill" };
    static InMemoryIntegrationAdmissionGroup Group() => new("tenant/source/SPY",
        (_, actor) => actor == LifecycleActor, (_, _, actor) => actor == LifecycleActor);
    static IntegrationGroupRegistration Register(IngestionLedgerAddress address, long revision) => new(address, revision, LifecycleActor, AdmissionTime);
    static IntegrationAdmissionBinding BindGroupMember(InMemoryIntegrationLifecycle member, IngestionLedgerAddress address) =>
        member.Bind(Connected(Profile()).ForConsumer(address).Compile(new ConnectionBindings().Bind(MarketData, Connection())));

    [Fact]
    public void GroupPauseOverridesLocalIntentAndResumePreservesLocalPause()
    {
        var group = Group();
        var forward = group.Register(Register(GroupForward, 0)).Receipt.Controller;
        var backfill = group.Register(Register(GroupBackfill, 1)).Receipt.Controller;
        var f = BindGroupMember(forward, GroupForward);
        var b = BindGroupMember(backfill, GroupBackfill);
        forward.Change(Change("active", 0, IntegrationDesiredState.Active));
        backfill.Change(Change("active", 0, IntegrationDesiredState.Active));
        var request = Admission(f) with { ExpectedGroupRevision = 2 };
        Assert.Equal(IntegrationAdmissionOutcome.GroupAdmissionStopped, forward.Admit(request).Outcome);
        Assert.Equal(IntegrationAdmissionOutcome.GroupRevisionConflict, forward.Admit(request with { ExpectedGroupRevision = null }).Outcome);
        group.Change(Change("active", 2, IntegrationDesiredState.Active));
        request = request with { ExpectedGroupRevision = 3 };
        var admitted = forward.Admit(request).Receipt;
        Assert.Equal(group.Inspect().Id, admitted.GroupId);
        Assert.Equal(3, admitted.GroupRevision);
        group.Change(Change("pause", 3, IntegrationDesiredState.Paused));
        Assert.True(forward.Inspect().Draining);
        Assert.Equal(IntegrationDesiredState.Active, forward.Inspect().Desired);
        Assert.Equal(IntegrationDesiredState.Paused, forward.Inspect().Group!.Desired);
        Assert.Same(admitted, forward.Admit(request).Receipt);
        Assert.Equal(IntegrationAdmissionOutcome.IdentityConflict, forward.Admit(request with { ExpectedGroupRevision = 4 }).Outcome);
        // A local active command cannot override group policy.
        backfill.Change(Change("local-resume", 1, IntegrationDesiredState.Active));
        Assert.Equal(IntegrationAdmissionOutcome.GroupAdmissionStopped,
            backfill.Admit(Admission(b, revision: 2) with { ExpectedGroupRevision = 4 }).Outcome);
        backfill.Change(Change("local-pause", 2, IntegrationDesiredState.Paused));
        group.Change(Change("resume", 4, IntegrationDesiredState.Active));
        Assert.True(forward.Inspect().Accepting);
        Assert.False(backfill.Inspect().Accepting);
        Assert.Equal(IntegrationAdmissionOutcome.AdmissionStopped,
            backfill.Admit(Admission(b, revision: 3) with { ExpectedGroupRevision = 5 }).Outcome);
    }

    [Fact]
    public void RegistrationIsAuthorizedFencedAndCannotReplaceMemberControllers()
    {
        var group = Group();
        var request = Register(GroupForward, 0);
        var denied = request with { Authorization = new("other", LifecycleActor.AuthorityScope, "denied") };
        Assert.Equal(IntegrationChangeOutcome.Unauthorized, group.Register(denied).Outcome);
        Assert.Equal(0, group.Inspect().MemberCount);
        var original = group.Register(request).Receipt;
        Assert.Equal(IntegrationChangeOutcome.Replayed, group.Register(request).Outcome);
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var reopened = JsonSerializer.Deserialize<IntegrationGroupRegistration>(JsonSerializer.Serialize(request, options), options)!;
        Assert.Same(original, group.Register(reopened).Receipt);
        Assert.Equal(IntegrationChangeOutcome.IdentityConflict, group.Register(request with { ExpectedRevision = 1 }).Outcome);
        Assert.Equal(IntegrationChangeOutcome.RevisionConflict, group.Register(Register(GroupBackfill, 0)).Outcome);
        Assert.Equal(IntegrationChangeOutcome.Unauthorized, group.Change(Change("denied", 1, IntegrationDesiredState.Active) with
            { Authorization = denied.Authorization }).Outcome);
        Assert.Equal(IntegrationChangeOutcome.RevisionConflict, group.Change(Change("stale", 0, IntegrationDesiredState.Active)).Outcome);
        var active = Change("active", 1, IntegrationDesiredState.Active);
        var receipt = group.Change(active).Receipt;
        group.Change(Change("pause", 2, IntegrationDesiredState.Paused));
        Assert.Same(receipt, group.Change(active).Receipt);
        Assert.Equal(IntegrationDesiredState.Paused, group.Inspect().Desired);
        Assert.Equal(IntegrationChangeOutcome.IdentityConflict, group.Change(active with { Desired = IntegrationDesiredState.Paused }).Outcome);
    }

    [Fact]
    public void NewMemberAndBindingCannotBypassAnExistingGroupPause()
    {
        var group = Group();
        var forward = group.Register(Register(GroupForward, 0)).Receipt.Controller;
        forward.Change(Change("active", 0, IntegrationDesiredState.Active));
        var original = Admission(BindGroupMember(forward, GroupForward)) with { ExpectedGroupRevision = 1 };
        var backfill = group.Register(Register(GroupBackfill, 1)).Receipt.Controller;
        backfill.Change(Change("active", 0, IntegrationDesiredState.Active));
        Assert.Equal(IntegrationAdmissionOutcome.GroupRevisionConflict, forward.Admit(original).Outcome);
        Assert.Equal(IntegrationAdmissionOutcome.GroupAdmissionStopped,
            backfill.Admit(Admission(BindGroupMember(backfill, GroupBackfill)) with { ExpectedGroupRevision = 2 }).Outcome);
        Assert.Equal(IntegrationAdmissionOutcome.BindingMismatch, forward.Admit(Admission(BindGroupMember(backfill, GroupBackfill)) with
            { ExpectedGroupRevision = 2 }).Outcome);
    }

    [Fact]
    public async Task GroupPauseAndTwoMemberAdmissionsHaveOneAtomicOrder()
    {
        var group = Group();
        var forward = group.Register(Register(GroupForward, 0)).Receipt.Controller;
        var backfill = group.Register(Register(GroupBackfill, 1)).Receipt.Controller;
        forward.Change(Change("active", 0, IntegrationDesiredState.Active));
        backfill.Change(Change("active", 0, IntegrationDesiredState.Active));
        group.Change(Change("active", 2, IntegrationDesiredState.Active));
        var f = Admission(BindGroupMember(forward, GroupForward)) with { ExpectedGroupRevision = 3 };
        var b = Admission(BindGroupMember(backfill, GroupBackfill)) with { ExpectedGroupRevision = 3 };
        using var barrier = new Barrier(3);
        var first = Task.Run(() => { barrier.SignalAndWait(); return forward.Admit(f); });
        var second = Task.Run(() => { barrier.SignalAndWait(); return backfill.Admit(b); });
        var pause = Task.Run(() => { barrier.SignalAndWait(); return group.Change(Change("pause", 3, IntegrationDesiredState.Paused)); });
        await Task.WhenAll(first, second, pause);
        Assert.True(pause.Result.IsAccepted);
        Assert.Equal(first.Result.IsAccepted ? 1 : 0, forward.Inspect().Pending.Length);
        Assert.Equal(second.Result.IsAccepted ? 1 : 0, backfill.Inspect().Pending.Length);
        foreach (var result in new[] { first.Result, second.Result })
            Assert.Contains(result.Outcome, new[] { IntegrationAdmissionOutcome.Applied, IntegrationAdmissionOutcome.GroupRevisionConflict });
        Assert.False(forward.Inspect().Accepting);
        Assert.False(backfill.Inspect().Accepting);
        Assert.Equal(IntegrationAdmissionOutcome.GroupAdmissionStopped, forward.Admit(f with
            { Continuation = new(new("after"), new("attempt/1")), ExpectedGroupRevision = 4 }).Outcome);
    }

    [Fact]
    public void GroupPolicyAndMemberPolicyAreBothRequiredForRegistration()
    {
        var group = new InMemoryIntegrationAdmissionGroup("restricted", (_, _) => true,
            (consumer, _, _) => consumer == GroupForward);
        Assert.Equal(IntegrationChangeOutcome.Unauthorized, group.Register(Register(GroupBackfill, 0)).Outcome);
        Assert.True(group.Register(Register(GroupForward, 0)).IsAccepted);
        Assert.Equal(1, group.Inspect().MemberCount);
    }
    [Fact]
    public async Task MembershipChangeAndPauseShareTheGroupRevisionFence()
    {
        var group = Group();
        group.Register(Register(GroupForward, 0));
        group.Change(Change("active", 1, IntegrationDesiredState.Active));
        using var barrier = new Barrier(2);
        var joining = Task.Run(() => { barrier.SignalAndWait(); return group.Register(Register(GroupBackfill, 2)); });
        var pausing = Task.Run(() => { barrier.SignalAndWait(); return group.Change(Change("pause", 2, IntegrationDesiredState.Paused)); });
        await Task.WhenAll(joining, pausing);
        Assert.NotEqual(joining.Result.IsAccepted, pausing.Result.IsAccepted);
        Assert.Equal(3, group.Inspect().Revision);
        if (joining.Result.IsAccepted)
            Assert.True(group.Change(Change("pause-after-join", 3, IntegrationDesiredState.Paused)).IsAccepted);
        var member = joining.Result.IsAccepted ? joining.Result.Receipt.Controller
            : group.Register(Register(GroupBackfill, 3)).Receipt.Controller;
        member.Change(Change("active", 0, IntegrationDesiredState.Active));
        Assert.Equal(IntegrationAdmissionOutcome.GroupAdmissionStopped, member.Admit(Admission(BindGroupMember(member, GroupBackfill)) with
            { ExpectedGroupRevision = group.Inspect().Revision }).Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void GroupAdmissionReplayDoesNotEnumerateMembersOrAllocateSnapshots(int otherMembers)
    {
        var group = Group();
        var member = group.Register(Register(GroupForward, 0)).Receipt.Controller;
        for (var i = 0; i < otherMembers; i++)
            group.Register(Register(GroupForward with { Flow = "other/" + i }, i + 1));
        group.Change(Change("active", otherMembers + 1, IntegrationDesiredState.Active));
        member.Change(Change("active", 0, IntegrationDesiredState.Active));
        var request = Admission(BindGroupMember(member, GroupForward)) with { ExpectedGroupRevision = otherMembers + 2 };
        var original = member.Admit(request).Receipt;
        for (var i = 0; i < 1024; i++) member.Admit(request);
        var before = GC.GetAllocatedBytesForCurrentThread();
        const int count = 4096;
        var wrong = 0;
        for (var i = 0; i < count; i++)
        {
            var result = member.Admit(request);
            if (result.Outcome != IntegrationAdmissionOutcome.Replayed || !ReferenceEquals(result.Receipt, original)) wrong++;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, wrong);
        Assert.True(allocated <= count * 256L, $"Grouped replay allocated {allocated / count} bytes/call.");
    }

}
