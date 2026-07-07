namespace Cohesive.AI.Semantics;

/// <summary>
/// Structural validation for authored ontologies.
/// </summary>
public static class OntologyValidator
{
    /// <summary>
    /// Validates one ontology.
    /// </summary>
    public static void Validate(Ontology ontology)
    {
        ArgumentNullException.ThrowIfNull(ontology);

        foreach (var relation in ontology.Relations)
        {
            EnsureConcept(ontology, relation.SourceConceptId, nameof(ConceptRelation));
            EnsureConcept(ontology, relation.TargetConceptId, nameof(ConceptRelation));
            EnsureRelationType(ontology, relation.RelationTypeId, nameof(ConceptRelation));
        }

        foreach (var rule in ontology.Rules)
        {
            switch (rule)
            {
                case RelationLawRule law:
                    EnsureRelationType(ontology, law.RelationTypeId, nameof(RelationLawRule));
                    break;

                case RelationDomainRule domain:
                    EnsureRelationType(ontology, domain.RelationTypeId, nameof(RelationDomainRule));
                    EnsureConcept(ontology, domain.DomainConceptId, nameof(RelationDomainRule));
                    break;

                case RelationRangeRule range:
                    EnsureRelationType(ontology, range.RelationTypeId, nameof(RelationRangeRule));
                    EnsureConcept(ontology, range.RangeConceptId, nameof(RelationRangeRule));
                    break;

                case SubRelationRule sub:
                    EnsureRelationType(ontology, sub.ChildRelationTypeId, nameof(SubRelationRule));
                    EnsureRelationType(ontology, sub.ParentRelationTypeId, nameof(SubRelationRule));
                    break;

                case RelationCardinalityRule cardinality:
                    EnsureRelationType(ontology, cardinality.RelationTypeId, nameof(RelationCardinalityRule));
                    EnsureConcept(ontology, cardinality.DomainConceptId, nameof(RelationCardinalityRule));
                    break;

                case ScopedSymbolMeaningRule meaning:
                    EnsureConcept(ontology, meaning.ConceptId, nameof(ScopedSymbolMeaningRule));
                    break;

                case ScopedDefaultMeaningRule defaultMeaning:
                    EnsureConcept(ontology, defaultMeaning.ConceptId, nameof(ScopedDefaultMeaningRule));
                    break;

                case ScopedAllowedSymbolsRule:
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported ontology rule type '{rule.GetType().Name}'.");
            }
        }
        
        static void EnsureConcept(Ontology ontology, string conceptId, string owner)
        {
            if (!ontology.Concepts.ContainsKey(conceptId))
                throw new InvalidOperationException($"{owner} references unknown concept '{conceptId}'.");
        }

        static void EnsureRelationType(Ontology ontology, string relationTypeId, string owner)
        {
            if (!ontology.RelationTypes.ContainsKey(relationTypeId))
                throw new InvalidOperationException($"{owner} references unknown relation type '{relationTypeId}'.");
        }
    }
}
