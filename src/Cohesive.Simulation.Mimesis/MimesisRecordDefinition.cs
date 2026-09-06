using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Mimesis;

sealed class MimesisMemberBinding
{
    internal MimesisMemberBinding(FieldPath member, string providerField, JsonElement arguments)
    {
        Member = member;
        ProviderField = providerField;
        Arguments = arguments;
    }

    internal FieldPath Member { get; }

    internal string ProviderField { get; }

    internal JsonElement Arguments { get; }
}

/// <summary>Typed authoring projection of one declarative Mimesis record snapshot.</summary>
/// <typeparam name="T">CLR object type represented by each imported catalog entry.</typeparam>
public sealed class MimesisRecordDefinition<T>
{
    internal MimesisRecordDefinition(
        ObjectTypeRef valueType,
        JsonElement configuration)
    {
        ValueType = valueType;
        Configuration = configuration;
    }

    /// <summary>Gets the CLR-derived portable object contract expected from Mimesis.</summary>
    public ObjectTypeRef ValueType { get; }

    /// <summary>Gets the detached canonical provider configuration sent to the bundled Mimesis process.</summary>
    public JsonElement Configuration { get; }
}

/// <summary>Fluent typed producer of a declarative Mimesis record definition.</summary>
/// <typeparam name="T">CLR object type represented by each imported catalog entry.</typeparam>
/// <remarks>
/// The builder callback and CLR expressions execute only while authoring. <see cref="Build"/> lowers them to a
/// closed JSON configuration and portable type contract; no callback or reflection behavior crosses the process
/// boundary or survives in the retained catalog.
/// </remarks>
public sealed class MimesisRecordBuilder<T>
{
    static readonly JsonElement EmptyArguments = JsonSerializer.SerializeToElement(new { });
    static readonly JsonSerializerOptions ArgumentJson = new(JsonSerializerDefaults.Web);
    readonly List<MimesisMemberBinding> bindings = [];

    internal MimesisRecordBuilder()
    {
    }

    /// <summary>Binds one direct CLR property to a parameterless, fully qualified Mimesis field.</summary>
    /// <typeparam name="TValue">CLR value type returned by the selected property.</typeparam>
    /// <param name="member">Direct readable-property selector rooted at <typeparamref name="T"/>.</param>
    /// <param name="providerField">Fully qualified Mimesis field, such as <c>person.full_name</c>.</param>
    /// <returns>This builder for continued declaration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> or <paramref name="providerField"/> is null.</exception>
    /// <exception cref="ArgumentException">The selector or provider field is invalid, or the member is already bound.</exception>
    public MimesisRecordBuilder<T> Member<TValue>(
        Expression<Func<T, TValue>> member,
        string providerField) =>
        Add(member, providerField, EmptyArguments);

