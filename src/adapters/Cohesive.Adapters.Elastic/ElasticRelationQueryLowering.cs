using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cohesive.Adapters.Elastic;

/// <summary>Canonical expression operation whose physical Elasticsearch realization may be selected by policy.</summary>
public enum ElasticQueryLoweringOperation
{
    /// <summary>Ordinal, case-sensitive text suffix comparison.</summary>
    Suffix = 0
}

/// <summary>Stable, versioned identity of one Elasticsearch query-lowering strategy.</summary>
public readonly record struct ElasticQueryLoweringStrategyId
{
    /// <summary>Creates a strategy identity.</summary>
    /// <param name="value">Stable, versioned identity text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or contains a control character.</exception>
    public ElasticQueryLoweringStrategyId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
        if (Value.Any(char.IsControl))
            throw new ArgumentException("An Elasticsearch lowering-strategy identity cannot contain control characters.", nameof(value));
    }

    /// <summary>Stable, versioned identity text.</summary>
    public string Value { get; }

    /// <summary>Returns the stable identity text.</summary>
    /// <returns>The value of <see cref="Value"/>, or an empty string for a default identity.</returns>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Source layer that contributed an Elasticsearch lowering preference.</summary>
public enum ElasticQueryLoweringPreferenceOrigin
{
    /// <summary>Framework-wide default with the lowest precedence.</summary>
    FrameworkDefault = 0,

    /// <summary>Default supplied by the Elasticsearch adapter.</summary>
    AdapterConvention = 1,

    /// <summary>Application or subsystem profile.</summary>
    ScopedProfile = 2,

    /// <summary>Explicit declaration local to the compilation request.</summary>
    ExplicitLocal = 3
}

/// <summary>Whether an unavailable preferred Elasticsearch lowering may fall back to another exact strategy.</summary>
public enum ElasticQueryLoweringFallbackPolicy
{
    /// <summary>Try later strategies in the declared preference order until one can preserve the semantics exactly.</summary>
    AllowExactFallback = 0,

    /// <summary>Require the first preferred strategy and fail when it is unavailable.</summary>
    RequirePreferred = 1
}

/// <summary>One attributable policy contribution for an Elasticsearch lowering operation.</summary>
public sealed record ElasticQueryLoweringPreference
{
    /// <summary>Creates a lowering preference contribution.</summary>
    /// <param name="operation">Canonical operation governed by the contribution.</param>
    /// <param name="origin">Configuration layer that supplied the contribution.</param>
    /// <param name="fallbackPolicy">Whether later exact strategies may be considered.</param>
    /// <param name="strategies">Strategy identities in intentional preference order.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="operation"/>, <paramref name="origin"/>, or <paramref name="fallbackPolicy"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="strategies"/> is empty, contains a default identity, or repeats an identity.
    /// </exception>
    public ElasticQueryLoweringPreference(
        ElasticQueryLoweringOperation operation,
        ElasticQueryLoweringPreferenceOrigin origin,
        ElasticQueryLoweringFallbackPolicy fallbackPolicy,
        ImmutableArray<ElasticQueryLoweringStrategyId> strategies)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported Elasticsearch lowering operation.");
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported Elasticsearch preference origin.");
        if (!Enum.IsDefined(fallbackPolicy))
            throw new ArgumentOutOfRangeException(nameof(fallbackPolicy), fallbackPolicy, "Unsupported Elasticsearch fallback policy.");

        var normalized = strategies.IsDefault ? [] : strategies;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("An Elasticsearch lowering preference requires at least one strategy.", nameof(strategies));
        if (normalized.Any(static strategy => string.IsNullOrWhiteSpace(strategy.Value)))
            throw new ArgumentException("An Elasticsearch lowering preference cannot contain a default strategy identity.", nameof(strategies));
        if (normalized.Distinct().Count() != normalized.Length)
            throw new ArgumentException("An Elasticsearch lowering preference cannot repeat a strategy identity.", nameof(strategies));

        Operation = operation;
        Origin = origin;
        FallbackPolicy = fallbackPolicy;
        Strategies = normalized;
    }

    /// <summary>Canonical operation governed by this contribution.</summary>
    public ElasticQueryLoweringOperation Operation { get; }

    /// <summary>Configuration layer that supplied this contribution.</summary>
    public ElasticQueryLoweringPreferenceOrigin Origin { get; }

    /// <summary>Whether later exact strategies may be considered.</summary>
    public ElasticQueryLoweringFallbackPolicy FallbackPolicy { get; }

    /// <summary>Strategy identities in intentional preference order.</summary>
    public ImmutableArray<ElasticQueryLoweringStrategyId> Strategies { get; }
}

