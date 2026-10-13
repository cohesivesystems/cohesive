using Cohesive.Adapters.AspNet;
using Cohesive.Identity;
using Cohesive.Adapters.AspNet.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Api;

public sealed class FixedRequestIdentityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fixed_and_resolved_identity_registration_are_mutually_exclusive(bool fixedFirst)
    {
        var services = new ServiceCollection();
        var identity = new IdentityContext(new PrincipalRef("local", PrincipalKind.User));
        if (fixedFirst) services.AddFixedRequestIdentity(identity);
        else services.AddRequestIdentityContext();
        var before = services.ToArray();
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (fixedFirst) services.AddRequestIdentityContext();
            else services.AddFixedRequestIdentity(identity);
        });
        Assert.Equal(before, services.ToArray());
    }

    [Fact]
    public void Duplicate_fixed_identity_fails_before_mutating_registration()
    {
        var services = new ServiceCollection();
        var identity = new IdentityContext(new PrincipalRef("local", PrincipalKind.User));
        services.AddFixedRequestIdentity(identity);
        Assert.Throws<InvalidOperationException>(() => services.AddFixedRequestIdentity(identity));
        Assert.Single(services);
    }

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
