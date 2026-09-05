using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

/// <summary>Outcome assigned by a local property evaluator to one generated or shrunk case.</summary>
public enum PropertyCaseDisposition
{
    /// <summary>The case satisfies the property and counts toward the requested successful-case bound.</summary>
    Passed = 0,

    /// <summary>The case violates the property and is eligible for semantic shrinking.</summary>
    Failed = 1,

    /// <summary>The case does not meet a local precondition and counts against the discard bound.</summary>
    Discarded = 2
}

/// <summary>Stable caller-authored classification attached to one property-case evaluation.</summary>
public readonly record struct PropertyCaseClassification
{
    /// <summary>Creates a case classification.</summary>
    /// <param name="value">Stable nonempty classification identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public PropertyCaseClassification(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Gets the exact ordinal classification identity.</summary>
    public string Value { get; }

    /// <summary>Returns the classification identity.</summary>
    /// <returns>The exact value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Local disposition and classifications returned for one property case.</summary>
/// <remarks>
/// Evaluation callbacks are runtime interpretation policy. They are expected to be deterministic for the supplied
/// observation or typed value and are never retained in canonical generation IR.
/// </remarks>
public sealed record PropertyCaseEvaluation
{
    static readonly PropertyCaseEvaluation PassedWithoutClassifications = new(PropertyCaseDisposition.Passed, []);
    static readonly PropertyCaseEvaluation FailedWithoutClassifications = new(PropertyCaseDisposition.Failed, []);
    static readonly PropertyCaseEvaluation DiscardedWithoutClassifications = new(PropertyCaseDisposition.Discarded, []);

    /// <summary>Creates an evaluation result.</summary>
    /// <param name="disposition">Property disposition for the evaluated case.</param>
    /// <param name="classifications">Stable classifications; order and duplicates are non-semantic.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is undefined.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="classifications"/> contains a default classification.
    /// </exception>
    [JsonConstructor]
    public PropertyCaseEvaluation(
        PropertyCaseDisposition disposition,
        ImmutableArray<PropertyCaseClassification> classifications)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Property disposition is undefined.");
        }

        Disposition = disposition;
        Classifications = Normalize(classifications);
    }

    /// <summary>Gets whether the case passed, failed, or was discarded.</summary>
    public PropertyCaseDisposition Disposition { get; }

    /// <summary>Gets classifications in unique ordinal identity order.</summary>
    public ImmutableArray<PropertyCaseClassification> Classifications { get; }

    /// <summary>Compares normalized evaluation values structurally.</summary>
    /// <param name="other">Other evaluation value.</param>
    /// <returns><see langword="true"/> when disposition and classifications are equal.</returns>
    public bool Equals(PropertyCaseEvaluation? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Disposition == other.Disposition
        && Classifications.SequenceEqual(other.Classifications);

    /// <summary>Returns a structural hash code for this normalized evaluation.</summary>
    /// <returns>A hash code derived from disposition and classifications.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Disposition);
        foreach (var classification in Classifications)
        {
            hash.Add(classification);
        }

        return hash.ToHashCode();
    }

    /// <summary>Creates an unclassified passing evaluation.</summary>
    /// <returns>A cached passing evaluation.</returns>
    public static PropertyCaseEvaluation Pass() => PassedWithoutClassifications;

    /// <summary>Creates a classified passing evaluation.</summary>
    /// <param name="classifications">Stable nonempty classification identities.</param>
    /// <returns>A passing evaluation with normalized classifications.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="classifications"/> or one of its items is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A classification is empty or white-space.</exception>
    public static PropertyCaseEvaluation Pass(params string[] classifications) =>
        Create(PropertyCaseDisposition.Passed, classifications);

    /// <summary>Creates an unclassified failing evaluation.</summary>
    /// <returns>A cached failing evaluation.</returns>
    public static PropertyCaseEvaluation Fail() => FailedWithoutClassifications;

    /// <summary>Creates a classified failing evaluation.</summary>
    /// <param name="classifications">Stable nonempty classification identities.</param>
    /// <returns>A failing evaluation with normalized classifications.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="classifications"/> or one of its items is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A classification is empty or white-space.</exception>
    public static PropertyCaseEvaluation Fail(params string[] classifications) =>
        Create(PropertyCaseDisposition.Failed, classifications);

    /// <summary>Creates an unclassified discarded evaluation.</summary>
    /// <returns>A cached discarded evaluation.</returns>
    public static PropertyCaseEvaluation Discard() => DiscardedWithoutClassifications;

    /// <summary>Creates a classified discarded evaluation.</summary>
    /// <param name="classifications">Stable nonempty classification identities.</param>
    /// <returns>A discarded evaluation with normalized classifications.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="classifications"/> or one of its items is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A classification is empty or white-space.</exception>
    public static PropertyCaseEvaluation Discard(params string[] classifications) =>
        Create(PropertyCaseDisposition.Discarded, classifications);

    static PropertyCaseEvaluation Create(PropertyCaseDisposition disposition, string[] classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        var normalized = ImmutableArray.CreateBuilder<PropertyCaseClassification>(classifications.Length);
        foreach (var classification in classifications)
        {
            normalized.Add(new(classification));
        }

        return new(disposition, normalized.MoveToImmutable());
    }

    static ImmutableArray<PropertyCaseClassification> Normalize(
        ImmutableArray<PropertyCaseClassification> classifications)
    {
        if (classifications.IsDefaultOrEmpty)
        {
            return [];
        }

        SortedSet<string> identities = new(StringComparer.Ordinal);
        foreach (var classification in classifications)
        {
            if (string.IsNullOrWhiteSpace(classification.Value))
            {
                throw new ArgumentException(
                    "Property-case classifications cannot contain a default or empty identity.",
                    nameof(classifications));
            }
            identities.Add(classification.Value);
        }

        var normalized = ImmutableArray.CreateBuilder<PropertyCaseClassification>(identities.Count);
        foreach (var identity in identities)
        {
            normalized.Add(new(identity));
        }

        return normalized.MoveToImmutable();
    }
}