/// <summary>Inputs supplied to an Elasticsearch query-lowering strategy.</summary>
public sealed class ElasticQueryLoweringContext
{
    /// <summary>Creates a lowering context for one canonical operation.</summary>
    /// <param name="operation">Canonical operation being lowered.</param>
    /// <param name="field">Physical field binding for the canonical value operand.</param>
    /// <param name="value">Reusable value template for the operation's text operand.</param>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> already has a physical transform or embeds a non-text constant.
    /// </exception>
    public ElasticQueryLoweringContext(
        ElasticQueryLoweringOperation operation,
        ElasticRelationQueryFieldBinding field,
        ElasticQueryValueTemplate value)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported Elasticsearch lowering operation.");
        Field = Guard.RequireNotNull(field);
        Value = Guard.RequireNotNull(value);
        if (value.Transform != ElasticQueryValueTransform.None)
            throw new ArgumentException("A lowering context requires an untransformed canonical value template.", nameof(value));
        if (value.SourceKind == ElasticQueryValueSourceKind.Constant
            && value.Constant.Kind != Cohesive.Model.ObservationValueKind.String)
        {
            throw new ArgumentException("A suffix-lowering constant must be canonical text.", nameof(value));
        }
        Operation = operation;
    }

    /// <summary>Canonical operation being lowered.</summary>
    public ElasticQueryLoweringOperation Operation { get; }

    /// <summary>Physical field binding for the canonical value operand.</summary>
    public ElasticRelationQueryFieldBinding Field { get; }

    /// <summary>
    /// Untransformed text value. Parameter-backed values have already been semantically proven to be text by the
    /// canonical compiler before this extension point is invoked.
    /// </summary>
    public ElasticQueryValueTemplate Value { get; }
}

/// <summary>Outcome returned by an Elasticsearch query-lowering strategy.</summary>
public enum ElasticQueryLoweringStrategyResultKind
{
    /// <summary>The strategy can preserve the requested semantics and emitted a physical query template.</summary>
    Eligible = 0,

    /// <summary>The strategy cannot preserve the requested semantics for the supplied binding.</summary>
    Ineligible = 1
}

/// <summary>Exact physical query or attributable ineligibility returned by a lowering strategy.</summary>
public sealed class ElasticQueryLoweringStrategyResult
{
    ElasticQueryLoweringStrategyResult(
        ElasticQueryLoweringStrategyResultKind kind,
        ElasticQueryTemplate? query,
        string explanation)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Elasticsearch lowering result kind.");
        if (kind == ElasticQueryLoweringStrategyResultKind.Eligible != (query is not null))
            throw new ArgumentException("Only an eligible Elasticsearch lowering result can retain a query template.", nameof(query));
        Kind = kind;
        Query = query;
        Explanation = Guard.RequireNotNullOrWhiteSpace(explanation);
    }

    /// <summary>Whether the strategy emitted a query or explained why it was ineligible.</summary>
    public ElasticQueryLoweringStrategyResultKind Kind { get; }

    /// <summary>Exact physical query template, or <see langword="null"/> when the strategy is ineligible.</summary>
    public ElasticQueryTemplate? Query { get; }

    /// <summary>Stable, human-readable eligibility or failure explanation.</summary>
    public string Explanation { get; }

    /// <summary>Creates an eligible strategy result.</summary>
    /// <param name="query">Exact physical query template emitted by the strategy.</param>
    /// <param name="explanation">Attributable explanation of the selected realization.</param>
    /// <returns>An eligible result containing <paramref name="query"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="explanation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="explanation"/> is empty.</exception>
    public static ElasticQueryLoweringStrategyResult Eligible(
        ElasticQueryTemplate query,
        string explanation) =>
        new(ElasticQueryLoweringStrategyResultKind.Eligible, Guard.RequireNotNull(query), explanation);

    /// <summary>Creates an ineligible strategy result.</summary>
    /// <param name="explanation">Actionable reason the strategy cannot preserve the requested semantics.</param>
    /// <returns>An ineligible result with no query template.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="explanation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="explanation"/> is empty.</exception>
    public static ElasticQueryLoweringStrategyResult Ineligible(string explanation) =>
        new(ElasticQueryLoweringStrategyResultKind.Ineligible, query: null, explanation);
}

