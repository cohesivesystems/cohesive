using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Schema-local grounding that attaches one ontology concept to a concrete structural target.
/// </summary>
/// <example>
/// <code>
/// var grounding = new ConceptGrounding(
///     groundingId: "n1_shipto_name",
///     conceptId: "party.ship_to.name",
///     target: new FieldGroundingTarget(FieldPath.Parse("fld_n1_shipto")),
///     condition: new PredicateGroundingCondition(new ConstraintExpr.EqualsExpr("N1-01", "ST")),
///     origin: new ScopedSymbolGroundingOrigin("N1-01", "ST"),
///     strength: GroundingStrength.Conditional
/// );
/// </code>
/// </example>
public sealed record ConceptGrounding
{
    /// <summary>
    /// Creates one concept grounding.
    /// </summary>
    public ConceptGrounding(
        string groundingId,
        string conceptId,
        GroundingTarget target,
        GroundingCondition? condition = null,
        GroundingOrigin? origin = null,
        GroundingStrength strength = GroundingStrength.Asserted,
        ImmutableDictionary<string, string>? properties = null
        )
    {
        GroundingId = Guard.RequireNotNullOrWhiteSpace(groundingId).Trim();
        ConceptId = Guard.RequireNotNullOrWhiteSpace(conceptId).Trim();
        Target = Guard.RequireNotNull(target);
        Condition = condition ?? new AlwaysGroundingCondition();
        Origin = origin ?? new InferredGroundingOrigin("grounding");
        Strength = strength;
        Properties = NormalizeProperties(properties);
    }

    /// <summary>
    /// Stable grounding identifier.
    /// </summary>
    public string GroundingId { get; init; }

    /// <summary>
    /// Grounded concept identifier.
    /// </summary>
    public string ConceptId { get; init; }

    /// <summary>
    /// Structural target that this concept is attached to.
    /// </summary>
    public GroundingTarget Target { get; init; }

    /// <summary>
    /// Optional applicability condition for the grounding.
    /// </summary>
    public GroundingCondition Condition { get; init; }

    /// <summary>
    /// Authoring or derivation origin for the grounding.
    /// </summary>
    public GroundingOrigin Origin { get; init; }

    /// <summary>
    /// Confidence class for this grounding.
    /// </summary>
    public GroundingStrength Strength { get; init; }

    /// <summary>
    /// Optional extra grounding metadata.
    /// </summary>
    public ImmutableDictionary<string, string> Properties { get; init; }

    static ImmutableDictionary<string, string> NormalizeProperties(ImmutableDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
            return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

        var normalized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in properties)
        {
            var key = rawKey?.Trim();
            var value = rawValue?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            normalized[key] = value;
        }

        return normalized.ToImmutable();
    }
}

/// <summary>
/// Structural target for one concept grounding.
/// </summary>
public abstract record GroundingTarget;

/// <summary>
/// Field-level grounding target.
/// </summary>
public sealed record FieldGroundingTarget : GroundingTarget
{
    /// <summary>
    /// Creates one field grounding target.
    /// </summary>
    public FieldGroundingTarget(FieldPath fieldPath)
    {
        FieldPath = fieldPath;
    }

    /// <summary>
    /// Grounded field path.
    /// </summary>
    public FieldPath FieldPath { get; init; }
}

/// <summary>
/// Type-level grounding target.
/// </summary>
public sealed record TypeGroundingTarget : GroundingTarget
{
    /// <summary>
    /// Creates one type grounding target.
    /// </summary>
    public TypeGroundingTarget(TypeId typeId)
    {
        TypeId = typeId;
    }

    /// <summary>
    /// Grounded type id.
    /// </summary>
    public TypeId TypeId { get; init; }
}

/// <summary>
/// Applicability condition for a concept grounding.
/// </summary>
public abstract record GroundingCondition;

/// <summary>
/// Unconditional grounding condition.
/// </summary>
public sealed record AlwaysGroundingCondition : GroundingCondition;

/// <summary>
/// Predicate-based grounding condition.
/// </summary>
public sealed record PredicateGroundingCondition : GroundingCondition
{
    /// <summary>
    /// Creates one predicate grounding condition.
    /// </summary>
    public PredicateGroundingCondition(ConstraintExpr expression)
    {
        Expression = Guard.RequireNotNull(expression);
    }

    /// <summary>
    /// Required predicate.
    /// </summary>
    public ConstraintExpr Expression { get; init; }
}

/// <summary>
/// Provenance for one concept grounding.
/// </summary>
public abstract record GroundingOrigin;

/// <summary>
/// Grounding authored directly on a field annotation.
/// </summary>
public sealed record AnnotationGroundingOrigin : GroundingOrigin
{
    /// <summary>
    /// Creates one annotation grounding origin.
    /// </summary>
    public AnnotationGroundingOrigin(string annotationKey)
    {
        AnnotationKey = Guard.RequireNotNullOrWhiteSpace(annotationKey).Trim();
    }

    /// <summary>
    /// Annotation key that produced the grounding.
    /// </summary>
    public string AnnotationKey { get; init; }
}

/// <summary>
/// Grounding produced by a scoped symbol lookup in the ontology closure.
/// </summary>
public sealed record ScopedSymbolGroundingOrigin : GroundingOrigin
{
    /// <summary>
    /// Creates one scoped-symbol grounding origin.
    /// </summary>
    public ScopedSymbolGroundingOrigin(string scope, string symbol)
    {
        Scope = Guard.RequireNotNullOrWhiteSpace(scope).Trim();
        Symbol = Guard.RequireNotNullOrWhiteSpace(symbol).Trim();
    }

    /// <summary>
    /// Scope that resolved the symbol.
    /// </summary>
    public string Scope { get; init; }

    /// <summary>
    /// Symbol value that triggered the grounding.
    /// </summary>
    public string Symbol { get; init; }
}

/// <summary>
/// Grounding inferred from a heuristic or authored hint rule.
/// </summary>
public sealed record InferredGroundingOrigin : GroundingOrigin
{
    /// <summary>
    /// Creates one inferred grounding origin.
    /// </summary>
    public InferredGroundingOrigin(string ruleId)
    {
        RuleId = Guard.RequireNotNullOrWhiteSpace(ruleId).Trim();
    }

    /// <summary>
    /// Rule identifier that produced the grounding.
    /// </summary>
    public string RuleId { get; init; }
}

/// <summary>
/// Confidence class for one concept grounding.
/// </summary>
public enum GroundingStrength
{
    /// <summary>The concept grounding is explictly asserted.</summary>
    Asserted = 0,
    
    /// <summary>The concept grounding is conditional.</summary>
    Conditional = 1,
    
    /// <summary>The concept grounding is inferred.</summary>
    Inferred = 2
}
