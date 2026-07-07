using System.Collections.Immutable;
using System.Diagnostics;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Pure authored ontology containing concepts, relation types, relations, and higher-order rules.
/// </summary>
public sealed record Ontology
{
    /// <summary>
    /// Creates one ontology.
    /// </summary>
    public Ontology(
        ImmutableDictionary<string, Concept>? concepts = null,
        ImmutableDictionary<string, RelationType>? relationTypes = null,
        ImmutableArray<ConceptRelation> relations = default,
        ImmutableArray<OntologyRule> rules = default
        )
    {
        ValidateConcepts(concepts);
        Concepts = concepts ?? [];
        RelationTypes = NormalizeRelationTypes(relationTypes);
        Rules = NormalizeRules(rules);
        Relations = NormalizeRelations(relations, BuildRelationLawFlags(Rules));
    }

    [Conditional("DEBUG")]
    static void ValidateConcepts(ImmutableDictionary<string, Concept>? concepts)
    {
        if (concepts is null)
            return;
        
        foreach (var (conceptId, concept) in concepts)
        {
            if (conceptId != concept.ConceptId)
                throw new ArgumentException($"Concept key {conceptId} doesn't match concept value id {concept.ConceptId}");
        }   
    }
    
    /// <summary>
    /// Concepts keyed by concept id.
    /// </summary>
    public ImmutableDictionary<string, Concept> Concepts { get; init; }

    /// <summary>
    /// Relation types keyed by relation-type id.
    /// </summary>
    public ImmutableDictionary<string, RelationType> RelationTypes { get; init; }

    /// <summary>
    /// Ontology relations.
    /// </summary>
    public ImmutableArray<ConceptRelation> Relations { get; init; }

    /// <summary>
    /// Higher-order ontology rules.
    /// </summary>
    public ImmutableArray<OntologyRule> Rules { get; init; }

    /// <summary>
    /// Resolves one concept by id.
    /// </summary>
    public Concept? GetConcept(string conceptId)
        => CollectionExtensions.GetValueOrDefault(Concepts, conceptId);

    /// <summary>
    /// Resolves one relation type by id.
    /// </summary>
    public RelationType? GetRelationType(string relationTypeId)
        => CollectionExtensions.GetValueOrDefault(RelationTypes, relationTypeId);

    /// <summary>
    /// Combines multiple ontologies into one normalized ontology.
    /// </summary>
    /// <remarks>
    /// Union is currently last-write-wins on duplicate concept/relation-type ids,
    /// so cross-domain union is only safe if ids are globally namespaced or duplicate definitions are validated.
    /// </remarks>
    public static Ontology Union(params ReadOnlySpan<Ontology?> ontologies)
    {
        if (ontologies.Length == 0)
            return new();

        var concepts = ImmutableDictionary.CreateBuilder<string, Concept>(StringComparer.Ordinal);
        var relationTypes = ImmutableDictionary.CreateBuilder<string, RelationType>(StringComparer.Ordinal);
        List<ConceptRelation> relations = [];
        List<OntologyRule> rules = [];

        foreach (var ontology in ontologies)
        {
            if (ontology is null)
                continue;

            foreach (var (conceptId, concept) in ontology.Concepts)
                concepts[conceptId] = concept;

            foreach (var (relationTypeId, relationType) in ontology.RelationTypes)
                relationTypes[relationTypeId] = relationType;

            relations.AddRange(ontology.Relations);
            rules.AddRange(ontology.Rules);
        }

        return new(
            concepts: concepts.ToImmutable(),
            relationTypes: relationTypes.ToImmutable(),
            relations: [.. relations],
            rules: [.. rules]);
    }

    static ImmutableDictionary<string, RelationType> NormalizeRelationTypes(ImmutableDictionary<string, RelationType>? relationTypes)
    {
        var normalized = ImmutableDictionary.CreateBuilder<string, RelationType>(StringComparer.Ordinal);
        foreach (var (relationTypeId, relationType) in StandardRelationTypes.CreateDefaults())
            normalized[relationTypeId] = relationType;

        if (relationTypes is not null)
        {
            foreach (var (relationTypeId, relationType) in relationTypes)
            {
                if (string.IsNullOrWhiteSpace(relationTypeId) || relationType is null)
                    continue;
                normalized[relationTypeId.Trim()] = relationType;
            }
        }

        return normalized.ToImmutable();
    }

    static ImmutableArray<OntologyRule> NormalizeRules(ImmutableArray<OntologyRule> rules)
    {
        Dictionary<string, OntologyRule> normalized = new(StringComparer.Ordinal);

        foreach (var rule in StandardRelationTypes.CreateDefaultRules())
            normalized[BuildRuleKey(rule)] = rule;

        if (!rules.IsDefaultOrEmpty)
        {
            foreach (var rule in rules)
            {
                if (rule is null)
                    continue;

                var normalizedRule = NormalizeRule(rule);
                normalized[BuildRuleKey(normalizedRule)] = normalizedRule;
            }
        }

        return [..
            normalized.Values
                .OrderBy(BuildRuleKey, StringComparer.Ordinal)];
    }

