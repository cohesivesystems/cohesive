using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticQueryLoweringPolicyTests
{
    [Fact]
    public void Default_PrefersExactReversedFieldPrefixAndRecordsSkippedFallback()
    {
        var resolution = ElasticQueryLoweringPolicy.Default.Resolve(Context(
            Capabilities(
                ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix,
                ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix),
            "A😀β"));

        Assert.True(resolution.IsSuccessful);
        Assert.Equal(ElasticQueryLoweringStrategies.ReversedFieldPrefixId, resolution.Decision.SelectedStrategy);
        Assert.Equal(
            [ElasticQueryLoweringAttemptDisposition.Selected, ElasticQueryLoweringAttemptDisposition.NotConsidered],
            resolution.Decision.Attempts.Select(static attempt => attempt.Disposition));
        using var request = ElasticSdkRequestTestSupport.Serialize(
            Request(resolution.Query!).Bind(EmptyParameters));
        Assert.Equal(
            "β😀A",
            request.RootElement
                .GetProperty("query")
                .GetProperty("prefix")
                .GetProperty("customer.name.reversed")
                .GetProperty("value")
                .GetString());
    }

    [Fact]
    public void Default_FallsBackToExactWildcardAndRecordsRejection()
    {
        var resolution = ElasticQueryLoweringPolicy.Default.Resolve(Context(
            Capabilities(ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix),
            "A*?\\"));

        Assert.True(resolution.IsSuccessful);
        Assert.Equal(ElasticQueryLoweringStrategies.WildcardExactKeywordId, resolution.Decision.SelectedStrategy);
        Assert.Equal(
            [ElasticQueryLoweringAttemptDisposition.Rejected, ElasticQueryLoweringAttemptDisposition.Selected],
            resolution.Decision.Attempts.Select(static attempt => attempt.Disposition));
        using var request = ElasticSdkRequestTestSupport.Serialize(
            Request(resolution.Query!).Bind(EmptyParameters));
        Assert.Equal(
            "*A\\*\\?\\\\",
            request.RootElement
                .GetProperty("query")
                .GetProperty("wildcard")
                .GetProperty("customer.name.keyword")
                .GetProperty("value")
                .GetString());
    }

    [Fact]
    public void RequirePreferred_DoesNotUseAnEligibleFallback()
    {
        var policy = ElasticQueryLoweringPolicy.CreateConventional(
            additionalPreferences:
            [
                new(
                    ElasticQueryLoweringOperation.Suffix,
                    ElasticQueryLoweringPreferenceOrigin.ExplicitLocal,
                    ElasticQueryLoweringFallbackPolicy.RequirePreferred,
                    [
                        ElasticQueryLoweringStrategies.ReversedFieldPrefixId,
                        ElasticQueryLoweringStrategies.WildcardExactKeywordId
                    ])
            ]);

        var resolution = policy.Resolve(Context(
            Capabilities(ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix),
            "Inc"));

        Assert.False(resolution.IsSuccessful);
        Assert.Null(resolution.Query);
        Assert.Null(resolution.Decision.SelectedStrategy);
        Assert.Equal(
            [ElasticQueryLoweringAttemptDisposition.Rejected, ElasticQueryLoweringAttemptDisposition.NotConsidered],
            resolution.Decision.Attempts.Select(static attempt => attempt.Disposition));
    }

    [Fact]
    public void Composition_IsOrderIndependentAndUsesDocumentedPrecedence()
    {
        ElasticQueryLoweringPreference scoped = new(
            ElasticQueryLoweringOperation.Suffix,
            ElasticQueryLoweringPreferenceOrigin.ScopedProfile,
            ElasticQueryLoweringFallbackPolicy.RequirePreferred,
            [ElasticQueryLoweringStrategies.WildcardExactKeywordId]);
        ElasticQueryLoweringPreference local = new(
            ElasticQueryLoweringOperation.Suffix,
            ElasticQueryLoweringPreferenceOrigin.ExplicitLocal,
            ElasticQueryLoweringFallbackPolicy.RequirePreferred,
            [ElasticQueryLoweringStrategies.ReversedFieldPrefixId]);

        var first = ElasticQueryLoweringPolicy.CreateConventional(additionalPreferences: [scoped, local]);
        var second = ElasticQueryLoweringPolicy.CreateConventional(additionalPreferences: [local, scoped]);
        var registrationFirst = new ElasticQueryLoweringPolicy(
            [ElasticQueryLoweringStrategies.WildcardExactKeyword, ElasticQueryLoweringStrategies.ReversedFieldPrefix],
            [local]);
        var registrationSecond = new ElasticQueryLoweringPolicy(
            [ElasticQueryLoweringStrategies.ReversedFieldPrefix, ElasticQueryLoweringStrategies.WildcardExactKeyword],
            [local]);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(registrationFirst.Fingerprint, registrationSecond.Fingerprint);
        Assert.Equal(
            ElasticQueryLoweringPreferenceOrigin.ExplicitLocal,
            first.GetEffectivePreference(ElasticQueryLoweringOperation.Suffix).Origin);
        Assert.Equal(
            ElasticQueryLoweringStrategies.ReversedFieldPrefixId,
            first.Resolve(Context(
                Capabilities(
                    ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix,
                    ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix),
                "Inc")).Decision.SelectedStrategy);
    }

    [Fact]
    public void Composition_RejectsConflictingPreferencesAtTheSameOrigin()
    {
        Assert.Throws<ArgumentException>(() => ElasticQueryLoweringPolicy.CreateConventional(
            additionalPreferences:
            [
                new(
                    ElasticQueryLoweringOperation.Suffix,
                    ElasticQueryLoweringPreferenceOrigin.ExplicitLocal,
                    ElasticQueryLoweringFallbackPolicy.RequirePreferred,
                    [ElasticQueryLoweringStrategies.WildcardExactKeywordId]),
                new(
                    ElasticQueryLoweringOperation.Suffix,
                    ElasticQueryLoweringPreferenceOrigin.ExplicitLocal,
                    ElasticQueryLoweringFallbackPolicy.RequirePreferred,
                    [ElasticQueryLoweringStrategies.ReversedFieldPrefixId])
            ]));
    }

    static ElasticQueryLoweringContext Context(
        ElasticRelationQueryFieldSemanticCapabilities capabilities,
        string suffix) =>
        new(
            ElasticQueryLoweringOperation.Suffix,
            Field(capabilities),
            ElasticQueryValueTemplate.FromConstant(ObservationValue.FromString(suffix)));

    static ElasticRelationQueryFieldSemanticCapabilities Capabilities(
        params ElasticRelationQueryFieldSemanticCapabilities[] capabilities) =>
        capabilities.Aggregate(
            ElasticRelationQueryFieldSemanticCapabilities.None,
            static (current, capability) => current | capability);

    static ElasticRelationQueryFieldBinding Field(
        ElasticRelationQueryFieldSemanticCapabilities capabilities) =>
        new(
            new RelationQueryInputId("customer-name"),
            FieldPath.Parse("customer.name"),
            FieldPath.Parse("customer.name.keyword"),
            ElasticRelationQueryFieldMappingKind.Keyword,
            retrievalKind: ElasticRelationQueryFieldRetrievalKind.Source,
            retrievalEncoding: ElasticRelationQueryFieldValueEncoding.JsonString,
            documentScope: ElasticRelationQueryFieldDocumentScope.RootDocument,
            semanticCapabilities: capabilities,
            reversedSuffixField: capabilities.HasFlag(ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix)
                ? FieldPath.Parse("customer.name.reversed")
                : null,
            semanticProfile: "ordinal-keyword-v1");

    static ElasticSearchRequestTemplate Request(ElasticQueryTemplate query) =>
        new(
            "loads-read",
            query,
            ["id"],
            [],
            ElasticSearchPageTemplate.Unpaged,
            ElasticAggregationTemplate.None);

    static readonly IReadOnlyDictionary<QueryParameterId, ObservationValue> EmptyParameters =
        new Dictionary<QueryParameterId, ObservationValue>();
}
