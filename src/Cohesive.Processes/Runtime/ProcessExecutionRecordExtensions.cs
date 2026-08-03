using System.Text.Json;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Helpers for reading typed data from retained process execution records.
/// </summary>
public static class ProcessExecutionRecordExtensions
{
    static readonly JsonSerializerOptions JsonOptions = ProcessSerialization.CreateJsonOptions();

    /// <param name="record">Process execution record to inspect.</param>
    extension(ProcessExecutionRecord record)
    {
        /// <summary>
        /// Returns the first process parameter assignable to or deserializable as <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">Expected parameter type.</typeparam>
        /// <returns>The typed parameter value when present; otherwise <see langword="null"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
        public T? TryGetParameter<T>()
            where T : class
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.Parameters is null)
                return null;

            foreach (var parameter in record.Parameters.Values)
            {
                if (TryConvert<T>(parameter) is { } typed)
                    return typed;
            }

            return null;
        }

        /// <summary>
        /// Returns the process output assignable to or deserializable as <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">Expected output type.</typeparam>
        /// <returns>The typed output value when present; otherwise <see langword="null"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
        public T? TryGetOutput<T>() where T : class
        {
            ArgumentNullException.ThrowIfNull(record);
            return TryConvert<T>(record.Output);
        }

        /// <summary>
        /// Resolves the best available failure message from the retained process execution record.
        /// </summary>
        /// <returns>A trimmed failure message when present; otherwise <see langword="null"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
        public string? ResolveFailureMessage()
        {
            ArgumentNullException.ThrowIfNull(record);
            return NormalizeFailureMessage(record.FailureMessage)
                   ?? NormalizeFailureMessage(record.Error?.ErrorMessage)
                   ?? NormalizeFailureMessage(record.Error?.InnerError?.ErrorMessage);
        }
    }

    static T? TryConvert<T>(object? value) where T : class
    {
        if (value is T typed)
            return typed;

        if (value is JsonElement json)
        {
            try
            {
                return json.Deserialize<T>(JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    static string? NormalizeFailureMessage(string? message) =>
        string.IsNullOrWhiteSpace(message) ? null : message.Trim();
}
