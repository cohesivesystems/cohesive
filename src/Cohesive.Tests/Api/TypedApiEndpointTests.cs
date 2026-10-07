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
            .RouteParameter<string>("id").Returns<Detail>().Result(ApiResultKind.NotFound);
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
}
