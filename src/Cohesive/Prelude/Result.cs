using System.Text.Json.Serialization;

namespace Cohesive.Prelude;

/// <summary>
/// Result discriminators.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResultType
{
    Success = 1,
    Failure = 2,
}

/// <summary>
/// Generic success/failure union modeled as a tagged record struct.
/// </summary>
[Union]
public readonly partial record struct Result<TSuccess, TError>(ResultType Type, TSuccess? Success = default, TError? Failure = default);

/// <summary>
/// Static helper methods for constructing and reading Result values.
/// </summary>
public static class Result
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static Result<TSuccess, TError> Success<TSuccess, TError>(TSuccess value) =>
        Result<TSuccess, TError>.FromSuccess(value: value);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static Result<TSuccess, TError> Failure<TSuccess, TError>(TError value) =>
        Result<TSuccess, TError>.FromFailure(value: value);

    extension<TSuccess, TError>(Result<TSuccess, TError> value)
    {
        /// <summary>
        /// Indicates whether the result is a success.
        /// </summary>
        public bool IsSuccess => value.IsSuccess();

        /// <summary>
        /// Indicates whether the result is a failure.
        /// </summary>
        public bool IsFailure => value.IsFailure();
        
        /// <summary>
        /// Returns the success value when present, otherwise returns null/default.
        /// </summary>
        public TSuccess? TryGetSuccess() =>
            value.TryGetSuccess(value: out var success) ? success! : default;

        /// <summary>
        /// Returns the failure value when present, otherwise returns null/default.
        /// </summary>
        public TError? TryGetFailure() =>
            value.TryGetFailure(value: out var failure) ? failure : default;

        /// <summary>
        /// Returns the success value or throws when the result is not successful.
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public TSuccess GetSuccessOrThrow() =>
            value.TryGetSuccess(value: out var success) 
                ? success! 
                : throw new InvalidOperationException(message: $"Expected {nameof(ResultType.Success)} but found '{value.Type}'.");

        /// <summary>
        /// Converts a result into its corresponding Either representation.
        /// </summary>
        public Either<TSuccess?, TError?> AsEither() => value.ToEither();
    }

    extension<TSuccess, TError>(Either<TSuccess, TError> value)
    {
        /// <summary>
        /// Converts an Either value into its corresponding Result representation.
        /// </summary>
        public Result<TSuccess, TError> ToResult() => value.Match(
            onCase1: static success => Success<TSuccess, TError>(value: success),
            onCase2: static failure => Failure<TSuccess, TError>(value: failure)
            );
    }
}
