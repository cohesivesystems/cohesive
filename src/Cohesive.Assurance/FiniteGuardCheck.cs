using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Authoring;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;

namespace Cohesive.Assurance;

/// <summary>Authors a complete finite catalog and a guard implication without depending on Simulation.</summary>
public static class FiniteGuardCheck
{
    /// <summary>Declares every member of the finite domain; this is a catalog, not a sampling campaign.</summary>
    /// <typeparam name="T">Authoring-time projection of the sole state or pre-state input.</typeparam>
    /// <param name="claim">Derived state or pre-state claim to check.</param>
    /// <param name="input">The exact typed input used by the claim.</param>
    /// <param name="members">Complete declared catalog, defensively materialized; order determines witness search order.</param>
    /// <param name="source">Exact authoritative catalog and assumption source; also the guard source for the expression overload.</param>
    /// <param name="configuration">Exact lowering/checker convention configuration.</param>
    /// <returns>Source awaiting a required guard predicate.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Input identity or type does not match the claim.</exception>
    /// <exception cref="NotSupportedException">The claim is outside the one-input state/pre-state profile.</exception>
    public static Declaration<T> Define<T>(ClaimDocument claim, IExpressionInput<T> input,
        IReadOnlyList<T> members, ClaimReference source, ClaimReference configuration) where T : notnull =>
        new(claim, input, members, source, configuration);

    /// <summary>Finite catalog source awaiting its guard.</summary>
    /// <typeparam name="T">Authoring projection of the input.</typeparam>
    public sealed class Declaration<T> where T : notnull
    {
        readonly ClaimDocument claim;
        readonly IExpressionInput<T> input;
        readonly ImmutableArray<ObservationValue> members;
        readonly ClaimReference source, configuration;
        internal Declaration(ClaimDocument claim, IExpressionInput<T> input, IReadOnlyList<T> members,
            ClaimReference source, ClaimReference configuration)
        {
            ArgumentNullException.ThrowIfNull(claim); ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(members); ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(configuration);
            FiniteGuardCheckDocument.ValidateProfile(claim);
            var declared = claim.Definition.Inputs[0];
            if (declared.Id != input.Binding.Id || declared.Contract.Type != input.Binding.Type)
                throw new ArgumentException("The typed input must match the claim's exact binding and type.", nameof(input));
            this.claim = claim; this.input = input; this.source = source; this.configuration = configuration;
            this.members = [.. members.Select(value => ObservationValue.FromObject(value))];
        }

        /// <summary>Lowers the enabling guard through the shared expression authoring layer.</summary>
        /// <param name="guard">Pure typed guard source, never compiled or invoked.</param>
        /// <returns>Complete source that can add assumptions and lower to a check document.</returns>
        /// <exception cref="ArgumentNullException">Guard is null.</exception>
        /// <exception cref="ExpressionAuthoringException">The expression is unsupported or captures host state.</exception>
        public Source<T> When(Expression<Func<T, bool>> guard) =>
            new(claim, input, members, source, configuration, Lower(input, guard, source, "guard"), Expr.Const(true), [new("/guard", source)]);

        /// <summary>Authors a portable guard supplied by another semantic producer with exact source mappings.</summary>
        /// <param name="guard">Structural predicate source over this declaration's input; no host callbacks are accepted.</param>
        /// <param name="sources">Nonempty mappings under /guard to exact source artifacts; retained as evidence dependencies.</param>
        /// <returns>Source ready for optional assumptions and validated lowering.</returns>
        /// <exception cref="ArgumentNullException">Guard is null.</exception>
        /// <exception cref="ArgumentException">Source mappings are missing or invalid when built.</exception>
        public Source<T> When(Expr guard, ImmutableArray<ClaimSourceLocation> sources)
        {
            ArgumentNullException.ThrowIfNull(guard);
            return new(claim, input, members, source, configuration, guard, Expr.Const(true), sources);
        }
    }

