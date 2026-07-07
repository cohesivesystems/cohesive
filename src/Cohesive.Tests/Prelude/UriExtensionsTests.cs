namespace Cohesive.Tests.Prelude;

public sealed class UriExtensionsTests
{
    [Theory]
    [InlineData("/api/", "/v1/", "api/v1")]
    [InlineData("", "/v1/", "v1")]
    [InlineData("/api/", "", "api")]
    [InlineData("/", "/", "")]
    [InlineData(null, "/v1/", "v1")]
    public void CombineSegments_WithTwoSegments_TrimsSeparatorsAndSkipsEmptySegments(string? firstSegment, string? secondSegment, string expected)
    {
        var result = Uri.CombineSegments(firstSegment, secondSegment);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CombineSegments_WithSegmentSpan_TrimsSeparatorsAndSkipsEmptySegments()
    {
        ReadOnlySpan<string?> segments =
        [
            "/api/",
            "",
            "/v1/",
            "/users/",
            "/"
        ];
        var result = Uri.CombineSegments(segments);
        Assert.Equal("api/v1/users", result);
    }

    [Fact]
    public void CombineSegments_WithSegmentSpan_ReturnsEmptyWhenAllSegmentsAreEmpty()
    {
        ReadOnlySpan<string?> segments =
        [
            "",
            "/",
            null
        ];
        var result = Uri.CombineSegments(segments);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void CreateRouteUri_WithRouteTemplateAndObjectValues_PreservesBasePathAndBindsRoute()
    {
        var baseUri = new Uri("https://api.example.com/v1/");
        var routeTemplate = RouteTemplate.Parse("/shapes/{id}/search?q={query}&type={type}");
        var result = Uri.CreateRouteUri(baseUri, routeTemplate, new
        {
            id = "shape 1",
            query = "address line",
            type = "scalar"
        });
        Assert.Equal("https://api.example.com/v1/shapes/shape%201/search?q=address%20line&type=scalar", result.AbsoluteUri);
    }

    [Fact]
    public void CreateRouteUri_WithStringTemplateAndDictionaryValues_PreservesFragment()
    {
        var baseUri = new Uri("https://api.example.com/api");
        var result = Uri.CreateRouteUri(
            baseUri,
            "/tasks/{processId}?status={status}#results",
            new Dictionary<string, string>
            {
                ["processId"] = "compile-1",
                ["status"] = "Running"
            });
        Assert.Equal("https://api.example.com/api/tasks/compile-1?status=Running#results", result.AbsoluteUri);
    }

    [Fact]
    public void CreateRouteUri_WithParameterlessRouteTemplate_CreatesAbsoluteUri()
    {
        var baseUri = new Uri("https://api.example.com/v1/");
        var result = Uri.CreateRouteUri(baseUri, new RouteTemplate("/tasks"));
        Assert.Equal("https://api.example.com/v1/tasks", result.AbsoluteUri);
    }

    [Fact]
    public void CreateRouteUri_WithNullValuesAndParameterlessRouteTemplate_CreatesAbsoluteUri()
    {
        var baseUri = new Uri("https://api.example.com/v1/");
        var result = Uri.CreateRouteUri(baseUri, new RouteTemplate("/tasks"), (object?)null);
        Assert.Equal("https://api.example.com/v1/tasks", result.AbsoluteUri);
    }

    [Fact]
    public void CreateRouteUri_WithRelativeBaseUri_Throws()
    {
        var baseUri = new Uri("v1/", UriKind.Relative);
        var exception = Assert.Throws<ArgumentException>(() => Uri.CreateRouteUri(baseUri, new RouteTemplate("/tasks")));
        Assert.Equal("baseUri", exception.ParamName);
        Assert.Contains("Base URI must be absolute.", exception.Message, StringComparison.Ordinal);
    }
}
