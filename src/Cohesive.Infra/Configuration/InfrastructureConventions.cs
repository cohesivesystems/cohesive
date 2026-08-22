using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Configuration;

/// <summary>Stable subject to which one infrastructure configuration setting applies.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureConfigurationSubject
{
    /// <summary>Creates a configuration subject.</summary>
    /// <param name="value">Stable definition-, environment-, node-, binding-, or target-scoped subject identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureConfigurationSubject(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable subject identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw subject identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one infrastructure configuration setting.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureSettingId
{
    /// <summary>Creates a setting identity.</summary>
    /// <param name="value">Stable setting identity interpreted by its owning compiler or adapter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureSettingId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable setting identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw setting identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of one deterministic infrastructure convention profile.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureConventionProfileId
{
    /// <summary>Creates a convention-profile identity.</summary>
    /// <param name="value">Stable identity that changes when the profile's semantic selection policy changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureConventionProfileId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable convention-profile identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw convention-profile identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>One attributable candidate value supplied by an explicit declaration, profile, or convention.</summary>
public sealed record InfrastructureConfigurationCandidate
{
    /// <summary>Creates a configuration candidate.</summary>
    /// <param name="subject">Stable subject receiving the setting.</param>
    /// <param name="setting">Stable setting identity.</param>
    /// <param name="value">Canonical serialized setting value owned by the resulting effective artifact.</param>
    /// <param name="origin">Suite-wide authority tier that supplied the candidate.</param>
    /// <param name="authority">Stable identity and version of the declaration, profile, or convention.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> or <paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or string value is empty or white-space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureConfigurationCandidate(
        InfrastructureConfigurationSubject subject,
        InfrastructureSettingId setting,
        string value,
        EffectiveConfigurationOrigin origin,
        string authority)
    {
        if (string.IsNullOrWhiteSpace(subject.Value))
            throw new ArgumentException("An infrastructure configuration candidate requires a subject.", nameof(subject));
        if (string.IsNullOrWhiteSpace(setting.Value))
            throw new ArgumentException("An infrastructure configuration candidate requires a setting.", nameof(setting));
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported effective-configuration origin.");

        Subject = subject;
        Setting = setting;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
    }

    /// <summary>Stable subject receiving the setting.</summary>
    public InfrastructureConfigurationSubject Subject { get; }

    /// <summary>Stable setting identity.</summary>
    public InfrastructureSettingId Setting { get; }

    /// <summary>Canonical serialized setting value.</summary>
    public string Value { get; }

    /// <summary>Suite-wide authority tier that supplied the candidate.</summary>
    public EffectiveConfigurationOrigin Origin { get; }

    /// <summary>Stable identity and version of the supplying authority.</summary>
    public string Authority { get; }
}

/// <summary>Portable, versioned collection of convention-produced configuration candidates.</summary>
public sealed record InfrastructureConventionProfile
{
    /// <summary>Creates a deterministic infrastructure convention profile.</summary>
    /// <param name="id">Stable, versioned profile identity.</param>
    /// <param name="candidates">Candidates supplied by the profile.</param>
    /// <exception cref="ArgumentException">The identity is default, a candidate is <see langword="null"/>, or an exact candidate is duplicated.</exception>
    [JsonConstructor]
    public InfrastructureConventionProfile(
        InfrastructureConventionProfileId id,
        ImmutableArray<InfrastructureConfigurationCandidate> candidates = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure convention profile requires a stable identity.", nameof(id));

        Id = id;
        Candidates = NormalizeCandidates(candidates, nameof(candidates));
    }

    /// <summary>Stable, versioned profile identity.</summary>
    public InfrastructureConventionProfileId Id { get; }

    /// <summary>Candidates in deterministic subject, setting, authority-tier, authority, and value order.</summary>
    public ImmutableArray<InfrastructureConfigurationCandidate> Candidates { get; }

    /// <summary>Compares convention profiles structurally.</summary>
    /// <param name="other">Other profile.</param>
    /// <returns><see langword="true"/> when the identities and every normalized candidate are equal.</returns>
    public bool Equals(InfrastructureConventionProfile? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Candidates.SequenceEqual(other.Candidates);

    /// <summary>Returns a structural hash code for this profile.</summary>
    /// <returns>A hash code derived from the identity and normalized candidates.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        foreach (var candidate in Candidates)
            hash.Add(candidate);
        return hash.ToHashCode();
    }

    internal static ImmutableArray<InfrastructureConfigurationCandidate> NormalizeCandidates(
        ImmutableArray<InfrastructureConfigurationCandidate> candidates,
        string paramName)
    {
        if (candidates.IsDefaultOrEmpty)
            return [];
        if (candidates.Any(static candidate => candidate is null))
            throw new ArgumentException("Infrastructure configuration candidates cannot contain null.", paramName);

        var ordered = candidates.Sort(InfrastructureConfigurationCandidateComparer.Ordinal);
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1] == ordered[index])
                throw new ArgumentException("An exact infrastructure configuration candidate is duplicated.", paramName);
        }
        return ordered;
    }
}

