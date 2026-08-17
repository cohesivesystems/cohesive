using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cohesive.Adapters.Elastic;

/// <summary>
/// Owns one validated, single-line UTF-8 JSON object at the Elasticsearch adapter wire boundary.
/// </summary>
/// <remarks>
/// Instances own their byte storage. Serializer-produced instances avoid a parse-and-rewrite cycle, while
/// <see cref="Parse(ReadOnlyMemory{byte}, string)"/> validates and copies opaque caller input exactly once.
/// </remarks>
[JsonConverter(typeof(ElasticJsonObjectConverter))]
internal sealed class ElasticJsonObject
{
    readonly byte[] bytes;

    ElasticJsonObject(byte[] bytes, bool trustedSerializerOutput)
    {
        if (trustedSerializerOutput)
        {
            if (bytes.Length == 0)
            {
                throw new ArgumentException("An Elasticsearch JSON object cannot be empty.", nameof(bytes));
            }
            if (bytes.AsSpan().IndexOfAny((byte)'\r', (byte)'\n') >= 0)
            {
                throw new ArgumentException("An Elasticsearch JSON object must occupy one wire line.", nameof(bytes));
            }
            var content = TrimAsciiWhiteSpace(bytes.AsSpan());
            if (content.Length < 2 || content[0] != (byte)'{' || content[^1] != (byte)'}')
            {
                throw new ArgumentException("Serialized Elasticsearch wire content must be a JSON object.", nameof(bytes));
            }
        }

        this.bytes = bytes;
    }

    static ReadOnlySpan<byte> TrimAsciiWhiteSpace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhiteSpace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsAsciiWhiteSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    static bool IsAsciiWhiteSpace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>Gets the owned object bytes as read-only memory.</summary>
    internal ReadOnlyMemory<byte> Bytes => bytes;

    /// <summary>Gets the UTF-8 byte length.</summary>
    internal int Length => bytes.Length;

    /// <summary>Serializes a trusted POCO exactly once with the supplied serializer policy.</summary>
    internal static ElasticJsonObject Serialize<T>(T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        return new(JsonSerializer.SerializeToUtf8Bytes(value, options), trustedSerializerOutput: true);
    }

    /// <summary>Serializes a trusted wire DTO exactly once with source-generated metadata.</summary>
    internal static ElasticJsonObject Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), trustedSerializerOutput: true);
    }

    /// <summary>
    /// Validates an opaque UTF-8 JSON object and takes an exact defensive copy without normalizing its lexical form.
    /// </summary>
    internal static ElasticJsonObject Parse(ReadOnlyMemory<byte> value, string parameterName)
    {
        if (value.IsEmpty)
        {
            throw new ArgumentException("An Elasticsearch JSON object cannot be empty.", parameterName);
        }

        if (value.Span.IndexOfAny((byte)'\r', (byte)'\n') >= 0)
        {
            throw new ArgumentException("An Elasticsearch JSON object must occupy one wire line.", parameterName);
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Elasticsearch wire content must contain exactly one JSON object.", parameterName);
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Elasticsearch wire content must contain exactly one valid JSON object.", parameterName, exception);
        }

        return new(value.ToArray(), trustedSerializerOutput: false);
    }

    /// <summary>Returns a defensive copy of the owned UTF-8 object bytes.</summary>
    internal byte[] ToArray() => [.. bytes];

    /// <summary>Compares two optional UTF-8 JSON objects by their JSON value rather than their lexical encoding.</summary>
    internal static bool DeepEquals(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            return left.IsEmpty && right.IsEmpty;
        }

        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Embeds an already validated Elasticsearch JSON object without parsing or re-encoding it.</summary>
internal sealed class ElasticJsonObjectConverter : JsonConverter<ElasticJsonObject>
{
    public override ElasticJsonObject Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Elastic JSON objects are created through explicit serialization or parsing boundaries.");

    public override void Write(Utf8JsonWriter writer, ElasticJsonObject value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteRawValue(value.Bytes.Span, skipInputValidation: true);
    }
}

/// <summary>Typed construction of Elasticsearch materialization request objects.</summary>
internal static partial class ElasticMaterializationWireJson
{
    static readonly ElasticJsonObject MatchAll = Serialize(new MatchAllQueryBody(new()));
    static readonly ElasticJsonObject RemoveWriteBlock = Serialize(new RemoveWriteBlockBodyValue(null));

