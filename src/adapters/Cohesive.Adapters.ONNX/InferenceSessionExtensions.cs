using Microsoft.ML.OnnxRuntime;

namespace Cohesive.Adapters.ONNX;

/// <summary>
/// Extension helpers for resolving ONNX input and output tensor names.
/// </summary>
static class InferenceSessionExtensions
{
    /// <param name="session">ONNX inference session.</param>
    extension(InferenceSession session)
    {
        /// <summary>
        /// Resolves an output name using an explicit configured name, ordered substring fallbacks, then first output.
        /// </summary>
        /// <param name="configuredName">Configured output name.</param>
        /// <param name="outputNameSubstrings">Ordered case-insensitive output-name substrings to try as fallbacks.</param>
        /// <returns>Resolved output name.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the model defines no outputs.</exception>
        internal string ResolveOutputName(string configuredName, params ReadOnlySpan<string?> outputNameSubstrings)
            => ResolveName(session, session.OutputNames, configuredName: configuredName, fallbackSubstrings: outputNameSubstrings, emptyCollectionMessage: "ONNX model has no outputs.");

        /// <summary>
        /// Resolves an input name using an explicit configured name, ordered substring fallbacks, then first input.
        /// </summary>
        /// <param name="configuredName">Configured input name.</param>
        /// <param name="inputNameSubstrings">Ordered case-insensitive input-name substrings to try as fallbacks.</param>
        /// <returns>Resolved input name.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the model defines no inputs.</exception>
        internal string ResolveInputName(string configuredName, params ReadOnlySpan<string?> inputNameSubstrings)
            => ResolveName(session, session.InputNames, configuredName, fallbackSubstrings: inputNameSubstrings, emptyCollectionMessage: "ONNX model has no inputs.");
    }

    static string ResolveName(InferenceSession session, IReadOnlyCollection<string> names, string configuredName, ReadOnlySpan<string?> fallbackSubstrings, string emptyCollectionMessage)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredName);
        ArgumentException.ThrowIfNullOrWhiteSpace(emptyCollectionMessage);

        if (names.Contains(configuredName, StringComparer.Ordinal))
            return configuredName;

        if (!fallbackSubstrings.IsEmpty)
        {
            for (var i = 0; i < fallbackSubstrings.Length; i++)
            {
                var substring = fallbackSubstrings[i];
                if (string.IsNullOrWhiteSpace(substring))
                    continue;

                var candidate = names.FirstOrDefault(x => x.Contains(substring, StringComparison.OrdinalIgnoreCase));
                if (candidate is not null)
                    return candidate;
            }
        }

        var first = names.FirstOrDefault();
        if (first is not null)
            return first;

        throw new InvalidOperationException(emptyCollectionMessage);
    }
}
