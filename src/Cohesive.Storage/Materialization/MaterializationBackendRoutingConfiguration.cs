using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable setting names in the v1 backend-pool routing configuration.</summary>
public static class MaterializationBackendRoutingSettingNames
{
    /// <summary>Target selected for newly admitted reads.</summary>
    public const string ReadTarget = "readTarget";

    /// <summary>Target selected for newly admitted writes.</summary>
    public const string WriteTarget = "writeTarget";

    internal static ImmutableArray<string> All { get; } = [ReadTarget, WriteTarget];
}

/// <summary>Partial independent read/write target settings supplied by one precedence layer.</summary>
public sealed record MaterializationBackendRoutingSettings
{
    /// <summary>Creates a partial backend-routing settings layer.</summary>
    /// <param name="readTarget">Optional exact target selected for newly admitted reads.</param>
    /// <param name="writeTarget">Optional exact target selected for newly admitted writes.</param>
    /// <exception cref="ArgumentException">A supplied target identity is default.</exception>
    [JsonConstructor]
    public MaterializationBackendRoutingSettings(
        MaterializationTargetId? readTarget = null,
        MaterializationTargetId? writeTarget = null)
    {
        if (readTarget is { } selectedRead)
            MaterializationContract.RequireDefinedIdentity(selectedRead.Value, nameof(readTarget));
        if (writeTarget is { } selectedWrite)
            MaterializationContract.RequireDefinedIdentity(selectedWrite.Value, nameof(writeTarget));
        ReadTarget = readTarget;
        WriteTarget = writeTarget;
    }

    /// <summary>Optional exact target selected for newly admitted reads.</summary>
    public MaterializationTargetId? ReadTarget { get; }

    /// <summary>Optional exact target selected for newly admitted writes.</summary>
    public MaterializationTargetId? WriteTarget { get; }

    internal bool IsEmpty => ReadTarget is null && WriteTarget is null;
}

/// <summary>One attributable partial backend-routing configuration layer.</summary>
public sealed record MaterializationBackendRoutingConfigurationLayer
{
    /// <summary>Creates one partial read/write routing layer.</summary>
    /// <param name="origin">Explicit, scoped-profile, or adapter-convention tier.</param>
    /// <param name="authority">Stable identity and version of the supplying authority.</param>
    /// <param name="settings">One or both exact target settings supplied by the layer.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="origin"/> is unsupported or attempts to replace the framework-default layer.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authority"/> or <paramref name="settings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authority"/> is empty or <paramref name="settings"/> supplies no value.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendRoutingConfigurationLayer(
        EffectiveConfigurationOrigin origin,
        string authority,
        MaterializationBackendRoutingSettings settings)
    {
        if (!Enum.IsDefined(origin) || origin == EffectiveConfigurationOrigin.FrameworkDefault)
        {
            throw new ArgumentOutOfRangeException(
                nameof(origin),
                origin,
                "A supplied backend-routing layer must be explicit, scoped, or an adapter convention.");
        }

        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (settings.IsEmpty)
            throw new ArgumentException("A backend-routing layer must supply at least one target.", nameof(settings));
    }

    /// <summary>Configuration-precedence tier.</summary>
    public EffectiveConfigurationOrigin Origin { get; }

    /// <summary>Stable identity and version of the supplying authority.</summary>
    public string Authority { get; }

    /// <summary>Partial read/write target settings supplied by this layer.</summary>
    public MaterializationBackendRoutingSettings Settings { get; }
}

/// <summary>Fully resolved independent read/write targets with complete effective-configuration explanation.</summary>
public sealed record MaterializationBackendRoutingConfiguration
{
    /// <summary>Creates one complete attributable backend-routing configuration.</summary>
    /// <param name="readTarget">Exact target selected for newly admitted reads.</param>
    /// <param name="writeTarget">Exact target selected for newly admitted writes.</param>
    /// <param name="configuration">Exactly one effective-configuration decision for each known setting.</param>
    /// <exception cref="ArgumentException">
    /// A target is default or provenance is absent, null, duplicated, or does not cover every known setting.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendRoutingConfiguration(
        MaterializationTargetId readTarget,
        MaterializationTargetId writeTarget,
        ImmutableArray<EffectiveConfigurationDecision> configuration)
    {
        MaterializationContract.RequireDefinedIdentity(readTarget.Value, nameof(readTarget));
        MaterializationContract.RequireDefinedIdentity(writeTarget.Value, nameof(writeTarget));
        if (configuration.IsDefault || configuration.Any(static decision => decision is null))
        {
            throw new ArgumentException(
                "A resolved backend-routing configuration requires complete non-null provenance.",
                nameof(configuration));
        }

        if (configuration.GroupBy(static decision => decision.Setting).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "A resolved backend-routing configuration cannot repeat setting provenance.",
                nameof(configuration));
        }

        var suppliedNames = configuration
            .Select(static decision => decision.Setting)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!suppliedNames.SequenceEqual(MaterializationBackendRoutingSettingNames.All))
        {
            throw new ArgumentException(
                "A resolved backend-routing configuration must attribute every known setting exactly once.",
                nameof(configuration));
        }

        ReadTarget = readTarget;
        WriteTarget = writeTarget;
        Configuration = [.. configuration.OrderBy(static decision => decision.Setting, StringComparer.Ordinal)];
    }

    /// <summary>Exact target selected for newly admitted reads.</summary>
    public MaterializationTargetId ReadTarget { get; }

    /// <summary>Exact target selected for newly admitted writes.</summary>
    public MaterializationTargetId WriteTarget { get; }

    /// <summary>Complete per-setting provenance in deterministic setting order.</summary>
    public ImmutableArray<EffectiveConfigurationDecision> Configuration { get; }

    /// <summary>Compares configurations structurally, including effective provenance.</summary>
    /// <param name="other">Configuration to compare.</param>
    /// <returns><see langword="true"/> when effective values and attributions are equal.</returns>
    public bool Equals(MaterializationBackendRoutingConfiguration? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && ReadTarget == other.ReadTarget
        && WriteTarget == other.WriteTarget
        && Configuration.SequenceEqual(other.Configuration);

    /// <summary>Returns a structural hash code for effective values and provenance.</summary>
    /// <returns>A hash derived from both target selections and every attribution.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ReadTarget);
        hash.Add(WriteTarget);
        foreach (var decision in Configuration)
            hash.Add(decision);
        return hash.ToHashCode();
    }
}