/// <summary>Bounded policy for one provider-neutral property-case run.</summary>
public sealed record PropertyCaseRunOptions
{
    /// <summary>Conventional bounded property-run policy.</summary>
    public static PropertyCaseRunOptions Default { get; } = new();

    /// <summary>Creates a property-run policy.</summary>
    /// <param name="requiredPassedCases">Number of passing generated cases required for a successful run.</param>
    /// <param name="maximumDiscardedCases">Maximum discarded generated and shrink cases permitted.</param>
    /// <param name="maximumShrinkCandidates">Maximum valid or invalid shrink candidates inspected.</param>
    /// <remarks>Values are validated into structured diagnostics when a run begins.</remarks>
    public PropertyCaseRunOptions(
        int requiredPassedCases = 100,
        int maximumDiscardedCases = 100,
        int maximumShrinkCandidates = 1_000)
    {
        RequiredPassedCases = requiredPassedCases;
        MaximumDiscardedCases = maximumDiscardedCases;
        MaximumShrinkCandidates = maximumShrinkCandidates;
    }

    /// <summary>Gets the number of passing generated cases required for success.</summary>
    public int RequiredPassedCases { get; }

    /// <summary>Gets the maximum number of discarded generated and shrink cases.</summary>
    public int MaximumDiscardedCases { get; }

    /// <summary>Gets the maximum number of shrink candidates inspected after the first failure.</summary>
    public int MaximumShrinkCandidates { get; }
}

/// <summary>One normalized classification count from initially generated cases.</summary>
public sealed record PropertyCaseCoverage
{
    internal PropertyCaseCoverage(PropertyCaseClassification classification, long count)
    {
        Classification = classification;
        Count = count;
    }

    /// <summary>Gets the stable classification.</summary>
    public PropertyCaseClassification Classification { get; }

    /// <summary>Gets how many initially generated pass, fail, or discard cases carried the classification.</summary>
    public long Count { get; }
}

/// <summary>Bounded execution evidence for one property-case run.</summary>
public sealed record PropertyCaseRunEvidence
{
    internal PropertyCaseRunEvidence(
        long generatedCaseCount,
        long passedCaseCount,
        long discardedCaseCount,
        long shrinkCandidateCount,
        long discardedShrinkCandidateCount,
        ImmutableArray<PropertyCaseCoverage> coverage)
    {
        GeneratedCaseCount = generatedCaseCount;
        PassedCaseCount = passedCaseCount;
        DiscardedCaseCount = discardedCaseCount;
        ShrinkCandidateCount = shrinkCandidateCount;
        DiscardedShrinkCandidateCount = discardedShrinkCandidateCount;
        Coverage = coverage;
    }

    /// <summary>Gets the number of initially generated cases evaluated, including pass, fail, and discard.</summary>
    public long GeneratedCaseCount { get; }

    /// <summary>Gets the number of initially generated cases that passed.</summary>
    public long PassedCaseCount { get; }

    /// <summary>Gets the total discarded case count across initial generation and shrinking.</summary>
    public long DiscardedCaseCount { get; }

    /// <summary>Gets the number of semantic shrink candidates inspected.</summary>
    public long ShrinkCandidateCount { get; }

    /// <summary>Gets how many shrink candidates were discarded by validation or the local evaluator.</summary>
    public long DiscardedShrinkCandidateCount { get; }

    /// <summary>Gets generated-case classification counts in ordinal classification order.</summary>
    public ImmutableArray<PropertyCaseCoverage> Coverage { get; }