    internal static ElasticJsonObject MatchAllQuery => MatchAll;

    internal static ElasticJsonObject RemoveWriteBlockBody => RemoveWriteBlock;

    internal static ElasticJsonObject BooleanTermQuery(string field, bool value) =>
        Serialize(new BooleanTermQueryBody(new(StringComparer.Ordinal) { [RequireValue(field, nameof(field))] = value }));

    internal static ElasticJsonObject StringTermQuery(string field, string value) =>
        Serialize(new StringTermQueryBody(
            new(StringComparer.Ordinal) { [RequireValue(field, nameof(field))] = RequireValue(value, nameof(value)) }));

    internal static ElasticJsonObject FilteredQuery(ElasticJsonObject first, ElasticJsonObject second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return Serialize(new FilteredQueryBody(new([first, second])));
    }

    internal static ElasticJsonObject CreateControlIndexBody(
        string initialMarkerAlias,
        int maximumIndexedIdentityCharacters)
    {
        initialMarkerAlias = RequireValue(initialMarkerAlias, nameof(initialMarkerAlias));
        RequirePositive(maximumIndexedIdentityCharacters, nameof(maximumIndexedIdentityCharacters));
        return Serialize(new IndexCreateBody(
            new(Hidden: true, NumberOfShards: 1),
            new(
                Dynamic: false,
                new(StringComparer.Ordinal)
                {
                    ["documentKind"] = Keyword(),
                    ["generationId"] = Keyword(maximumIndexedIdentityCharacters),
                    ["retained"] = new(Type: "boolean")
                }),
            new(StringComparer.Ordinal) { [initialMarkerAlias] = new(IsHidden: true) }));
    }

    internal static ElasticJsonObject CreateGenerationIndexBody(
        string bindingFingerprint,
        string templateFingerprint,
        string generationId,
        string ownerAlias,
        int maximumIndexedIdentityCharacters)
    {
        bindingFingerprint = RequireValue(bindingFingerprint, nameof(bindingFingerprint));
        templateFingerprint = RequireValue(templateFingerprint, nameof(templateFingerprint));
        generationId = RequireValue(generationId, nameof(generationId));
        ownerAlias = RequireValue(ownerAlias, nameof(ownerAlias));
        RequirePositive(maximumIndexedIdentityCharacters, nameof(maximumIndexedIdentityCharacters));

        var metadataProperties = new Dictionary<string, FieldMapping>(StringComparer.Ordinal)
        {
            ["generationId"] = NonIndexedKeyword(),
            ["itemId"] = Keyword(maximumIndexedIdentityCharacters),
            ["mutationId"] = NonIndexedKeyword(),
            ["mutationFingerprint"] = NonIndexedKeyword(),
            ["version"] = new(Type: "long"),
            ["deleted"] = new(Type: "boolean")
        };
        return Serialize(new IndexCreateBody(
            new(Hidden: true),
            new(
                Dynamic: null,
                new(StringComparer.Ordinal)
                {
                    [ElasticMaterializationTargetBinding.MetadataField] = new(
                        Type: "object",
                        Dynamic: false,
                        Properties: metadataProperties)
                },
                Meta: new(StringComparer.Ordinal)
                {
                    ["cohesive_binding"] = bindingFingerprint,
                    ["cohesive_template"] = templateFingerprint,
                    ["cohesive_generation"] = generationId
                }),
            new(StringComparer.Ordinal) { [ownerAlias] = new(IsHidden: true) }));
    }

