using System.Collections.Immutable;
using System.Text;
using Cohesive.Execution;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Stable Durable Task physical identities for canonical Process executions.</summary>
public static class DurableTaskProcessExecutionIdentity
{
    /// <summary>Derives the authority-scoped physical orchestration ID for one logical Process instance.</summary>
    /// <param name="authorityScope">Exact authority and optional tenant that isolate the physical execution.</param>
    /// <param name="processInstanceId">Canonical logical Process instance identity.</param>
    /// <returns>
    /// The same opaque physical orchestration ID used by <see cref="DurableTaskSequentialProcessClientExtensions.ScheduleCohesiveProcessAsync"/>.
    /// </returns>
    /// <remarks>
    /// The authority and tenant participate in the versioned hash but are never copied into the returned identifier.
    /// Callers must supply trusted authority scope rather than deriving it from an untrusted logical identifier.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="authorityScope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="processInstanceId"/> is the default identity.</exception>
    public static string GetPhysicalInstanceId(
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId) =>
        DurableTaskSequentialProcessIdentities.OrchestrationInstance(authorityScope, processInstanceId);
}

/// <summary>Stable Scheduler tag names and projection for canonical Process orchestration instances.</summary>
/// <remarks>
/// Tags are immutable operational discovery metadata. The retained Process start and canonical control/continuation
/// state remain semantic authority, while changing lifecycle and semantic location are published as
/// <see cref="ExecutionStatus"/> custom status.
/// </remarks>
public static class DurableTaskProcessTags
{
    /// <summary>Maximum UTF-8 byte length supported by Durable Task Scheduler for one tag value.</summary>
    public const int MaximumValueSizeInBytes = 1000;

    /// <summary>Projection-version tag name.</summary>
    public const string ProjectionVersionTagName = "cohesive.process.tags.version";

    /// <summary>Current exact tag-projection version.</summary>
    public const string ProjectionVersion = "cohesive.process.tags/v1";

    /// <summary>Logical Process-instance tag name.</summary>
    public const string ProcessInstanceIdTagName = "cohesive.process.instance";

    /// <summary>Stable execution-definition identity tag name.</summary>
    public const string DefinitionIdTagName = "cohesive.process.definition";

    /// <summary>Exact execution-definition revision tag name.</summary>
    public const string DefinitionRevisionIdTagName = "cohesive.process.definition.revision";

    /// <summary>Execution-definition fingerprint algorithm tag name.</summary>
    public const string DefinitionFingerprintAlgorithmTagName = "cohesive.process.definition.fingerprint.algorithm";

    /// <summary>Execution-definition fingerprint canonicalization tag name.</summary>
    public const string DefinitionFingerprintCanonicalizationTagName =
        "cohesive.process.definition.fingerprint.canonicalization";

    /// <summary>Execution-definition fingerprint value tag name.</summary>
    public const string DefinitionFingerprintValueTagName = "cohesive.process.definition.fingerprint.value";

    /// <summary>Projects immutable payload-free Scheduler discovery tags from canonical Process-start evidence.</summary>
    /// <param name="receipt">Durably admitted canonical Process start.</param>
    /// <returns>Exact versioned tag projection in deterministic ordinal key order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A projected value exceeds <see cref="MaximumValueSizeInBytes"/> UTF-8 bytes.
    /// </exception>
    public static IReadOnlyDictionary<string, string> Create(ProcessStartReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var request = receipt.Request;
        var definition = request.Definition;
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [ProjectionVersionTagName] = ProjectionVersion,
            [ProcessInstanceIdTagName] = request.InitialContinuation.ProcessInstanceId.Value,
            [DefinitionIdTagName] = definition.DefinitionId.Value,
            [DefinitionRevisionIdTagName] = definition.RevisionId.Value,
            [DefinitionFingerprintAlgorithmTagName] = definition.Fingerprint.Algorithm,
            [DefinitionFingerprintCanonicalizationTagName] = definition.Fingerprint.Canonicalization,
            [DefinitionFingerprintValueTagName] = definition.Fingerprint.Value
        };

        foreach (var (name, value) in values)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > MaximumValueSizeInBytes)
            {
                throw new ArgumentException(
                    $"Durable Task Process tag '{name}' is {byteCount} UTF-8 bytes; Scheduler permits at most "
                    + $"{MaximumValueSizeInBytes} bytes per tag value.",
                    nameof(receipt));
            }
        }

        return values.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    internal static bool TryValidate(
        IReadOnlyDictionary<string, string> observed,
        ProcessStartReceipt receipt,
        out string? conflict)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(receipt);
        var hasRecognizedTag = observed.Keys.Any(IsRecognizedTagName);
        if (!hasRecognizedTag)
        {
            conflict = null;
            return true;
        }

        var expected = Create(receipt);
        foreach (var (name, expectedValue) in expected)
        {
            if (!observed.TryGetValue(name, out var observedValue))
            {
                conflict = $"contains a partial canonical Process tag projection missing '{name}'";
                return false;
            }

            if (!string.Equals(observedValue, expectedValue, StringComparison.Ordinal))
            {
                conflict = $"contains canonical Process tag '{name}' that conflicts with its retained start receipt";
                return false;
            }
        }

        conflict = null;
        return true;
    }

    static bool IsRecognizedTagName(string name) => name is
        ProjectionVersionTagName
        or ProcessInstanceIdTagName
        or DefinitionIdTagName
        or DefinitionRevisionIdTagName
        or DefinitionFingerprintAlgorithmTagName
        or DefinitionFingerprintCanonicalizationTagName
        or DefinitionFingerprintValueTagName;
}