    /// <summary>Compares bounded run evidence structurally.</summary>
    /// <param name="other">Other bounded run evidence.</param>
    /// <returns><see langword="true"/> when every count and coverage entry is equal.</returns>
    public bool Equals(PropertyCaseRunEvidence? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && GeneratedCaseCount == other.GeneratedCaseCount
        && PassedCaseCount == other.PassedCaseCount
        && DiscardedCaseCount == other.DiscardedCaseCount
        && ShrinkCandidateCount == other.ShrinkCandidateCount
        && DiscardedShrinkCandidateCount == other.DiscardedShrinkCandidateCount
        && Coverage.SequenceEqual(other.Coverage);

    /// <summary>Returns a structural hash code for this bounded run evidence.</summary>
    /// <returns>A hash code derived from every count and coverage entry.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GeneratedCaseCount);
        hash.Add(PassedCaseCount);
        hash.Add(DiscardedCaseCount);
        hash.Add(ShrinkCandidateCount);
        hash.Add(DiscardedShrinkCandidateCount);
        foreach (var item in Coverage)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Exact replay coordinates for one generated or semantically shrunk property case.</summary>
public sealed record PropertyCaseReplayEvidence
{
    /// <summary>Creates property-case replay evidence.</summary>
    /// <param name="origin">Exact deterministic generation coordinates for the original case.</param>
    /// <param name="shrinker">Exact semantic shrinker identity and version.</param>
    /// <param name="shrinkChoices">
    /// Accepted zero-based candidate ordinal at each successive semantic shrink step.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="origin"/> or <paramref name="shrinker"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shrinker"/> is empty or <paramref name="shrinkChoices"/> contains a negative ordinal.
    /// </exception>
    [JsonConstructor]
    public PropertyCaseReplayEvidence(
        GenerationReplayEvidence origin,
        string shrinker,
        ImmutableArray<int> shrinkChoices)
    {
        Origin = Guard.RequireNotNull(origin);
        Shrinker = Guard.RequireNotNullOrWhiteSpace(shrinker);
        ShrinkChoices = shrinkChoices.IsDefault ? [] : shrinkChoices;
        foreach (var choice in ShrinkChoices)
        {
            if (choice < 0)
            {
                throw new ArgumentException(
                    "Property-case shrink choices cannot contain a negative candidate ordinal.",
                    nameof(shrinkChoices));
            }
        }
    }

    /// <summary>Gets the original generated case coordinates.</summary>
    public GenerationReplayEvidence Origin { get; }

    /// <summary>Gets the exact shrinker identity and version.</summary>
    public string Shrinker { get; }

    /// <summary>Gets accepted candidate ordinals in successive shrink-step order.</summary>
    public ImmutableArray<int> ShrinkChoices { get; }

    /// <summary>Compares exact property-case replay coordinates structurally.</summary>
    /// <param name="other">Other replay coordinates.</param>
    /// <returns><see langword="true"/> when origin, shrinker, and every shrink choice are equal.</returns>
    public bool Equals(PropertyCaseReplayEvidence? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Origin == other.Origin
        && string.Equals(Shrinker, other.Shrinker, StringComparison.Ordinal)
        && ShrinkChoices.SequenceEqual(other.ShrinkChoices);

    /// <summary>Returns a structural hash code for these exact replay coordinates.</summary>
    /// <returns>A hash code derived from origin, shrinker, and every shrink choice.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Origin);
        hash.Add(Shrinker, StringComparer.Ordinal);
        foreach (var choice in ShrinkChoices)
        {
            hash.Add(choice);
        }

        return hash.ToHashCode();
    }

    /// <summary>Encodes this evidence as an opaque canonical URL-safe replay token.</summary>
    /// <returns>A current-version property-case replay token.</returns>
    public string ToToken() => PropertyCaseReplayTokenCodec.Encode(this);

    /// <summary>Decodes a canonical current-version property-case replay token.</summary>
    /// <param name="token">Opaque token created by <see cref="ToToken"/>.</param>
    /// <returns>Decoded replay evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException">The token is malformed, noncanonical, or uses another version.</exception>
    public static PropertyCaseReplayEvidence ParseToken(string token) =>
        PropertyCaseReplayTokenCodec.Decode(token);
}

/// <summary>One failing property case with classifications and exact semantic replay evidence.</summary>
public sealed record PropertyCase
{
    internal PropertyCase(
        Observation observation,
        ImmutableArray<PropertyCaseClassification> classifications,
        PropertyCaseReplayEvidence replay)
    {
        Observation = observation;
        Classifications = classifications;
        Replay = replay;
    }

    /// <summary>Gets the authoritative identity-free counterexample observation.</summary>
    public Observation Observation { get; }

    /// <summary>Gets the failing evaluation's normalized classifications.</summary>
    public ImmutableArray<PropertyCaseClassification> Classifications { get; }

    /// <summary>Gets exact origin and semantic-shrink replay coordinates.</summary>
    public PropertyCaseReplayEvidence Replay { get; }

    /// <summary>Compares failing property cases structurally.</summary>
    /// <param name="other">Other failing property case.</param>
    /// <returns><see langword="true"/> when observation, classifications, and replay evidence are equal.</returns>
    public bool Equals(PropertyCase? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Observation.Equals(other.Observation)
        && Classifications.SequenceEqual(other.Classifications)
        && Replay == other.Replay;

    /// <summary>Returns a structural hash code for this failing property case.</summary>
    /// <returns>A hash code derived from observation, classifications, and replay evidence.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Observation);
        foreach (var classification in Classifications)
        {
            hash.Add(classification);
        }
        hash.Add(Replay);
        return hash.ToHashCode();
    }
}

/// <summary>Terminal outcome of one bounded property-case run.</summary>
public enum PropertyCaseRunStatus
{
    /// <summary>The run options were invalid and no case was evaluated.</summary>
    Invalid = 0,

    /// <summary>The requested number of cases passed without finding a counterexample.</summary>
    Passed = 1,

    /// <summary>A counterexample was found and no smaller failing semantic candidate remains.</summary>
    CounterexampleFound = 2,

    /// <summary>A discard or shrink bound prevented a conclusive result.</summary>
    Exhausted = 3
}

/// <summary>Result of one bounded provider-neutral property-case run.</summary>
public sealed record PropertyCaseRunResult
{
    internal PropertyCaseRunResult(
        PropertyCaseRunStatus status,
        PropertyCase? bestCounterexample,
        PropertyCaseRunEvidence evidence,
        DocumentValidationResult validation)
    {
        Status = status;
        BestCounterexample = bestCounterexample;
        Evidence = evidence;
        Validation = validation;
    }

    /// <summary>Gets the terminal run status.</summary>
    public PropertyCaseRunStatus Status { get; }

    /// <summary>
    /// Gets the stable minimal counterexample, or the best failing case reached before exhaustion; otherwise null.
    /// </summary>
    public PropertyCase? BestCounterexample { get; }

    /// <summary>Gets bounded case, discard, shrink, and coverage evidence.</summary>
    public PropertyCaseRunEvidence Evidence { get; }

    /// <summary>Gets structured option or exhaustion diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Gets whether the run reached a conclusive pass or counterexample.</summary>
    public bool IsConclusive => Status is PropertyCaseRunStatus.Passed or PropertyCaseRunStatus.CounterexampleFound;
}

/// <summary>Reference bounded property-case generation, semantic shrinking, and replay interpretation.</summary>
public static class ReferencePropertyCaseInterpreter
{
    /// <summary>Exact semantic shrinker identity and version.</summary>
    public const string ShrinkerIdentity = "cohesive-simulation-property-shrinker/v1";

    const string RequiredPassedCasesInvalidCode =
        "simulation.propertyCase.options.requiredPassedCasesInvalid";
    const string MaximumDiscardedCasesInvalidCode =
        "simulation.propertyCase.options.maximumDiscardedCasesInvalid";
    const string MaximumShrinkCandidatesInvalidCode =
        "simulation.propertyCase.options.maximumShrinkCandidatesInvalid";
    const string DiscardLimitExceededCode = "simulation.propertyCase.discardLimitExceeded";
    const string ShrinkLimitExceededCode = "simulation.propertyCase.shrinkLimitExceeded";

    /// <summary>Checks a Boolean property against a deterministic bounded sequence of raw observations.</summary>
    /// <param name="plan">Validated provider-neutral generation plan.</param>
    /// <param name="seed">Caller-supplied deterministic root seed.</param>
    /// <param name="property">Deterministic local property; false identifies a failing case.</param>
    /// <param name="scope">Optional stable stream scope; null selects <see cref="GenerationScope.Default"/>.</param>
    /// <param name="options">Optional bounded run policy.</param>
    /// <param name="cancellationToken">Token observed between generation, evaluation, and shrink steps.</param>
    /// <returns>Conclusive pass/counterexample evidence, or structured invalid/exhausted evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="property"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is present but invalid.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static PropertyCaseRunResult Check(
        CompiledGenerationPlan plan,
        long seed,
        Func<Observation, bool> property,
        GenerationScope? scope = null,
        PropertyCaseRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Check(
            plan,
            seed,
            observation => property(observation)
                ? PropertyCaseEvaluation.Pass()
                : PropertyCaseEvaluation.Fail(),
            scope,
            options,
            cancellationToken);
    }

    /// <summary>Checks a classified property against a deterministic bounded sequence of raw observations.</summary>
    /// <param name="plan">Validated provider-neutral generation plan.</param>
    /// <param name="seed">Caller-supplied deterministic root seed.</param>
    /// <param name="evaluate">Deterministic local disposition and classification policy.</param>
    /// <param name="scope">Optional stable stream scope; null selects <see cref="GenerationScope.Default"/>.</param>
    /// <param name="options">Optional bounded run policy.</param>
    /// <param name="cancellationToken">Token observed between generation, evaluation, and shrink steps.</param>
    /// <returns>Conclusive pass/counterexample evidence, or structured invalid/exhausted evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="evaluate"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is present but invalid.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="evaluate"/> returns null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static PropertyCaseRunResult Check(
        CompiledGenerationPlan plan,
        long seed,
        Func<Observation, PropertyCaseEvaluation> evaluate,
        GenerationScope? scope = null,
        PropertyCaseRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(evaluate);
        var effectiveScope = scope ?? GenerationScope.Default;
        GenerationScope.Validate(effectiveScope, nameof(scope));
        var effectiveOptions = options ?? PropertyCaseRunOptions.Default;
        var optionValidation = ValidateOptions(effectiveOptions);
        if (!optionValidation.IsValid)
        {
            return new(
                PropertyCaseRunStatus.Invalid,
                bestCounterexample: null,
                EmptyEvidence(),
                optionValidation);
        }

        Dictionary<string, long> coverage = new(StringComparer.Ordinal);
        long generatedCaseCount = 0;
        long passedCaseCount = 0;
        long discardedCaseCount = 0;

        while (passedCaseCount < effectiveOptions.RequiredPassedCases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generated = ReferenceGenerationInterpreter.Generate(
                plan,
                seed,
                effectiveScope,
                sequenceIndex: generatedCaseCount);
            generatedCaseCount++;
            var evaluation = Evaluate(evaluate, generated.Observation);
            switch (evaluation.Disposition)
            {
                case PropertyCaseDisposition.Passed:
                    passedCaseCount++;
                    AddCoverage(coverage, evaluation.Classifications);
                    break;

                case PropertyCaseDisposition.Discarded:
                    AddCoverage(coverage, evaluation.Classifications);
                    discardedCaseCount++;
                    if (discardedCaseCount > effectiveOptions.MaximumDiscardedCases)
                    {
                        return Exhausted(
                            bestCounterexample: null,
                            generatedCaseCount,
                            passedCaseCount,
                            discardedCaseCount,
                            shrinkCandidateCount: 0,
                            discardedShrinkCandidateCount: 0,
                            coverage,
                            DiscardLimitExceededCode,
                            "The property run exceeded its maximum discarded-case count.",
                            "/run/discardedCases");
                    }
                    break;

                case PropertyCaseDisposition.Failed:
                    AddCoverage(coverage, evaluation.Classifications);
                    return Shrink(
                        plan,
                        generated,
                        evaluation,
                        evaluate,
                        effectiveOptions,
                        generatedCaseCount,
                        passedCaseCount,
                        discardedCaseCount,
                        coverage,
                        cancellationToken);

                default:
                    throw new InvalidOperationException(
                        $"Property evaluator returned undefined disposition '{evaluation.Disposition}'.");
            }
        }

        return new(
            PropertyCaseRunStatus.Passed,
            bestCounterexample: null,
            CreateEvidence(
                generatedCaseCount,
                passedCaseCount,
                discardedCaseCount,
                shrinkCandidateCount: 0,
                discardedShrinkCandidateCount: 0,
                coverage),
            DocumentValidationResult.Valid);
    }

