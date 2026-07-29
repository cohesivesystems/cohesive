using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>Stable setting names in the v1 AIMD controller policy.</summary>
public static class AimdControlPolicySettingsNames
{
    /// <summary>Additive recovery step.</summary>
    public const string AdditiveIncrease = "additiveIncrease";

    /// <summary>Multiplicative congestion factor in basis points.</summary>
    public const string MultiplicativeDecrease = "multiplicativeDecrease";

    /// <summary>Consecutive healthy observations required before recovery.</summary>
    public const string HealthyObservationCount = "healthyObservationCount";

    /// <summary>Recovery cooldown after a decrease.</summary>
    public const string RecoveryCooldown = "recoveryCooldown";

    /// <summary>Minimum time between applied operating-point changes.</summary>
    public const string MinimumDwell = "minimumDwell";

    /// <summary>Maximum accepted observation age.</summary>
    public const string MaximumObservationAge = "maximumObservationAge";

    /// <summary>Minimum samples required for each objective measurement.</summary>
    public const string MinimumSampleCount = "minimumSampleCount";

    internal static ImmutableArray<string> All { get; } =
    [
        AdditiveIncrease,
        HealthyObservationCount,
        MaximumObservationAge,
        MinimumDwell,
        MinimumSampleCount,
        MultiplicativeDecrease,
        RecoveryCooldown
    ];
}

/// <summary>Partial settings supplied by one deterministic configuration-precedence layer.</summary>
public sealed record AimdControlPolicySettings
{
    /// <summary>Creates a partial AIMD settings layer.</summary>
    /// <param name="additiveIncrease">Optional positive actuator-unit increase step.</param>
    /// <param name="multiplicativeDecreaseBasisPoints">Optional factor in the inclusive range 1 through 9,999.</param>
    /// <param name="healthyObservationCount">Optional consecutive healthy evidence count.</param>
    /// <param name="recoveryCooldownMilliseconds">Optional non-negative recovery cooldown.</param>
    /// <param name="minimumDwellMilliseconds">Optional non-negative minimum dwell.</param>
    /// <param name="maximumObservationAgeMilliseconds">Optional positive maximum observation age.</param>
    /// <param name="minimumSampleCount">Optional positive minimum sample count.</param>
    /// <exception cref="ArgumentOutOfRangeException">A supplied value is outside its supported range.</exception>
    [JsonConstructor]
    public AimdControlPolicySettings(
        long? additiveIncrease = null,
        long? multiplicativeDecreaseBasisPoints = null,
        long? healthyObservationCount = null,
        long? recoveryCooldownMilliseconds = null,
        long? minimumDwellMilliseconds = null,
        long? maximumObservationAgeMilliseconds = null,
        long? minimumSampleCount = null)
    {
        ValidatePositive(additiveIncrease, nameof(additiveIncrease));
        if (multiplicativeDecreaseBasisPoints is not null
            && multiplicativeDecreaseBasisPoints is <= 0 or >= 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(multiplicativeDecreaseBasisPoints),
                multiplicativeDecreaseBasisPoints,
                "A multiplicative-decrease factor must be between zero and one whole.");
        }
        ValidatePositive(healthyObservationCount, nameof(healthyObservationCount));
        ValidateNonNegative(recoveryCooldownMilliseconds, nameof(recoveryCooldownMilliseconds));
        ValidateNonNegative(minimumDwellMilliseconds, nameof(minimumDwellMilliseconds));
        ValidatePositive(maximumObservationAgeMilliseconds, nameof(maximumObservationAgeMilliseconds));
        ValidatePositive(minimumSampleCount, nameof(minimumSampleCount));

