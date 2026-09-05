using Cohesive.Adapters.Sql;
using System.Collections.Immutable;

namespace Cohesive.Adapters.Postgres;

/// <summary>Shared-builder SQL authority for the normalized PostgreSQL Process durable store.</summary>
internal sealed class PostgresProcessDurableStoreSql
{
    const string InstanceAlias = "instance";
    const string PageAlias = "page";
    const long MaximumRootResultBytes = 64L * 1024 * 1024;

    const string AuthorityBinding = "authority";
    const string InstanceBinding = "instance";
    const string ExpectedRevisionBinding = "expected-revision";
    const string NextRevisionBinding = "next-revision";
    const string StorageFormatBinding = "storage-format";
    const string AggregateFingerprintBinding = "aggregate-fingerprint";
    const string AggregateBytesBinding = "aggregate-bytes";
    const string PageManifestBinding = "page-manifest";
    const string PageCountBinding = "page-count";
    const string PageFingerprintsBinding = "page-fingerprints";
    const string PageFingerprintBinding = "page-fingerprint";
    const string PageContentBinding = "page-content";
    const string PageBytesBinding = "page-bytes";

    readonly SqlCommandTemplate loadRoot;
    readonly SqlCommandTemplate loadPages;
    readonly SqlCommandTemplate findPages;
    readonly SqlCommandTemplate insertPage;
    readonly SqlCommandTemplate insertRoot;
    readonly SqlCommandTemplate updateRoot;
    readonly SqlCommandTemplate providerNow;
    readonly long maximumAggregateResultBytes;

    internal PostgresProcessDurableStoreSql(PostgresProcessDurableStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        maximumAggregateResultBytes = options.MaximumAggregateBytes ?? long.MaxValue;
        loadRoot = new SqlSelectBuilder(options.Instances, InstanceAlias)
            .Select(SqlExpression.Column(InstanceAlias, "physical_revision"), "physical_revision")
            .Select(SqlExpression.Column(InstanceAlias, "storage_format"), "storage_format")
            .Select(SqlExpression.Column(InstanceAlias, "aggregate_fingerprint"), "aggregate_fingerprint")
            .Select(SqlExpression.Column(InstanceAlias, "aggregate_bytes"), "aggregate_bytes")
            .Select(SqlExpression.Column(InstanceAlias, "page_manifest"), "page_manifest")
            .Select(SqlExpression.Column(InstanceAlias, "page_count"), "page_count")
            .Where(Equal(InstanceAlias, "authority_id", AuthorityBinding))
            .Where(Equal(InstanceAlias, "instance_id", InstanceBinding))
            .BuildTemplate(PostgresSqlDialect.Instance);
        loadPages = CreatePageLookup(options, includeContent: true);
        findPages = CreatePageLookup(options, includeContent: false);
        insertPage = new SqlInsertBuilder(options.Pages)
            .Value("authority_id", SqlExpression.RuntimeParameter(AuthorityBinding))
            .Value("page_fingerprint", SqlExpression.RuntimeParameter(PageFingerprintBinding))
            .Value("content", SqlExpression.RuntimeParameter(PageContentBinding))
            .Value("content_bytes", SqlExpression.RuntimeParameter(PageBytesBinding))
            .Value("created_at", SqlExpression.Intrinsic(PostgresSqlDialect.ClockTimestampIntrinsic))
            .OnConflictDoNothing(["authority_id", "page_fingerprint"])
            .Returning(SqlExpression.UnqualifiedColumn("page_fingerprint"), "page_fingerprint")
            .BuildTemplate(PostgresSqlDialect.Instance);
        insertRoot = new SqlInsertBuilder(options.Instances)
            .Value("authority_id", SqlExpression.RuntimeParameter(AuthorityBinding))
            .Value("instance_id", SqlExpression.RuntimeParameter(InstanceBinding))
            .Value("physical_revision", SqlExpression.RuntimeParameter(NextRevisionBinding))
            .Value("storage_format", SqlExpression.RuntimeParameter(StorageFormatBinding))
            .Value("aggregate_fingerprint", SqlExpression.RuntimeParameter(AggregateFingerprintBinding))
            .Value("aggregate_bytes", SqlExpression.RuntimeParameter(AggregateBytesBinding))
            .Value("page_manifest", SqlExpression.RuntimeParameter(PageManifestBinding))
            .Value("page_count", SqlExpression.RuntimeParameter(PageCountBinding))
            .Value("updated_at", SqlExpression.Intrinsic(PostgresSqlDialect.ClockTimestampIntrinsic))
            .OnConflictDoNothing(["authority_id", "instance_id"])
            .Returning(SqlExpression.UnqualifiedColumn("physical_revision"), "physical_revision")
            .BuildTemplate(PostgresSqlDialect.Instance);
        updateRoot = new SqlUpdateBuilder(options.Instances)
            .Set("physical_revision", SqlExpression.RuntimeParameter(NextRevisionBinding))
            .Set("storage_format", SqlExpression.RuntimeParameter(StorageFormatBinding))
            .Set("aggregate_fingerprint", SqlExpression.RuntimeParameter(AggregateFingerprintBinding))
            .Set("aggregate_bytes", SqlExpression.RuntimeParameter(AggregateBytesBinding))
            .Set("page_manifest", SqlExpression.RuntimeParameter(PageManifestBinding))
            .Set("page_count", SqlExpression.RuntimeParameter(PageCountBinding))
            .Set("updated_at", SqlExpression.Intrinsic(PostgresSqlDialect.ClockTimestampIntrinsic))
            .Where(Equal(tableAlias: null, "authority_id", AuthorityBinding))
            .Where(Equal(tableAlias: null, "instance_id", InstanceBinding))
            .Where(Equal(tableAlias: null, "physical_revision", ExpectedRevisionBinding))
            .Returning(SqlExpression.UnqualifiedColumn("physical_revision"), "physical_revision")
            .BuildTemplate(PostgresSqlDialect.Instance);
        providerNow = new SqlSelectBuilder()
            .Select(SqlExpression.Intrinsic(PostgresSqlDialect.ClockTimestampIntrinsic), "provider_now")
            .BuildTemplate(PostgresSqlDialect.Instance);
    }

