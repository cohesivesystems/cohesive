using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cohesive.Tests.Prelude;

/// <summary>
/// Local discriminator enum used to verify Union generation inside the test project.
/// </summary>
public enum LocalResultType
{
    Ok = 1,
    Err = 2,
}

/// <summary>
/// Local tagged union defined in tests to validate analyzer execution in Cohesive.Tests.
/// </summary>
[Union]
public readonly partial record struct LocalResult(LocalResultType Type, int? Ok, string? Err);

/// <summary>
/// Alternate tagged union used to validate discriminator-name overrides.
/// </summary>
public enum OutcomeType
{
    Ok = 1,
    Error = 2
}

/// <summary>
/// Generic outcome union with an explicit discriminator property override.
/// </summary>
[Union(discriminatorPropertyName: "Kind")]
public readonly partial record struct Outcome<TValue>(OutcomeType Kind, TValue? Ok, string? Error);


/// <summary>
/// Unit tests for generated Either arities in the Prelude namespace.
/// </summary>
public sealed class UnionTests
{
    [Fact]
    public void Either2_Case1_ExposesExpectedContract()
    {
        var either = Either<int, string>.FromCase1(value: 42);

        Assert.Equal(expected: 2, actual: ((IDiscriminatedUnion)either).CaseCount);
        Assert.Equal(expected: 0, actual: ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Equal(expected: 42, actual: ((IDiscriminatedUnion)either).CaseValue);
        Assert.True(condition: either.IsCase1());
        Assert.False(condition: either.IsCase2());
        Assert.True(condition: either.TryGetCase1(value: out var case1Value));
        Assert.Equal(expected: 42, actual: case1Value);
        Assert.False(condition: either.TryGetCase2(value: out var case2Value));
        Assert.Null(case2Value);

        var folded = either.Match(
            onCase1: value => $"n:{value}",
            onCase2: value => $"s:{value}");

        Assert.Equal(expected: "n:42", actual: folded);
    }

    [Fact]
    public void Either2_Case2_ExposesExpectedContract()
    {
        var either = Either<int, string>.FromCase2(value: "hello");
        
        Assert.Equal(expected: 1, actual: ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Equal(expected: "hello", actual: ((IDiscriminatedUnion)either).CaseValue);
        Assert.False(condition: either.IsCase1());
        Assert.True(condition: either.IsCase2());
        Assert.False(condition: either.TryGetCase1(value: out var case1Value));
        Assert.Equal(expected: 0, actual: case1Value);
        Assert.True(condition: either.TryGetCase2(value: out var case2Value));
        Assert.Equal(expected: "hello", actual: case2Value);
    }

    [Fact]
    public void Either2_DefaultValue_IsUninitialized()
    {
        var either = default(Either<int, string>);

        Assert.Equal(expected: (Either2Type)0, actual: either.Type);
        Assert.Throws<InvalidOperationException>(
            testCode: () => _ = ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Throws<InvalidOperationException>(
            testCode: () => _ = ((IDiscriminatedUnion)either).CaseValue);
        Assert.Throws<InvalidOperationException>(
            testCode: () => either.Match(
                onCase1: value => value.ToString(),
                onCase2: value => value));
    }

    [Fact]
    public void Either2_Deconstruct_SupportsTuplePatternMatching()
    {
        var either = Either<int, string>.FromCase2(value: "matched");

        var matched = either switch
        {
            (Either2Type.Case1, int value) => $"n:{value}",
            (Either2Type.Case2, string value) => $"s:{value}",
            _ => throw new InvalidOperationException(message: "Unexpected either case."),
        };

        Assert.Equal(expected: "s:matched", actual: matched);
    }

    [Fact]
    public void Either2_JsonSerialization_UsesStringDiscriminatorAndActiveCaseOnly()
    {
        var either = Either<int, string?>.FromCase2(value: "json");
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Either<int, string?>.EitherJsonConverter());
        var json = JsonSerializer.Serialize(value: either, options: options);

        using var document = JsonDocument.Parse(json: json);
        var root = document.RootElement;
        Assert.Equal(expected: "Case2", actual: root.GetProperty(propertyName: "type").GetString());
        Assert.False(condition: root.TryGetProperty(propertyName: "case1", value: out _));
        Assert.Equal(expected: "json", actual: root.GetProperty(propertyName: "case2").GetString());
    }

    [Fact]
    public void Either2_JsonSerialization_OmitsNullActivePayload()
    {
        var either = Either<string?, int>.FromCase1(value: null);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Either<string?, int>.EitherJsonConverter());
        var json = JsonSerializer.Serialize(value: either, options: options);

        using var document = JsonDocument.Parse(json: json);
        var root = document.RootElement;
        Assert.Equal(expected: "Case1", actual: root.GetProperty(propertyName: "type").GetString());
        Assert.False(condition: root.TryGetProperty(propertyName: "case1", value: out _));
        Assert.False(condition: root.TryGetProperty(propertyName: "case2", value: out _));
    }

    [Fact]
    public void Either2_JsonDeserialization_RoundTripsCasePayload()
    {
        var json = "{\"type\":\"Case1\",\"case1\":42}";
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Either<int, string>.EitherJsonConverter());
        var value = JsonSerializer.Deserialize<Either<int, string>>(json: json, options: options);

        Assert.True(condition: value.IsCase1());
        Assert.True(condition: value.TryGetCase1(value: out var caseValue));
        Assert.Equal(expected: 42, actual: caseValue);
    }