/// <summary>Extension point for an exact, versioned Elasticsearch query-lowering strategy.</summary>
public interface IElasticQueryLoweringStrategy
{
    /// <summary>Stable identity that includes the strategy's semantic version.</summary>
    ElasticQueryLoweringStrategyId Id { get; }

    /// <summary>Canonical operation implemented by the strategy.</summary>
    ElasticQueryLoweringOperation Operation { get; }

    /// <summary>Attempts to lower one canonical operation without weakening its semantics.</summary>
    /// <param name="context">Canonical operand and physical field facts available to the strategy.</param>
    /// <returns>An exact physical query or an attributable ineligibility explanation.</returns>
    /// <remarks>
    /// Implementations must be deterministic and side-effect free for equivalent contexts. A stable strategy identity
    /// must change when emitted semantics or physical requirements change.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    ElasticQueryLoweringStrategyResult TryLower(ElasticQueryLoweringContext context);
}

/// <summary>Disposition of one configured strategy in an Elasticsearch lowering decision.</summary>
public enum ElasticQueryLoweringAttemptDisposition
{
    /// <summary>The strategy was eligible and selected.</summary>
    Selected = 0,

    /// <summary>The strategy was considered and rejected with an attributable explanation.</summary>
    Rejected = 1,

    /// <summary>The strategy was not evaluated because an earlier selection or fallback policy made it unnecessary.</summary>
    NotConsidered = 2
}

/// <summary>Attributable record of one preferred strategy's treatment during lowering.</summary>
public sealed record ElasticQueryLoweringAttempt
{
    /// <summary>Creates one lowering-attempt record.</summary>
    /// <param name="strategy">Stable identity of the configured strategy.</param>
    /// <param name="preferenceRank">Zero-based position in the effective preference.</param>
    /// <param name="disposition">How the strategy participated in the decision.</param>
    /// <param name="explanation">Stable explanation of the disposition.</param>
    /// <exception cref="ArgumentException"><paramref name="strategy"/> is a default identity or <paramref name="explanation"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preferenceRank"/> is negative or <paramref name="disposition"/> is unsupported.
    /// </exception>
    public ElasticQueryLoweringAttempt(
        ElasticQueryLoweringStrategyId strategy,
        int preferenceRank,
        ElasticQueryLoweringAttemptDisposition disposition,
        string explanation)
    {
        if (string.IsNullOrWhiteSpace(strategy.Value))
            throw new ArgumentException("An Elasticsearch lowering attempt requires a strategy identity.", nameof(strategy));
        if (preferenceRank < 0)
            throw new ArgumentOutOfRangeException(nameof(preferenceRank), preferenceRank, "A strategy preference rank cannot be negative.");
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported Elasticsearch lowering disposition.");
        Strategy = strategy;
        PreferenceRank = preferenceRank;
        Disposition = disposition;
        Explanation = Guard.RequireNotNullOrWhiteSpace(explanation);
    }

    /// <summary>Stable identity of the configured strategy.</summary>
    public ElasticQueryLoweringStrategyId Strategy { get; }

    /// <summary>Zero-based position in the effective preference.</summary>
    public int PreferenceRank { get; }

    /// <summary>How the strategy participated in the decision.</summary>
    public ElasticQueryLoweringAttemptDisposition Disposition { get; }

    /// <summary>Stable explanation of the disposition.</summary>
    public string Explanation { get; }
}

/// <summary>Fingerprint of one normalized Elasticsearch lowering policy or concrete decision.</summary>
public readonly record struct ElasticQueryLoweringFingerprint
{
    /// <summary>Creates a lowering fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">A string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A string is empty.</exception>
    public ElasticQueryLoweringFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }

    /// <summary>Returns the lowercase hexadecimal digest.</summary>
    /// <returns>The value of <see cref="Value"/>, or an empty string for a default fingerprint.</returns>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Selected and considered/rejected strategies for one Elasticsearch lowering operation.</summary>
public sealed class ElasticQueryLoweringDecision
{
    internal ElasticQueryLoweringDecision(
        ElasticQueryLoweringOperation operation,
        ElasticQueryLoweringPreferenceOrigin preferenceOrigin,
        ElasticQueryLoweringFallbackPolicy fallbackPolicy,
        ElasticQueryLoweringFingerprint policyFingerprint,
        ElasticQueryLoweringStrategyId preferredStrategy,
        ElasticQueryLoweringStrategyId? selectedStrategy,
        ImmutableArray<ElasticQueryLoweringAttempt> attempts)
    {
        Operation = operation;
        PreferenceOrigin = preferenceOrigin;
        FallbackPolicy = fallbackPolicy;
        PolicyFingerprint = policyFingerprint;
        PreferredStrategy = preferredStrategy;
        SelectedStrategy = selectedStrategy;
        Attempts = attempts;
        Fingerprint = ElasticQueryLoweringFingerprinter.ComputeDecision(this);
    }

