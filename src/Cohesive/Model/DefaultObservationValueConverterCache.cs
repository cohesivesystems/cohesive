using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Compiles allocation-light conversions for the framework's fixed default observation materialization contract.
/// Customized serializer contracts deliberately remain on the JSON compatibility path.
/// </summary>
static class DefaultObservationValueConverterCache
{
    static readonly ConcurrentDictionary<Type, ConverterEntry> Converters = [];

    public static Delegate? Get(Type targetType) =>
        Converters.GetOrAdd(
            targetType,
            static currentType => new(new ConverterBuilder().Build(currentType)))
        .Converter;

    sealed record ConverterEntry(Delegate? Converter);

    sealed class ConverterBuilder
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
            if (targetType == typeof(ObservationValue))
            {
                return value;
            }

            if (targetType == typeof(string))
            {
                return CallConverter(value, nameof(ReadString));
            }

            if (targetType == typeof(byte[]))
            {
                return CallConverter(value, nameof(ReadBytes));
            }

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
                return elementConverter is null
                    ? null
                    : Expression.Call(
                        ReadArrayMethod.MakeGenericMethod(elementType),
                        value,
                        ConverterConstant(elementType, elementConverter));
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
                || HasJsonContractCustomization(targetType))
            {
                return null;
            }

            var properties = ShapeTypeInspector.GetReadableProperties(targetType);
            if (properties.Length == 0
                || properties.Any(HasJsonContractCustomization))
            {
                return null;
            }

            var byProperty = properties.ToDictionary(
                static property => property.Name,
                StringComparer.OrdinalIgnoreCase);
            var constructors = targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (constructors.Length != 1 || constructors.Any(HasJsonContractCustomization))
            {
                return null;
            }

            var constructor = constructors[0];
            var constructorParameters = constructor.GetParameters();
            if (constructorParameters.Length != properties.Length)
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

            Expression materialize = Expression.New(constructor, constructorArguments);
            materialize = Expression.Block(
                Expression.Call(RequireObjectMethod, value),
                materialize);
            if (!targetType.IsValueType)
            {
                materialize = Expression.Condition(
                    Expression.Equal(
                        Expression.Property(value, nameof(ObservationValue.Kind)),
                        Expression.Constant(ObservationValueKind.Null)),
                    Expression.Default(targetType),
                    materialize);
            }
            return materialize;
        }

        static MethodCallExpression ReadProperty(
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
                Expression.Constant(explicitDefault));

        static ConstantExpression ConverterConstant(Type targetType, Delegate converter) =>
            Expression.Constant(converter, ConverterType(targetType));

        static Type ConverterType(Type targetType) =>
            typeof(Func<,>).MakeGenericType(typeof(ObservationValue), targetType);

        static MethodCallExpression CallConverter(Expression value, string methodName) =>
            Expression.Call(typeof(ConverterBuilder), methodName, typeArguments: null, value);

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

        static TProperty ReadObjectProperty<TProperty>(
            ObservationValue value,
            string propertyName,
            Func<ObservationValue, TProperty> converter,
            bool hasExplicitDefault,
            object? explicitDefault)
        {
            if (!TryGetPropertyIgnoreCase(value.Fields!, propertyName, out var propertyValue))
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

        // Mutable CLR output must own its bytes; never expose the immutable observation's backing storage.
        static byte[]? ReadBytes(ObservationValue value) =>
            value.Kind == ObservationValueKind.Bytes
                ? value.Bytes.ToArray()
                : DeserializeDefault<byte[]>(value);

        static int ReadInt32(ObservationValue value) =>
            value.Kind == ObservationValueKind.Int64
                && value.Int64 is >= int.MinValue and <= int.MaxValue
                ? (int)value.Int64
                : DeserializeDefault<int>(value);

        static long ReadInt64(ObservationValue value) =>
            value.Kind == ObservationValueKind.Int64
                ? value.Int64
                : DeserializeDefault<long>(value);

        static double ReadDouble(ObservationValue value) =>
            value.Kind is ObservationValueKind.Int64
                or ObservationValueKind.Double
                or ObservationValueKind.Decimal
                ? value.GetDouble()
                : DeserializeDefault<double>(value);

        static decimal ReadDecimal(ObservationValue value)
        {
            if (value.Kind is ObservationValueKind.Int64
                    or ObservationValueKind.Double
                    or ObservationValueKind.Decimal
                && value.TryGetCanonicalNumericDecimal(out var direct))
            {
                return direct;
            }

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
