using System.Collections.Immutable;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Flags describing algebraic laws on a relation type.
/// </summary>
[Flags]
public enum RelationLawFlags
{
    None = 0,
    Reflexive = 1 << 0,
    Irreflexive = 1 << 1,
    Symmetric = 1 << 2,
    Antisymmetric = 1 << 3,
    Asymmetric = 1 << 4,
    Transitive = 1 << 5,
    Functional = 1 << 6,
    InverseFunctional = 1 << 7
}

/// <summary>
/// Higher-order ontology rule over relation types, scoped symbols, or relation families.
/// </summary>
public abstract record OntologyRule;

/// <summary>
/// Global law flags over one relation type.
/// </summary>
public sealed record RelationLawRule : OntologyRule
{
    /// <summary>
    /// Creates one relation-law rule.
    /// </summary>
    public RelationLawRule(string relationTypeId, RelationLawFlags flags)
    {
        RelationTypeId = Guard.RequireNotNullOrWhiteSpace(relationTypeId).Trim();
        Flags = flags;
    }

    /// <summary>
    /// Relation type governed by this rule.
    /// </summary>
    public string RelationTypeId { get; init; }

    /// <summary>
    /// Law flags applied to the relation type.
    /// </summary>
    public RelationLawFlags Flags { get; init; }
}

/// <summary>
/// Domain restriction on a relation type.
/// </summary>
public sealed record RelationDomainRule : OntologyRule
{
    /// <summary>
    /// Creates one relation-domain rule.
    /// </summary>
    public RelationDomainRule(string relationTypeId, string domainConceptId)
    {
        RelationTypeId = Guard.RequireNotNullOrWhiteSpace(relationTypeId).Trim();
        DomainConceptId = Guard.RequireNotNullOrWhiteSpace(domainConceptId).Trim();
    }

    /// <summary>
    /// Restricted relation type.
    /// </summary>
    public string RelationTypeId { get; init; }

    /// <summary>
    /// Required domain concept.
    /// </summary>
    public string DomainConceptId { get; init; }
}

/// <summary>
/// Range restriction on a relation type.
/// </summary>
public sealed record RelationRangeRule : OntologyRule
{
    /// <summary>
    /// Creates one relation-range rule.
    /// </summary>
    public RelationRangeRule(string relationTypeId, string rangeConceptId)
    {
        RelationTypeId = Guard.RequireNotNullOrWhiteSpace(relationTypeId).Trim();
        RangeConceptId = Guard.RequireNotNullOrWhiteSpace(rangeConceptId).Trim();
    }

    /// <summary>
    /// Restricted relation type.
    /// </summary>
    public string RelationTypeId { get; init; }

    /// <summary>
    /// Required range concept.
    /// </summary>
    public string RangeConceptId { get; init; }
}

/// <summary>
/// Relation-type hierarchy rule.
/// </summary>
public sealed record SubRelationRule : OntologyRule
{
    /// <summary>
    /// Creates one sub-relation rule.
    /// </summary>
    public SubRelationRule(string childRelationTypeId, string parentRelationTypeId)
    {
        ChildRelationTypeId = Guard.RequireNotNullOrWhiteSpace(childRelationTypeId).Trim();
        ParentRelationTypeId = Guard.RequireNotNullOrWhiteSpace(parentRelationTypeId).Trim();
    }

    /// <summary>
    /// Child relation type id.
    /// </summary>
    public string ChildRelationTypeId { get; init; }

    /// <summary>
    /// Parent relation type id.
    /// </summary>
    public string ParentRelationTypeId { get; init; }
}

/// <summary>
/// Cardinality rule over one relation type and domain concept.
/// </summary>
public sealed record RelationCardinalityRule : OntologyRule
{
    /// <summary>
    /// Creates one relation-cardinality rule.
    /// </summary>
    public RelationCardinalityRule(string relationTypeId, string domainConceptId, int? min = null, int? max = null)
    {
        RelationTypeId = Guard.RequireNotNullOrWhiteSpace(relationTypeId).Trim();
        DomainConceptId = Guard.RequireNotNullOrWhiteSpace(domainConceptId).Trim();
        Min = ValidateBound(min, nameof(min));
        Max = ValidateBound(max, nameof(max));
        if (Min.HasValue && Max.HasValue && Min.Value > Max.Value)
            throw new InvalidOperationException("Cardinality min cannot exceed max.");
    }

