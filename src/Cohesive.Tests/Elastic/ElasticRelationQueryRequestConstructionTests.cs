using System.Collections.Immutable;
using Cohesive.Adapters.Elastic;
using Cohesive.Model;
using Cohesive.Relations.IR;
using global::Elastic.Clients.Elasticsearch;
using global::Elastic.Clients.Elasticsearch.Aggregations;
using global::Elastic.Clients.Elasticsearch.QueryDsl;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticRelationQueryRequestConstructionTests
{
    [Fact]
    public void Bind_EmitsDeterministicExactRowRequest()
    {
        var status = new QueryParameterId("status");
        var template = new ElasticSearchRequestTemplate(
            "loads-read",
            ElasticQueryTemplate.Boolean(
                filter:
                [
                    ElasticQueryTemplate.Term(
                        "status.keyword",
                        ElasticQueryValueTemplate.FromParameter(status)),
                    ElasticQueryTemplate.Range(
                        "weight",
                        lower: new(
                            ElasticQueryValueTemplate.FromConstant(ObservationValue.FromInt64(10)),
                            ElasticRangeBoundKind.Inclusive))
                ],
                mustNot: [ElasticQueryTemplate.Exists("deletedAt")]),
            ["status", "id", "status"],
            [
                new ElasticSearchSort("createdAt", QuerySortDirection.Descending, QueryNullPlacement.Last),
                new ElasticSearchSort("id.keyword", QuerySortDirection.Ascending, QueryNullPlacement.First)
            ],
            ElasticSearchPageTemplate.OffsetPage(offset: 20, limit: 10),
            ElasticAggregationTemplate.None);
        var parameters = new Dictionary<QueryParameterId, ObservationValue>
        {
            [status] = ObservationValue.FromString("Booked")
        };

        var first = template.Bind(parameters);
        var second = template.Bind(parameters);
        var firstQuery = first.Query!;
        var firstBoolean = firstQuery.Bool!;
        var firstSource = first.Source!;
        Indices expectedIndices = "loads-read";
        Assert.True(firstSource.TryGetSourceFilter(out _));

        Assert.Equal(expectedIndices, first.Indices);
        Assert.False(first.AllowPartialSearchResults);
        Assert.Equal(20, first.From);
        Assert.Equal(10, first.Size);
        Assert.Equal(2, first.Sort?.Count);
        Assert.Equal(2, firstBoolean.Filter?.Count);
        Assert.Single(firstBoolean.MustNot!);
        Assert.IsType<TermQuery>(firstBoolean.Filter!.First().Term);
        Assert.IsType<NumberRangeQuery>(firstBoolean.Filter!.Last().Range);
        Assert.NotSame(first, second);
        Assert.Equal(
            ElasticSdkRequestTestSupport.SerializeToString(first),
            ElasticSdkRequestTestSupport.SerializeToString(second));

        using var document = ElasticSdkRequestTestSupport.Serialize(first);
        var root = document.RootElement;
        var boolean = root.GetProperty("query").GetProperty("bool");
        var filters = boolean.GetProperty("filter");
        Assert.Equal(
            "Booked",
            filters[0]
                .GetProperty("term")
                .GetProperty("status.keyword")
                .GetProperty("value")
                .GetString());
        Assert.Equal(
            10L,
            filters[1]
                .GetProperty("range")
                .GetProperty("weight")
                .GetProperty("gte")
                .GetInt64());
        Assert.Equal(
            "deletedAt",
            boolean
                .GetProperty("must_not")
                .GetProperty("exists")
                .GetProperty("field")
                .GetString());
        Assert.Equal(20, root.GetProperty("from").GetInt32());
        Assert.Equal(10, root.GetProperty("size").GetInt32());
        Assert.Equal(2, root.GetProperty("sort").GetArrayLength());
        Assert.Equal(2, root.GetProperty("_source").GetProperty("includes").GetArrayLength());
    }

    [Fact]
    public void Bind_ReturnsFreshMutableSdkRequestGraphs()
    {
        var status = new QueryParameterId("status");
        var template = Request(
            query: ElasticQueryTemplate.Term(
                "status.keyword",
                ElasticQueryValueTemplate.FromParameter(status)),
            sorts:
            [
                new ElasticSearchSort(
                    "id.keyword",
                    QuerySortDirection.Ascending,
                    QueryNullPlacement.Last)
            ],
            page: ElasticSearchPageTemplate.OffsetPage(offset: 0, limit: 10));
        var parameters = new Dictionary<QueryParameterId, ObservationValue>
        {
            [status] = ObservationValue.FromString("Booked")
        };

        var first = template.Bind(parameters);
        var second = template.Bind(parameters);
        var firstQuery = first.Query!;
        var secondQuery = second.Query!;
        var firstSource = first.Source!;
        var secondSource = second.Source!;
        Assert.True(firstSource.TryGetSourceFilter(out var firstSourceFilter));
        Assert.True(secondSource.TryGetSourceFilter(out var secondSourceFilter));
        var firstTerm = Assert.IsType<TermQuery>(firstQuery.Term);
        var secondTerm = Assert.IsType<TermQuery>(secondQuery.Term);
        var firstSort = Assert.IsType<FieldSort>(Assert.Single(first.Sort!).Field);
        var secondSort = Assert.IsType<FieldSort>(Assert.Single(second.Sort!).Field);

        Assert.NotSame(first, second);
        Assert.NotSame(firstQuery, secondQuery);
        Assert.NotSame(firstTerm, secondTerm);
        Assert.NotSame(first.Sort, second.Sort);
        Assert.NotSame(firstSort, secondSort);
        Assert.NotSame(firstSourceFilter, secondSourceFilter);

        firstTerm.Value = FieldValue.String("Cancelled");
        firstSort.Order = SortOrder.Desc;
        firstSourceFilter.Includes = Fields.FromStrings(["changed"]);
        first.Size = 99;

        Assert.Equal(FieldValue.String("Booked"), secondTerm.Value);
        Assert.Equal(SortOrder.Asc, secondSort.Order);
        Assert.Equal(10, second.Size);
        using var secondDocument = ElasticSdkRequestTestSupport.Serialize(second);
        Assert.Equal(
            "id",
            secondDocument.RootElement
                .GetProperty("_source")
                .GetProperty("includes")
                .GetString());
    }

    [Fact]
    public void SearchAfterPage_AllowsInitialAndContinuedRequests()
    {
        var sorts = ImmutableArray.Create(
            new ElasticSearchSort("createdAt", QuerySortDirection.Ascending, QueryNullPlacement.Last),
            new ElasticSearchSort("id.keyword", QuerySortDirection.Ascending, QueryNullPlacement.Last));
        var initial = Request(
            sorts: sorts,
            page: ElasticSearchPageTemplate.SearchAfterPage(limit: 25, after: []));
        var continued = Request(
            sorts: sorts,
            page: ElasticSearchPageTemplate.SearchAfterPage(
                limit: 25,
                after:
                [
                    ElasticQueryValueTemplate.FromConstant(ObservationValue.FromString("2026-07-16T12:00:00Z")),
                    ElasticQueryValueTemplate.FromParameter(new QueryParameterId("last-id"))
                ]));

        var initialRequest = initial.Bind(EmptyParameters);
        var continuedRequest = continued.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new QueryParameterId("last-id")] = ObservationValue.FromString("load-42")
        });
        var continuedAfter = continuedRequest.SearchAfter!;

        Assert.Null(initialRequest.SearchAfter);
        Assert.Equal(25, initialRequest.Size);
        Assert.Equal(2, initialRequest.Sort?.Count);
        Assert.Equal(2, continuedAfter.Count);
        Assert.Equal(
            FieldValue.String("2026-07-16T12:00:00Z"),
            continuedAfter.First());
        Assert.Equal(FieldValue.String("load-42"), continuedAfter.Last());

        using (var initialDocument = ElasticSdkRequestTestSupport.Serialize(initialRequest))
        {
            var root = initialDocument.RootElement;
            Assert.False(root.TryGetProperty("search_after", out _));
            Assert.Equal(25, root.GetProperty("size").GetInt32());
            Assert.Equal(2, root.GetProperty("sort").GetArrayLength());
        }
        using (var continuedDocument = ElasticSdkRequestTestSupport.Serialize(continuedRequest))
        {
            var after = continuedDocument.RootElement.GetProperty("search_after");
            Assert.Equal("2026-07-16T12:00:00Z", after[0].GetString());
            Assert.Equal("load-42", after[1].GetString());
        }

        Assert.Throws<ArgumentException>(() => Request(
            sorts: [],
            page: ElasticSearchPageTemplate.SearchAfterPage(limit: 25, after: [])));
        Assert.Throws<ArgumentException>(() => Request(
            sorts: sorts,
            page: ElasticSearchPageTemplate.SearchAfterPage(
                limit: 25,
                after: [ElasticQueryValueTemplate.FromConstant(ObservationValue.FromString("only-one"))])));
    }

    [Fact]
    public void ValueTransforms_EscapeWildcardSyntaxAndReverseUnicodeScalars()
    {
        var suffix = new QueryParameterId("suffix");
        var wildcard = Request(
            query: ElasticQueryTemplate.Wildcard(
                "reference.keyword",
                ElasticQueryValueTemplate.FromParameter(suffix, ElasticQueryValueTransform.WildcardSuffix)));
        var reversed = Request(
            query: ElasticQueryTemplate.Prefix(
                "reference.reversed",
                ElasticQueryValueTemplate.FromParameter(suffix, ElasticQueryValueTransform.ReverseUnicodeScalars)));

        var parameters = new Dictionary<QueryParameterId, ObservationValue>
        {
            [suffix] = ObservationValue.FromString("A*?\\😀β")
        };
        var wildcardRequest = wildcard.Bind(parameters);
        var reversedRequest = reversed.Bind(parameters);
        var wildcardQuery = wildcardRequest.Query!;
        var reversedQuery = reversedRequest.Query!;

        Assert.Equal("*A\\*\\?\\\\😀β", wildcardQuery.Wildcard?.Value);
        Assert.Equal("β😀\\?*A", reversedQuery.Prefix?.Value);

        using var wildcardDocument = ElasticSdkRequestTestSupport.Serialize(wildcardRequest);
        using var reversedDocument = ElasticSdkRequestTestSupport.Serialize(reversedRequest);

        Assert.Equal(
            "*A\\*\\?\\\\😀β",
            wildcardDocument.RootElement
                .GetProperty("query")
                .GetProperty("wildcard")
                .GetProperty("reference.keyword")
                .GetProperty("value")
                .GetString());
        Assert.Equal(
            "β😀\\?*A",
            reversedDocument.RootElement
                .GetProperty("query")
                .GetProperty("prefix")
                .GetProperty("reference.reversed")
                .GetProperty("value")
                .GetString());
    }

    [Fact]
    public void Aggregations_EmitExactCountContracts()
    {
        var globalCount = Request(aggregation: ElasticAggregationTemplate.CountRows());
        var groupedCount = Request(
            aggregation: ElasticAggregationTemplate.CompositeCount(
                "groups",
                size: 50,
                sources:
                [
                    new("customer", "customerId.keyword", QuerySortDirection.Ascending),
                    new("status", "status.keyword", QuerySortDirection.Descending)
                ],
                after:
                [
                    ElasticQueryValueTemplate.FromConstant(ObservationValue.FromString("customer-9")),
                    ElasticQueryValueTemplate.FromParameter(new QueryParameterId("status-after"))
                ]));

        var globalRequest = globalCount.Bind(EmptyParameters);
        var groupedRequest = groupedCount.Bind(new Dictionary<QueryParameterId, ObservationValue>
        {
            [new QueryParameterId("status-after")] = ObservationValue.FromString("Booked")
        });
        var globalSource = globalRequest.Source!;
        var trackTotalHits = globalRequest.TrackTotalHits!;
        Assert.True(globalSource.TryGetBool(out var sourceEnabled));

        Assert.Equal(0, globalRequest.Size);
        Assert.False(sourceEnabled);
        Assert.True(trackTotalHits.Value1);
        Assert.Null(globalRequest.Aggregations);

        Assert.True(groupedRequest.Aggregations!.TryGetValue("groups", out var groups));
        var composite = Assert.IsType<CompositeAggregation>(groups.Composite);
        Assert.Equal(50, composite.Size);
        Assert.Equal(2, composite.Sources?.Count);
        var sources = composite.Sources!.ToArray();
        var customerTerms = Assert.IsType<CompositeTermsAggregation>(sources[0].Value.Terms);
        var statusTerms = Assert.IsType<CompositeTermsAggregation>(sources[1].Value.Terms);
        Assert.Equal("customer", sources[0].Key);
        Assert.Equal("customerId.keyword", customerTerms.Field?.ToString());
        Assert.Equal(SortOrder.Asc, customerTerms.Order);
        Assert.Equal("status", sources[1].Key);
        Assert.Equal("status.keyword", statusTerms.Field?.ToString());
        Assert.Equal(SortOrder.Desc, statusTerms.Order);

        using (var globalDocument = ElasticSdkRequestTestSupport.Serialize(globalRequest))
        {
            var root = globalDocument.RootElement;
            Assert.Equal(0, root.GetProperty("size").GetInt32());
            Assert.False(root.GetProperty("_source").GetBoolean());
            Assert.True(root.GetProperty("track_total_hits").GetBoolean());
        }
        using (var groupedDocument = ElasticSdkRequestTestSupport.Serialize(groupedRequest))
        {
            var root = groupedDocument.RootElement;
            var serializedComposite = root
                .GetProperty("aggregations")
                .GetProperty("groups")
                .GetProperty("composite");
            Assert.Equal(50, serializedComposite.GetProperty("size").GetInt32());
            Assert.Equal(2, serializedComposite.GetProperty("sources").GetArrayLength());
            Assert.Equal(
                "customer-9",
                serializedComposite.GetProperty("after").GetProperty("customer").GetString());
            Assert.Equal(
                "Booked",
                serializedComposite.GetProperty("after").GetProperty("status").GetString());
        }
    }

    [Fact]
    public void ConstructionAndBinding_RejectInexactOrInconsistentInputs()
    {
        Assert.Throws<ArgumentException>(() => ElasticQueryValueTemplate.FromConstant(
            ObservationValue.FromDouble(double.NaN)));
        Assert.Throws<ArgumentException>(() => ElasticQueryValueTemplate.FromConstant(
            ObservationValue.FromArray([])));
        Assert.Throws<ArgumentException>(() => ElasticQueryValueTemplate.FromConstant(
            new ObservationValue(ObservationValueKind.String, s: "\uD800")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElasticAggregationTemplate.CompositeCount(
            "groups",
            size: 0,
            sources: [new("status", "status.keyword", QuerySortDirection.Ascending)]));
        Assert.Throws<ArgumentException>(() => Request(
            page: ElasticSearchPageTemplate.OffsetPage(offset: 0, limit: 10),
            aggregation: ElasticAggregationTemplate.CountRows()));
        Assert.Throws<ArgumentException>(() => Request(
            sorts: [new ElasticSearchSort("id.keyword", QuerySortDirection.Ascending, QueryNullPlacement.Last)],
            aggregation: ElasticAggregationTemplate.CountRows()));
        Assert.Throws<ArgumentException>(() => new ElasticSearchRequestTemplate(
            "loads-read",
            ElasticQueryTemplate.MatchAll(),
            ["id"],
            [],
            ElasticSearchPageTemplate.Unpaged,
            ElasticAggregationTemplate.CountRows()));

        var missingParameter = Request(
            query: ElasticQueryTemplate.Term(
                "status.keyword",
                ElasticQueryValueTemplate.FromParameter(new QueryParameterId("status"))));
        Assert.Throws<ArgumentException>(() => missingParameter.Bind(EmptyParameters));

        var nonTextTransform = Request(
            query: ElasticQueryTemplate.Wildcard(
                "status.keyword",
                ElasticQueryValueTemplate.FromParameter(
                    new QueryParameterId("suffix"),
                    ElasticQueryValueTransform.WildcardSuffix)));
        Assert.Throws<ArgumentException>(() => nonTextTransform.Bind(
            new Dictionary<QueryParameterId, ObservationValue>
            {
                [new QueryParameterId("suffix")] = ObservationValue.FromInt64(42)
            }));

        var nullTerm = Request(
            query: ElasticQueryTemplate.Term(
                "status.keyword",
                ElasticQueryValueTemplate.FromConstant(ObservationValue.Null)));
        Assert.Throws<ArgumentException>(() => nullTerm.Bind(EmptyParameters));

        var booleanRange = Request(
            query: ElasticQueryTemplate.Range(
                "active",
                lower: new(
                    ElasticQueryValueTemplate.FromConstant(ObservationValue.FromBool(true)),
                    ElasticRangeBoundKind.Inclusive)));
        Assert.Throws<ArgumentException>(() => booleanRange.Bind(EmptyParameters));

        var mixedRange = Request(
            query: ElasticQueryTemplate.Range(
                "mixed",
                lower: new(
                    ElasticQueryValueTemplate.FromConstant(ObservationValue.FromInt64(1)),
                    ElasticRangeBoundKind.Inclusive),
                upper: new(
                    ElasticQueryValueTemplate.FromConstant(ObservationValue.FromString("z")),
                    ElasticRangeBoundKind.Inclusive)));
        Assert.Throws<ArgumentException>(() => mixedRange.Bind(EmptyParameters));

        var nullCompositeContinuation = Request(
            aggregation: ElasticAggregationTemplate.CompositeCount(
                "groups",
                size: 10,
                sources: [new("status", "status.keyword", QuerySortDirection.Ascending)],
                after: [ElasticQueryValueTemplate.FromConstant(ObservationValue.Null)]));
        Assert.Throws<ArgumentException>(() => nullCompositeContinuation.Bind(EmptyParameters));
    }

    static readonly IReadOnlyDictionary<QueryParameterId, ObservationValue> EmptyParameters =
        new Dictionary<QueryParameterId, ObservationValue>();

    static ElasticSearchRequestTemplate Request(
        ElasticQueryTemplate? query = null,
        ImmutableArray<ElasticSearchSort> sorts = default,
        ElasticSearchPageTemplate? page = null,
        ElasticAggregationTemplate? aggregation = null)
    {
        var effectiveAggregation = aggregation ?? ElasticAggregationTemplate.None;
        return new(
            "loads-read",
            query ?? ElasticQueryTemplate.MatchAll(),
            effectiveAggregation.Kind == ElasticAggregationTemplateKind.None ? ["id"] : [],
            sorts,
            page ?? ElasticSearchPageTemplate.Unpaged,
            effectiveAggregation);
    }
}
