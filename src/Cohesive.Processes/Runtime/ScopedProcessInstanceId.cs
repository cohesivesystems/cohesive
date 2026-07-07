using System.Diagnostics.CodeAnalysis;
using Cohesive.Model;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Structured process instance id using a process type, scope id, and unique suffix.
/// </summary>
/// <param name="ProcessType">Normalized process type segment.</param>
/// <param name="ScopeId">Normalized scope segment, such as a tenant id.</param>
/// <param name="Suffix">Normalized process instance suffix.</param>
public sealed record ScopedProcessInstanceId(
    string ProcessType,
    string ScopeId,
    string Suffix
    )
{
    /// <summary>Separator between scoped process id segments.</summary>
    public const string Separator = "--";

    /// <summary>
    /// Creates a new scoped process instance id with a version-7 GUID suffix.
    /// </summary>
    /// <param name="processType">Process type segment.</param>
    /// <param name="scopeId">Scope segment, such as a tenant id.</param>
    /// <returns>A scoped process instance id string.</returns>
    public static string CreateId(string processType, string scopeId) =>
        Create(processType, scopeId, Guid.CreateVersion7().ToString("N"));

    /// <summary>
    /// Creates a scoped process instance id from explicit segments.
    /// </summary>
    /// <param name="processType">Process type segment.</param>
    /// <param name="scopeId">Scope segment, such as a tenant id.</param>
    /// <param name="suffix">Process instance suffix.</param>
    /// <returns>A scoped process instance id string.</returns>
    public static string Create(
        string processType,
        string scopeId,
        string suffix
        ) => $"{CreatePrefix(processType, scopeId)}{NormalizePart(suffix)}";

    /// <summary>
    /// Creates the prefix shared by all process instance ids in a process type and scope.
    /// </summary>
    /// <param name="processType">Process type segment.</param>
    /// <param name="scopeId">Scope segment, such as a tenant id.</param>
    /// <returns>A scoped process instance id prefix.</returns>
    public static string CreatePrefix(string processType, string scopeId) =>
        $"{NormalizePart(processType)}{Separator}{NormalizePart(scopeId)}{Separator}";

    /// <summary>
    /// Tries to parse a scoped process instance id or scoped process prefix.
    /// </summary>
    /// <param name="value">Process instance id or prefix.</param>
    /// <param name="parsed">Parsed scoped process id when parsing succeeds.</param>
    /// <returns><see langword="true"/> when the value contains process type and scope segments.</returns>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out ScopedProcessInstanceId? parsed
        )
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var firstSeparator = trimmed.IndexOf(Separator, StringComparison.Ordinal);
        if (firstSeparator <= 0)
            return false;

        var scopeStart = firstSeparator + Separator.Length;
        var secondSeparator = trimmed.IndexOf(Separator, scopeStart, StringComparison.Ordinal);
        if (secondSeparator <= scopeStart)
            return false;

        var processType = trimmed[..firstSeparator];
        var scopeId = trimmed[scopeStart..secondSeparator];
        if (string.IsNullOrWhiteSpace(processType) || string.IsNullOrWhiteSpace(scopeId))
            return false;

        parsed = new(
            ProcessType: processType,
            ScopeId: scopeId,
            Suffix: trimmed[(secondSeparator + Separator.Length)..]
            );
        return true;
    }

    /// <summary>
    /// Returns whether this id matches the expected process type and scope.
    /// </summary>
    /// <param name="processType">Expected process type segment.</param>
    /// <param name="scopeId">Expected scope segment.</param>
    /// <returns><see langword="true"/> when both segments match after normalization.</returns>
    public bool Matches(string processType, string scopeId) =>
        MatchesProcessType(processType) && MatchesScope(scopeId);

    /// <summary>
    /// Returns whether this id matches the expected process type.
    /// </summary>
    /// <param name="processType">Expected process type segment.</param>
    /// <returns><see langword="true"/> when the process type matches after normalization.</returns>
    public bool MatchesProcessType(string processType) =>
        string.Equals(ProcessType, NormalizePart(processType), StringComparison.Ordinal);

    /// <summary>
    /// Returns whether this id matches the expected scope.
    /// </summary>
    /// <param name="scopeId">Expected scope segment.</param>
    /// <returns><see langword="true"/> when the scope matches after normalization.</returns>
    public bool MatchesScope(string scopeId) =>
        string.Equals(ScopeId, NormalizePart(scopeId), StringComparison.Ordinal);

    /// <summary>
    /// Normalizes a scoped process id segment.
    /// </summary>
    /// <param name="value">Segment value to normalize.</param>
    /// <returns>A normalized segment value.</returns>
    public static string NormalizePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "default";

        return IdentifierNormalizer.Normalize(
            value.Trim(),
            IdentifierNormalizationOptions.Slug with
            {
                AdditionalAllowedCharacters = "-_",
                CollapseSeparators = false,
                EmptyFallback = "default",
                TrimSeparators = false
            });
    }

    /// <inheritdoc />
    public override string ToString() => $"{CreatePrefix(ProcessType, ScopeId)}{Suffix}";
}
