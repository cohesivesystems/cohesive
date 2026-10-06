using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Tests.Api;

public sealed class AuthoredTransitionBindingTests
{
    sealed record State(string Status);

    [Fact]
    public void Valid_declaration_is_prepared_at_binding_construction()
    {
        var binding = EntityApiOperationBinding.Transition("Submit", Declare("body"),
            (_, _) => true, (_, _) => Results.Ok());
        Assert.NotNull(binding);
    }

    [Fact]
    public void Invalid_declaration_reports_diagnostics_before_any_request()
    {
        var exception = Assert.Throws<TransitionApiPreparationException>(() =>
            EntityApiOperationBinding.Transition("Submit", Declare("set-status"),
                (_, _) => true, (_, _) => Results.Ok()));
        Assert.Contains("transitions.ir.nodeIdentityDuplicate", exception.Message);
        Assert.Contains("Submit", exception.Message);
        Assert.False(exception.Compilation.IsSuccessful);
        Assert.Contains(exception.Compilation.Validation.Diagnostics, diagnostic => diagnostic.Code == "transitions.ir.nodeIdentityDuplicate");
    }

    static Transition<State, bool, string> Declare(string bodyId) => TransitionAuthoring.Create<State, bool, string>(
        ObjectEntityDefinition.For<State>().Shape,
        new(new("test/submit"), new("1"), new(bodyId),
            new(new(TransitionAuthoring.Producer), new("test/submit"), DocumentOrigin.Generated)),
        transition => transition.Set(new("set-status"), state => state.Status, "Submitted")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Submitted"));
}
