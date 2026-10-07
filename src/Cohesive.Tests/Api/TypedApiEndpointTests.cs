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

    public sealed record CreateDetail(string Name);
    public sealed record FindDetail(string Prefix);

    [Fact]
    public void Request_and_response_are_declared_once_with_explicit_transport()
    {
        var root = Cohesive.Api.Api.Define();
        var create = root.Command("Create").Route("POST", "/details");
        ApiEndpoint<CreateDetail, Detail> body = create.BuildBody<CreateDetail, Detail>(ApiResultKind.Created);
        Assert.Equal(typeof(CreateDetail), body.Operation.RequestType);
        Assert.Equal(typeof(CreateDetail), body.Operation.Http!.Body!.BodyType);
        Assert.Null(body.Operation.Http.Query);
        Assert.Equal(ApiResultKind.Created, body.Operation.PrimaryResult.Kind);
        Assert.Same(body.Operation, create.Build<CreateDetail, Detail>().Operation);
        Assert.Same(body.Operation, create.BuildBody<CreateDetail, Detail>().Operation);
        ApiEndpoint<CreateDetail, Detail> projected = body.WithHttp(body.Operation.Http);
        Assert.Equal(body.Id, projected.Id);
        Assert.Equal(body.Operation.RequestType, projected.Operation.RequestType);
        Assert.Throws<ArgumentException>(() => body.WithHttp(new HttpBinding("POST", "/details", parameters: [],
            body: new HttpBodyBinding(typeof(FindDetail)))));

        ApiEndpoint<FindDetail, Detail> query = root.Query("Find").Route("GET", "/details")
            .BuildQuery<FindDetail, Detail>();
        Assert.Equal(typeof(FindDetail), query.Operation.Http!.Query!.QueryType);
        Assert.Null(query.Operation.Http.Body);
        Assert.Equal(2, root.Build().Operations.Count);
    }

    [Fact]
    public void Conflicting_input_or_transport_fails_before_registration()
    {
        var root = Cohesive.Api.Api.Define();
        var declaration = root.Command("Create").Route("POST", "/details").Body<CreateDetail>();
        Assert.Throws<ArgumentException>(() => declaration.Build<FindDetail, Detail>());
        Assert.Throws<ArgumentException>(() => declaration.BuildBody<FindDetail, Detail>());
        Assert.Throws<ArgumentException>(() => declaration.BuildQuery<CreateDetail, Detail>());
        Assert.Empty(root.Build().Operations);
        var body = declaration.Build<CreateDetail, Detail>();
        Assert.Equal(typeof(CreateDetail), body.Operation.RequestType);
        Assert.Throws<ArgumentException>(() => declaration.BuildQuery<CreateDetail, Detail>());
        Assert.Single(root.Build().Operations);
    }

    [Fact]
    public void Route_parameter_is_not_a_request_dto()
    {
        var root = Cohesive.Api.Api.Define();
        var declaration = root.Query("Get").Route("GET", "/details/{id}").RouteParameter<string>("id");
        Assert.Throws<ArgumentException>(() => declaration.Build<string, Detail>());
        Assert.Empty(root.Build().Operations);
        Assert.Equal(typeof(void), declaration.Build<Detail>().Operation.RequestType);
    }

}