    /// <summary>Replays one exact generated or semantically shrunk counterexample.</summary>
    /// <param name="plan">Exact compiled generation plan named by the evidence.</param>
    /// <param name="evidence">Origin generation and accepted semantic shrink coordinates.</param>
    /// <returns>The exact counterexample observation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="evidence"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="evidence"/> names an unsupported semantic shrinker.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Origin evidence is incompatible with <paramref name="plan"/> or a shrink choice cannot be replayed.
    /// </exception>
    public static Observation Replay(
        CompiledGenerationPlan plan,
        PropertyCaseReplayEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!string.Equals(evidence.Shrinker, ShrinkerIdentity, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Property-case replay requires shrinker '{evidence.Shrinker}', but this interpreter supports "
                + $"only '{ShrinkerIdentity}'.");
        }

        var current = ReferenceGenerationInterpreter.Replay(plan, evidence.Origin).Observation;
        for (var step = 0; step < evidence.ShrinkChoices.Length; step++)
        {
            var expectedOrdinal = evidence.ShrinkChoices[step];
            var ordinal = 0;
            ShrinkCandidate selected = default;
            var found = false;
            foreach (var candidate in EnumerateShrinkCandidates(plan, current))
            {
                if (ordinal == expectedOrdinal)
                {
                    selected = candidate;
                    found = true;
                    break;
                }
                ordinal++;
            }

            if (!found || selected.Observation is null)
            {
                throw new ArgumentException(
                    $"Property-case shrink choice '{expectedOrdinal}' at step '{step}' is not a valid candidate.",
                    nameof(evidence));
            }
            current = selected.Observation;
        }