    /// <summary>Immutable source; only derived portable expressions and values enter the check document.</summary>
    /// <typeparam name="T">Authoring projection of the input.</typeparam>
    public sealed class Source<T> where T : notnull
    {
        readonly ClaimDocument claim;
        readonly IExpressionInput<T> input;
        readonly ImmutableArray<ObservationValue> members;
        readonly ClaimReference source, configuration;
        readonly Expr guard, assumptions;
        readonly ImmutableArray<ClaimSourceLocation> guardSources;
        internal Source(ClaimDocument claim, IExpressionInput<T> input, ImmutableArray<ObservationValue> members,
            ClaimReference source, ClaimReference configuration, Expr guard, Expr assumptions,
            ImmutableArray<ClaimSourceLocation> guardSources)
        {
            this.claim = claim; this.input = input; this.members = members; this.source = source;
            this.configuration = configuration; this.guard = guard; this.assumptions = assumptions;
            this.guardSources = guardSources;
        }

        /// <summary>Conjoins another explicit domain assumption.</summary>
        /// <param name="assumption">Pure typed assumption source.</param>
        /// <returns>New source retaining the conjunction of all assumptions.</returns>
        /// <exception cref="ArgumentNullException">Assumption is null.</exception>
        /// <exception cref="ExpressionAuthoringException">The expression cannot be lowered.</exception>
        public Source<T> Assuming(Expression<Func<T, bool>> assumption) => new(claim, input, members, source,
            configuration, guard, Expr.And(assumptions, Lower(input, assumption, source, "assumptions")), guardSources);

        /// <summary>Materializes the derived finite-domain check document.</summary>
        /// <returns>Validated immutable plan containing no host-language callbacks or CLR types.</returns>
        /// <exception cref="ArgumentException">A member violates the input contract or guard source mappings are missing or invalid.</exception>
        /// <exception cref="ClaimValidationException">A predicate is outside the supported expression profile.</exception>
        public FiniteGuardCheckDocument Build() => new(claim, members, guard, assumptions, source, configuration, guardSources);
    }

    static Expr Lower<T>(IExpressionInput<T> input, Expression<Func<T, bool>> expression,
        ClaimReference source, string path) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(expression);
        ClrAuthoringContext context = new();
        return new ExpressionLowerer(context.ResolveMemberPath)
            .LowerValue(expression, [input.Binding], source.Source.Value + "/" + path).RequireValue().Value;
    }
}

/// <summary>Derived portable plan for D ∧ A ∧ G ⇒ P over an explicitly listed finite catalog.</summary>
public sealed class FiniteGuardCheckDocument
{
    internal static readonly JsonSerializerOptions Options = AssuranceJson.Options;