        AdditiveIncrease = additiveIncrease;
        MultiplicativeDecreaseBasisPoints = multiplicativeDecreaseBasisPoints;
        HealthyObservationCount = healthyObservationCount;
        RecoveryCooldownMilliseconds = recoveryCooldownMilliseconds;
        MinimumDwellMilliseconds = minimumDwellMilliseconds;
        MaximumObservationAgeMilliseconds = maximumObservationAgeMilliseconds;
        MinimumSampleCount = minimumSampleCount;
    }

    /// <summary>Optional positive actuator-unit increase step.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? AdditiveIncrease { get; }

    /// <summary>Optional multiplicative decrease factor in basis points.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? MultiplicativeDecreaseBasisPoints { get; }

    /// <summary>Optional consecutive healthy evidence count.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? HealthyObservationCount { get; }

    /// <summary>Optional recovery cooldown in milliseconds.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? RecoveryCooldownMilliseconds { get; }

    /// <summary>Optional minimum dwell in milliseconds.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? MinimumDwellMilliseconds { get; }

    /// <summary>Optional maximum observation age in milliseconds.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? MaximumObservationAgeMilliseconds { get; }

    /// <summary>Optional minimum sample count.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? MinimumSampleCount { get; }

    internal bool IsEmpty =>
        AdditiveIncrease is null
        && MultiplicativeDecreaseBasisPoints is null
        && HealthyObservationCount is null
        && RecoveryCooldownMilliseconds is null
        && MinimumDwellMilliseconds is null
        && MaximumObservationAgeMilliseconds is null
        && MinimumSampleCount is null;

    static void ValidatePositive(long? value, string parameterName)
    {
        if (value is <= 0 or > ControlQuantity.MaximumPortableValue)
            throw new ArgumentOutOfRangeException(parameterName, value, "A supplied policy value must be positive and portable.");
    }

    static void ValidateNonNegative(long? value, string parameterName)
    {
        if (value is < 0 or > ControlQuantity.MaximumPortableValue)
            throw new ArgumentOutOfRangeException(parameterName, value, "A supplied duration must be non-negative and portable.");
    }
}

/// <summary>One attributable partial policy layer considered by deterministic precedence resolution.</summary>
public sealed record AimdControlPolicyLayer
{
    /// <summary>Creates a partial policy layer.</summary>
    /// <param name="origin">Explicit, scoped-profile, or adapter-convention tier.</param>
    /// <param name="authority">Stable identity and version of the supplying authority.</param>
    /// <param name="settings">One or more settings supplied by the layer.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="origin"/> is unsupported or attempts to replace the framework convention layer.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authority"/> or <paramref name="settings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="authority"/> is empty or the settings layer is empty.</exception>
    [JsonConstructor]
    public AimdControlPolicyLayer(
        EffectiveConfigurationOrigin origin,
        string authority,
        AimdControlPolicySettings settings)
    {
        if (!Enum.IsDefined(origin) || origin == EffectiveConfigurationOrigin.FrameworkDefault)
        {
            throw new ArgumentOutOfRangeException(
                nameof(origin),
                origin,
                "A supplied layer must be explicit, scoped, or an adapter convention.");
        }

        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        Settings = Guard.RequireNotNull(settings);
        if (settings.IsEmpty)
            throw new ArgumentException("A policy layer must supply at least one setting.", nameof(settings));
    }

    /// <summary>Configuration-precedence tier.</summary>
    public EffectiveConfigurationOrigin Origin { get; }

    /// <summary>Stable identity and version of the authority.</summary>
    public string Authority { get; }

    /// <summary>Partial values supplied by this layer.</summary>
    public AimdControlPolicySettings Settings { get; }
}

/// <summary>Fully resolved, attributable bounded additive-increase/multiplicative-decrease policy.</summary>
public sealed record AimdControlPolicy
{
    /// <summary>Creates a fully resolved AIMD policy.</summary>
    /// <param name="actuator">Single actuator adjusted by the policy.</param>
    /// <param name="additiveIncrease">Positive recovery step in the actuator's canonical unit.</param>
    /// <param name="multiplicativeDecreaseBasisPoints">Congestion factor from 1 through 9,999 basis points.</param>
    /// <param name="healthyObservationCount">Consecutive healthy observations required before recovery.</param>
    /// <param name="recoveryCooldownMilliseconds">Cooldown after a decrease during which increases are blocked.</param>
    /// <param name="minimumDwellMilliseconds">Minimum time between applied changes.</param>
    /// <param name="maximumObservationAgeMilliseconds">Maximum accepted observation age.</param>
    /// <param name="minimumSampleCount">Minimum samples required by every objective measurement.</param>
    /// <param name="configuration">Complete per-setting provenance.</param>
    /// <exception cref="ArgumentException">A value/unit is invalid or provenance is missing, duplicated, or unknown.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="actuator"/> is unsupported or a numeric policy value is outside its supported range.
    /// </exception>
    [JsonConstructor]
    public AimdControlPolicy(
        ControlActuatorKind actuator,
        ControlQuantity additiveIncrease,
        long multiplicativeDecreaseBasisPoints,
        long healthyObservationCount,
        long recoveryCooldownMilliseconds,
        long minimumDwellMilliseconds,
        long maximumObservationAgeMilliseconds,
        long minimumSampleCount,
        ImmutableArray<EffectiveConfigurationDecision> configuration)
    {
        var unit = ControlUnitCatalog.ForActuator(actuator);
        if (additiveIncrease.Unit != unit || additiveIncrease.Value == 0)
            throw new ArgumentException($"AIMD increase for '{actuator}' must be positive and use unit '{unit}'.", nameof(additiveIncrease));
        if (multiplicativeDecreaseBasisPoints is <= 0 or >= 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(multiplicativeDecreaseBasisPoints),
                multiplicativeDecreaseBasisPoints,
                "A multiplicative-decrease factor must be between zero and one whole.");
        }
        RequirePositive(healthyObservationCount, nameof(healthyObservationCount));
        RequireNonNegative(recoveryCooldownMilliseconds, nameof(recoveryCooldownMilliseconds));
        RequireNonNegative(minimumDwellMilliseconds, nameof(minimumDwellMilliseconds));
        RequirePositive(maximumObservationAgeMilliseconds, nameof(maximumObservationAgeMilliseconds));
        RequirePositive(minimumSampleCount, nameof(minimumSampleCount));

