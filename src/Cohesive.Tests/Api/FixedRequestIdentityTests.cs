using Cohesive.Adapters.AspNet;
using Cohesive.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class FixedRequestIdentityTests
{
    [Fact]
    public async Task Fixed_identity_is_host_scoped_and_preserves_request_context()
    {
        var identity = new IdentityContext(new PrincipalRef("local", PrincipalKind.User));
        var services = new ServiceCollection();
        services.AddFixedRequestIdentity(identity);
        using var provider = services.BuildServiceProvider();
        using var other = new ServiceCollection().BuildServiceProvider();
        Assert.Empty(other.GetServices<IHttpOperationContextEnricher>());
        var context = OperationContext.Create();
        var actual = await provider.GetRequiredService<IHttpOperationContextEnricher>()
            .EnrichAsync(new DefaultHttpContext(), context);
        Assert.Same(identity, actual.GetIdentityContextOrDefault());
        Assert.Equal(context.CancellationToken, actual.CancellationToken);
    }

    [Fact]
    public void Fixed_identity_requires_explicit_identity() =>
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddFixedRequestIdentity(null!));
}