    [JsonConstructor]
    internal FiniteGuardCheckDocument(ClaimDocument claim, ImmutableArray<ObservationValue> members,
        Expr guard, Expr assumptions, ClaimReference source, ClaimReference configuration,
        ImmutableArray<ClaimSourceLocation> guardSources)
    {
        ArgumentNullException.ThrowIfNull(claim); ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(assumptions); ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateProfile(claim);
        var input = claim.Definition.Inputs[0];
        if (members.IsDefault) throw new ArgumentException("The entire finite catalog must be present.", nameof(members));
        foreach (var member in members)
            if (!input.Contract.IsSatisfiedByConstant(member))
                throw new ArgumentException("Every catalog member must satisfy the claim input contract.", nameof(members));
        var scope = new ExprScope(bindings: [new ExprScopeBinding(input.Id, input.Contract)]);
        foreach (var (name, expression) in new[] { ("guard", guard), ("assumptions", assumptions) })
        {
            var analysis = AssurancePredicates.Analyze(new(name), expression, scope);
            if (!analysis.IsValid) throw new ClaimValidationException(analysis.Validation);
        }
        Claim = claim; Members = members; Guard = guard; Assumptions = assumptions;
        Source = source; Configuration = configuration;
        if (guardSources.IsDefaultOrEmpty || guardSources.Any(x => x is null || x.Source is null
            || (x.Path != "/guard" && !(x.Path?.StartsWith("/guard/", StringComparison.Ordinal) ?? false))))
            throw new ArgumentException("Guard source mappings must identify exact artifacts at /guard or its descendants.", nameof(guardSources));
        GuardSources = [.. guardSources.Distinct().OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Source, ClaimReference.CanonicalComparer)];
    }

    /// <summary>Versioned finite-check profile and wire format.</summary>
    public string Schema => "cohesive-assurance-finite-guard/v2";
    /// <summary>Versioned source lowerer.</summary>
    public string Producer => ExpressionLowerer.Producer;
    /// <summary>Exact predicate capability profile.</summary>
    public string PredicateProfile => AssurancePredicates.ProfileId;
    /// <summary>Derived claim whose predicate is the implication consequent.</summary>
    public ClaimDocument Claim { get; }
    /// <summary>Complete ordered finite catalog; duplicate rows are harmless and remain attributable.</summary>
    public ImmutableArray<ObservationValue> Members { get; }
    /// <summary>Portable enabling predicate.</summary>
    public Expr Guard { get; }
    /// <summary>Portable conjunction of explicit domain assumptions.</summary>
    public Expr Assumptions { get; }
    /// <summary>Exact authoritative source for the catalog and assumptions; guard sources are retained separately.</summary>
    public ClaimReference Source { get; }
    /// <summary>Exact lowering and checker configuration.</summary>
    public ClaimReference Configuration { get; }
    /// <summary>Guard expression locations mapped to exact source artifacts, including referenced execution nodes.</summary>
    public ImmutableArray<ClaimSourceLocation> GuardSources { get; }
    /// <summary>Stable source locations for the derived plan, separate from the claim's source map.</summary>
    [JsonIgnore]
    public ImmutableArray<ClaimSourceLocation> SourceMap =>
        [new("/members", Source), .. GuardSources, new("/assumptions", Source)];

    /// <summary>Serializes the portable derived plan.</summary>
    /// <returns>Canonical JSON retaining all domain members and exact claim provenance.</returns>
    /// <exception cref="JsonException">A portable value cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A retained representation is unsupported.</exception>
    public string Serialize() => Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(this, Options));

    /// <summary>Restores a derived plan without making persisted IR an authoring surface.</summary>
    /// <param name="json">Canonical plan JSON.</param>
    /// <returns>Validated immutable plan.</returns>
    /// <exception cref="JsonException">The document is malformed, incompatible or noncanonical.</exception>
    public static FiniteGuardCheckDocument Deserialize(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(json, Options, "finite guard check", out FiniteGuardCheckDocument? result, out var error)
            || result is null) throw new JsonException($"{error.Failure} at {error.Location}: {error.Message}");
        return result;
    }

    /// <summary>Identifies this exact catalog, claim, guard, assumptions and configuration.</summary>
    /// <returns>Exact derived artifact reference using the shared canonical fingerprint mechanism.</returns>
    /// <exception cref="JsonException">A portable value cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A retained representation is unsupported.</exception>
    public ClaimReference Reference() => new(Source.Source, Source.Revision,
        Convert.ToHexString(StrictDocumentJson.ComputeCanonicalSha256(this, Options)));

    internal static void ValidateProfile(ClaimDocument claim)
    {
        var inputs = claim.Definition.Inputs;
        if (inputs.Length != 1 || (claim.Definition.Form == ClaimForm.StateInvariant
                ? inputs[0].Role != ClaimInputRole.State
                : claim.Definition.Form != ClaimForm.TransitionContract || inputs[0].Role != ClaimInputRole.Before))
            throw new NotSupportedException("Finite guard checking supports one state or pre-state input; temporal and multi-input claims require another profile.");
    }
}