        if (configuration.IsDefault || configuration.Any(static decision => decision is null))
            throw new ArgumentException("A resolved AIMD policy requires complete non-null provenance.", nameof(configuration));
        if (configuration.GroupBy(static decision => decision.Setting).Any(static group => group.Count() > 1))
            throw new ArgumentException("A resolved AIMD policy cannot repeat setting provenance.", nameof(configuration));
        var suppliedNames = configuration.Select(static decision => decision.Setting).Order(StringComparer.Ordinal).ToArray();
        if (!suppliedNames.SequenceEqual(AimdControlPolicySettingsNames.All))
            throw new ArgumentException("A resolved AIMD policy must attribute every known setting exactly once.", nameof(configuration));

        Actuator = actuator;
        AdditiveIncrease = additiveIncrease;
        MultiplicativeDecreaseBasisPoints = multiplicativeDecreaseBasisPoints;
        HealthyObservationCount = healthyObservationCount;
        RecoveryCooldownMilliseconds = recoveryCooldownMilliseconds;
        MinimumDwellMilliseconds = minimumDwellMilliseconds;
        MaximumObservationAgeMilliseconds = maximumObservationAgeMilliseconds;
        MinimumSampleCount = minimumSampleCount;
        Configuration = [.. configuration.OrderBy(static decision => decision.Setting, StringComparer.Ordinal)];
    }

    /// <summary>Single actuator adjusted by this policy.</summary>
    public ControlActuatorKind Actuator { get; }

    /// <summary>Positive additive recovery step.</summary>
    public ControlQuantity AdditiveIncrease { get; }

    /// <summary>Multiplicative congestion factor in basis points.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MultiplicativeDecreaseBasisPoints { get; }

    /// <summary>Required consecutive healthy-observation count.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long HealthyObservationCount { get; }

    /// <summary>Recovery cooldown in milliseconds.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long RecoveryCooldownMilliseconds { get; }

    /// <summary>Minimum dwell between applied changes in milliseconds.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MinimumDwellMilliseconds { get; }

    /// <summary>Maximum accepted observation age in milliseconds.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumObservationAgeMilliseconds { get; }

    /// <summary>Minimum sample count required for each objective.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MinimumSampleCount { get; }

    /// <summary>Complete per-setting provenance in deterministic setting order.</summary>
    public ImmutableArray<EffectiveConfigurationDecision> Configuration { get; }

    /// <summary>Compares policies structurally, including provenance.</summary>
    /// <param name="other">Policy to compare.</param>
    /// <returns><see langword="true"/> when all effective values and attributions are equal.</returns>
    public bool Equals(AimdControlPolicy? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Actuator == other.Actuator
        && AdditiveIncrease == other.AdditiveIncrease
        && MultiplicativeDecreaseBasisPoints == other.MultiplicativeDecreaseBasisPoints
        && HealthyObservationCount == other.HealthyObservationCount
        && RecoveryCooldownMilliseconds == other.RecoveryCooldownMilliseconds
        && MinimumDwellMilliseconds == other.MinimumDwellMilliseconds
        && MaximumObservationAgeMilliseconds == other.MaximumObservationAgeMilliseconds
        && MinimumSampleCount == other.MinimumSampleCount
        && Configuration.SequenceEqual(other.Configuration);

    /// <summary>Returns a structural hash code.</summary>
    /// <returns>A hash derived from effective values and provenance.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Actuator);
        hash.Add(AdditiveIncrease);
        hash.Add(MultiplicativeDecreaseBasisPoints);
        hash.Add(HealthyObservationCount);
        hash.Add(RecoveryCooldownMilliseconds);
        hash.Add(MinimumDwellMilliseconds);
        hash.Add(MaximumObservationAgeMilliseconds);
        hash.Add(MinimumSampleCount);
        foreach (var decision in Configuration)
            hash.Add(decision);
        return hash.ToHashCode();
    }

    static void RequirePositive(long value, string parameterName)
    {
        if (value is <= 0 or > ControlQuantity.MaximumPortableValue)
            throw new ArgumentOutOfRangeException(parameterName, value, "A policy value must be positive and portable.");
    }

    static void RequireNonNegative(long value, string parameterName)
    {
        if (value is < 0 or > ControlQuantity.MaximumPortableValue)
            throw new ArgumentOutOfRangeException(parameterName, value, "A policy duration must be non-negative and portable.");
    }
}