    internal PostgresNpgsqlCommand LoadRoot(string authority, string instance) => Command(
        template: loadRoot,
        parameters: new Dictionary<string, PostgresNpgsqlParameter>(StringComparer.Ordinal)
        {
            [AuthorityBinding] = Text(authority),
            [InstanceBinding] = Text(instance)
        },
        resultTypes:
        [
            PostgresRelationQueryScalarType.Int64,
            PostgresRelationQueryScalarType.Text,
            PostgresRelationQueryScalarType.Text,
            PostgresRelationQueryScalarType.Int64,
            PostgresRelationQueryScalarType.Text,
            PostgresRelationQueryScalarType.Int32
        ],
        maximumResultBytes: MaximumRootResultBytes);

    internal PostgresNpgsqlCommand LoadPages(string authority, ImmutableArray<string> fingerprints) =>
        PageLookup(loadPages, authority, fingerprints, includeContent: true);

    internal PostgresNpgsqlCommand FindPages(string authority, ImmutableArray<string> fingerprints) =>
        PageLookup(findPages, authority, fingerprints, includeContent: false);

    internal PostgresNpgsqlCommand InsertPage(
        string authority,
        PostgresProcessDurablePage page) => Command(
        template: insertPage,
        parameters: new Dictionary<string, PostgresNpgsqlParameter>(StringComparer.Ordinal)
        {
            [AuthorityBinding] = Text(authority),
            [PageFingerprintBinding] = Text(page.Fingerprint),
            [PageContentBinding] = new(page.Content.AsSpan().ToArray(), PostgresRelationQueryScalarType.Bytea, IsArray: false),
            [PageBytesBinding] = new(page.Content.Length, PostgresRelationQueryScalarType.Int32, IsArray: false)
        },
        resultTypes: [PostgresRelationQueryScalarType.Text],
        maximumResultBytes: MaximumRootResultBytes);

    internal PostgresNpgsqlCommand InsertRoot(
        string authority,
        string instance,
        PostgresProcessDurablePagedAggregate aggregate) =>
        StoreRoot(
            template: insertRoot,
            authority: authority,
            instance: instance,
            expectedRevision: null,
            nextRevision: 1,
            aggregate: aggregate);

    internal PostgresNpgsqlCommand UpdateRoot(
        string authority,
        string instance,
        long expectedRevision,
        PostgresProcessDurablePagedAggregate aggregate) =>
        StoreRoot(
            template: updateRoot,
            authority: authority,
            instance: instance,
            expectedRevision: expectedRevision,
            nextRevision: checked(expectedRevision + 1),
            aggregate: aggregate);

    internal string ProviderNowSql => providerNow.Text;