    static ImmutableDictionary<string, RelationLawFlags> BuildRelationLawFlags(ImmutableArray<OntologyRule> rules)
    {
        var flags = ImmutableDictionary.CreateBuilder<string, RelationLawFlags>(StringComparer.Ordinal);
        foreach (var rule in rules.OfType<RelationLawRule>())
            flags[rule.RelationTypeId] = rule.Flags;
        return flags.ToImmutable();
    }

    static ImmutableArray<ConceptRelation> NormalizeRelations(ImmutableArray<ConceptRelation> relations, ImmutableDictionary<string, RelationLawFlags> relationLawFlagsByTypeId)
    {
        if (relations.IsDefaultOrEmpty)
            return [];

        return [..
            relations
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.SourceConceptId)
                    && !string.IsNullOrWhiteSpace(x.TargetConceptId)
                    && !string.IsNullOrWhiteSpace(x.RelationTypeId)
                )
                .Select(x => Canonicalize(x, relationLawFlagsByTypeId))
                .Where(x => !string.Equals(x.SourceConceptId, x.TargetConceptId, StringComparison.Ordinal))
                .Distinct()
                .OrderBy(x => x.SourceConceptId, StringComparer.Ordinal)
                .ThenBy(x => x.TargetConceptId, StringComparer.Ordinal)
                .ThenBy(x => x.RelationTypeId, StringComparer.Ordinal)
                .ThenBy(x => x.Weight)];
    }

    static ConceptRelation Canonicalize(ConceptRelation relation, ImmutableDictionary<string, RelationLawFlags> relationLawFlagsByTypeId)
    {
        var source = relation.SourceConceptId.Trim();
        var target = relation.TargetConceptId.Trim();
        var relationTypeId = relation.RelationTypeId.Trim();
        var flags = relationLawFlagsByTypeId.GetValueOrDefault(relationTypeId, RelationLawFlags.None);
        if (flags.HasFlag(RelationLawFlags.Symmetric) && StringComparer.Ordinal.Compare(source, target) > 0)
            (source, target) = (target, source);

        return new(
            sourceConceptId: source,
            targetConceptId: target,
            relationTypeId: relationTypeId,
            weight: relation.Weight,
            properties: relation.Properties
            );
    }

    static OntologyRule NormalizeRule(OntologyRule rule)
    {
        return rule switch
        {
            RelationLawRule law => new RelationLawRule(law.RelationTypeId, law.Flags),
            RelationDomainRule domain => new RelationDomainRule(domain.RelationTypeId, domain.DomainConceptId),
            RelationRangeRule range => new RelationRangeRule(range.RelationTypeId, range.RangeConceptId),
            SubRelationRule sub => new SubRelationRule(sub.ChildRelationTypeId, sub.ParentRelationTypeId),
            RelationCardinalityRule cardinality => new RelationCardinalityRule(cardinality.RelationTypeId, cardinality.DomainConceptId, cardinality.Min, cardinality.Max),
            ScopedSymbolMeaningRule meaning => new ScopedSymbolMeaningRule(meaning.Scope, meaning.Symbol, meaning.ConceptId),
            ScopedDefaultMeaningRule defaultMeaning => new ScopedDefaultMeaningRule(scope: defaultMeaning.Scope, conceptId: defaultMeaning.ConceptId),
            ScopedAllowedSymbolsRule allowed => new ScopedAllowedSymbolsRule(allowed.Scope, allowed.AllowedSymbols),
            _ => throw new InvalidOperationException($"Unsupported ontology rule type '{rule.GetType().Name}'.")
        };
    }

    static string BuildRuleKey(OntologyRule rule)
    {
        return rule switch
        {
            RelationLawRule law => $"law|{law.RelationTypeId}",
            RelationDomainRule domain => $"domain|{domain.RelationTypeId}|{domain.DomainConceptId}",
            RelationRangeRule range => $"range|{range.RelationTypeId}|{range.RangeConceptId}",
            SubRelationRule sub => $"subrelation|{sub.ChildRelationTypeId}|{sub.ParentRelationTypeId}",
            RelationCardinalityRule cardinality => $"cardinality|{cardinality.RelationTypeId}|{cardinality.DomainConceptId}|{cardinality.Min?.ToString() ?? "*"}|{cardinality.Max?.ToString() ?? "*"}",
            ScopedSymbolMeaningRule meaning => $"scoped-meaning|{meaning.Scope}|{meaning.Symbol}|{meaning.ConceptId}",
            ScopedDefaultMeaningRule defaultMeaning => $"scoped-default|{defaultMeaning.Scope}|{defaultMeaning.ConceptId}",
            ScopedAllowedSymbolsRule allowed => $"allowed-symbols|{allowed.Scope}|{string.Join("|", allowed.AllowedSymbols)}",
            _ => throw new InvalidOperationException($"Unsupported ontology rule type '{rule.GetType().Name}'.")
        };
    }
}