    [Fact]
    public void Either2_JsonDeserialization_RejectsMismatchedPayload()
    {
        var json = "{\"type\":\"Case1\",\"case2\":\"x\"}";
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Either<int?, string>.EitherJsonConverter());

        Assert.Throws<JsonException>(
            testCode: () => JsonSerializer.Deserialize<Either<int?, string>>(json: json, options: options));
    }

    [Fact]
    public void Either2_JsonSerialization_DefaultValue_Throws()
    {
        var value = default(Either<int, string>);
        var options = new JsonSerializerOptions();
        options.Converters.Add(new Either<int, string>.EitherJsonConverter());

        Assert.Throws<InvalidOperationException>(
            testCode: () => JsonSerializer.Serialize(value: value, options: options));
    }

    [Fact]
    public void Either3_Case3_FoldsAndExtractsCorrectly()
    {
        var either = Either<int, string, bool>.FromCase3(value: true);

        Assert.Equal(expected: 3, actual: ((IDiscriminatedUnion)either).CaseCount);
        Assert.Equal(expected: 2, actual: ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Equal(expected: true, actual: ((IDiscriminatedUnion)either).CaseValue);
        Assert.True(condition: either.IsCase3());
        Assert.True(condition: either.TryGetCase3(value: out var case3Value));
        Assert.True(condition: case3Value);

        var folded = either.Match(
            onCase1: value => value.ToString(),
            onCase2: value => value,
            onCase3: value => value ? "T" : "F");

        Assert.Equal(expected: "T", actual: folded);
    }

    [Fact]
    public void Either4_Case4_FoldsAndExtractsCorrectly()
    {
        var either = Either<int, string, bool, decimal>.FromCase4(value: 9.5m);

        Assert.Equal(expected: 4, actual: ((IDiscriminatedUnion)either).CaseCount);
        Assert.Equal(expected: 3, actual: ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Equal(expected: 9.5m, actual: ((IDiscriminatedUnion)either).CaseValue);
        Assert.True(condition: either.IsCase4());
        Assert.True(condition: either.TryGetCase4(value: out var case4Value));
        Assert.Equal(expected: 9.5m, actual: case4Value);

        var folded = either.Match(
            onCase1: value => value.ToString(),
            onCase2: value => value,
            onCase3: value => value ? "T" : "F",
            onCase4: value => value.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(expected: "9.5", actual: folded);
    }

    [Fact]
    public void Either5_Case5_FoldsAndExtractsCorrectly()
    {
        var either = Either<int, string, bool, decimal, Guid>.FromCase5(value: Guid.Empty);

        Assert.Equal(expected: 5, actual: ((IDiscriminatedUnion)either).CaseCount);
        Assert.Equal(expected: 4, actual: ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Equal(expected: Guid.Empty, actual: ((IDiscriminatedUnion)either).CaseValue);
        Assert.True(condition: either.IsCase5());
        Assert.True(condition: either.TryGetCase5(value: out var case5Value));
        Assert.Equal(expected: Guid.Empty, actual: case5Value);

        var folded = either.Match(
            onCase1: value => value.ToString(),
            onCase2: value => value,
            onCase3: value => value ? "T" : "F",
            onCase4: value => value.ToString(CultureInfo.InvariantCulture),
            onCase5: value => value.ToString());

        Assert.Equal(expected: Guid.Empty.ToString(), actual: folded);
    }

    [Fact]
    public void Either6_Case6_FoldsAndExtractsCorrectly()
    {
        var either = Either<int, string, bool, decimal, Guid, DateTime>.FromCase6(value: DateTime.UnixEpoch);

        Assert.Equal(expected: 6, actual: ((IDiscriminatedUnion)either).CaseCount);
        Assert.Equal(expected: 5, actual: ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Equal(expected: DateTime.UnixEpoch, actual: ((IDiscriminatedUnion)either).CaseValue);
        Assert.True(condition: either.IsCase6());
        Assert.True(condition: either.TryGetCase6(value: out var case6Value));
        Assert.Equal(expected: DateTime.UnixEpoch, actual: case6Value);

        var folded = either.Match(
            onCase1: value => value.ToString(),
            onCase2: value => value,
            onCase3: value => value ? "T" : "F",
            onCase4: value => value.ToString(CultureInfo.InvariantCulture),
            onCase5: value => value.ToString(),
            onCase6: value => value.ToString("O"));

        Assert.Equal(expected: DateTime.UnixEpoch.ToString("O"), actual: folded);
    }

    [Fact]
    public void Either7_Case7_FoldsAndExtractsCorrectly()
    {
        var either = Either<int, string, bool, decimal, Guid, DateTime, long>.FromCase7(value: 77L);
        
        Assert.Equal(expected: 7, actual: ((IDiscriminatedUnion)either).CaseCount);
        Assert.Equal(expected: 6, actual: ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Equal(expected: 77L, actual: ((IDiscriminatedUnion)either).CaseValue);
        Assert.True(condition: either.IsCase7());
        Assert.True(condition: either.TryGetCase7(value: out var case7Value));
        Assert.Equal(expected: 77L, actual: case7Value);

        var folded = either.Match(
            onCase1: value => value.ToString(),
            onCase2: value => value,
            onCase3: value => value ? "T" : "F",
            onCase4: value => value.ToString(CultureInfo.InvariantCulture),
            onCase5: value => value.ToString(),
            onCase6: value => value.ToString("O"),
            onCase7: value => value.ToString());

        Assert.Equal(expected: "77", actual: folded);
    }

    [Fact]
    public void Either8_Case8_FoldsAndExtractsCorrectly()
    {
        var either = Either<int, string, bool, decimal, Guid, DateTime, long, Uri>.FromCase8(value: new Uri(uriString: "https://example.test"));

        Assert.Equal(expected: 8, actual: ((IDiscriminatedUnion)either).CaseCount);
        Assert.Equal(expected: 7, actual: ((IDiscriminatedUnion)either).CaseIndex);
        Assert.Equal(expected: new Uri(uriString: "https://example.test"), actual: ((IDiscriminatedUnion)either).CaseValue);
        Assert.True(condition: either.IsCase8());
        Assert.True(condition: either.TryGetCase8(value: out var case8Value));
        Assert.Equal(expected: new Uri(uriString: "https://example.test"), actual: case8Value);

        var folded = either.Match(
            onCase1: value => value.ToString(),
            onCase2: value => value,
            onCase3: value => value ? "T" : "F",
            onCase4: value => value.ToString(CultureInfo.InvariantCulture),
            onCase5: value => value.ToString(),
            onCase6: value => value.ToString("O"),
            onCase7: value => value.ToString(),
            onCase8: value => value.ToString()
            );
        
        Assert.Equal(expected: "https://example.test/", actual: folded);
    }
    
    [Fact]
    public void EitherStaticFactoryClass_CreatesExpectedEitherInstance()
    {
        var either = Either.FromCase2<int, string>(value: "via-static-factory");
        
        Assert.True(either.IsCase2());
        Assert.True(either.TryGetCase2(out var value));
        Assert.Equal("via-static-factory", value);
    }

    [Fact]
    public void EitherEnumerableExtensions_Split_ForEither2_SplitsIntoTwoBuckets()
    {
        int[] values = [1, 2, 3, 4, 5];

        var (case1, case2) = values.Split(static value =>
            value % 2 == 0
                ? Either<int, string>.FromCase1(value: value)
                : Either<int, string>.FromCase2(value: $"odd:{value}")
                );

        Assert.Equal(expected: [2, 4], actual: case1);
        Assert.Equal(expected: ["odd:1", "odd:3", "odd:5"], actual: case2);
    }

    [Fact]
    public void EitherEnumerableExtensions_Split_ForEither3_SplitsIntoThreeBuckets()
    {
        int[] values = [0, 1, 2, 3, 4, 5];

        var (case1, case2, case3) = values.Split(selector: static value =>
        {
            if (value % 3 == 0)
            {
                return Either<string, int, decimal>.FromCase1(value: $"three:{value}");
            }

            if (value % 3 == 1)
            {
                return Either<string, int, decimal>.FromCase2(value: value);
            }

            return Either<string, int, decimal>.FromCase3(value: value + 0.5m);
        });

        Assert.Equal(expected: ["three:0", "three:3"], actual: case1);
        Assert.Equal(expected: [1, 4], actual: case2);
        Assert.Equal(expected: [2.5m, 5.5m], actual: case3);
    }
    
    [Fact]
    public void ResultTaggedUnion_DefaultTypeDiscriminator_IsGenerated()
    {
        var success = Result<int, string>.FromSuccess(value: 123);
        
        Assert.Equal(expected: ResultType.Success, actual: success.Type);
        Assert.True(condition: success.IsSuccess());
        Assert.False(condition: success.IsFailure());
        Assert.True(condition: success.TryGetSuccess(value: out var successValue));
        Assert.Equal(expected: 123, actual: successValue);
        Assert.False(condition: success.TryGetFailure(value: out var failureValue));
        Assert.Null(@object: failureValue);

        var fromEither = Result<int, string>.FromEither(
            value: Either<int, string?>.FromCase2(value: "failed"));
        Assert.Equal(expected: ResultType.Failure, actual: fromEither.Type);
        Assert.False(condition: fromEither.IsSuccess());
        Assert.True(condition: fromEither.IsFailure());
        Assert.True(condition: fromEither.TryGetFailure(value: out var fromEitherFailure));
        Assert.Equal(expected: "failed", actual: fromEitherFailure);
    }

    [Fact]
    public void ResultStaticClass_ConstructsSuccessAndFailure()
    {
        var success = Result.Success<int, string>(value: 10);
        Assert.Equal(expected: ResultType.Success, actual: success.Type);
        Assert.Equal(expected: 10, actual: success.TryGetSuccess());

        var failure = Result.Failure<int, string>(value: "bad");
        Assert.Equal(expected: ResultType.Failure, actual: failure.Type);
        Assert.Equal(expected: "bad", actual: failure.TryGetFailure());
    }

    [Fact]
    public void ResultStaticClass_TryGetMethods_ReturnDefaultForOtherCase()
    {
        var success = Result.Success<int, string>(value: 42);
        Assert.Equal(expected: 42, actual: success.TryGetSuccess());
        Assert.Null(@object: success.TryGetFailure());

        var failure = Result.Failure<int, string>(value: "boom");
        Assert.Equal(expected: 0, actual: failure.TryGetSuccess());
        Assert.Equal(expected: "boom", actual: failure.TryGetFailure());
    }

    [Fact]
    public void ResultStaticClass_GetSuccessOrThrow_ThrowsOnFailure()
    {
        var failure = Result.Failure<int, string>(value: "nope");

        Assert.Throws<InvalidOperationException>(testCode: () => _ = failure.GetSuccessOrThrow());
    }

    [Fact]
    public void ResultEitherMappers_EitherSuccess_ToResultAndBack()
    {
        var either = Either<int, string>.FromCase1(value: 9);
        var result = either.ToResult();
        var mappedEither = result.AsEither();

        Assert.True(condition: result.IsSuccess());
        Assert.Equal(expected: 9, actual: result.GetSuccessOrThrow());
        Assert.True(condition: mappedEither.IsCase1());
        Assert.True(condition: mappedEither.TryGetCase1(value: out var mappedValue));
        Assert.Equal(expected: 9, actual: mappedValue);
    }

    [Fact]
    public void ResultEitherMappers_EitherFailure_ToResultAndBack()
    {
        var either = Either<int, string>.FromCase2(value: "oops");
        var result = either.ToResult();
        var mappedEither = result.AsEither();

        Assert.True(condition: result.IsFailure());
        Assert.Equal(expected: "oops", actual: result.TryGetFailure());
        Assert.True(condition: mappedEither.IsCase2());
        Assert.True(condition: mappedEither.TryGetCase2(value: out var mappedValue));
        Assert.Equal(expected: "oops", actual: mappedValue);
    }

    [Fact]
    public void ResultJsonSerialization_Success_HasExpectedShape()
    {
        var success = Result.Success<int, string>(value: 123);
        var json = JsonSerializer.Serialize(value: success);

        using var document = JsonDocument.Parse(json: json);
        var root = document.RootElement;

        Assert.Equal(expected: JsonValueKind.Object, actual: root.ValueKind);
        Assert.Equal(expected: nameof(ResultType.Success), actual: root.GetProperty(propertyName: nameof(Result<,>.Type)).GetString());
        Assert.Equal(expected: 123, actual: root.GetProperty(propertyName: nameof(Result<,>.Success)).GetInt32());
        Assert.Equal(expected: JsonValueKind.Null, actual: root.GetProperty(propertyName: nameof(Result<,>.Failure)).ValueKind);
        Assert.False(condition: root.TryGetProperty(propertyName: nameof(Result<,>.IsSuccess), value: out _));
        Assert.False(condition: root.TryGetProperty(propertyName: nameof(Result<,>.IsFailure), value: out _));
    }

    [Fact]
    public void ResultJsonSerialization_Failure_HasExpectedShape()
    {
        var failure = Result.Failure<int?, string>(value: "boom");
        var json = JsonSerializer.Serialize(value: failure);

        using var document = JsonDocument.Parse(json: json);
        var root = document.RootElement;

        Assert.Equal(expected: JsonValueKind.Object, actual: root.ValueKind);
        Assert.Equal(expected: nameof(ResultType.Failure), actual: root.GetProperty(propertyName: nameof(Result<,>.Type)).GetString());
        Assert.Equal(expected: JsonValueKind.Null, actual: root.GetProperty(propertyName: nameof(Result<,>.Success)).ValueKind);
        Assert.Equal(expected: "boom", actual: root.GetProperty(propertyName: nameof(Result<,>.Failure)).GetString());
        Assert.False(condition: root.TryGetProperty(propertyName: nameof(Result<,>.IsSuccess), value: out _));
        Assert.False(condition: root.TryGetProperty(propertyName: nameof(Result<,>.IsFailure), value: out _));
    }

    [Fact]
    public void ResultJsonDeserialization_RoundTripsTaggedCase()
    {
        var json = """{"Type":"Failure","Success":null,"Failure":"failed"}""";
        var json2 = """{"Type":"Failure","Failure":"failed"}""";
        Check(JsonSerializer.Deserialize<Result<int?, string>>(json: json));
        Check(JsonSerializer.Deserialize<Result<int?, string>>(json: json2));
        return;

        static void Check(Result<int?, string> result)
        {
            Assert.Equal(expected: ResultType.Failure, actual: result.Type);
            Assert.True(condition: result.IsFailure());
            Assert.False(condition: result.IsSuccess());
            Assert.Equal(expected: "failed", actual: result.TryGetFailure());
        }
    }
    
    [Fact]
    public void OutcomeTaggedUnion_OverrideDiscriminatorProperty_IsGenerated()
    {
        var ok = Outcome<int>.FromOk(value: 7);
        Assert.Equal(expected: OutcomeType.Ok, actual: ok.Kind);
        Assert.True(condition: ok.IsOk());
        Assert.False(condition: ok.IsError());
        Assert.True(condition: ok.TryGetOk(value: out var okValue));
        Assert.Equal(expected: 7, actual: okValue);

        var error = Outcome<int>.FromError(value: "boom");
        Assert.Equal(expected: OutcomeType.Error, actual: error.Kind);
        Assert.False(condition: error.IsOk());
        Assert.True(condition: error.IsError());
        Assert.True(condition: error.TryGetError(value: out var errorValue));
        Assert.Equal(expected: "boom", actual: errorValue);
    }

    [Fact]
    public void LocalTaggedUnion_InTests_IsGenerated()
    {
        var ok = LocalResult.FromOk(value: 9);
        Assert.Equal(expected: LocalResultType.Ok, actual: ok.Type);
        Assert.True(condition: ok.IsOk());
        Assert.False(condition: ok.IsErr());
        Assert.True(condition: ok.TryGetOk(value: out var okValue));
        Assert.Equal(expected: 9, actual: okValue);

        var err = LocalResult.FromErr(value: "x");
        Assert.Equal(expected: LocalResultType.Err, actual: err.Type);
        Assert.False(condition: err.IsOk());
        Assert.True(condition: err.IsErr());
        Assert.True(condition: err.TryGetErr(value: out var errValue));
        Assert.Equal(expected: "x", actual: errValue);
    }

    [Fact]
    public void LocalTaggedUnion_Deconstruct_SupportsTuplePatternMatching()
    {
        var value = LocalResult.FromErr(value: "oops");

        var matched = value switch
        {
            (0, int ok) => $"ok:{ok}",
            (1, string err) => $"err:{err}",
            _ => throw new InvalidOperationException(message: "Unexpected union case."),
        };

        Assert.Equal(expected: "err:oops", actual: matched);
    }

    [Fact]
    public void LinkedList_CreateAndEnumerate()
    {
        var emptyList = LinkedList.Empty<string>();
        var one = LinkedList.NonEmpty("Item", emptyList);
        var two = LinkedList.NonEmpty("Item", LinkedList.NonEmpty("Item2", emptyList));
        var foldedList = two.Fold(new List<string>(), (ls, item) =>
        {
            ls.Add(item);
            return ls;
        });
        LinkedList<string> initList = ["Item", "Item2"];
        
        Assert.Empty(emptyList);
        Assert.Equal(["Item"], one.ToArray());
        Assert.Equal(["Item", "Item2"], two.ToArray());
        Assert.Equal(["Item", "Item2"], initList.ToArray());
        Assert.Equal(["Item", "Item2"], foldedList);
    }

    [Fact]
    public void LinkedList_Cons_RejectsNullTail()
    {
        Assert.Throws<ArgumentNullException>(testCode: () => LinkedList.NonEmpty(head: "Item", tail: null!));
    }
}

[Union]
[CollectionBuilder(typeof(LinkedList), nameof(LinkedList.Create))]
public abstract partial record LinkedList<T> : IEnumerable<T>
{
    public sealed record Empty : LinkedList<T>;

    public sealed record NonEmpty(T Head, LinkedList<T> Tail) : LinkedList<T>;

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    struct Enumerator : IEnumerator<T>
    {
        LinkedList<T>? remaining;
        T? current;

        internal Enumerator(LinkedList<T> list)
        {
            ArgumentNullException.ThrowIfNull(argument: list);
            remaining = list;
            current = default;
        }

        public T Current => current!;

        object? IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (remaining is null)
                return false;

            switch (remaining)
            {
                case Empty:
                    remaining = null;
                    current = default;
                    return false;
                case NonEmpty n:
                    current = n.Head;
                    remaining = n.Tail;
                    return true;
                default:
                    throw new InvalidOperationException(message: "Unknown LinkedList union case.");
            }
        }

        public void Reset() => throw new NotSupportedException(message: "Reset is not supported.");

        public void Dispose()
        {
            remaining = null;
            current = default;
        }
    }
}

public static class LinkedList
{
    public static LinkedList<T> Create<T>(ReadOnlySpan<T> items)
    {
        var list = Empty<T>();
        for (var i = items.Length - 1; i >= 0; i--)
            list = NonEmpty(items[i], list);
        return list;
    }
    
    public static TResult Fold<T, TResult>(this LinkedList<T> list, TResult init, Func<TResult, T, TResult> func) => list switch
    {
        LinkedList<T>.Empty => init,
        LinkedList<T>.NonEmpty n => n.Tail.Fold(func(init, n.Head), func),
        _ => throw new InvalidOperationException(message: "Unknown LinkedList union case.")
    };
    
    public static LinkedList<T> Empty<T>() => 
        LinkedList<T>.FromEmpty(new LinkedList<T>.Empty());

    public static LinkedList<T> NonEmpty<T>(T head, LinkedList<T> tail)
    {
        ArgumentNullException.ThrowIfNull(argument: tail);
        return LinkedList<T>.FromNonEmpty(new LinkedList<T>.NonEmpty(head, tail));
    }
}