    internal static ElasticJsonObject CreateMultiGetBody(
        ImmutableArray<string> ids,
        ElasticMultiGetSourceProjection sourceProjection)
    {
        if (ids.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An Elasticsearch multi-get body requires at least one identity.", nameof(ids));
        }
        if (!Enum.IsDefined(sourceProjection))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceProjection), sourceProjection, "Unsupported source projection.");
        }

        foreach (var id in ids)
        {
            RequireValue(id, nameof(ids));
        }

        var source = sourceProjection == ElasticMultiGetSourceProjection.MaterializationMetadata
            ? new[] { ElasticMaterializationTargetBinding.MetadataField }
            : null;
        return Serialize(new MultiGetBody(
            [.. ids.Select(id => new MultiGetDocument(id, source))]));
    }

    internal static ElasticJsonObject CreateScanBody(ElasticScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Serialize(new ScanBody(
            checked(request.MaximumItems + 1),
            TrackTotalHits: false,
            Source: true,
            request.Query,
            [new(StringComparer.Ordinal) { [request.SortField] = "asc" }],
            request.AfterSortValue is null ? null : [request.AfterSortValue]));
    }

    internal static ElasticJsonObject CreateCountBody(ElasticJsonObject query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Serialize(new CountBody(query));
    }

    internal static ElasticJsonObject CreateDeleteOwnedIndexBody(string index, string ownerAlias) =>
        Serialize(new AliasActionsBody(
        [
            RemoveAlias(index, ownerAlias),
            new(RemoveIndex: new(RequireValue(index, nameof(index))))
        ]));

    internal static ElasticJsonObject CreateAliasBody(ElasticAliasCasRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<AliasAction> actions = [RemoveAlias(request.MarkerIndex, request.ExpectedMarkerAlias)];
        if (request.ExpectedNextOwnerAlias is { } ownerAlias
            && request.NextReadIndex is { } ownedNextIndex)
        {
            actions.Add(RemoveAlias(ownedNextIndex, ownerAlias));
            actions.Add(AddHiddenAlias(ownedNextIndex, ownerAlias));
        }
        if (request.ReadAlias is { } readAlias && request.ExpectedReadIndex is { } expectedReadIndex)
        {
            actions.Add(RemoveAlias(expectedReadIndex, readAlias));
        }
        if (request.ReadAlias is { } publishedAlias && request.NextReadIndex is { } nextReadIndex)
        {
            actions.Add(new(Add: new(
                nextReadIndex,
                publishedAlias,
                IsHidden: null,
                request.ReadAliasFilter,
                request.Routing,
                request.SearchRouting,
                request.IndexRouting,
                request.IsWriteIndex)));
        }
        actions.Add(AddHiddenAlias(request.MarkerIndex, request.NextMarkerAlias));
        return Serialize(new AliasActionsBody([.. actions]));
    }

    static AliasAction RemoveAlias(string index, string alias) =>
        new(Remove: new(RequireValue(index, nameof(index)), RequireValue(alias, nameof(alias)), MustExist: true));

    static AliasAction AddHiddenAlias(string index, string alias) =>
        new(Add: new(RequireValue(index, nameof(index)), RequireValue(alias, nameof(alias)), IsHidden: true));

    static FieldMapping Keyword(int? ignoreAbove = null) => new(Type: "keyword", IgnoreAbove: ignoreAbove);

    static FieldMapping NonIndexedKeyword() => new(Type: "keyword", Index: false, DocValues: false);

    static string RequireValue(string value, string parameterName) =>
        ElasticMaterializationPhysicalNames.RequireValue(value, parameterName);

    static int RequirePositive(int value, string parameterName) =>
        ElasticMaterializationPhysicalNames.RequirePositive(value, parameterName);

    static ElasticJsonObject Serialize<T>(T value)
    {
        var typeInfo = (JsonTypeInfo<T>?)WireJsonContext.Default.GetTypeInfo(typeof(T));
        return ElasticJsonObject.Serialize(
            value,
            typeInfo ?? throw new InvalidOperationException($"No generated Elasticsearch wire metadata exists for {typeof(T).Name}."));
    }

    sealed record EmptyObject;

    sealed record MatchAllQueryBody([property: JsonPropertyName("match_all")] EmptyObject MatchAll);

    sealed record BooleanTermQueryBody([property: JsonPropertyName("term")] Dictionary<string, bool> Term);

    sealed record StringTermQueryBody([property: JsonPropertyName("term")] Dictionary<string, string> Term);

    sealed record FilteredQueryBody([property: JsonPropertyName("bool")] FilterClause Bool);

    sealed record FilterClause([property: JsonPropertyName("filter")] ElasticJsonObject[] Filter);

    sealed record IndexCreateBody(
        [property: JsonPropertyName("settings")] IndexSettings Settings,
        [property: JsonPropertyName("mappings")] IndexMappings Mappings,
        [property: JsonPropertyName("aliases")] Dictionary<string, IndexAlias> Aliases);

    sealed record IndexSettings(
        [property: JsonPropertyName("index.hidden")] bool Hidden,
        [property: JsonPropertyName("number_of_shards")] int? NumberOfShards = null);

    sealed record IndexMappings(
        [property: JsonPropertyName("dynamic")] bool? Dynamic,
        [property: JsonPropertyName("properties")] Dictionary<string, FieldMapping> Properties,
        [property: JsonPropertyName("_meta")] Dictionary<string, string>? Meta = null);

    sealed record FieldMapping(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("dynamic")] bool? Dynamic = null,
        [property: JsonPropertyName("properties")] Dictionary<string, FieldMapping>? Properties = null,
        [property: JsonPropertyName("index")] bool? Index = null,
        [property: JsonPropertyName("doc_values")] bool? DocValues = null,
        [property: JsonPropertyName("ignore_above")] int? IgnoreAbove = null);

    sealed record IndexAlias([property: JsonPropertyName("is_hidden")] bool IsHidden);

    sealed record RemoveWriteBlockBodyValue(
        [property: JsonPropertyName("index.blocks.write"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        bool? WriteBlock);

    sealed record MultiGetBody(
        [property: JsonPropertyName("docs")] MultiGetDocument[] Documents);

    sealed record MultiGetDocument(
        [property: JsonPropertyName("_id")] string Id,
        [property: JsonPropertyName("_source")] string[]? Source);

    sealed record ScanBody(
        [property: JsonPropertyName("size")] int Size,
        [property: JsonPropertyName("track_total_hits")] bool TrackTotalHits,
        [property: JsonPropertyName("_source")] bool Source,
        [property: JsonPropertyName("query")] ElasticJsonObject Query,
        [property: JsonPropertyName("sort")] Dictionary<string, string>[] Sort,
        [property: JsonPropertyName("search_after")] string[]? SearchAfter);

    sealed record CountBody([property: JsonPropertyName("query")] ElasticJsonObject Query);

    sealed record AliasActionsBody([property: JsonPropertyName("actions")] AliasAction[] Actions);

    sealed record AliasAction(
        [property: JsonPropertyName("add")] AliasAdd? Add = null,
        [property: JsonPropertyName("remove")] AliasRemove? Remove = null,
        [property: JsonPropertyName("remove_index")] AliasRemoveIndex? RemoveIndex = null);

    sealed record AliasAdd(
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("alias")] string Alias,
        [property: JsonPropertyName("is_hidden")] bool? IsHidden = null,
        [property: JsonPropertyName("filter")] ElasticJsonObject? Filter = null,
        [property: JsonPropertyName("routing")] string? Routing = null,
        [property: JsonPropertyName("search_routing")] string? SearchRouting = null,
        [property: JsonPropertyName("index_routing")] string? IndexRouting = null,
        [property: JsonPropertyName("is_write_index")] bool? IsWriteIndex = null);

    sealed record AliasRemove(
        [property: JsonPropertyName("index")] string Index,
        [property: JsonPropertyName("alias")] string Alias,
        [property: JsonPropertyName("must_exist")] bool MustExist);

    sealed record AliasRemoveIndex([property: JsonPropertyName("index")] string Index);

    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        GenerationMode = JsonSourceGenerationMode.Serialization,
        WriteIndented = false)]
    [JsonSerializable(typeof(MatchAllQueryBody))]
    [JsonSerializable(typeof(BooleanTermQueryBody))]
    [JsonSerializable(typeof(StringTermQueryBody))]
    [JsonSerializable(typeof(FilteredQueryBody))]
    [JsonSerializable(typeof(IndexCreateBody))]
    [JsonSerializable(typeof(RemoveWriteBlockBodyValue))]
    [JsonSerializable(typeof(MultiGetBody))]
    [JsonSerializable(typeof(ScanBody))]
    [JsonSerializable(typeof(CountBody))]
    [JsonSerializable(typeof(AliasActionsBody))]
    sealed partial class WireJsonContext : JsonSerializerContext;
}