    /// <summary>
    /// Restricted relation type.
    /// </summary>
    public string RelationTypeId { get; init; }

    /// <summary>
    /// Domain concept to which the cardinality applies.
    /// </summary>
    public string DomainConceptId { get; init; }

    /// <summary>
    /// Optional minimum cardinality.
    /// </summary>
    public int? Min { get; init; }

    /// <summary>
    /// Optional maximum cardinality.
    /// </summary>
    public int? Max { get; init; }

    static int? ValidateBound(int? value, string paramName)
    {
        if (value.HasValue && value.Value < 0)
            throw new ArgumentOutOfRangeException(paramName, "Cardinality bounds must be non-negative.");
        return value;
    }
}

/// <summary>
/// Scoped symbol-to-concept meaning rule.
/// </summary>
public sealed record ScopedSymbolMeaningRule : OntologyRule
{
    /// <summary>
    /// Creates one scoped symbol-meaning rule.
    /// </summary>
    public ScopedSymbolMeaningRule(string scope, string symbol, string conceptId)
    {
        Scope = Guard.RequireNotNullOrWhiteSpace(scope).Trim();
        Symbol = Guard.RequireNotNullOrWhiteSpace(symbol).Trim();
        ConceptId = Guard.RequireNotNullOrWhiteSpace(conceptId).Trim();
    }

    /// <summary>
    /// Scoped namespace for the symbol.
    /// </summary>
    public string Scope { get; init; }

    /// <summary>
    /// Scoped symbol value.
    /// </summary>
    public string Symbol { get; init; }

    /// <summary>
    /// Concept id resolved by the symbol in the given scope.
    /// </summary>
    public string ConceptId { get; init; }
}

/// <summary>
/// Default concept meaning for one scope when no explicit symbol meaning applies.
/// </summary>
public sealed record ScopedDefaultMeaningRule : OntologyRule
{
    /// <summary>
    /// Creates one scoped default-meaning rule.
    /// </summary>
    public ScopedDefaultMeaningRule(string scope, string conceptId)
    {
        Scope = Guard.RequireNotNullOrWhiteSpace(scope).Trim();
        ConceptId = Guard.RequireNotNullOrWhiteSpace(conceptId).Trim();
    }

    /// <summary>
    /// Scoped namespace for the default meaning.
    /// </summary>
    public string Scope { get; init; }

    /// <summary>
    /// Concept id resolved when no explicit scoped symbol meaning applies.
    /// </summary>
    public string ConceptId { get; init; }
}

/// <summary>
/// Allowed symbol set for one scope.
/// </summary>
public sealed record ScopedAllowedSymbolsRule : OntologyRule
{
    /// <summary>
    /// Creates one scoped allowed-symbols rule.
    /// </summary>
    public ScopedAllowedSymbolsRule(string scope, ImmutableArray<string> allowedSymbols)
    {
        Scope = Guard.RequireNotNullOrWhiteSpace(scope).Trim();
        AllowedSymbols = NormalizeAllowedSymbols(allowedSymbols);
        if (AllowedSymbols.IsDefaultOrEmpty)
            throw new InvalidOperationException("Allowed symbol sets cannot be empty.");
    }

    /// <summary>
    /// Scoped namespace for the allowed symbols.
    /// </summary>
    public string Scope { get; init; }

    /// <summary>
    /// Allowed symbols in the given scope.
    /// </summary>
    public ImmutableArray<string> AllowedSymbols { get; init; }

    static ImmutableArray<string> NormalizeAllowedSymbols(ImmutableArray<string> allowedSymbols)
    {
        if (allowedSymbols.IsDefaultOrEmpty)
            return [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> normalized = [];
        foreach (var raw in allowedSymbols)
        {
            var value = raw?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!seen.Add(value))
                continue;
            normalized.Add(value);
        }

        return [.. normalized.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
    }
}
