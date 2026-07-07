using System.Collections.Immutable;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Fluent builder for authored ontologies.
/// </summary>
public sealed class OntologyBuilder
{
    readonly Dictionary<string, Concept> concepts = new(StringComparer.Ordinal);
    readonly Dictionary<string, RelationType> relationTypes = new(StringComparer.Ordinal);
    readonly List<ConceptRelation> relations = [];
    readonly List<OntologyRule> rules = [];

    /// <summary>
    /// Adds one concept.
    /// </summary>
    public OntologyBuilder AddConcept(Concept concept)
    {
        ArgumentNullException.ThrowIfNull(concept);
        concepts[concept.ConceptId] = concept;
        return this;
    }

    /// <summary>
    /// Adds one relation type.
    /// </summary>
    public OntologyBuilder AddRelationType(RelationType relationType)
    {
        ArgumentNullException.ThrowIfNull(relationType);
        relationTypes[relationType.RelationTypeId] = relationType;
        return this;
    }

    /// <summary>
    /// Adds one relation.
    /// </summary>
    public OntologyBuilder AddRelation(ConceptRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        relations.Add(relation);
        return this;
    }

    /// <summary>
    /// Adds one rule.
    /// </summary>
    public OntologyBuilder AddRule(OntologyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rules.Add(rule);
        return this;
    }

    /// <summary>
    /// Adds multiple rules.
    /// </summary>
    public OntologyBuilder AddRules(IEnumerable<OntologyRule> ontologyRules)
    {
        ArgumentNullException.ThrowIfNull(ontologyRules);
        foreach (var rule in ontologyRules)
            AddRule(rule);
        return this;
    }

    /// <summary>
    /// Adds a child-to-parent subsumption relation.
    /// </summary>
    public OntologyBuilder AddParent(string childConceptId, string parentConceptId, double weight = 1d)
        => AddRelation(new(childConceptId, parentConceptId, StandardRelationTypeIds.SubConceptOf, weight));

    /// <summary>
    /// Adds a parent-to-child convenience relation by emitting a child-to-parent subsumption relation.
    /// </summary>
    public OntologyBuilder AddChild(string parentConceptId, string childConceptId, double weight = 1d)
        => AddParent(childConceptId, parentConceptId, weight);

    /// <summary>
    /// Adds an equivalence relation.
    /// </summary>
    public OntologyBuilder AddEquivalent(string leftConceptId, string rightConceptId, double weight = 1d)
        => AddRelation(new(leftConceptId, rightConceptId, StandardRelationTypeIds.EquivalentTo, weight));

    /// <summary>
    /// Adds a part-to-whole relation.
    /// </summary>
    public OntologyBuilder AddPartOf(string partConceptId, string wholeConceptId, double weight = 1d)
        => AddRelation(new(partConceptId, wholeConceptId, StandardRelationTypeIds.PartOf, weight));

    /// <summary>
    /// Adds an owner-to-property relation.
    /// </summary>
    public OntologyBuilder AddHasProperty(string ownerConceptId, string propertyConceptId, double weight = 1d)
        => AddRelation(new(ownerConceptId, propertyConceptId, StandardRelationTypeIds.HasProperty, weight));

    /// <summary>
    /// Adds a disjointness relation.
    /// </summary>
    public OntologyBuilder AddDisjoint(string leftConceptId, string rightConceptId, double weight = 1d)
        => AddRelation(new(leftConceptId, rightConceptId, StandardRelationTypeIds.DisjointWith, weight));

    /// <summary>
    /// Adds a derivation relation.
    /// </summary>
    public OntologyBuilder AddDerivedFrom(string derivedConceptId, string sourceConceptId, double weight = 1d)
        => AddRelation(new(derivedConceptId, sourceConceptId, StandardRelationTypeIds.DerivedFrom, weight));

    /// <summary>
    /// Adds a scoped symbol-meaning rule.
    /// </summary>
    public OntologyBuilder AddScopedMeaning(string scope, string symbol, string conceptId)
        => AddRule(new ScopedSymbolMeaningRule(scope: scope, symbol: symbol, conceptId: conceptId));

    /// <summary>
    /// Adds an allowed symbol-set rule for one scope.
    /// </summary>
    public OntologyBuilder AddAllowedSymbols(string scope, params ReadOnlySpan<string> allowedSymbols)
        => AddRule(new ScopedAllowedSymbolsRule(scope, [.. allowedSymbols]));

    /// <summary>
    /// Builds the ontology.
    /// </summary>
    public Ontology Build(bool validate = true)
    {
        var ontology = new Ontology(
            concepts: concepts.ToImmutableDictionary(StringComparer.Ordinal),
            relationTypes: relationTypes.ToImmutableDictionary(StringComparer.Ordinal),
            relations: [.. relations],
            rules: [.. rules]
            );

        if (validate)
            OntologyValidator.Validate(ontology);

        return ontology;
    }
}
