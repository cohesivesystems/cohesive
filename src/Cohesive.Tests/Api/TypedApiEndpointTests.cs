using Cohesive.Api;

namespace Cohesive.Tests.Api;

public sealed class TypedApiEndpointTests
{
    public sealed record Detail(string Id);

    [Fact]
    public void Typed_handle_retains_exact_operation_and_alternative_results()
    {
        var root = Cohesive.Api.Api.Define();
        var declaration = root.Query("Details").Route("GET", "/details/{id}")
            .RouteParameter<string>("id").Result(ApiResultKind.NotFound);
        ApiEndpoint<Detail> typed = declaration.Build<Detail>();
        Assert.Same(declaration.Build().Operation, typed.Operation);
        Assert.Same(typed.Operation, declaration.Build<Detail>().Operation);
        Assert.Same(typed.Operation, Assert.Single(root.Build().Operations));
        Assert.Contains(typed.Operation.Results, result => result.Kind == ApiResultKind.NotFound && result.BodyType == typeof(void));
        ApiEndpoint<Detail> projected = typed.WithHttp(typed.Operation.Http!);
        Assert.Equal(typed.Id, projected.Id);
        Assert.Equal(typeof(Detail), projected.Operation.ResponseType);
        Assert.Equal(typed.Operation.Results, projected.Operation.Results);
    }

    [Fact]
    public void Wrong_type_tag_fails_before_registering_an_operation()
    {
        var root = Cohesive.Api.Api.Define();
        var declaration = root.Query("Details").Returns<Detail>();
        Assert.Throws<ArgumentException>(() => declaration.Build<string>());
        Assert.Empty(root.Build().Operations);
        Assert.Equal(typeof(Detail), declaration.Build<Detail>().Operation.ResponseType);
        Assert.Single(root.Build().Operations);
    }
    [Fact]
    public void Typed_build_declares_created_once_and_rejects_conflicting_rebuilds()
    {
        var root = Cohesive.Api.Api.Define();
        var declaration = root.Command("Create").Route("POST", "/details");
        var typed = declaration.Build<Detail>(ApiResultKind.Created);
        Assert.Equal(typeof(Detail), typed.Operation.ResponseType);
        Assert.Equal(ApiResultKind.Created, typed.Operation.PrimaryResult.Kind);
        Assert.Same(typed.Operation, declaration.Build<Detail>().Operation);
        Assert.Throws<ArgumentException>(() => declaration.Build<Detail>(ApiResultKind.Success));
        Assert.Throws<ArgumentException>(() => declaration.Build<string>());
        Assert.Single(root.Build().Operations);
    }

    [Fact]
    public void Invalid_primary_kind_does_not_register_or_overwrite_a_declaration()
    {
        var root = Cohesive.Api.Api.Define();
        var declaration = root.Command("Create");
        Assert.Throws<ArgumentOutOfRangeException>(() => declaration.Build<Detail>(ApiResultKind.Conflict));
        Assert.Empty(root.Build().Operations);
        declaration.Returns<Detail>(ApiResultKind.Created);
        Assert.Throws<ArgumentException>(() => declaration.Build<Detail>(ApiResultKind.Accepted));
        Assert.Empty(root.Build().Operations);
        Assert.Equal(ApiResultKind.Created, declaration.Build<Detail>().Operation.PrimaryResult.Kind);
    }

}