        return current;
    }

    /// <summary>Replays one exact property case from its compact token.</summary>
    /// <param name="plan">Exact compiled generation plan named by the token.</param>
    /// <param name="token">Canonical current-version property-case replay token.</param>
    /// <returns>The exact counterexample observation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="token"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="FormatException"><paramref name="token"/> is malformed or noncanonical.</exception>
    /// <exception cref="NotSupportedException">The token names an unsupported semantic shrinker.</exception>
    /// <exception cref="ArgumentException">The token is incompatible with <paramref name="plan"/>.</exception>
    public static Observation Replay(CompiledGenerationPlan plan, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Replay(plan, PropertyCaseReplayEvidence.ParseToken(token));
    }

    static PropertyCaseRunResult Shrink(
        CompiledGenerationPlan plan,
        GeneratedObservation generated,
        PropertyCaseEvaluation failingEvaluation,
        Func<Observation, PropertyCaseEvaluation> evaluate,
        PropertyCaseRunOptions options,
        long generatedCaseCount,
        long passedCaseCount,
        long discardedCaseCount,
        Dictionary<string, long> coverage,
        CancellationToken cancellationToken)
    {
        var current = generated.Observation;
        var currentEvaluation = failingEvaluation;
        var choices = ImmutableArray.CreateBuilder<int>();
        HashSet<Observation> visited = [current];
        long shrinkCandidateCount = 0;
        long discardedShrinkCandidateCount = 0;

        while (true)
        {
            var improved = false;
            var ordinal = 0;
            foreach (var candidate in EnumerateShrinkCandidates(plan, current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Observation is not null && !visited.Add(candidate.Observation))
                {
                    ordinal++;
                    continue;
                }
                if (shrinkCandidateCount >= options.MaximumShrinkCandidates)
                {
                    return Exhausted(
                        CreateCase(current, currentEvaluation, generated.Replay, choices),
                        generatedCaseCount,
                        passedCaseCount,
                        discardedCaseCount,
                        shrinkCandidateCount,
                        discardedShrinkCandidateCount,
                        coverage,
                        ShrinkLimitExceededCode,
                        "The property run reached its maximum semantic shrink-candidate count before proving a "
                        + "stable minimum.",
                        "/run/shrinkCandidates");
                }

                shrinkCandidateCount++;
                if (candidate.Observation is null)
                {
                    discardedCaseCount++;
                    discardedShrinkCandidateCount++;
                    if (discardedCaseCount > options.MaximumDiscardedCases)
                    {
                        return Exhausted(
                            CreateCase(current, currentEvaluation, generated.Replay, choices),
                            generatedCaseCount,
                            passedCaseCount,
                            discardedCaseCount,
                            shrinkCandidateCount,
                            discardedShrinkCandidateCount,
                            coverage,
                            DiscardLimitExceededCode,
                            $"A semantic shrink candidate was invalid: {candidate.ValidationError}",
                            "/run/discardedCases");
                    }
                    ordinal++;
                    continue;
                }

                var evaluation = Evaluate(evaluate, candidate.Observation);
                if (evaluation.Disposition == PropertyCaseDisposition.Discarded)
                {
                    discardedCaseCount++;
                    discardedShrinkCandidateCount++;
                    if (discardedCaseCount > options.MaximumDiscardedCases)
                    {
                        return Exhausted(
                            CreateCase(current, currentEvaluation, generated.Replay, choices),
                            generatedCaseCount,
                            passedCaseCount,
                            discardedCaseCount,
                            shrinkCandidateCount,
                            discardedShrinkCandidateCount,
                            coverage,
                            DiscardLimitExceededCode,
                            "The property run exceeded its maximum discarded-case count while shrinking.",
                            "/run/discardedCases");
                    }
                }
                else if (evaluation.Disposition == PropertyCaseDisposition.Failed)
                {
                    current = candidate.Observation;
                    currentEvaluation = evaluation;
                    choices.Add(ordinal);
                    improved = true;
                    break;
                }
                ordinal++;
            }

            if (improved)
            {
                continue;
            }

            return new(
                PropertyCaseRunStatus.CounterexampleFound,
                CreateCase(current, currentEvaluation, generated.Replay, choices),
                CreateEvidence(
                    generatedCaseCount,
                    passedCaseCount,
                    discardedCaseCount,
                    shrinkCandidateCount,
                    discardedShrinkCandidateCount,
                    coverage),
                DocumentValidationResult.Valid);
        }
    }

    static IEnumerable<ShrinkCandidate> EnumerateShrinkCandidates(
        CompiledGenerationPlan plan,
        Observation current)
    {
        var shape = plan.OutputShape.Graph.GetShape(plan.OutputShape.ShapeId);
        HashSet<Observation> emitted = [];
        foreach (var binding in plan.Bindings)
        {
            foreach (var candidate in EnumerateBindingShrinks(plan, shape, binding, current))
            {
                if (candidate.Observation is null || emitted.Add(candidate.Observation))
                    yield return candidate;
            }
        }

        foreach (var member in plan.Members)
        {
            var currentValue = current.GetField(member.Identity.Value);
            foreach (var candidateValue in EnumerateValueShrinks(member.Generator, currentValue))
            {
                var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
                foreach (var field in current.Fields)
                {
                    fields.Add(field.Key, field.Value);
                }

                fields[member.Identity.Value] = candidateValue;
                var candidateFields = fields.ToImmutable();
                if (!ObservationValidator.TryValidateAgainstShape(
                        candidateFields,
                        shape,
                        out var validationError,
                        plan.OutputShape.Graph))
                {
                    yield return new(Observation: null, ValidationError: validationError);
                    continue;
                }

                var observation = Observation.Create(plan.OutputShape, candidateFields);
                if (!observation.Equals(current) && emitted.Add(observation))
                {
                    yield return new(observation, ValidationError: null);
                }
            }
        }
    }

    static IEnumerable<ShrinkCandidate> EnumerateBindingShrinks(
        CompiledGenerationPlan plan,
        Shape shape,
        RecordGenerationBinding binding,
        Observation current)
    {
        foreach (var sourceValue in EnumerateBindingSourceValues(binding.Generator))
        {
            var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            foreach (var field in current.Fields)
                fields.Add(field.Key, field.Value);

            Dictionary<ValueBindingId, ObservationValue> values = new(1)
            {
                [binding.Identity] = sourceValue
            };
            foreach (var member in plan.Members)
            {
                if (member.Generator is not ExpressionGenerationNode expression
                    || !ReferenceGenerationInterpreter.ReferencesBinding(
                        expression.Expression,
                        binding.Identity))
                {
                    continue;
                }

                fields[member.Identity.Value] = ReferenceGenerationInterpreter.EvaluateExpression(
                    expression.Expression,
                    values);
            }

            var candidateFields = fields.ToImmutable();
            if (!ObservationValidator.TryValidateAgainstShape(
                    candidateFields,
                    shape,
                    out var validationError,
                    plan.OutputShape.Graph))
            {
                yield return new(Observation: null, ValidationError: validationError);
                continue;
            }

            var candidate = Observation.Create(plan.OutputShape, candidateFields);
            if (candidate.Equals(current))
                yield break;

            yield return new(candidate, ValidationError: null);
        }

        throw new InvalidOperationException(
            $"Generated observation does not correspond to a compiled value for binding '{binding.Identity.Value}'.");
    }

    static IEnumerable<ObservationValue> EnumerateBindingSourceValues(ValueGeneratorNode generator)
    {
        switch (generator)
        {
            case ConstantGenerationNode constant:
                yield return constant.Value;
                yield break;

            case WeightedCategoricalGenerationNode categorical:
                HashSet<ObservationValue> emitted = [];
                foreach (var option in categorical.Options)
                {
                    if (emitted.Add(option.Value))
                        yield return option.Value;
                }
                yield break;

            case CatalogGenerationNode catalog:
                HashSet<ObservationValue> catalogValues = [];
                foreach (var entry in catalog.Catalog.Definition.Entries)
                {
                    if (catalogValues.Add(entry.Value))
                        yield return entry.Value;
                }
                yield break;

            default:
                throw new NotSupportedException(
                    $"Reference property shrinking does not support binding source '{generator.GetType().Name}'.");
        }
    }

    static IEnumerable<ObservationValue> EnumerateValueShrinks(
        ValueGeneratorNode generator,
        ObservationValue current)
    {
        switch (generator)
        {
            case ConstantGenerationNode:
                yield break;

            case BernoulliGenerationNode when current.Bool:
                yield return ObservationValue.FromBool(false);
                yield break;

            case BernoulliGenerationNode:
                yield break;

            case Int32GenerationNode integer:
                foreach (var candidate in EnumerateInt32Shrinks(integer, checked((int)current.Int64)))
                {
                    yield return ObservationValue.FromInt64(candidate);
                }

                yield break;

            case WeightedCategoricalGenerationNode categorical:
                HashSet<ObservationValue> emitted = [];
                foreach (var option in categorical.Options)
                {
                    if (option.Value.Equals(current))
                    {
                        yield break;
                    }

                    if (emitted.Add(option.Value))
                    {
                        yield return option.Value;
                    }
                }
                throw new InvalidOperationException(
                    "A generated categorical value must equal one of the compiled generator's options.");

            case CatalogGenerationNode catalog:
                HashSet<ObservationValue> catalogValues = [];
                foreach (var entry in catalog.Catalog.Definition.Entries)
                {
                    if (entry.Value.Equals(current))
                        yield break;

                    if (catalogValues.Add(entry.Value))
                        yield return entry.Value;
                }
                throw new InvalidOperationException(
                    "A generated catalog value must equal one of the retained catalog's entries.");

            case ExpressionGenerationNode:
                yield break;

            default:
                throw new NotSupportedException(
                    $"Reference property shrinking does not support node '{generator.GetType().Name}'.");
        }
    }

    static IEnumerable<int> EnumerateInt32Shrinks(Int32GenerationNode generator, int current)
    {
        var target = current switch
        {
            > 0 => Math.Max(0, generator.Minimum),
            < 0 => Math.Min(0, generator.Maximum),
            _ => 0
        };
        if (target == current)
        {
            yield break;
        }

        yield return target;
        var distance = Math.Abs((long)current - target);
        for (var step = distance / 2; step > 0; step /= 2)
        {
            var candidate = current > target
                ? current - step
                : current + step;
            if (candidate != target && candidate != current)
            {
                yield return checked((int)candidate);
            }
        }
    }

    static PropertyCaseEvaluation Evaluate(
        Func<Observation, PropertyCaseEvaluation> evaluate,
        Observation observation) =>
        evaluate(observation)
        ?? throw new InvalidOperationException("A property evaluator cannot return null.");

    static PropertyCase CreateCase(
        Observation observation,
        PropertyCaseEvaluation evaluation,
        GenerationReplayEvidence origin,
        ImmutableArray<int>.Builder choices) =>
        new(
            observation,
            evaluation.Classifications,
            new(origin, ShrinkerIdentity, choices.ToImmutable()));

    static PropertyCaseRunResult Exhausted(
        PropertyCase? bestCounterexample,
        long generatedCaseCount,
        long passedCaseCount,
        long discardedCaseCount,
        long shrinkCandidateCount,
        long discardedShrinkCandidateCount,
        Dictionary<string, long> coverage,
        string code,
        string message,
        string location) =>
        new(
            PropertyCaseRunStatus.Exhausted,
            bestCounterexample,
            CreateEvidence(
                generatedCaseCount,
                passedCaseCount,
                discardedCaseCount,
                shrinkCandidateCount,
                discardedShrinkCandidateCount,
                coverage),
            Error(code, message, location));

    static PropertyCaseRunEvidence EmptyEvidence() =>
        new(
            generatedCaseCount: 0,
            passedCaseCount: 0,
            discardedCaseCount: 0,
            shrinkCandidateCount: 0,
            discardedShrinkCandidateCount: 0,
            coverage: []);

    static PropertyCaseRunEvidence CreateEvidence(
        long generatedCaseCount,
        long passedCaseCount,
        long discardedCaseCount,
        long shrinkCandidateCount,
        long discardedShrinkCandidateCount,
        Dictionary<string, long> coverage)
    {
        if (coverage.Count == 0)
        {
            return new(
                generatedCaseCount,
                passedCaseCount,
                discardedCaseCount,
                shrinkCandidateCount,
                discardedShrinkCandidateCount,
                coverage: []);
        }

        var counts = ImmutableArray.CreateBuilder<PropertyCaseCoverage>(coverage.Count);
        foreach (var item in coverage.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            counts.Add(new(new(item.Key), item.Value));
        }

        return new(
            generatedCaseCount,
            passedCaseCount,
            discardedCaseCount,
            shrinkCandidateCount,
            discardedShrinkCandidateCount,
            counts.MoveToImmutable());
    }

    static void AddCoverage(
        Dictionary<string, long> coverage,
        ImmutableArray<PropertyCaseClassification> classifications)
    {
        foreach (var classification in classifications)
        {
            coverage.TryGetValue(classification.Value, out var count);
            coverage[classification.Value] = checked(count + 1);
        }
    }

    static DocumentValidationResult ValidateOptions(PropertyCaseRunOptions options)
    {
        List<DocumentValidationDiagnostic> diagnostics = [];
        if (options.RequiredPassedCases <= 0)
        {
            diagnostics.Add(Diagnostic(
                RequiredPassedCasesInvalidCode,
                "The required passing-case count must be positive.",
                "/options/requiredPassedCases"));
        }
        if (options.MaximumDiscardedCases < 0)
        {
            diagnostics.Add(Diagnostic(
                MaximumDiscardedCasesInvalidCode,
                "The maximum discarded-case count cannot be negative.",
                "/options/maximumDiscardedCases"));
        }
        if (options.MaximumShrinkCandidates < 0)
        {
            diagnostics.Add(Diagnostic(
                MaximumShrinkCandidatesInvalidCode,
                "The maximum shrink-candidate count cannot be negative.",
                "/options/maximumShrinkCandidates"));
        }

        return diagnostics.Count == 0
            ? DocumentValidationResult.Valid
            : new(DocumentValidationDiagnostics.Normalize(diagnostics.ToImmutableArray()));
    }

    static DocumentValidationResult Error(string code, string message, string location) =>
        DocumentValidationResult.FromDiagnostics([Diagnostic(code, message, location)]);

    static DocumentValidationDiagnostic Diagnostic(string code, string message, string location) =>
        new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location,
            Evidence: new(stage: "property-case"));

    readonly record struct ShrinkCandidate(Observation? Observation, string? ValidationError);
}

static class PropertyCaseReplayTokenCodec
{
    const string Prefix = "csimpc1.";

    public static string Encode(PropertyCaseReplayEvidence evidence) =>
        CanonicalReplayTokenCodec.Encode(evidence, Prefix);

    public static PropertyCaseReplayEvidence Decode(string token) =>
        CanonicalReplayTokenCodec.Decode<PropertyCaseReplayEvidence>(
            token,
            Prefix,
            tokenName: "property-case replay token",
            evidenceContractName: "property-case replay evidence");
}
