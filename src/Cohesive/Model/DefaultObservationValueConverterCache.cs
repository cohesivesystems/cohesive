using System.Buffers.Text;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Compiles allocation-light conversions for the framework's fixed default observation materialization contract.
/// Customized serializer contracts deliberately remain on the JSON compatibility path.
/// </summary>
static class DefaultObservationValueConverterCache
{
    static readonly WeakPreparationCache<Type, ConverterEntry> Converters = new();
    static readonly WeakPreparationCache<Type, ConverterEntry> StrictConverters = new();

    public static Delegate? Get(Type targetType) =>
        Converters.Get(
            targetType,
            static currentType => new(new ConverterBuilder().Build(currentType)))
        .Converter;

    internal static Delegate? GetStrict(Type targetType) => StrictConverters.Get(targetType,
        static type => new(new ConverterBuilder(strict: true).Build(type))).Converter;

    // The hosted adapter owns this frozen, case-sensitive CLR profile and its preparation lifetime.
    // Consumer-configured profiles retain the serializer compatibility path.
    internal static Delegate? PrepareClrContract(Type targetType, JsonSerializerOptions options) =>
        // These policies can also be enabled by host feature switches. Their complete checks
        // remain serializer-owned rather than being partially reproduced by field materialization.
        options.RespectNullableAnnotations || options.RespectRequiredConstructorParameters
            ? null : new ConverterBuilder(strict: true, options).Build(targetType);

    sealed record ConverterEntry(Delegate? Converter);

    // Mutable CLR output must own its bytes; keep the default JSON compatibility fallback in one place.
    public static byte[]? ReadBytes(ObservationValue value) =>
        value.Kind == ObservationValueKind.Bytes
            ? value.Bytes.ToArray()
            : value.Deserialize<byte[]>(ObservationMaterializerDefaults.SerializerOptions);

    sealed class ConverterBuilder(bool strict = false, JsonSerializerOptions? options = null)
    {
        static readonly MethodInfo ReadArrayMethod = GetGenericMethod(nameof(ReadArray));
        static readonly MethodInfo ReadNullableMethod = GetGenericMethod(nameof(ReadNullable));
        static readonly MethodInfo ReadObjectPropertyMethod = GetGenericMethod(nameof(ReadObjectProperty));
        static readonly MethodInfo RequireObjectMethod = typeof(ConverterBuilder).GetMethod(
            nameof(RequireObject),
            BindingFlags.Static | BindingFlags.NonPublic)!;

        readonly Dictionary<Type, Delegate?> converters = [];
        readonly HashSet<Type> building = [];

        public Delegate? Build(Type targetType)
        {
            if (converters.TryGetValue(targetType, out var existing))
            {
                return existing;
            }

            if (!building.Add(targetType))
            {
                return null;
            }

            try
            {
                var value = Expression.Parameter(typeof(ObservationValue), "value");
                var body = BuildExpression(targetType, value);
                // Keep a customized or unsupported member on its JSON contract without
                // forcing its otherwise conventional parent through a full JSON round trip.
                if (body is null && strict && !targetType.IsPointer && !targetType.IsByRefLike && !targetType.IsByRef)
                    body = DeserializeExpression(targetType, value);
                if (body is null)
                {
                    converters[targetType] = null;
                    return null;
                }

                var delegateType = ConverterType(targetType);
                var converter = Expression.Lambda(delegateType, body, value).Compile();
                converters[targetType] = converter;
                return converter;
            }
            finally
            {
                building.Remove(targetType);
            }
        }