/// <summary>One effective configuration value with suite-wide origin attribution.</summary>
public sealed record InfrastructureEffectiveConfiguration
{
    /// <summary>Creates an effective configuration result.</summary>
    /// <param name="subject">Stable subject receiving the setting.</param>
    /// <param name="setting">Stable setting identity.</param>
    /// <param name="value">Canonical serialized effective value.</param>
    /// <param name="attribution">Origin and authority that supplied the value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> or <paramref name="attribution"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or value is empty, or attribution names a different setting.</exception>
    [JsonConstructor]
    public InfrastructureEffectiveConfiguration(
        InfrastructureConfigurationSubject subject,
        InfrastructureSettingId setting,
        string value,
        EffectiveConfigurationDecision attribution)
    {
        if (string.IsNullOrWhiteSpace(subject.Value))
            throw new ArgumentException("Effective infrastructure configuration requires a subject.", nameof(subject));
        if (string.IsNullOrWhiteSpace(setting.Value))
            throw new ArgumentException("Effective infrastructure configuration requires a setting.", nameof(setting));

        Attribution = Guard.RequireNotNull(attribution);
        var expectedSetting = QualifiedSetting(subject, setting);
        if (!string.Equals(Attribution.Setting, expectedSetting, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Effective-configuration attribution must name setting '{expectedSetting}'.",
                nameof(attribution));
        }

        Subject = subject;
        Setting = setting;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Stable subject receiving the setting.</summary>
    public InfrastructureConfigurationSubject Subject { get; }

    /// <summary>Stable setting identity.</summary>
    public InfrastructureSettingId Setting { get; }

    /// <summary>Canonical serialized effective value.</summary>
    public string Value { get; }

    /// <summary>Origin and authority that supplied the effective value.</summary>
    public EffectiveConfigurationDecision Attribution { get; }

    internal static string QualifiedSetting(
        InfrastructureConfigurationSubject subject,
        InfrastructureSettingId setting) => $"{subject.Value}/{setting.Value}";
}

/// <summary>Result of resolving explicit declarations, profiles, and conventions.</summary>
public sealed record InfrastructureConventionResolution
{
    /// <summary>Creates a convention resolution.</summary>
    /// <param name="configuration">Effective configuration values in deterministic order.</param>
    /// <param name="diagnostics">Structured ambiguity and validation diagnostics in deterministic order.</param>
    /// <exception cref="ArgumentException">
    /// A collection contains <see langword="null"/>, or a subject and setting receive more than one effective value.
    /// </exception>
    [JsonConstructor]
    public InfrastructureConventionResolution(
        ImmutableArray<InfrastructureEffectiveConfiguration> configuration = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Configuration = NormalizeConfiguration(configuration);
        Diagnostics = DocumentValidationDiagnostics.Normalize(diagnostics);
    }

    /// <summary>Effective values in deterministic subject and setting order.</summary>
    public ImmutableArray<InfrastructureEffectiveConfiguration> Configuration { get; }

    /// <summary>Structured diagnostics in deterministic order.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether resolution produced no error diagnostics.</summary>
    public bool IsValid => !Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <summary>Compares convention resolutions structurally.</summary>
    /// <param name="other">Other resolution.</param>
    /// <returns><see langword="true"/> when configuration and diagnostics are element-wise equal.</returns>
    public bool Equals(InfrastructureConventionResolution? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Configuration.SequenceEqual(other.Configuration)
        && Diagnostics.SequenceEqual(other.Diagnostics);

    /// <summary>Returns a structural hash code for this resolution.</summary>
    /// <returns>A hash code derived from configuration and diagnostics.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Configuration)
            hash.Add(item);
        foreach (var diagnostic in Diagnostics)
            hash.Add(diagnostic);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureEffectiveConfiguration> NormalizeConfiguration(
        ImmutableArray<InfrastructureEffectiveConfiguration> configuration)
    {
        if (configuration.IsDefaultOrEmpty)
            return [];
        if (configuration.Any(static item => item is null))
            throw new ArgumentException("Effective infrastructure configuration cannot contain null.", nameof(configuration));

        var ordered = configuration.Sort(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Subject.Value, right.Subject.Value);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Setting.Value, right.Setting.Value);
        });
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Subject == ordered[index].Subject
                && ordered[index - 1].Setting == ordered[index].Setting)
            {
                throw new ArgumentException(
                    $"Setting '{ordered[index].Setting.Value}' on '{ordered[index].Subject.Value}' is effective more than once.",
                    nameof(configuration));
            }
        }
        return ordered;
    }

}

/// <summary>Resolves infrastructure configuration using the shared deterministic authority precedence.</summary>
/// <remarks>
/// Same-tier candidates that supply the same canonical value converge. The ordinally first authority supplies the
/// retained attribution; same-tier candidates that supply different values remain an explicit ambiguity.
/// </remarks>
public static class InfrastructureConventionResolver
{
    const string ConventionResolutionStage = "infrastructure-convention-resolution";

