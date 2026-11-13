using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Model.Authoring;
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
        var api = Cohesive.Api.Api.Define();
        var transition = Declare("body");
        var endpoint = Bind(api, transition);
        Assert.Same(endpoint, Assert.Single(api.Build().Endpoints));
        Assert.Equal(typeof(bool), endpoint.Operation.Http!.Body!.BodyType);
        Assert.Equal(transition.Reference, endpoint.Operation.TransitionReference);
    }

    [Fact]
    public void Invalid_declaration_reports_diagnostics_before_any_request()
    {
        var api = Cohesive.Api.Api.Define();
        var exception = Assert.Throws<TransitionApiPreparationException>(() => Bind(api, Declare("set-status")));
        Assert.Empty(api.Build().Endpoints);
        Assert.Contains("transitions.ir.nodeIdentityDuplicate", exception.Message);
        Assert.Contains("Submit", exception.Message);
        Assert.False(exception.Compilation.IsSuccessful);
        Assert.Contains(exception.Compilation.Validation.Diagnostics, diagnostic => diagnostic.Code == "transitions.ir.nodeIdentityDuplicate");
    }

    [Fact]
    public void Qualified_request_contract_requires_the_supplied_shape_graph()
    {
        var entity = ObjectEntityDefinition.For<State>();
        TypeId inputType = new("named-submit-input");
        var graph = new ShapeGraph(new("submit-graph"), [entity.Shape], namedTypes:
        [
            new TypeDefinition.Structural(inputType,
                [new(new(nameof(SubmitInput.Confirmed)), new ScalarTypeRef(ScalarTypeKind.Bool))])
        ]);
        var transition = TransitionAuthoring.Create<State, SubmitInput, string>(
            entity.Shape,
            new(new("test/qualified-submit"), new("1"), new("body"),
                new(new(TransitionAuthoring.Producer), new("test/qualified-submit"), DocumentOrigin.Generated)),
            builder => builder.Set(new("set-status"), state => state.Status, "Submitted")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Submitted"),
            typeRefMapper: new DefaultClrTypeRefMapper(new Dictionary<Type, TypeRef>
            {
                [typeof(SubmitInput)] = new NamedTypeRef(inputType)
            }));
        var api = Cohesive.Api.Api.Define();
        var command = api.Entity<State>().Command("Submit").Route("POST", "/state/{id}/submit");
        var options = new EntityApiEndpointOptions { Entity = entity };

        Assert.Throws<TransitionApiPreparationException>(() => Bind(policy: null));
        Assert.Empty(api.Build().Endpoints);

        var endpoint = Bind(new() { ShapeGraph = graph });
        Assert.Same(endpoint, Assert.Single(api.Build().Endpoints));
        Assert.Equal(transition.Reference, endpoint.Operation.TransitionReference);
        Assert.Equal(typeof(SubmitInput), endpoint.Operation.Http!.Body!.BodyType);

        ApiEndpoint Bind(EntityTransitionCommandPolicy<SubmitInput>? policy) =>
            command.BindTransition(options, transition,
                static (EntityApiRequestContext _, SubmitInput request) => request,
                static (_, _) => Results.Ok(), policy);
    }

    sealed record SubmitInput(bool Confirmed);

    static Cohesive.Api.ApiEndpoint Bind(Cohesive.Api.ApiBuilder api, Transition<State, bool, string> transition) =>
        api.Entity<State>().Command("Submit").Route("POST", "/state/{id}/submit").RouteParameter<string>("id")
            .BindTransition(new() { Entity = ObjectEntityDefinition.For<State>() }, transition,
                static (EntityApiRequestContext _, bool request) => request, static (_, _) => Results.Ok());

    static Transition<State, bool, string> Declare(string bodyId) => TransitionAuthoring.Create<State, bool, string>(
        ObjectEntityDefinition.For<State>().Shape,
        new(new("test/submit"), new("1"), new(bodyId),
            new(new(TransitionAuthoring.Producer), new("test/submit"), DocumentOrigin.Generated)),
        transition => transition.Set(new("set-status"), state => state.Status, "Submitted")
            .Return(new("result"), TransitionOutcomeDisposition.Applied, "Submitted"));
}