        Expression? BuildExpression(Type targetType, Expression value)
        {
            if (options is not null && typeof(IImmutableObservationProjection).IsAssignableFrom(targetType))
                return null; // The owning payload caches only explicit deeply immutable interpretations.
            if (options is not null && targetType.IsEnum && SerializedEnumMemberCatalog.TryCreate(targetType,
                out var members, out _, out _))
            {
                // The serializer remains the authority. This is only an exact-literal fast path;
                // flags, compatibility spellings and undefined numeric values keep its full reader.
                var literals = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var wire in members!.WireMembers)
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(wire), targetType, options);
                        if (parsed is not null) literals[wire] = parsed;
                    }
                    catch (JsonException) { }
                }
                return Expression.Call(GetGenericMethod(nameof(ReadPreparedEnum)).MakeGenericMethod(targetType),
                    value, Expression.Constant(literals, typeof(IReadOnlyDictionary<string, object>)),
                    Expression.Constant(options));
            }

            if (targetType == typeof(ObservationValue)) return strict ? null : value;

            if (targetType == typeof(string))
            {
                return CallConverter(value, nameof(ReadString));
            }

            if (targetType == typeof(byte[])) return strict ? null : CallConverter(value, nameof(ReadBytes));

            if (targetType == typeof(bool))
            {
                return CallConverter(value, nameof(ReadBoolean));
            }

            if (targetType == typeof(int))
            {
                return CallConverter(value, nameof(ReadInt32));
            }

            if (targetType == typeof(long))
            {
                return CallConverter(value, nameof(ReadInt64));
            }

            if (targetType == typeof(double))
            {
                return CallConverter(value, nameof(ReadDouble));
            }

            if (targetType == typeof(decimal))
            {
                return CallConverter(value, nameof(ReadDecimal));
            }

            if (targetType == typeof(Guid))
            {
                return CallConverter(value, nameof(ReadGuid));
            }
            if (targetType == typeof(DateOnly))
            {
                return CallConverter(value, nameof(ReadDateOnly));
            }

            if (targetType == typeof(TimeOnly))
            {
                return CallConverter(value, nameof(ReadTimeOnly));
            }

            if (targetType == typeof(DateTimeOffset))
            {
                return CallConverter(value, nameof(ReadDateTimeOffset));
            }

            if (targetType == typeof(TimeSpan))
            {
                return CallConverter(value, nameof(ReadTimeSpan));
            }

            var nullableElement = Nullable.GetUnderlyingType(targetType);
            if (nullableElement is not null)
            {
                var elementConverter = Build(nullableElement);
                return elementConverter is null
                    ? null
                    : Expression.Call(
                        ReadNullableMethod.MakeGenericMethod(nullableElement),
                        value,
                        ConverterConstant(nullableElement, elementConverter));
            }

            if (targetType.IsArray && targetType.GetArrayRank() == 1)
            {
                var elementType = targetType.GetElementType()!;
                var elementConverter = Build(elementType);
                return elementConverter is null ? null : StrictContainer(targetType, value,
                    Expression.Call(ReadArrayMethod.MakeGenericMethod(elementType), value,
                        ConverterConstant(elementType, elementConverter)), ObservationValueKind.Array);
            }

            if (strict && targetType.IsGenericType && !HasJsonContractCustomization(targetType))
            {
                var definition = targetType.GetGenericTypeDefinition();
                if (definition == typeof(List<>) || definition == typeof(IReadOnlyList<>)
                    || definition == typeof(IReadOnlyCollection<>) || definition == typeof(IList<>)
                    || definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>))
                {
                    var element = targetType.GetGenericArguments()[0];
                    var converter = Build(element);
                    if (converter is not null)
                        return StrictContainer(targetType, value, Expression.Convert(
                            Expression.Call(GetGenericMethod(nameof(ReadList)).MakeGenericMethod(element),
                                value, ConverterConstant(element, converter)), targetType), ObservationValueKind.Array);
                }
            }

            return BuildConventionalObject(targetType, value);
        }

        Expression? BuildConventionalObject(Type targetType, Expression value)
        {
            if (targetType == typeof(object)
                || targetType.IsAbstract
                || targetType.IsInterface
                || targetType.IsPointer
                || targetType.IsByRefLike
                || typeof(IEnumerable).IsAssignableFrom(targetType)
                || HasJsonContractCustomization(targetType)
                || (options is not null && options.GetTypeInfo(targetType).Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object)
                || (strict && (typeof(IJsonOnDeserializing).IsAssignableFrom(targetType)
                    || typeof(IJsonOnDeserialized).IsAssignableFrom(targetType))))
            {
                return null;
            }

            if (options is not null && (options.GetTypeInfo(targetType).Properties.Any(static property => property.AttributeProvider is not PropertyInfo)
                || targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(static property =>
                    property.GetMethod?.IsPublic != true && property.SetMethod?.IsPublic == true)))
                return null;

            var properties = ShapeTypeInspector.GetReadableProperties(targetType);
            if (properties.Length == 0
                || properties.Any(HasJsonContractCustomization)
                || (strict && properties.Any(static property => property.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))))
            {
                return null;
            }

            var declaredConstructor = options is null ? null : targetType
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SingleOrDefault(static constructor => constructor.IsDefined(typeof(JsonConstructorAttribute)));
            if (declaredConstructor is { IsPublic: false }) return null;

            // Plain JSON prefers a public parameterless constructor and public setters.
            // Keep customized and write-only contracts on the serializer compatibility path.
            if (strict && declaredConstructor is null && targetType.GetConstructor(Type.EmptyTypes) is { } emptyConstructor)
            {
                if (targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Any(static property => property.GetMethod is null && property.SetMethod?.IsPublic == true)) return null;
                var instance = Expression.Variable(targetType, "instance");
                var statements = new List<Expression>
                {
                    RequireExpression(targetType, value, properties),
                    Expression.Assign(instance, Expression.New(emptyConstructor))
                };
                foreach (var property in properties.OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (property.SetMethod?.IsPublic != true) continue;
                    var converter = Build(property.PropertyType);
                    if (converter is null) return null;
                    // An absent property must keep its constructor/initializer value and skip its setter.
                    var fields = Expression.Property(value, nameof(ObservationValue.Fields));
                    var present = Expression.Call(fields, typeof(IReadOnlyDictionary<string, ObservationValue>)
                        .GetMethod(nameof(IReadOnlyDictionary<string, ObservationValue>.ContainsKey))!, Expression.Constant(property.Name));
                    statements.Add(Expression.IfThen(present, Expression.Assign(Expression.Property(instance, property),
                        ReadProperty(value, property.Name, property.PropertyType, converter, false, null))));
                }
                statements.Add(instance);
                Expression mutableProjection = Expression.Block([instance], statements);
                var result = targetType.IsValueType ? mutableProjection : Expression.Condition(
                    Expression.Equal(Expression.Property(value, nameof(ObservationValue.Kind)),
                        Expression.Constant(ObservationValueKind.Null)), Expression.Default(targetType), mutableProjection);
                return StrictContainer(targetType, value, result, ObservationValueKind.Object);
            }

            if (properties.Select(static property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.Length)
                return null;

            var byProperty = properties.ToDictionary(
                static property => property.Name,
                StringComparer.OrdinalIgnoreCase);
            var constructors = targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (declaredConstructor is null && (constructors.Length != 1 || constructors.Any(HasJsonContractCustomization)))
                return null;
            var constructor = declaredConstructor ?? constructors[0];
            if (options is not null && constructor.GetCustomAttributes().Any(static attribute =>
                attribute.GetType().Namespace == typeof(JsonConstructorAttribute).Namespace
                    && attribute is not JsonConstructorAttribute)) return null;
            var constructorParameters = constructor.GetParameters();
            if (constructorParameters.Length != properties.Length && (options is null
                || properties.Any(property => property.SetMethod is not null
                    && !constructorParameters.Any(parameter => string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase)))))
            {
                return null;
            }

            var constructorArguments = new Expression[constructorParameters.Length];
            HashSet<string> constructorProperties = new(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < constructorParameters.Length; index++)
            {
                var parameter = constructorParameters[index];
                if (!byProperty.TryGetValue(parameter.Name ?? string.Empty, out var property)
                    || property.PropertyType != parameter.ParameterType
                    || !constructorProperties.Add(property.Name))
                {
                    return null;
                }

                var converter = Build(parameter.ParameterType);
                if (converter is null)
                {
                    return null;
                }

                constructorArguments[index] = ReadProperty(
                    value,
                    property.Name,
                    parameter.ParameterType,
                    converter,
                    parameter.HasDefaultValue,
                    parameter.HasDefaultValue ? parameter.DefaultValue : null);
            }

            Expression materialize;
            if (options is not null)
            {
                // The existing JSON writer orders fields ordinally. Preserve member-converter
                // evaluation order even when constructor parameters use another order.
                var locals = constructorParameters.Select((parameter, index) =>
                    Expression.Variable(parameter.ParameterType, $"argument{index}")).ToArray();
                var reads = Enumerable.Range(0, locals.Length)
                    .OrderBy(index => byProperty[constructorParameters[index].Name!].Name, StringComparer.Ordinal)
                    .Select(index => (Expression)Expression.Assign(locals[index], constructorArguments[index]));
                materialize = Expression.Block(locals,
                    new[] { RequireExpression(targetType, value, properties) }.Concat(reads)
                        .Append(Expression.New(constructor, locals)));
            }
            else materialize = Expression.Block(RequireExpression(targetType, value, properties),
                Expression.New(constructor, constructorArguments));
            if (!targetType.IsValueType)
            {
                materialize = Expression.Condition(
                    Expression.Equal(
                        Expression.Property(value, nameof(ObservationValue.Kind)),
                        Expression.Constant(ObservationValueKind.Null)),
                    Expression.Default(targetType),
                    materialize);
            }
            return StrictContainer(targetType, value, materialize, ObservationValueKind.Object);
        }

        Expression StrictContainer(Type target, Expression value, Expression direct, ObservationValueKind expected)
        {
            if (!strict) return direct;
            var kind = Expression.Property(value, nameof(ObservationValue.Kind));
            Expression matches = Expression.Equal(kind, Expression.Constant(expected));
            if (!target.IsValueType) matches = Expression.OrElse(matches,
                Expression.Equal(kind, Expression.Constant(ObservationValueKind.Null)));
            return Expression.Condition(matches, direct,
                DeserializeExpression(target, value));
        }

        MethodCallExpression ReadProperty(
            Expression value,
            string propertyName,
            Type propertyType,
            Delegate converter,
            bool hasExplicitDefault,
            object? explicitDefault) =>
            Expression.Call(
                ReadObjectPropertyMethod.MakeGenericMethod(propertyType),
                value,
                Expression.Constant(propertyName),
                ConverterConstant(propertyType, converter),
                Expression.Constant(hasExplicitDefault),
                Expression.Constant(explicitDefault, typeof(object)),
                Expression.Constant(strict));

        static ConstantExpression ConverterConstant(Type targetType, Delegate converter) =>
            Expression.Constant(converter, ConverterType(targetType));

        static Type ConverterType(Type targetType) =>
            typeof(Func<,>).MakeGenericType(typeof(ObservationValue), targetType);

        Expression CallConverter(Expression value, string methodName)
        {
            var direct = Expression.Call(typeof(ConverterBuilder), methodName, typeArguments: null, value);
            if (!strict || direct.Type is not { } target
                || (target != typeof(int) && target != typeof(long) && target != typeof(double) && target != typeof(decimal)))
                return direct;
            var kind = Expression.Property(value, nameof(ObservationValue.Kind));
            var stringValue = Expression.Equal(kind, Expression.Constant(ObservationValueKind.String));
            var invalidDouble = Expression.AndAlso(
                Expression.Equal(kind, Expression.Constant(ObservationValueKind.Double)),
                Expression.Not(Expression.Call(typeof(double), nameof(double.IsFinite), null,
                    Expression.Property(value, nameof(ObservationValue.Double)))));
            var fallback = DeserializeExpression(target, value);
            return Expression.Condition(Expression.OrElse(stringValue, invalidDouble), fallback, direct);
        }

        Expression RequireExpression(Type target, Expression value, PropertyInfo[] properties) => options is null
            ? Expression.Call(RequireObjectMethod, value)
            : Expression.Call(GetGenericMethod(nameof(RequireContractObject)).MakeGenericMethod(target), value,
                Expression.Constant(properties.Select(static property => property.Name).ToHashSet(StringComparer.Ordinal)));

        Expression DeserializeExpression(Type target, Expression value) => Expression.Call(
            GetGenericMethod(nameof(DeserializeStrict)).MakeGenericMethod(target), value,
            Expression.Constant(options, typeof(JsonSerializerOptions)));

        static void RequireContractObject<T>(ObservationValue value, HashSet<string> knownNames)
        {
            RequireObject(value);
            var visitor = new KnownNamesVisitor(knownNames);
            ObservationFieldTraversal.Visit(value.Fields!, ref visitor);
            if (visitor.UnknownName is { } name)
                throw new JsonException($"JSON property '{name}' is not mapped on '{typeof(T)}'.");
        }

        struct KnownNamesVisitor(HashSet<string> knownNames) : IObservationFieldVisitor
        {
            internal string? UnknownName;
            public void Visit<TEnumerator>(TEnumerator fields, bool canonical)
                where TEnumerator : IEnumerator<KeyValuePair<string, ObservationValue>>
            {
                using (fields)
                    while (fields.MoveNext())
                        if (!knownNames.Contains(fields.Current.Key)
                            && (UnknownName is null || StringComparer.Ordinal.Compare(fields.Current.Key, UnknownName) < 0))
                            UnknownName = fields.Current.Key;
            }
        }

        static TEnum ReadPreparedEnum<TEnum>(ObservationValue value, IReadOnlyDictionary<string, object> literals,
            JsonSerializerOptions options) where TEnum : struct, Enum =>
            value.Kind == ObservationValueKind.String && value.String is { } text && literals.TryGetValue(text, out var literal)
                ? (TEnum)literal : value.Deserialize<TEnum>(options);

        static T DeserializeStrict<T>(ObservationValue value, JsonSerializerOptions? options) => value.Deserialize<T>(options)!;

        static bool HasJsonContractCustomization(MemberInfo member) =>
            member.GetCustomAttributes(inherit: true).Any(static attribute =>
                attribute.GetType().Namespace == typeof(JsonConverterAttribute).Namespace);

        static MethodInfo GetGenericMethod(string name) =>
            typeof(ConverterBuilder).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Observation value converter method '{name}' was not found.");

        static T? ReadNullable<T>(ObservationValue value, Func<ObservationValue, T> converter)
            where T : struct =>
            value.Kind == ObservationValueKind.Null ? null : converter(value);

        static TElement[]? ReadArray<TElement>(
            ObservationValue value,
            Func<ObservationValue, TElement> converter)
        {
            if (value.Kind == ObservationValueKind.Null)
            {
                return null;
            }

            var items = value.EnumerateArray();
            var result = new TElement[items.Length];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = converter(items[index]);
            }

            return result;
        }

        static List<TElement>? ReadList<TElement>(ObservationValue value, Func<ObservationValue, TElement> converter)
        {
            if (value.Kind == ObservationValueKind.Null) return null;
            var items = value.EnumerateArray();
            var result = new List<TElement>(items.Length);
            for (var index = 0; index < items.Length; index++) result.Add(converter(items[index]));
            return result;
        }

        static TProperty ReadObjectProperty<TProperty>(
            ObservationValue value,
            string propertyName,
            Func<ObservationValue, TProperty> converter,
            bool hasExplicitDefault,
            object? explicitDefault, bool caseSensitive)
        {
            var present = caseSensitive
                ? value.Fields!.TryGetValue(propertyName, out var propertyValue)
                : TryGetPropertyIgnoreCase(value.Fields!, propertyName, out propertyValue);
            if (!present)
            {
                if (!hasExplicitDefault || explicitDefault is null)
                {
                    return default!;
                }

                return (TProperty)explicitDefault;
            }

            return converter(propertyValue);
        }

        static string? ReadString(ObservationValue value) =>
            value.Kind is ObservationValueKind.String
                or ObservationValueKind.DateTimeOffset
                or ObservationValueKind.DateOnly
                or ObservationValueKind.TimeOnly
                or ObservationValueKind.TimeSpan
                or ObservationValueKind.Null
                ? value.GetString()
                : DeserializeDefault<string>(value);

        static bool ReadBoolean(ObservationValue value) =>
            value.Kind == ObservationValueKind.Bool
                ? value.Bool
                : DeserializeDefault<bool>(value);

        static byte[]? ReadBytes(ObservationValue value) => DefaultObservationValueConverterCache.ReadBytes(value);

        static int ReadInt32(ObservationValue value) =>
            value.Kind == ObservationValueKind.Int64
                && value.Int64 is >= int.MinValue and <= int.MaxValue
                ? (int)value.Int64
                : DeserializeDefault<int>(value);

        static long ReadInt64(ObservationValue value) =>
            value.Kind == ObservationValueKind.Int64
                ? value.Int64
                : DeserializeDefault<long>(value);

        static double ReadDouble(ObservationValue value)
        {
            if (value.Kind == ObservationValueKind.Decimal)
            {
                // Decimal's direct binary cast can differ by one ULP from the JSON reader.
                // Interpret the same numeric token without a string or heap buffer.
                Span<byte> token = stackalloc byte[64];
                if (Utf8Formatter.TryFormat(value.Decimal, token, out var length))
                {
                    var reader = new Utf8JsonReader(token[..length]);
                    reader.Read();
                    return reader.GetDouble();
                }
            }
            return value.Kind is ObservationValueKind.Int64 or ObservationValueKind.Double
                ? value.GetDouble() : DeserializeDefault<double>(value);
        }

        static decimal ReadDecimal(ObservationValue value)
        {
            if (value.Kind == ObservationValueKind.Double && double.IsFinite(value.Double))
            {
                Span<byte> token = stackalloc byte[64];
                if (Utf8Formatter.TryFormat(value.Double, token, out var length))
                {
                    var reader = new Utf8JsonReader(token[..length]);
                    reader.Read();
                    if (reader.TryGetDecimal(out var parsed)) return parsed;
                }
            }
            else if (value.Kind is ObservationValueKind.Int64 or ObservationValueKind.Decimal
                && value.TryGetCanonicalNumericDecimal(out var direct)) return direct;
            return DeserializeDefault<decimal>(value);
        }

        static Guid ReadGuid(ObservationValue value) =>
            value.Kind == ObservationValueKind.String
                && Guid.TryParseExact(value.String, "D", out var direct)
                ? direct
                : DeserializeDefault<Guid>(value);

        static DateOnly ReadDateOnly(ObservationValue value) =>
            value.Kind == ObservationValueKind.DateOnly
                ? value.GetDateOnly()
                : DeserializeDefault<DateOnly>(value);

        static TimeOnly ReadTimeOnly(ObservationValue value) =>
            value.Kind == ObservationValueKind.TimeOnly
                ? value.GetTimeOnly()
                : DeserializeDefault<TimeOnly>(value);

        static DateTimeOffset ReadDateTimeOffset(ObservationValue value) =>
            value.Kind == ObservationValueKind.DateTimeOffset
                ? value.GetDateTimeOffset()
                : DeserializeDefault<DateTimeOffset>(value);

        static TimeSpan ReadTimeSpan(ObservationValue value) =>
            value.Kind == ObservationValueKind.TimeSpan
                ? value.GetTimeSpan()
                : DeserializeDefault<TimeSpan>(value);

        static T DeserializeDefault<T>(ObservationValue value) =>
            value.Deserialize<T>(ObservationMaterializerDefaults.SerializerOptions)!;

        static void RequireObject(ObservationValue value)
        {
            if (value.Kind != ObservationValueKind.Object || value.Fields is null)
            {
                throw new InvalidOperationException($"Value kind '{value.Kind}' cannot be read as Object.");
            }
        }

        static bool TryGetPropertyIgnoreCase(
            IReadOnlyDictionary<string, ObservationValue> fields,
            string propertyName,
            out ObservationValue value)
        {
            if (fields.TryGetValue(propertyName, out value))
            {
                return true;
            }

            foreach (var property in fields)
            {
                if (!string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = property.Value;
                return true;
            }

            value = default;
            return false;
        }
    }
}