/// <summary>Deterministic convention and precedence resolver for the reference AIMD policy.</summary>
public static class AimdControlPolicyResolver
{
    /// <summary>Stable framework-wide v1 convention authority.</summary>
    public const string FrameworkConventionAuthority = "cohesive.control/aimd-conventions-v1";

    /// <summary>Resolves framework defaults and optional precedence layers into one attributable policy.</summary>
    /// <param name="actuator">Single actuator controlled by the resulting policy.</param>
    /// <param name="layers">
    /// Optional explicit, scoped-profile, and adapter-convention layers. Input order has no effect.
    /// </param>
    /// <returns>A fully resolved deterministic policy with provenance for every setting.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A layer is null or repeats a precedence tier.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="actuator"/> is unsupported.</exception>
    public static AimdControlPolicy Resolve(
        ControlActuatorKind actuator,
        params AimdControlPolicyLayer[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Any(static layer => layer is null))
            throw new ArgumentException("AIMD policy layers cannot contain null entries.", nameof(layers));
        if (layers.GroupBy(static layer => layer.Origin).Any(static group => group.Count() > 1))
            throw new ArgumentException("AIMD policy layers cannot repeat a precedence tier.", nameof(layers));

        var unit = ControlUnitCatalog.ForActuator(actuator);
        var additive = Select(
            layers,
            static settings => settings.AdditiveIncrease,
            1,
            AimdControlPolicySettingsNames.AdditiveIncrease);
        var decrease = Select(
            layers,
            static settings => settings.MultiplicativeDecreaseBasisPoints,
            5_000,
            AimdControlPolicySettingsNames.MultiplicativeDecrease);
        var healthy = Select(
            layers,
            static settings => settings.HealthyObservationCount,
            5,
            AimdControlPolicySettingsNames.HealthyObservationCount);
        var cooldown = Select(
            layers,
            static settings => settings.RecoveryCooldownMilliseconds,
            30_000,
            AimdControlPolicySettingsNames.RecoveryCooldown);
        var dwell = Select(
            layers,
            static settings => settings.MinimumDwellMilliseconds,
            5_000,
            AimdControlPolicySettingsNames.MinimumDwell);
        var maximumAge = Select(
            layers,
            static settings => settings.MaximumObservationAgeMilliseconds,
            60_000,
            AimdControlPolicySettingsNames.MaximumObservationAge);
        var minimumSamples = Select(
            layers,
            static settings => settings.MinimumSampleCount,
            1,
            AimdControlPolicySettingsNames.MinimumSampleCount);

        return new(
            actuator,
            new(additive.Value, unit),
            decrease.Value,
            healthy.Value,
            cooldown.Value,
            dwell.Value,
            maximumAge.Value,
            minimumSamples.Value,
            [
                additive.Decision,
                decrease.Decision,
                healthy.Decision,
                cooldown.Decision,
                dwell.Decision,
                maximumAge.Decision,
                minimumSamples.Decision
            ]);
    }

    static ResolvedSetting Select(
        IReadOnlyList<AimdControlPolicyLayer> layers,
        Func<AimdControlPolicySettings, long?> selector,
        long conventionValue,
        string setting)
    {
        AimdControlPolicyLayer? selected = null;
        long? value = null;
        foreach (var layer in layers)
        {
            var candidate = selector(layer.Settings);
            if (candidate is null || selected is not null && selected.Origin <= layer.Origin)
                continue;

            selected = layer;
            value = candidate;
        }

        return selected is null
            ? new(
                conventionValue,
                new(setting, EffectiveConfigurationOrigin.FrameworkDefault, FrameworkConventionAuthority))
            : new(value!.Value, new(setting, selected.Origin, selected.Authority));
    }

    readonly record struct ResolvedSetting(long Value, EffectiveConfigurationDecision Decision);
}
