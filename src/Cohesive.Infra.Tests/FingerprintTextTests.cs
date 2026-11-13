using Cohesive.Execution;

namespace Cohesive.Infra.Tests;

public sealed class FingerprintTextTests
{
    [Fact]
    public void AllInfrastructureFingerprintTypesUseTheSharedEncoding()
    {
        // Discover owning types so a new triple cannot silently omit the shared projection.
        var types = typeof(InfrastructureDefinitionFingerprint).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Name.EndsWith("Fingerprint", StringComparison.Ordinal)
                && t.GetProperty("Algorithm") is not null && t.GetProperty("Canonicalization") is not null
                && t.GetProperty("Value") is not null).ToArray();
        Assert.NotEmpty(types);
        foreach (var type in types)
        {
            var fingerprint = Activator.CreateInstance(type, "sha:256", "profile/v1%", "abc");
            var method = type.GetMethod("ToCanonicalString", Type.EmptyTypes);
            Assert.NotNull(method);
            Assert.Equal(new ExecutionDefinitionFingerprint("sha:256", "profile/v1%", "abc").ToCanonicalString(),
                method.Invoke(fingerprint, null));
        }
    }
}