    static SqlCommandTemplate CreatePageLookup(
        PostgresProcessDurableStoreOptions options,
        bool includeContent)
    {
        var builder = new SqlSelectBuilder(options.Pages, PageAlias)
            .Select(SqlExpression.Column(PageAlias, "page_fingerprint"), "page_fingerprint");
        if (includeContent)
        {
            builder.Select(SqlExpression.Column(PageAlias, "content"), "content");
            builder.Select(SqlExpression.Column(PageAlias, "content_bytes"), "content_bytes");
        }
        return builder
            .Where(Equal(PageAlias, "authority_id", AuthorityBinding))
            .Where(SqlExpression.EqualAny(
                SqlExpression.Column(PageAlias, "page_fingerprint"),
                PageFingerprintsBinding))
            .OrderBy(SqlExpression.Column(PageAlias, "page_fingerprint"))
            .BuildTemplate(PostgresSqlDialect.Instance);
    }

    PostgresNpgsqlCommand PageLookup(
        SqlCommandTemplate template,
        string authority,
        ImmutableArray<string> fingerprints,
        bool includeContent) => Command(
        template: template,
        parameters: new Dictionary<string, PostgresNpgsqlParameter>(StringComparer.Ordinal)
        {
            [AuthorityBinding] = Text(authority),
            [PageFingerprintsBinding] = new(
                fingerprints.ToArray(),
                PostgresRelationQueryScalarType.Text,
                IsArray: true)
        },
        resultTypes: includeContent
            ?
            [
                PostgresRelationQueryScalarType.Text,
                PostgresRelationQueryScalarType.Bytea,
                PostgresRelationQueryScalarType.Int32
            ]
            : [PostgresRelationQueryScalarType.Text],
        maximumResultBytes: includeContent ? maximumAggregateResultBytes : MaximumRootResultBytes);

    static PostgresNpgsqlCommand StoreRoot(
        SqlCommandTemplate template,
        string authority,
        string instance,
        long? expectedRevision,
        long nextRevision,
        PostgresProcessDurablePagedAggregate aggregate)
    {
        Dictionary<string, PostgresNpgsqlParameter> parameters = new(StringComparer.Ordinal)
        {
            [AuthorityBinding] = Text(authority),
            [InstanceBinding] = Text(instance),
            [NextRevisionBinding] = new(nextRevision, PostgresRelationQueryScalarType.Int64, IsArray: false),
            [StorageFormatBinding] = Text(PostgresProcessDurableStorePaging.Format),
            [AggregateFingerprintBinding] = Text(aggregate.AggregateFingerprint),
            [AggregateBytesBinding] = new((long)aggregate.AggregateBytes, PostgresRelationQueryScalarType.Int64, IsArray: false),
            [PageManifestBinding] = Text(aggregate.Manifest),
            [PageCountBinding] = new(aggregate.Pages.Length, PostgresRelationQueryScalarType.Int32, IsArray: false)
        };
        if (expectedRevision is { } revision)
        {
            parameters.Add(
                ExpectedRevisionBinding,
                new(revision, PostgresRelationQueryScalarType.Int64, IsArray: false));
        }
        return Command(
            template: template,
            parameters: parameters,
            resultTypes: [PostgresRelationQueryScalarType.Int64],
            maximumResultBytes: MaximumRootResultBytes);
    }

    static PostgresNpgsqlCommand Command(
        SqlCommandTemplate template,
        IReadOnlyDictionary<string, PostgresNpgsqlParameter> parameters,
        ImmutableArray<PostgresRelationQueryScalarType> resultTypes,
        long maximumResultBytes)
    {
        var ordered = ImmutableArray.CreateBuilder<PostgresNpgsqlParameter>(template.Parameters.Length);
        foreach (var slot in template.Parameters)
        {
            if (slot.Binding is null || !parameters.TryGetValue(slot.Binding, out var parameter))
            {
                throw new InvalidOperationException(
                    $"The PostgreSQL Process-store SQL binding '{slot.Binding ?? "<constant>"}' is unavailable.");
            }
            ordered.Add(parameter);
        }
        if (parameters.Count != ordered.Count)
        {
            throw new InvalidOperationException(
                "The PostgreSQL Process-store SQL invocation supplied an unused parameter binding.");
        }
        return new(
            Text: template.Text,
            Parameters: ordered.MoveToImmutable(),
            ResultTypes: resultTypes,
            MaximumResultBytes: maximumResultBytes);
    }

    static PostgresNpgsqlParameter Text(string value) =>
        new(value, PostgresRelationQueryScalarType.Text, IsArray: false);

    static SqlExpression Equal(string? tableAlias, string column, string binding)
    {
        var left = tableAlias is null
            ? SqlExpression.UnqualifiedColumn(column)
            : SqlExpression.Column(tableAlias, column);
        return SqlExpression.Binary(
            @operator: SqlBinaryOperator.Equal,
            left: left,
            right: SqlExpression.RuntimeParameter(binding));
    }
}