    /// <summary>Stable diagnostic codes emitted by convention resolution.</summary>
    public static class DiagnosticCodes
    {
        /// <summary>Several equally authoritative candidates supply different values.</summary>
        public const string AmbiguousEffectiveValue = "infra.conventions.effectiveValue.ambiguous";
    }

    /// <summary>Resolves candidates contributed by the supplied profiles.</summary>
    /// <param name="profiles">Profiles whose candidates participate in resolution.</param>
    /// <returns>Effective values and any ambiguity diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A supplied profile is <see langword="null"/>.</exception>
    public static InfrastructureConventionResolution Resolve(
        IEnumerable<InfrastructureConventionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var candidates = new List<InfrastructureConfigurationCandidate>();
        foreach (var profile in profiles)
        {
            if (profile is null)
                throw new ArgumentException("Infrastructure convention profiles cannot contain null.", nameof(profiles));
            candidates.AddRange(profile.Candidates);
        }

        candidates.Sort(InfrastructureConfigurationCandidateComparer.Ordinal);
        var configuration = ImmutableArray.CreateBuilder<InfrastructureEffectiveConfiguration>();
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();

        for (var start = 0; start < candidates.Count;)
        {
            var end = start + 1;
            while (end < candidates.Count
                   && candidates[end].Subject == candidates[start].Subject
                   && candidates[end].Setting == candidates[start].Setting)
            {
                end++;
            }

            ResolveSetting(candidates, start, end, configuration, diagnostics);
            start = end;
        }

        return new(configuration.ToImmutable(), diagnostics.ToImmutable());
    }

    static void ResolveSetting(
        List<InfrastructureConfigurationCandidate> candidates,
        int start,
        int end,
        ImmutableArray<InfrastructureEffectiveConfiguration>.Builder configuration,
        ImmutableArray<DocumentValidationDiagnostic>.Builder diagnostics)
    {
        var winningOrigin = candidates[start].Origin;
        var winner = candidates[start];
        var ambiguous = false;
        for (var index = start + 1; index < end && candidates[index].Origin == winningOrigin; index++)
        {
            if (!string.Equals(candidates[index].Value, winner.Value, StringComparison.Ordinal))
            {
                ambiguous = true;
                break;
            }
        }

        if (ambiguous)
        {
            var authorities = new List<string>();
            for (var index = start; index < end && candidates[index].Origin == winningOrigin; index++)
            {
                if (!authorities.Contains(candidates[index].Authority, StringComparer.Ordinal))
                    authorities.Add(candidates[index].Authority);
            }
            authorities.Sort(StringComparer.Ordinal);
            var distinctValues = new HashSet<string>(StringComparer.Ordinal);
            for (var index = start; index < end && candidates[index].Origin == winningOrigin; index++)
                distinctValues.Add(candidates[index].Value);

            diagnostics.Add(new(
                DiagnosticCodes.AmbiguousEffectiveValue,
                DiagnosticSeverity.Error,
                $"Setting '{winner.Setting.Value}' on '{winner.Subject.Value}' has several equally authoritative candidates from {string.Join(", ", authorities)}.",
                Location: $"/configuration/{winner.Subject.Value}/{winner.Setting.Value}",
                SchemaLocation: InfrastructureEffectiveConfiguration.QualifiedSetting(winner.Subject, winner.Setting),
                Evidence: new(
                    stage: ConventionResolutionStage,
                    subject: InfrastructureEffectiveConfiguration.QualifiedSetting(winner.Subject, winner.Setting),
                    sourceReferences: [.. authorities],
                    resolutionOptions:
                    [
                        "Declare one explicit local value for the setting.",
                        "Change the equally authoritative profiles so they converge on one canonical value."
                    ],
                    expected: $"one canonical value at authority tier '{winningOrigin}'",
                    observed: $"{distinctValues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} different canonical values from {authorities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} authorities")));
            return;
        }

        configuration.Add(new(
            winner.Subject,
            winner.Setting,
            winner.Value,
            new(
                InfrastructureEffectiveConfiguration.QualifiedSetting(winner.Subject, winner.Setting),
                winner.Origin,
                winner.Authority)));
    }
}

sealed class InfrastructureConfigurationCandidateComparer : IComparer<InfrastructureConfigurationCandidate>
{
    internal static InfrastructureConfigurationCandidateComparer Ordinal { get; } = new();

    InfrastructureConfigurationCandidateComparer()
    {
    }

    public int Compare(InfrastructureConfigurationCandidate? x, InfrastructureConfigurationCandidate? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var comparison = StringComparer.Ordinal.Compare(x.Subject.Value, y.Subject.Value);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(x.Setting.Value, y.Setting.Value);
        if (comparison != 0)
            return comparison;
        comparison = x.Origin.CompareTo(y.Origin);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(x.Authority, y.Authority);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(x.Value, y.Value);
    }
}