    /// <summary>Canonical operation being lowered.</summary>
    public ElasticQueryLoweringOperation Operation { get; }

    /// <summary>Winning configuration layer under the documented precedence rules.</summary>
    public ElasticQueryLoweringPreferenceOrigin PreferenceOrigin { get; }

    /// <summary>Effective fallback behavior.</summary>
    public ElasticQueryLoweringFallbackPolicy FallbackPolicy { get; }

    /// <summary>Normalized policy fingerprint under which this decision was made.</summary>
    public ElasticQueryLoweringFingerprint PolicyFingerprint { get; }

    /// <summary>First strategy in the effective preference.</summary>
    public ElasticQueryLoweringStrategyId PreferredStrategy { get; }

    /// <summary>Selected exact strategy, or <see langword="null"/> when lowering failed.</summary>
    public ElasticQueryLoweringStrategyId? SelectedStrategy { get; }

    /// <summary>Every configured strategy with its considered, rejected, or skipped disposition.</summary>
    public ImmutableArray<ElasticQueryLoweringAttempt> Attempts { get; }

    /// <summary>Deterministic fingerprint over the complete decision and explanations.</summary>
    public ElasticQueryLoweringFingerprint Fingerprint { get; }

    /// <summary>Whether an exact strategy was selected.</summary>
    public bool IsSelected => SelectedStrategy is not null;

    internal void AppendCanonical(StringBuilder builder)
    {
        ElasticQueryLoweringFingerprinter.Append(builder, (int)Operation);
        ElasticQueryLoweringFingerprinter.Append(builder, (int)PreferenceOrigin);
        ElasticQueryLoweringFingerprinter.Append(builder, (int)FallbackPolicy);
        ElasticQueryLoweringFingerprinter.Append(builder, PolicyFingerprint.Algorithm);
        ElasticQueryLoweringFingerprinter.Append(builder, PolicyFingerprint.Canonicalization);
        ElasticQueryLoweringFingerprinter.Append(builder, PolicyFingerprint.Value);
        ElasticQueryLoweringFingerprinter.Append(builder, PreferredStrategy.Value);
        ElasticQueryLoweringFingerprinter.Append(builder, SelectedStrategy?.Value);
        ElasticQueryLoweringFingerprinter.Append(builder, Attempts.Length);
        foreach (var attempt in Attempts)
        {
            ElasticQueryLoweringFingerprinter.Append(builder, attempt.Strategy.Value);
            ElasticQueryLoweringFingerprinter.Append(builder, attempt.PreferenceRank);
            ElasticQueryLoweringFingerprinter.Append(builder, (int)attempt.Disposition);
            ElasticQueryLoweringFingerprinter.Append(builder, attempt.Explanation);
        }
    }
}

/// <summary>Physical query and attributable decision produced by policy-directed lowering.</summary>
public sealed class ElasticQueryLoweringResolution
{
    internal ElasticQueryLoweringResolution(
        ElasticQueryTemplate? query,
        ElasticQueryLoweringDecision decision)
    {
        if (decision.IsSelected != (query is not null))
            throw new ArgumentException("Only a successful Elasticsearch lowering decision can retain a query.", nameof(query));
        Query = query;
        Decision = decision;
    }

    /// <summary>Exact physical query template, or <see langword="null"/> when no strategy was selected.</summary>
    public ElasticQueryTemplate? Query { get; }

    /// <summary>Attributable preference and strategy decision.</summary>
    public ElasticQueryLoweringDecision Decision { get; }

    /// <summary>Whether lowering selected an exact physical strategy.</summary>
    public bool IsSuccessful => Query is not null;
}

