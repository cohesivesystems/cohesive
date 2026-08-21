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

    readonly PostgresSqlCommandTemplate loadRoot;
    readonly PostgresSqlCommandTemplate loadPages;
    readonly PostgresSqlCommandTemplate findPages;
    readonly PostgresSqlCommandTemplate insertPage;
    readonly PostgresSqlCommandTemplate insertRoot;
    readonly PostgresSqlCommandTemplate updateRoot;
    readonly PostgresSqlCommandTemplate providerNow;
    readonly long maximumAggregateResultBytes;

    internal PostgresProcessDurableStoreSql(PostgresProcessDurableStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        maximumAggregateResultBytes = options.MaximumAggregateBytes ?? long.MaxValue;
        loadRoot = new PostgresSqlSelectBuilder(options.Instances, InstanceAlias)
            .Select(PostgresSqlExpression.Column(InstanceAlias, "physical_revision"), "physical_revision")
            .Select(PostgresSqlExpression.Column(InstanceAlias, "storage_format"), "storage_format")
            .Select(PostgresSqlExpression.Column(InstanceAlias, "aggregate_fingerprint"), "aggregate_fingerprint")
            .Select(PostgresSqlExpression.Column(InstanceAlias, "aggregate_bytes"), "aggregate_bytes")
            .Select(PostgresSqlExpression.Column(InstanceAlias, "page_manifest"), "page_manifest")
            .Select(PostgresSqlExpression.Column(InstanceAlias, "page_count"), "page_count")
            .Where(Equal(InstanceAlias, "authority_id", AuthorityBinding))
            .Where(Equal(InstanceAlias, "instance_id", InstanceBinding))
            .BuildTemplate();
        loadPages = CreatePageLookup(options, includeContent: true);
        findPages = CreatePageLookup(options, includeContent: false);
        insertPage = new PostgresSqlInsertBuilder(options.Pages)
            .Value("authority_id", PostgresSqlExpression.RuntimeParameter(AuthorityBinding))
            .Value("page_fingerprint", PostgresSqlExpression.RuntimeParameter(PageFingerprintBinding))
            .Value("content", PostgresSqlExpression.RuntimeParameter(PageContentBinding))
            .Value("content_bytes", PostgresSqlExpression.RuntimeParameter(PageBytesBinding))
            .Value("created_at", PostgresSqlExpression.Function(PostgresSqlFunction.ClockTimestamp))
            .OnConflictDoNothing(["authority_id", "page_fingerprint"])
            .Returning(PostgresSqlExpression.UnqualifiedColumn("page_fingerprint"), "page_fingerprint")
            .BuildTemplate();
        insertRoot = new PostgresSqlInsertBuilder(options.Instances)
            .Value("authority_id", PostgresSqlExpression.RuntimeParameter(AuthorityBinding))
            .Value("instance_id", PostgresSqlExpression.RuntimeParameter(InstanceBinding))
            .Value("physical_revision", PostgresSqlExpression.RuntimeParameter(NextRevisionBinding))
            .Value("storage_format", PostgresSqlExpression.RuntimeParameter(StorageFormatBinding))
            .Value("aggregate_fingerprint", PostgresSqlExpression.RuntimeParameter(AggregateFingerprintBinding))
            .Value("aggregate_bytes", PostgresSqlExpression.RuntimeParameter(AggregateBytesBinding))
            .Value("page_manifest", PostgresSqlExpression.RuntimeParameter(PageManifestBinding))
            .Value("page_count", PostgresSqlExpression.RuntimeParameter(PageCountBinding))
            .Value("updated_at", PostgresSqlExpression.Function(PostgresSqlFunction.ClockTimestamp))
            .OnConflictDoNothing(["authority_id", "instance_id"])
            .Returning(PostgresSqlExpression.UnqualifiedColumn("physical_revision"), "physical_revision")
            .BuildTemplate();
        updateRoot = new PostgresSqlUpdateBuilder(options.Instances)
            .Set("physical_revision", PostgresSqlExpression.RuntimeParameter(NextRevisionBinding))
            .Set("storage_format", PostgresSqlExpression.RuntimeParameter(StorageFormatBinding))
            .Set("aggregate_fingerprint", PostgresSqlExpression.RuntimeParameter(AggregateFingerprintBinding))
            .Set("aggregate_bytes", PostgresSqlExpression.RuntimeParameter(AggregateBytesBinding))
            .Set("page_manifest", PostgresSqlExpression.RuntimeParameter(PageManifestBinding))
            .Set("page_count", PostgresSqlExpression.RuntimeParameter(PageCountBinding))
            .Set("updated_at", PostgresSqlExpression.Function(PostgresSqlFunction.ClockTimestamp))
            .Where(Equal(tableAlias: null, "authority_id", AuthorityBinding))
            .Where(Equal(tableAlias: null, "instance_id", InstanceBinding))
            .Where(Equal(tableAlias: null, "physical_revision", ExpectedRevisionBinding))
            .Returning(PostgresSqlExpression.UnqualifiedColumn("physical_revision"), "physical_revision")
            .BuildTemplate();
        providerNow = new PostgresSqlSelectBuilder()
            .Select(PostgresSqlExpression.Function(PostgresSqlFunction.ClockTimestamp), "provider_now")
            .BuildTemplate();
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

    static PostgresSqlCommandTemplate CreatePageLookup(
        PostgresProcessDurableStoreOptions options,
        bool includeContent)
    {
        var builder = new PostgresSqlSelectBuilder(options.Pages, PageAlias)
            .Select(PostgresSqlExpression.Column(PageAlias, "page_fingerprint"), "page_fingerprint");
        if (includeContent)
        {
            builder.Select(PostgresSqlExpression.Column(PageAlias, "content"), "content");
            builder.Select(PostgresSqlExpression.Column(PageAlias, "content_bytes"), "content_bytes");
        }
        return builder
            .Where(Equal(PageAlias, "authority_id", AuthorityBinding))
            .Where(PostgresSqlExpression.EqualAny(
                PostgresSqlExpression.Column(PageAlias, "page_fingerprint"),
                PageFingerprintsBinding))
            .OrderBy(PostgresSqlExpression.Column(PageAlias, "page_fingerprint"))
            .BuildTemplate();
    }

    PostgresNpgsqlCommand PageLookup(
        PostgresSqlCommandTemplate template,
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
        PostgresSqlCommandTemplate template,
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
        PostgresSqlCommandTemplate template,
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

    static PostgresSqlExpression Equal(string? tableAlias, string column, string binding)
    {
        var left = tableAlias is null
            ? PostgresSqlExpression.UnqualifiedColumn(column)
            : PostgresSqlExpression.Column(tableAlias, column);
        return PostgresSqlExpression.Binary(
            @operator: PostgresSqlBinaryOperator.Equal,
            left: left,
            right: PostgresSqlExpression.RuntimeParameter(binding));
    }
}
