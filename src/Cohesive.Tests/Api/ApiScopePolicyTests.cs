using Cohesive.Api;

namespace Cohesive.Tests.Api;

public sealed class ApiScopePolicyTests
{
    [Fact]
    public void Constructor_ResourceBinding_RequiresResourceParameterName()
    {
        var error = Assert.Throws<ArgumentException>(() => new ApiScopePolicy(
            scopeKind: "shipping.tenant",
            cardinality: ApiScopeCardinality.Single,
            binding: ApiScopeBinding.Resource,
            access: ApiScopeAccess.ValidateAccessible,
            allowDefaultScope: false
            )
        );

        Assert.Equal("resourceParameterName", error.ParamName);
    }

    [Fact]
    public void Constructor_NonResourceBinding_RejectsResourceDerivation()
    {
        var error = Assert.Throws<ArgumentException>(() => new ApiScopePolicy(
            scopeKind: "shipping.tenant",
            cardinality: ApiScopeCardinality.Single,
            binding: ApiScopeBinding.Header,
            access: ApiScopeAccess.RequireSelected,
            singleScopeParameterName: "X-Tenant-Id",
            resourceDerivation: new(
                strategy: ApiResourceScopeDerivationStrategies.StructuredResourceId,
                format: ApiResourceIdFormats.ScopedProcessInstanceId,
                scopeField: ApiResourceScopeFields.ScopeId
                ),
            allowDefaultScope: false
            )
        );

        Assert.Equal("resourceDerivation", error.ParamName);
    }

    [Fact]
    public void ResourceScopeDerivation_NormalizesStructuredMetadata()
    {
        var derivation = new ApiResourceScopeDerivation(
            strategy: " structuredResourceId ",
            format: " scopedProcessInstanceId ",
            scopeField: " scopeId "
            );

        Assert.Equal("structuredResourceId", derivation.Strategy);
        Assert.Equal("scopedProcessInstanceId", derivation.Format);
        Assert.Equal("scopeId", derivation.ScopeField);
    }
}