/// <summary>
/// Normalized, attributable policy for selecting exact Elasticsearch query-lowering strategies.
/// </summary>
public sealed class ElasticQueryLoweringPolicy
{
    static readonly ImmutableArray<ElasticQueryLoweringPreference> ConventionalPreferences =
    [
        new(
            ElasticQueryLoweringOperation.Suffix,
            ElasticQueryLoweringPreferenceOrigin.FrameworkDefault,
            ElasticQueryLoweringFallbackPolicy.AllowExactFallback,
            [ElasticQueryLoweringStrategies.WildcardExactKeywordId]),
        new(
            ElasticQueryLoweringOperation.Suffix,
            ElasticQueryLoweringPreferenceOrigin.AdapterConvention,
            ElasticQueryLoweringFallbackPolicy.AllowExactFallback,
            [
                ElasticQueryLoweringStrategies.ReversedFieldPrefixId,
                ElasticQueryLoweringStrategies.WildcardExactKeywordId
            ])
    ];

    readonly ImmutableDictionary<ElasticQueryLoweringOperation, ElasticQueryLoweringPreference> effectiveByOperation;
    readonly ImmutableDictionary<ElasticQueryLoweringStrategyId, IElasticQueryLoweringStrategy> strategiesById;

    /// <summary>Creates a policy from strategy registrations and layered preference contributions.</summary>
    /// <param name="strategies">Versioned strategy implementations in any registration order.</param>
    /// <param name="preferences">
    /// Layer contributions in any order. Strategy order inside each contribution remains intentional.
    /// </param>
    /// <remarks>
    /// Contributions are normalized by operation and origin. The highest origin wins using
    /// explicit local &gt; scoped profile &gt; adapter convention &gt; framework default. Equivalent duplicate
    /// contributions collapse; conflicting contributions at the same origin are rejected, making composition
    /// independent of registration order.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A strategy registration is null, default, repeated, or declares an unsupported operation; a preference is
    /// null; same-origin preferences conflict; or an operation has no effective preference.
    /// </exception>
    public ElasticQueryLoweringPolicy(
        ImmutableArray<IElasticQueryLoweringStrategy> strategies,
        ImmutableArray<ElasticQueryLoweringPreference> preferences)
    {
        Strategies = NormalizeStrategies(strategies);
        strategiesById = Strategies.ToImmutableDictionary(static strategy => strategy.Id);
        Preferences = NormalizePreferences(preferences);
        effectiveByOperation = Preferences
            .GroupBy(static preference => preference.Operation)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.MaxBy(static preference => preference.Origin)!);

        foreach (var operation in Enum.GetValues<ElasticQueryLoweringOperation>())
        {
            if (!effectiveByOperation.ContainsKey(operation))
                throw new ArgumentException($"Elasticsearch lowering operation '{operation}' has no effective preference.", nameof(preferences));
        }