/// <summary>Deterministic per-setting precedence resolver for materialization backend routing.</summary>
public static class MaterializationBackendRoutingConfigurationResolver
{
    /// <summary>Stable v1 authority for the optional pool default-target convention.</summary>
    public const string FrameworkConventionAuthority =
        "cohesive.storage/materialization-backend-routing/default-target/v1";

    /// <summary>Resolves independent read/write targets from explicit configuration layers and the pool default.</summary>
    /// <param name="definition">Canonical backend-pool definition constraining every selected target.</param>
    /// <param name="layers">
    /// Optional explicit, scoped-profile, and adapter-convention layers. Input order has no effect.
    /// </param>
    /// <returns>Complete read/write selection with one attributable decision per setting.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="layers"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A layer is null, two layers supply the same setting at one precedence tier, or a selected target is outside
    /// <paramref name="definition"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A setting is unresolved and the pool declares no safe default target.
    /// </exception>
    public static MaterializationBackendRoutingConfiguration Resolve(
        MaterializationBackendPoolDefinition definition,
        params MaterializationBackendRoutingConfigurationLayer[] layers)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Any(static layer => layer is null))
            throw new ArgumentException("Backend-routing layers cannot contain null entries.", nameof(layers));

        var read = Select(
            definition,
            layers,
            static settings => settings.ReadTarget,
            MaterializationBackendRoutingSettingNames.ReadTarget);
        var write = Select(
            definition,
            layers,
            static settings => settings.WriteTarget,
            MaterializationBackendRoutingSettingNames.WriteTarget);
        return new(
            read.Target,
            write.Target,
            [read.Decision, write.Decision]);
    }

    static ResolvedTarget Select(
        MaterializationBackendPoolDefinition definition,
        IReadOnlyList<MaterializationBackendRoutingConfigurationLayer> layers,
        Func<MaterializationBackendRoutingSettings, MaterializationTargetId?> selector,
        string setting)
    {
        if (layers.Where(layer => selector(layer.Settings) is not null)
            .GroupBy(static layer => layer.Origin)
            .Any(static group => group.Skip(1).Any()))
        {
            throw new ArgumentException(
                $"Backend-routing setting '{setting}' is supplied more than once at one precedence tier.",
                nameof(layers));
        }

        MaterializationBackendRoutingConfigurationLayer? selected = null;
        MaterializationTargetId? target = null;
        foreach (var layer in layers)
        {
            var candidate = selector(layer.Settings);
            if (candidate is null)
                continue;

            if (selected is null || layer.Origin < selected.Origin)
            {
                selected = layer;
                target = candidate;
                continue;
            }

        }

        EffectiveConfigurationDecision decision;
        if (selected is null)
        {
            target = definition.DefaultTarget
                ?? throw new InvalidOperationException(
                    $"Backend-routing setting '{setting}' is unresolved and pool '{definition.Id.Value}' declares no safe default target.");
            decision = new(
                setting,
                EffectiveConfigurationOrigin.FrameworkDefault,
                FrameworkConventionAuthority);
        }
        else
        {
            decision = new(setting, selected.Origin, selected.Authority);
        }

        var resolved = target.GetValueOrDefault();
        if (!definition.Members.Any(member => member.Id == resolved))
        {
            throw new ArgumentException(
                $"Backend-routing setting '{setting}' selects target '{resolved.Value}', which is not a member of pool '{definition.Id.Value}'.",
                nameof(layers));
        }

        return new(resolved, decision);
    }

    readonly record struct ResolvedTarget(
        MaterializationTargetId Target,
        EffectiveConfigurationDecision Decision);
}