    /// <summary>Binds one direct CLR property to a fully qualified Mimesis field with typed keyword arguments.</summary>
    /// <typeparam name="TValue">CLR value type returned by the selected property.</typeparam>
    /// <typeparam name="TArguments">JSON-serializable argument object type.</typeparam>
    /// <param name="member">Direct readable-property selector rooted at <typeparamref name="T"/>.</param>
    /// <param name="providerField">Fully qualified Mimesis field, such as <c>person.email</c>.</param>
    /// <param name="arguments">
    /// Mimesis keyword arguments. Web JSON naming is applied, so an anonymous <c>Domains</c> property becomes
    /// <c>domains</c>; names containing underscores must be authored with those underscores explicitly.
    /// </param>
    /// <returns>This builder for continued declaration.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="member"/>, <paramref name="providerField"/>, or <paramref name="arguments"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The selector or provider field is invalid, the member is already bound, or arguments do not serialize as a
    /// duplicate-free JSON object.
    /// </exception>
    /// <exception cref="JsonException"><paramref name="arguments"/> cannot be serialized as JSON.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="TArguments"/> has no JSON serializer.</exception>
    public MimesisRecordBuilder<T> Member<TValue, TArguments>(
        Expression<Func<T, TValue>> member,
        string providerField,
        TArguments arguments)
        where TArguments : notnull
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return Add(member, providerField, JsonSerializer.SerializeToElement(arguments, ArgumentJson));
    }

    /// <summary>Binds one direct CLR property using an exact JSON keyword-argument object.</summary>
    /// <typeparam name="TValue">CLR value type returned by the selected property.</typeparam>
    /// <param name="member">Direct readable-property selector rooted at <typeparamref name="T"/>.</param>
    /// <param name="providerField">Fully qualified Mimesis field, such as <c>numeric.integer_number</c>.</param>
    /// <param name="arguments">Exact duplicate-free JSON object passed as Mimesis keyword arguments.</param>
    /// <returns>This builder for continued declaration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="member"/> or <paramref name="providerField"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The selector or provider field is invalid, the member is already bound, or <paramref name="arguments"/> is
    /// not a duplicate-free JSON object.
    /// </exception>
    public MimesisRecordBuilder<T> Member<TValue>(
        Expression<Func<T, TValue>> member,
        string providerField,
        JsonElement arguments) =>
        Add(member, providerField, arguments);

    /// <summary>Lowers the captured CLR authoring projection to an immutable provider definition.</summary>
    /// <returns>A definition with canonical binding order, configuration JSON, and a portable CLR-derived contract.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="T"/> is not a portable object type, no members are bound, a binding does not address a
    /// member in the inferred contract, or a required member is unbound.
    /// </exception>
    internal MimesisRecordDefinition<T> Build()
    {
        DefaultClrTypeRefMapper typeMapper = new();
        if (typeMapper.Map(typeof(T), nullability: null) is not ObjectTypeRef valueType
            || !IsPortable(valueType))
        {
            throw new ArgumentException(
                $"CLR type '{typeof(T).FullName}' must have a fully portable object contract.",
                nameof(T));
        }

        if (bindings.Count == 0)
            throw new ArgumentException("A Mimesis record definition requires at least one member binding.");

        var ordered = bindings.ToImmutableArray().Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Member.ToString(), right.Member.ToString()));
        Dictionary<string, MimesisMemberBinding> byMember = new(StringComparer.Ordinal);
        foreach (var binding in ordered)
        {
            _ = binding.Member.TryGetDirectFieldName(out var memberName);
            if (!byMember.TryAdd(memberName, binding))
                throw new ArgumentException($"Mimesis member '{memberName}' is bound more than once.");
            if (!valueType.Fields.Any(field => string.Equals(field.Name, memberName, StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Mimesis member '{memberName}' is absent from CLR type '{typeof(T).FullName}'.");
            }
        }

        foreach (var field in valueType.Fields)
        {
            if (field.Presence == FieldPresence.Required && !byMember.ContainsKey(field.Name))
            {
                throw new ArgumentException(
                    $"Required CLR member '{field.Name}' has no Mimesis field binding.");
            }
        }

        return new(valueType, CreateConfiguration(ordered));
    }

    MimesisRecordBuilder<T> Add<TValue>(
        Expression<Func<T, TValue>> member,
        string providerField,
        JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(member);
        var boxed = Expression.Lambda<Func<T, object?>>(
            Expression.Convert(member.Body, typeof(object)),
            member.Parameters);
        var path = FieldPath.Capture(boxed);
        if (!path.TryGetDirectFieldName(out var memberName))
        {
            throw new ArgumentException(
                "A Mimesis member selector must be a direct CLR field or property access.",
                nameof(member));
        }
        if (bindings.Any(binding =>
                binding.Member.TryGetDirectFieldName(out var existing)
                && string.Equals(existing, memberName, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Mimesis member '{memberName}' is already bound.", nameof(member));
        }

        bindings.Add(new(
            path,
            ValidateProviderField(providerField),
            NormalizeArguments(arguments, nameof(arguments))));
        return this;
    }

    static JsonElement CreateConfiguration(ImmutableArray<MimesisMemberBinding> bindings)
    {
        JsonArray members = [];
        foreach (var binding in bindings)
        {
            _ = binding.Member.TryGetDirectFieldName(out var fieldName);
            members.Add(new JsonObject
            {
                ["arguments"] = JsonNode.Parse(binding.Arguments.GetRawText()),
                ["field"] = binding.ProviderField,
                ["path"] = new JsonArray(fieldName)
            });
        }

        JsonObject configuration = new()
        {
            ["members"] = members,
            ["schemaVersion"] = MimesisGenerationCatalog.ConfigurationSchemaVersion
        };
        var canonical = CanonicalJsonWriter.GetCanonicalSequenceBytes(
            configuration,
            StrictDocumentJson.CreateOptions());
        using var document = JsonDocument.Parse(canonical);
        return document.RootElement.Clone();
    }

    static JsonElement NormalizeArguments(JsonElement arguments, string parameterName)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Mimesis field arguments must be a JSON object.", parameterName);
        if (StrictDocumentJson.TryFindDuplicateProperty(arguments, "/arguments", out var duplicateLocation))
        {
            throw new ArgumentException(
                $"Mimesis field arguments contain a duplicate property at '{duplicateLocation}'.",
                parameterName);
        }

        return arguments.Clone();
    }

    static string ValidateProviderField(string providerField)
    {
        providerField = Guard.RequireNotNullOrWhiteSpace(providerField);
        var segments = providerField.Split('.');
        if (segments.Length < 2 || segments.Any(static segment => !IsIdentifier(segment)))
        {
            throw new ArgumentException(
                "A Mimesis provider field must be fully qualified using identifier segments, such as 'person.full_name'.",
                nameof(providerField));
        }

        return providerField;

        static bool IsIdentifier(string value)
        {
            if (value.Length == 0
                || value.StartsWith("__", StringComparison.Ordinal)
                || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
            {
                return false;
            }

            foreach (var character in value.AsSpan(1))
            {
                if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
                    return false;
            }
            return true;
        }
    }

    static bool IsPortable(TypeRef type) => type switch
    {
        OpaqueRuntimeTypeRef => false,
        ArrayTypeRef array => IsPortable(array.ElementType),
        ObjectTypeRef obj => obj.Fields.All(static field => IsPortable(field.Type)),
        _ => true
    };
}