        EffectivePreferences =
        [
            .. effectiveByOperation.Values.OrderBy(static preference => preference.Operation)
        ];
        Fingerprint = ElasticQueryLoweringFingerprinter.ComputePolicy(this);
    }

    /// <summary>Conventional exact lowering policy supplied by the framework and Elasticsearch adapter.</summary>
    public static ElasticQueryLoweringPolicy Conventional { get; } = new(
        ElasticQueryLoweringStrategies.BuiltIn,
        ConventionalPreferences);

    /// <summary>Default exact lowering policy supplied by the framework and Elasticsearch adapter.</summary>
    public static ElasticQueryLoweringPolicy Default => Conventional;

    /// <summary>Registered strategy implementations sorted by stable identity.</summary>
    public ImmutableArray<IElasticQueryLoweringStrategy> Strategies { get; }

    /// <summary>All normalized configuration contributions sorted by operation and precedence layer.</summary>
    public ImmutableArray<ElasticQueryLoweringPreference> Preferences { get; }

    /// <summary>Winning preference for each operation after applying configuration precedence.</summary>
    public ImmutableArray<ElasticQueryLoweringPreference> EffectivePreferences { get; }

    /// <summary>Deterministic fingerprint over normalized strategy registrations and every policy contribution.</summary>
    public ElasticQueryLoweringFingerprint Fingerprint { get; }

    /// <summary>
    /// Creates a conventional policy augmented by extension strategies and higher-precedence preference contributions.
    /// </summary>
    /// <param name="additionalStrategies">Additional exact strategy implementations.</param>
    /// <param name="additionalPreferences">Additional scoped-profile or explicit-local preferences.</param>
    /// <returns>A normalized policy containing the built-in conventions and supplied extensions.</returns>
    /// <exception cref="ArgumentException">
    /// An extension conflicts with a built-in registration or a same-origin preference contribution.
    /// </exception>
    public static ElasticQueryLoweringPolicy CreateConventional(
        ImmutableArray<IElasticQueryLoweringStrategy> additionalStrategies = default,
        ImmutableArray<ElasticQueryLoweringPreference> additionalPreferences = default)
    {
        var normalizedStrategies = additionalStrategies.IsDefault ? [] : additionalStrategies;
        var normalizedPreferences = additionalPreferences.IsDefault ? [] : additionalPreferences;
        return new(
            [.. ElasticQueryLoweringStrategies.BuiltIn, .. normalizedStrategies],
            [.. ConventionalPreferences, .. normalizedPreferences]);
    }

    /// <summary>Returns the winning preference for one operation.</summary>
    /// <param name="operation">Canonical operation whose effective preference is requested.</param>
    /// <returns>The highest-precedence normalized contribution.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is unsupported.</exception>
    public ElasticQueryLoweringPreference GetEffectivePreference(ElasticQueryLoweringOperation operation)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported Elasticsearch lowering operation.");
        return effectiveByOperation[operation];
    }

    /// <summary>Selects and invokes an exact physical strategy according to the effective policy.</summary>
    /// <param name="context">Canonical operation and physical field evidence.</param>
    /// <returns>
    /// An exact physical query when one is available, plus the selected and considered/rejected strategy decision.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A registered extension returns <see langword="null"/> or reports an eligible result without a query.
    /// </exception>
    /// <remarks>Exceptions thrown by a registered extension strategy are propagated to the caller.</remarks>
    public ElasticQueryLoweringResolution Resolve(ElasticQueryLoweringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var preference = GetEffectivePreference(context.Operation);
        var attempts = ImmutableArray.CreateBuilder<ElasticQueryLoweringAttempt>(preference.Strategies.Length);
        ElasticQueryTemplate? selectedQuery = null;
        ElasticQueryLoweringStrategyId? selectedStrategy = null;

        for (var rank = 0; rank < preference.Strategies.Length; rank++)
        {
            var strategyId = preference.Strategies[rank];
            if (selectedStrategy is not null)
            {
                attempts.Add(new(
                    strategyId,
                    rank,
                    ElasticQueryLoweringAttemptDisposition.NotConsidered,
                    $"Strategy '{selectedStrategy.Value.Value}' was already selected."));
                continue;
            }
            if (rank > 0 && preference.FallbackPolicy == ElasticQueryLoweringFallbackPolicy.RequirePreferred)
            {
                attempts.Add(new(
                    strategyId,
                    rank,
                    ElasticQueryLoweringAttemptDisposition.NotConsidered,
                    "The effective policy requires the first preferred strategy and disables fallback."));
                continue;
            }
            if (!strategiesById.TryGetValue(strategyId, out var strategy))
            {
                attempts.Add(new(
                    strategyId,
                    rank,
                    ElasticQueryLoweringAttemptDisposition.Rejected,
                    $"Strategy '{strategyId.Value}' is preferred but is not registered."));
                continue;
            }
            if (strategy.Operation != context.Operation)
            {
                attempts.Add(new(
                    strategyId,
                    rank,
                    ElasticQueryLoweringAttemptDisposition.Rejected,
                    $"Strategy '{strategyId.Value}' implements '{strategy.Operation}', not '{context.Operation}'."));
                continue;
            }

            var result = strategy.TryLower(context)
                         ?? throw new InvalidOperationException($"Elasticsearch lowering strategy '{strategy.Id.Value}' returned null.");
            if (result.Kind == ElasticQueryLoweringStrategyResultKind.Ineligible)
            {
                attempts.Add(new(
                    strategyId,
                    rank,
                    ElasticQueryLoweringAttemptDisposition.Rejected,
                    result.Explanation));
                continue;
            }
            if (result.Query is null)
                throw new InvalidOperationException($"Eligible Elasticsearch lowering strategy '{strategy.Id.Value}' returned no query.");

            selectedQuery = result.Query;
            selectedStrategy = strategyId;
            attempts.Add(new(
                strategyId,
                rank,
                ElasticQueryLoweringAttemptDisposition.Selected,
                result.Explanation));
        }

        ElasticQueryLoweringDecision decision = new(
            context.Operation,
            preference.Origin,
            preference.FallbackPolicy,
            Fingerprint,
            preference.Strategies[0],
            selectedStrategy,
            attempts.MoveToImmutable());
        return new(selectedQuery, decision);
    }

    static ImmutableArray<IElasticQueryLoweringStrategy> NormalizeStrategies(
        ImmutableArray<IElasticQueryLoweringStrategy> strategies)
    {
        var normalized = strategies.IsDefault ? [] : strategies;
        if (normalized.Any(static strategy => strategy is null))
            throw new ArgumentException("Elasticsearch lowering strategies cannot contain null entries.", nameof(strategies));
        foreach (var strategy in normalized)
        {
            if (string.IsNullOrWhiteSpace(strategy.Id.Value))
                throw new ArgumentException("An Elasticsearch lowering strategy requires a stable identity.", nameof(strategies));
            if (!Enum.IsDefined(strategy.Operation))
                throw new ArgumentException($"Elasticsearch lowering strategy '{strategy.Id.Value}' declares an unsupported operation.", nameof(strategies));
        }
        if (normalized.GroupBy(static strategy => strategy.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Elasticsearch lowering strategy identities must be unique.", nameof(strategies));
        return [.. normalized.OrderBy(static strategy => strategy.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<ElasticQueryLoweringPreference> NormalizePreferences(
        ImmutableArray<ElasticQueryLoweringPreference> preferences)
    {
        var normalized = preferences.IsDefault ? [] : preferences;
        if (normalized.Any(static preference => preference is null))
            throw new ArgumentException("Elasticsearch lowering preferences cannot contain null entries.", nameof(preferences));

        List<ElasticQueryLoweringPreference> distinct = [];
        foreach (var group in normalized.GroupBy(static preference => (preference.Operation, preference.Origin)))
        {
            var candidates = group.ToArray();
            var first = candidates[0];
            if (candidates.Skip(1).Any(candidate => !Equivalent(first, candidate)))
            {
                throw new ArgumentException(
                    $"Elasticsearch lowering operation '{group.Key.Operation}' has conflicting '{group.Key.Origin}' preferences.",
                    nameof(preferences));
            }
            distinct.Add(first);
        }
        return
        [
            .. distinct
                .OrderBy(static preference => preference.Operation)
                .ThenBy(static preference => preference.Origin)
        ];

        static bool Equivalent(
            ElasticQueryLoweringPreference left,
            ElasticQueryLoweringPreference right) =>
            left.FallbackPolicy == right.FallbackPolicy
            && left.Strategies.SequenceEqual(right.Strategies);
    }
}

/// <summary>Stable built-in Elasticsearch query-lowering strategies.</summary>
public static class ElasticQueryLoweringStrategies
{
    /// <summary>Stable identity of the exact leading-wildcard suffix strategy.</summary>
    public static ElasticQueryLoweringStrategyId WildcardExactKeywordId { get; } = new(
        "cohesive.adapters.elastic/lowering/suffix/wildcard-exact-keyword/v1");

    /// <summary>Stable identity of the exact reversed-field prefix strategy.</summary>
    public static ElasticQueryLoweringStrategyId ReversedFieldPrefixId { get; } = new(
        "cohesive.adapters.elastic/lowering/suffix/reversed-field-prefix/v1");

    /// <summary>Exact leading-wildcard suffix strategy.</summary>
    public static IElasticQueryLoweringStrategy WildcardExactKeyword { get; } = new WildcardExactKeywordStrategy();

    /// <summary>Exact reversed-field prefix suffix strategy.</summary>
    public static IElasticQueryLoweringStrategy ReversedFieldPrefix { get; } = new ReversedFieldPrefixStrategy();

    /// <summary>All built-in strategies sorted by stable identity.</summary>
    public static ImmutableArray<IElasticQueryLoweringStrategy> BuiltIn { get; } =
    [
        ReversedFieldPrefix,
        WildcardExactKeyword
    ];

    sealed class WildcardExactKeywordStrategy : IElasticQueryLoweringStrategy
    {
        public ElasticQueryLoweringStrategyId Id => WildcardExactKeywordId;

        public ElasticQueryLoweringOperation Operation => ElasticQueryLoweringOperation.Suffix;

        public ElasticQueryLoweringStrategyResult TryLower(ElasticQueryLoweringContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Operation != Operation)
                return ElasticQueryLoweringStrategyResult.Ineligible("The leading-wildcard strategy implements only canonical suffix comparisons.");
            if (!context.Field.SemanticCapabilities.HasFlag(ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix))
            {
                return ElasticQueryLoweringStrategyResult.Ineligible(
                    "The field binding does not attest exact ordinal suffix semantics for a leading-wildcard query.");
            }
            if (context.Field.QueryField is not { } queryField)
                return ElasticQueryLoweringStrategyResult.Ineligible("The field binding has no indexed query field.");

            var physicalName = PhysicalName(queryField);
            return ElasticQueryLoweringStrategyResult.Eligible(
                ElasticQueryTemplate.Wildcard(
                    physicalName,
                    Transform(context.Value, ElasticQueryValueTransform.WildcardSuffix)),
                $"Binding capability '{ElasticRelationQueryFieldSemanticCapabilities.WildcardSuffix}' preserves ordinal suffix semantics on '{physicalName}'.");
        }
    }

    sealed class ReversedFieldPrefixStrategy : IElasticQueryLoweringStrategy
    {
        public ElasticQueryLoweringStrategyId Id => ReversedFieldPrefixId;

        public ElasticQueryLoweringOperation Operation => ElasticQueryLoweringOperation.Suffix;

        public ElasticQueryLoweringStrategyResult TryLower(ElasticQueryLoweringContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.Operation != Operation)
                return ElasticQueryLoweringStrategyResult.Ineligible("The reversed-field strategy implements only canonical suffix comparisons.");
            if (!context.Field.SemanticCapabilities.HasFlag(ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix))
            {
                return ElasticQueryLoweringStrategyResult.Ineligible(
                    "The field binding does not attest an exact reversed-field prefix realization for suffix semantics.");
            }
            if (context.Field.ReversedSuffixField is not { } reversedField)
                return ElasticQueryLoweringStrategyResult.Ineligible("The field binding has no reversed suffix field.");

            var physicalName = PhysicalName(reversedField);
            return ElasticQueryLoweringStrategyResult.Eligible(
                ElasticQueryTemplate.Prefix(
                    physicalName,
                    Transform(context.Value, ElasticQueryValueTransform.ReverseUnicodeScalars)),
                $"Binding capability '{ElasticRelationQueryFieldSemanticCapabilities.ReversedPrefixSuffix}' preserves ordinal suffix semantics on '{physicalName}'.");
        }
    }

    static ElasticQueryValueTemplate Transform(
        ElasticQueryValueTemplate value,
        ElasticQueryValueTransform transform) =>
        value.SourceKind switch
        {
            ElasticQueryValueSourceKind.Constant => ElasticQueryValueTemplate.FromConstant(value.Constant, transform),
            ElasticQueryValueSourceKind.Parameter when value.Parameter is { } parameter =>
                ElasticQueryValueTemplate.FromParameter(parameter, transform),
            _ => throw new InvalidOperationException("An Elasticsearch lowering value has no canonical source.")
        };

    static string PhysicalName(Cohesive.Model.FieldPath path) =>
        string.Join('.', path.Segments.Select(static segment => segment.Segment));
}

static class ElasticQueryLoweringFingerprinter
{
    const string Algorithm = "sha256";
    const string PolicyCanonicalization = "cohesive.relations.elastic-lowering-policy/v1-c14n/v1";
    const string DecisionCanonicalization = "cohesive.relations.elastic-lowering-decision/v1-c14n/v1";

    public static ElasticQueryLoweringFingerprint ComputePolicy(ElasticQueryLoweringPolicy policy)
    {
        StringBuilder canonical = new();
        Append(canonical, PolicyCanonicalization);
        Append(canonical, policy.Strategies.Length);
        foreach (var strategy in policy.Strategies)
        {
            Append(canonical, strategy.Id.Value);
            Append(canonical, (int)strategy.Operation);
        }
        Append(canonical, policy.Preferences.Length);
        foreach (var preference in policy.Preferences)
        {
            Append(canonical, (int)preference.Operation);
            Append(canonical, (int)preference.Origin);
            Append(canonical, (int)preference.FallbackPolicy);
            Append(canonical, preference.Strategies.Length);
            foreach (var strategy in preference.Strategies)
                Append(canonical, strategy.Value);
        }
        return Hash(PolicyCanonicalization, canonical);
    }

    public static ElasticQueryLoweringFingerprint ComputeDecision(ElasticQueryLoweringDecision decision)
    {
        StringBuilder canonical = new();
        Append(canonical, DecisionCanonicalization);
        decision.AppendCanonical(canonical);
        return Hash(DecisionCanonicalization, canonical);
    }

    public static void Append(StringBuilder builder, string? value)
    {
        builder
            .Append(value?.Length ?? -1)
            .Append(':')
            .Append(value)
            .Append(';');
    }

    public static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));

    static ElasticQueryLoweringFingerprint Hash(string canonicalization, StringBuilder canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new(Algorithm, canonicalization, Convert.ToHexStringLower(bytes));
    }
}
