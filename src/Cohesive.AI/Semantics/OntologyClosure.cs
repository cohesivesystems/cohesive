using System.Collections.Immutable;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Compiled ontology view optimized for fast semantic lookups.
/// </summary>
public sealed class OntologyClosure : IConceptLabelResolver
{
    readonly ImmutableDictionary<string, int> canonicalConceptIndexByConceptId;
    readonly ImmutableArray<string> canonicalConceptIds;
    readonly ImmutableDictionary<string, int> relationTypeIndexByRelationTypeId;
    readonly ImmutableArray<ConceptRelation> relations;
    readonly ImmutableArray<int> relationTargetConceptIndexByRelationIndex;
    readonly ImmutableArray<int> relationTypeIndexByRelationIndex;
    readonly ImmutableArray<ImmutableArray<int>> outgoingRelationIndexesByConceptIndex;
    readonly ImmutableArray<ImmutableArray<int>> incomingRelationIndexesByConceptIndex;
    readonly ImmutableArray<ImmutableArray<int>> relationIndexesByTypeIndex;
    readonly ImmutableArray<ImmutableArray<int>> ancestorsByConceptIndex;
    readonly ImmutableArray<ImmutableArray<int>> descendantsByConceptIndex;
    readonly ImmutableArray<ImmutableArray<ushort>> taxonomyHopDistanceByConceptIndex;
    readonly ImmutableArray<ImmutableArray<int>> disjointConceptIndexesByConceptIndex;
    readonly ImmutableArray<RelationLawFlags> relationLawFlagsByTypeIndex;
    readonly ImmutableArray<ImmutableArray<int>> relationTypeAncestorsByTypeIndex;
    readonly ImmutableArray<ImmutableArray<int>> domainRestrictionConceptIndexesByTypeIndex;
    readonly ImmutableArray<ImmutableArray<int>> rangeRestrictionConceptIndexesByTypeIndex;
    readonly ImmutableArray<ImmutableArray<CardinalityRuleEntry>> cardinalityRulesByTypeIndex;
    readonly ImmutableDictionary<string, ImmutableDictionary<string, ImmutableArray<int>>> scopedMeaningConceptIndexesByScope;
    readonly ImmutableDictionary<string, int> scopedDefaultMeaningConceptIndexByScope;
    readonly ImmutableDictionary<string, ImmutableArray<string>> allowedSymbolsByScope;
    readonly ImmutableDictionary<string, ImmutableArray<string>> scopesByLastToken;

    OntologyClosure(
        Ontology sourceOntology,
        ImmutableDictionary<string, int> canonicalConceptIndexByConceptId,
        ImmutableArray<string> canonicalConceptIds,
        ImmutableDictionary<string, int> relationTypeIndexByRelationTypeId,
        ImmutableArray<string> relationTypeIds,
        ImmutableArray<ConceptRelation> relations,
        ImmutableArray<int> relationTargetConceptIndexByRelationIndex,
        ImmutableArray<int> relationTypeIndexByRelationIndex,
        ImmutableArray<ImmutableArray<int>> outgoingRelationIndexesByConceptIndex,
        ImmutableArray<ImmutableArray<int>> incomingRelationIndexesByConceptIndex,
        ImmutableArray<ImmutableArray<int>> relationIndexesByTypeIndex,
        ImmutableArray<ImmutableArray<int>> ancestorsByConceptIndex,
        ImmutableArray<ImmutableArray<int>> descendantsByConceptIndex,
        ImmutableArray<ImmutableArray<ushort>> taxonomyHopDistanceByConceptIndex,
        ImmutableArray<ImmutableArray<int>> disjointConceptIndexesByConceptIndex,
        ImmutableArray<RelationLawFlags> relationLawFlagsByTypeIndex,
        ImmutableArray<ImmutableArray<int>> relationTypeAncestorsByTypeIndex,
        ImmutableArray<ImmutableArray<int>> domainRestrictionConceptIndexesByTypeIndex,
        ImmutableArray<ImmutableArray<int>> rangeRestrictionConceptIndexesByTypeIndex,
        ImmutableArray<ImmutableArray<CardinalityRuleEntry>> cardinalityRulesByTypeIndex,
        ImmutableDictionary<string, ImmutableDictionary<string, ImmutableArray<int>>> scopedMeaningConceptIndexesByScope,
        ImmutableDictionary<string, int> scopedDefaultMeaningConceptIndexByScope,
        ImmutableDictionary<string, ImmutableArray<string>> allowedSymbolsByScope,
        ImmutableDictionary<string, ImmutableArray<string>> scopesByLastToken
        )
    {
        SourceOntology = sourceOntology;
        this.canonicalConceptIndexByConceptId = canonicalConceptIndexByConceptId;
        this.canonicalConceptIds = canonicalConceptIds;
        this.relationTypeIndexByRelationTypeId = relationTypeIndexByRelationTypeId;
        this.relations = relations;
        this.relationTargetConceptIndexByRelationIndex = relationTargetConceptIndexByRelationIndex;
        this.relationTypeIndexByRelationIndex = relationTypeIndexByRelationIndex;
        this.outgoingRelationIndexesByConceptIndex = outgoingRelationIndexesByConceptIndex;
        this.incomingRelationIndexesByConceptIndex = incomingRelationIndexesByConceptIndex;
        this.relationIndexesByTypeIndex = relationIndexesByTypeIndex;
        this.ancestorsByConceptIndex = ancestorsByConceptIndex;
        this.descendantsByConceptIndex = descendantsByConceptIndex;
        this.taxonomyHopDistanceByConceptIndex = taxonomyHopDistanceByConceptIndex;
        this.disjointConceptIndexesByConceptIndex = disjointConceptIndexesByConceptIndex;
        this.relationLawFlagsByTypeIndex = relationLawFlagsByTypeIndex;
        this.relationTypeAncestorsByTypeIndex = relationTypeAncestorsByTypeIndex;
        this.domainRestrictionConceptIndexesByTypeIndex = domainRestrictionConceptIndexesByTypeIndex;
        this.rangeRestrictionConceptIndexesByTypeIndex = rangeRestrictionConceptIndexesByTypeIndex;
        this.cardinalityRulesByTypeIndex = cardinalityRulesByTypeIndex;
        this.scopedMeaningConceptIndexesByScope = scopedMeaningConceptIndexesByScope;
        this.scopedDefaultMeaningConceptIndexByScope = scopedDefaultMeaningConceptIndexByScope;
        this.allowedSymbolsByScope = allowedSymbolsByScope;
        this.scopesByLastToken = scopesByLastToken;
        ConceptCount = canonicalConceptIds.Length;
        RelationTypeCount = relationTypeIds.Length;
        RelationCount = relations.Length;
    }

    readonly record struct CardinalityRuleEntry(int DomainConceptIndex, RelationCardinalityRule Rule);

    /// <summary>
    /// Source ontology used to build this closure.
    /// </summary>
    public Ontology SourceOntology { get; }

    /// <summary>
    /// Number of canonical concepts in the closure.
    /// </summary>
    public int ConceptCount { get; }

    /// <summary>
    /// Number of relation types in the closure.
    /// </summary>
    public int RelationTypeCount { get; }

    /// <summary>
    /// Number of canonicalized direct relations in the closure.
    /// </summary>
    public int RelationCount { get; }

    /// <summary>
    /// Compiles one ontology into a fast lookup closure.
    /// </summary>
    public static OntologyClosure Create(Ontology ontology)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        OntologyValidator.Validate(ontology);

        var relationLawFlagsByTypeId = BuildRelationLawFlags(ontology);
        var relationTypeAncestorsByTypeId = BuildRelationTypeAncestors(ontology);
        var allConceptIds = CollectConceptIds(ontology);
        var equivalenceRelations = GetRelations(ontology.Relations, relationTypeAncestorsByTypeId, StandardRelationTypeIds.EquivalentTo);
        var canonicalConceptIdByConceptId = BuildCanonicalConceptMap(allConceptIds, equivalenceRelations);
        var canonicalConceptIds = allConceptIds
            .Select(x => canonicalConceptIdByConceptId[x])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToImmutableArray();

        var canonicalRelations = CanonicalizeRelations(
            relations: ontology.Relations,
            canonicalConceptByConceptId: canonicalConceptIdByConceptId,
            relationLawFlagsByTypeId: relationLawFlagsByTypeId
            );

        var canonicalSubConceptRelations = FilterRelations(
            canonicalRelations,
            relationTypeAncestorsByTypeId,
            StandardRelationTypeIds.SubConceptOf,
            excludeSelf: true);
        EnsureAcyclic(canonicalConceptIds, canonicalSubConceptRelations);

        var ancestorsByConceptId = BuildAncestors(canonicalConceptIds, canonicalSubConceptRelations);
        var descendantsByConceptId = BuildDescendants(canonicalConceptIds, ancestorsByConceptId);
        var disjointConceptsByConceptId = BuildDisjointSets(
            canonicalConceptIds,
            FilterRelations(canonicalRelations, relationTypeAncestorsByTypeId, StandardRelationTypeIds.DisjointWith, excludeSelf: true));
        var domainRestrictionsByRelationTypeId = BuildRelationConceptRuleIndex(
            ontology.Rules.OfType<RelationDomainRule>(),
            canonicalConceptIdByConceptId,
            static rule => rule.RelationTypeId,
            static rule => rule.DomainConceptId);
        var rangeRestrictionsByRelationTypeId = BuildRelationConceptRuleIndex(
            ontology.Rules.OfType<RelationRangeRule>(),
            canonicalConceptIdByConceptId,
            static rule => rule.RelationTypeId,
            static rule => rule.RangeConceptId);
        var cardinalityRules = NormalizeCardinalityRules(ontology.Rules.OfType<RelationCardinalityRule>(), canonicalConceptIdByConceptId);
        var scopedMeaningsByScope = BuildScopedMeanings(ontology.Rules.OfType<ScopedSymbolMeaningRule>(), canonicalConceptIdByConceptId);
        var scopedDefaultMeaningByScope = BuildScopedDefaultMeanings(ontology.Rules.OfType<ScopedDefaultMeaningRule>(), canonicalConceptIdByConceptId);
        var allowedSymbolsByScope = BuildAllowedSymbols(ontology.Rules.OfType<ScopedAllowedSymbolsRule>());
        var scopesByLastToken = BuildScopesByLastToken(scopedMeaningsByScope.Keys, scopedDefaultMeaningByScope.Keys, allowedSymbolsByScope.Keys);

        var canonicalConceptIndexByConceptId = BuildCanonicalConceptIndexMap(canonicalConceptIds, canonicalConceptIdByConceptId);
        ImmutableArray<string> relationTypeIds = [.. ontology.RelationTypes.Keys.OrderBy(x => x, StringComparer.Ordinal)];
        var relationTypeIndexByRelationTypeId = BuildRelationTypeIndexMap(relationTypeIds);
        ImmutableArray<int> relationSourceConceptIndexByRelationIndex = [..
            canonicalRelations.Select(x => canonicalConceptIndexByConceptId[x.SourceConceptId])];
        ImmutableArray<int> relationTargetConceptIndexByRelationIndex = [..
            canonicalRelations.Select(x => canonicalConceptIndexByConceptId[x.TargetConceptId])];
        ImmutableArray<int> relationTypeIndexByRelationIndex = [..
            canonicalRelations.Select(x => relationTypeIndexByRelationTypeId[x.RelationTypeId])];

        return new(
            sourceOntology: ontology,
            canonicalConceptIndexByConceptId: canonicalConceptIndexByConceptId,
            canonicalConceptIds: canonicalConceptIds,
            relationTypeIndexByRelationTypeId: relationTypeIndexByRelationTypeId,
            relationTypeIds: relationTypeIds,
            relations: canonicalRelations,
            relationTargetConceptIndexByRelationIndex: relationTargetConceptIndexByRelationIndex,
            relationTypeIndexByRelationIndex: relationTypeIndexByRelationIndex,
            outgoingRelationIndexesByConceptIndex: BuildRelationAdjacencyByConceptIndex(
                conceptCount: canonicalConceptIds.Length,
                relations: canonicalRelations,
                relationSourceConceptIndexByRelationIndex: relationSourceConceptIndexByRelationIndex,
                relationTargetConceptIndexByRelationIndex: relationTargetConceptIndexByRelationIndex,
                outgoing: true),
            incomingRelationIndexesByConceptIndex: BuildRelationAdjacencyByConceptIndex(
                conceptCount: canonicalConceptIds.Length,
                relations: canonicalRelations,
                relationSourceConceptIndexByRelationIndex: relationSourceConceptIndexByRelationIndex,
                relationTargetConceptIndexByRelationIndex: relationTargetConceptIndexByRelationIndex,
                outgoing: false
                ),
            relationIndexesByTypeIndex: BuildRelationIndexesByTypeIndex(
                relationTypeCount: relationTypeIds.Length,
                relations: canonicalRelations,
                relationTypeIndexByRelationIndex: relationTypeIndexByRelationIndex
                ),
            ancestorsByConceptIndex: CompileConceptSetIndex(canonicalConceptIds, canonicalConceptIndexByConceptId, ancestorsByConceptId),
            descendantsByConceptIndex: CompileConceptSetIndex(canonicalConceptIds, canonicalConceptIndexByConceptId, descendantsByConceptId),
            taxonomyHopDistanceByConceptIndex: BuildTaxonomyHopDistanceMatrix(
                canonicalConceptIds,
                canonicalConceptIndexByConceptId,
                canonicalSubConceptRelations),
            disjointConceptIndexesByConceptIndex: CompileConceptSetIndex(canonicalConceptIds, canonicalConceptIndexByConceptId, disjointConceptsByConceptId),
            relationLawFlagsByTypeIndex: CompileRelationLawFlags(relationTypeIds, relationLawFlagsByTypeId),
            relationTypeAncestorsByTypeIndex: CompileRelationTypeSetIndex(relationTypeIds, relationTypeIndexByRelationTypeId, relationTypeAncestorsByTypeId),
            domainRestrictionConceptIndexesByTypeIndex: CompileRelationTypeToConceptSetIndex(relationTypeIds, canonicalConceptIndexByConceptId, domainRestrictionsByRelationTypeId),
            rangeRestrictionConceptIndexesByTypeIndex: CompileRelationTypeToConceptSetIndex(relationTypeIds, canonicalConceptIndexByConceptId, rangeRestrictionsByRelationTypeId),
            cardinalityRulesByTypeIndex: CompileCardinalityRulesByTypeIndex(relationTypeIds, relationTypeIndexByRelationTypeId, canonicalConceptIndexByConceptId, cardinalityRules),
            scopedMeaningConceptIndexesByScope: CompileScopedMeaningConceptIndexes(scopedMeaningsByScope, canonicalConceptIndexByConceptId),
            scopedDefaultMeaningConceptIndexByScope: CompileScopedDefaultMeaningConceptIndexes(scopedDefaultMeaningByScope, canonicalConceptIndexByConceptId),
            allowedSymbolsByScope: CompileAllowedSymbols(allowedSymbolsByScope),
            scopesByLastToken: scopesByLastToken
            );
    }

    /// <summary>
    /// Resolves one concept id to its canonical equivalent.
    /// </summary>
    public string Canonicalize(string conceptId)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
            return string.Empty;

        var key = conceptId.Trim();
        return TryGetCanonicalConceptIndex(key, out var conceptIndex)
            ? canonicalConceptIds[conceptIndex]
            : key;
    }

    /// <inheritdoc />
    public string? ResolveLabel(string conceptId)
    {
        var normalized = conceptId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var canonicalConceptId = Canonicalize(normalized);
        return SourceOntology.GetConcept(canonicalConceptId)?.Label?.Trim()
               ?? SourceOntology.GetConcept(normalized)?.Label?.Trim();
    }

    /// <summary>
    /// Canonicalizes, trims, deduplicates, and sorts one concept-id set.
    /// </summary>
    public ImmutableArray<string> NormalizeConceptIds(ImmutableArray<string> conceptIds)
    {
        if (conceptIds.IsDefaultOrEmpty)
            return [];

        return [..
            conceptIds
                .Select(Canonicalize)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Returns direct outgoing relations for one concept.
    /// </summary>
    public ImmutableArray<ConceptRelation> GetOutgoingRelations(string conceptId, string? relationTypeId = null)
    {
        if (!TryGetCanonicalConceptIndex(conceptId, out var conceptIndex))
            return [];

        if (string.IsNullOrWhiteSpace(relationTypeId))
            return MaterializeRelations(outgoingRelationIndexesByConceptIndex[conceptIndex]);

        if (!TryGetRelationTypeIndex(relationTypeId, out var relationTypeIndex))
            return [];

        return MaterializeRelations([..
            outgoingRelationIndexesByConceptIndex[conceptIndex]
                .Where(x => IsRelationTypeSubtype(relationTypeIndexByRelationIndex[x], relationTypeIndex))]);
    }

    /// <summary>
    /// Returns direct incoming relations for one concept.
    /// </summary>
    public ImmutableArray<ConceptRelation> GetIncomingRelations(string conceptId, string? relationTypeId = null)
    {
        if (!TryGetCanonicalConceptIndex(conceptId, out var conceptIndex))
            return [];

        if (string.IsNullOrWhiteSpace(relationTypeId))
            return MaterializeRelations(incomingRelationIndexesByConceptIndex[conceptIndex]);

        if (!TryGetRelationTypeIndex(relationTypeId, out var relationTypeIndex))
            return [];

        return MaterializeRelations([..
            incomingRelationIndexesByConceptIndex[conceptIndex]
                .Where(x => IsRelationTypeSubtype(relationTypeIndexByRelationIndex[x], relationTypeIndex))]);
    }

    /// <summary>
    /// Returns direct relations for one relation type.
    /// </summary>
    public ImmutableArray<ConceptRelation> GetRelationsByType(string relationTypeId)
    {
        return TryGetRelationTypeIndex(relationTypeId, out var relationTypeIndex)
            ? MaterializeRelations(relationIndexesByTypeIndex[relationTypeIndex])
            : [];
    }

    /// <summary>
    /// Returns ancestor concepts for one concept along <c>subconcept_of</c>.
    /// </summary>
    public ImmutableArray<string> GetAncestors(string conceptId)
    {
        return TryGetCanonicalConceptIndex(conceptId, out var conceptIndex)
            ? MaterializeConcepts(ancestorsByConceptIndex[conceptIndex])
            : [];
    }

    /// <summary>
    /// Returns descendant concepts for one concept along <c>subconcept_of</c>.
    /// </summary>
    public ImmutableArray<string> GetDescendants(string conceptId)
    {
        return TryGetCanonicalConceptIndex(conceptId, out var conceptIndex)
            ? MaterializeConcepts(descendantsByConceptIndex[conceptIndex])
            : [];
    }

    /// <summary>
    /// Indicates whether two concepts are equivalent after canonicalization.
    /// </summary>
    public bool IsEquivalent(string leftConceptId, string rightConceptId)
    {
        return TryGetCanonicalConceptIndex(leftConceptId, out var leftConceptIndex)
               && TryGetCanonicalConceptIndex(rightConceptId, out var rightConceptIndex)
               && leftConceptIndex == rightConceptIndex;
    }

    /// <summary>
    /// Indicates whether one concept is a subconcept of another.
    /// </summary>
    public bool IsSubConceptOf(string childConceptId, string parentConceptId)
    {
        if (!TryGetCanonicalConceptIndex(childConceptId, out var childConceptIndex)
            || !TryGetCanonicalConceptIndex(parentConceptId, out var parentConceptIndex))
        {
            return false;
        }

        return childConceptIndex == parentConceptIndex
               || ContainsSortedIndex(ancestorsByConceptIndex[childConceptIndex], parentConceptIndex);
    }

    /// <summary>
    /// Tries to resolve the taxonomy hop distance between two concepts over the canonical <c>subconcept_of</c> graph.
    /// </summary>
    public bool TryGetTaxonomyDistance(string leftConceptId, string rightConceptId, out int hops)
    {
        hops = 0;
        if (!TryGetCanonicalConceptIndex(leftConceptId, out var leftConceptIndex)
            || !TryGetCanonicalConceptIndex(rightConceptId, out var rightConceptIndex))
        {
            return false;
        }

        var distance = taxonomyHopDistanceByConceptIndex[leftConceptIndex][rightConceptIndex];
        if (distance == ushort.MaxValue)
            return false;

        hops = distance;
        return true;
    }

    /// <summary>
    /// Indicates whether two concepts are disjoint.
    /// </summary>
    public bool IsDisjoint(string leftConceptId, string rightConceptId)
    {
        if (!TryGetCanonicalConceptIndex(leftConceptId, out var leftConceptIndex)
            || !TryGetCanonicalConceptIndex(rightConceptId, out var rightConceptIndex)
            || leftConceptIndex == rightConceptIndex)
        {
            return false;
        }

        return ContainsSortedIndex(disjointConceptIndexesByConceptIndex[leftConceptIndex], rightConceptIndex);
    }

    /// <summary>
    /// Indicates whether a direct relation exists between two canonicalized concepts.
    /// </summary>
    public bool HasDirectRelation(string sourceConceptId, string targetConceptId, string relationTypeId)
    {
        if (!TryGetCanonicalConceptIndex(sourceConceptId, out var sourceConceptIndex)
            || !TryGetCanonicalConceptIndex(targetConceptId, out var targetConceptIndex)
            || !TryGetRelationTypeIndex(relationTypeId, out var relationTypeIndex))
        {
            return false;
        }

        foreach (var relationIndex in outgoingRelationIndexesByConceptIndex[sourceConceptIndex])
        {
            if (relationTargetConceptIndexByRelationIndex[relationIndex] == targetConceptIndex
                && IsRelationTypeSubtype(relationTypeIndexByRelationIndex[relationIndex], relationTypeIndex))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns law flags for one relation type.
    /// </summary>
    public RelationLawFlags GetRelationLawFlags(string relationTypeId)
    {
        return TryGetRelationTypeIndex(relationTypeId, out var relationTypeIndex)
            ? relationLawFlagsByTypeIndex[relationTypeIndex]
            : RelationLawFlags.None;
    }

    /// <summary>
    /// Indicates whether one relation type is equal to or a subtype of another.
    /// </summary>
    public bool IsRelationTypeSubtype(string childRelationTypeId, string parentRelationTypeId)
    {
        return TryGetRelationTypeIndex(childRelationTypeId, out var childRelationTypeIndex)
               && TryGetRelationTypeIndex(parentRelationTypeId, out var parentRelationTypeIndex)
               && IsRelationTypeSubtype(childRelationTypeIndex, parentRelationTypeIndex);
    }

    /// <summary>
    /// Returns domain restrictions for one relation type.
    /// </summary>
    public ImmutableArray<string> GetDomainRestrictions(string relationTypeId)
    {
        return TryGetRelationTypeIndex(relationTypeId, out var relationTypeIndex)
            ? MaterializeConcepts(domainRestrictionConceptIndexesByTypeIndex[relationTypeIndex])
            : [];
    }

    /// <summary>
    /// Returns range restrictions for one relation type.
    /// </summary>
    public ImmutableArray<string> GetRangeRestrictions(string relationTypeId)
    {
        return TryGetRelationTypeIndex(relationTypeId, out var relationTypeIndex)
            ? MaterializeConcepts(rangeRestrictionConceptIndexesByTypeIndex[relationTypeIndex])
            : [];
    }

    /// <summary>
    /// Returns cardinality constraints for one relation type and optional domain concept.
    /// </summary>
    public ImmutableArray<RelationCardinalityRule> GetCardinalityRules(string relationTypeId, string? domainConceptId = null)
    {
        if (!TryGetRelationTypeIndex(relationTypeId, out var relationTypeIndex))
            return [];

        var domainConceptIndex = string.IsNullOrWhiteSpace(domainConceptId)
            ? -1
            : TryGetCanonicalConceptIndex(domainConceptId, out var resolvedConceptIndex)
                ? resolvedConceptIndex
                : -1;

        if (!string.IsNullOrWhiteSpace(domainConceptId) && domainConceptIndex < 0)
            return [];

        return [..
            cardinalityRulesByTypeIndex[relationTypeIndex]
                .Where(x => domainConceptIndex < 0 || x.DomainConceptIndex == domainConceptIndex)
                .Select(x => x.Rule)];
    }

    /// <summary>
    /// Resolves one scoped symbol to a concept id.
    /// </summary>
    public bool TryGetScopedMeaning(string scope, string symbol, out string conceptId)
    {
        conceptId = string.Empty;
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(symbol))
            return false;

        var normalizedScope = scope.Trim();
        var normalizedSymbol = symbol.Trim();
        if (!scopedMeaningConceptIndexesByScope.TryGetValue(normalizedScope, out var meanings)
            || !meanings.TryGetValue(normalizedSymbol, out var conceptIndexes)
            || conceptIndexes.IsDefaultOrEmpty)
        {
            return false;
        }

        conceptId = canonicalConceptIds[conceptIndexes[0]];
        return true;
    }
    
    /// <summary>
    /// Resolves one scoped symbol to a concept id.
    /// </summary>
    /// <param name="scope">The scope.</param>
    /// <param name="symbol">The symbol to resolve to a concept in the scope.</param>
    /// <returns>The concept id associated with the given symbol in the given scope.</returns>
    public string? TryGetScopedMeaning(string scope, string symbol) => 
        TryGetScopedMeaning(scope: scope, symbol: symbol, out var conceptId) ? conceptId : null;

    /// <summary>
    /// Returns all concept meanings for one scoped symbol.
    /// </summary>
    public ImmutableArray<string> GetScopedMeanings(string scope, string symbol)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(symbol))
            return [];

        var normalizedScope = scope.Trim();
        var normalizedSymbol = symbol.Trim();
        return scopedMeaningConceptIndexesByScope.TryGetValue(normalizedScope, out var meanings)
               && meanings.TryGetValue(normalizedSymbol, out var conceptIndexes)
            ? MaterializeConcepts(conceptIndexes)
            : [];
    }

    /// <summary>
    /// Returns ontology scopes that match the supplied scope by exact match or last-token suffix match.
    /// Exact matches are returned first.
    /// </summary>
    public ImmutableArray<string> GetMatchingScopes(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return [];

        var normalizedScope = scope.Trim();
        SortedSet<string> matches = new(StringComparer.Ordinal);

        if (scopedMeaningConceptIndexesByScope.ContainsKey(normalizedScope)
            || scopedDefaultMeaningConceptIndexByScope.ContainsKey(normalizedScope)
            || allowedSymbolsByScope.ContainsKey(normalizedScope))
        {
            matches.Add(normalizedScope);
        }

        var lastToken = LastPathToken(normalizedScope);
        if (lastToken.Length > 0 && scopesByLastToken.TryGetValue(lastToken, out var scopes))
        {
            foreach (var candidateScope in scopes)
                matches.Add(candidateScope);
        }

        return [..
            matches
                .OrderByDescending(x => string.Equals(x, normalizedScope, StringComparison.Ordinal))
                .ThenBy(x => x, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Returns allowed symbols for one scope.
    /// </summary>
    public bool TryGetAllowedSymbols(string scope, out ImmutableArray<string> allowedSymbols)
    {
        allowedSymbols = [];
        if (string.IsNullOrWhiteSpace(scope))
            return false;

        if (!allowedSymbolsByScope.TryGetValue(scope.Trim(), out var symbols))
            return false;

        allowedSymbols = symbols;
        return !allowedSymbols.IsDefaultOrEmpty;
    }

    /// <summary>
    /// Resolves the default concept meaning for one scope.
    /// </summary>
    public bool TryGetScopedDefaultMeaning(string scope, out string conceptId)
    {
        conceptId = string.Empty;
        if (string.IsNullOrWhiteSpace(scope))
            return false;

        if (!scopedDefaultMeaningConceptIndexByScope.TryGetValue(scope.Trim(), out var conceptIndex))
            return false;

        conceptId = canonicalConceptIds[conceptIndex];
        return true;
    }

    bool TryGetCanonicalConceptIndex(string? conceptId, out int conceptIndex)
    {
        conceptIndex = -1;
        if (string.IsNullOrWhiteSpace(conceptId))
            return false;

        return canonicalConceptIndexByConceptId.TryGetValue(conceptId.Trim(), out conceptIndex);
    }

    bool TryGetRelationTypeIndex(string? relationTypeId, out int relationTypeIndex)
    {
        relationTypeIndex = -1;
        if (string.IsNullOrWhiteSpace(relationTypeId))
            return false;

        return relationTypeIndexByRelationTypeId.TryGetValue(relationTypeId.Trim(), out relationTypeIndex);
    }

    bool IsRelationTypeSubtype(int childRelationTypeIndex, int parentRelationTypeIndex)
    {
        return childRelationTypeIndex == parentRelationTypeIndex
               || ContainsSortedIndex(relationTypeAncestorsByTypeIndex[childRelationTypeIndex], parentRelationTypeIndex);
    }

    ImmutableArray<string> MaterializeConcepts(ImmutableArray<int> conceptIndexes)
        => [.. conceptIndexes.Select(x => canonicalConceptIds[x])];

    ImmutableArray<ConceptRelation> MaterializeRelations(ImmutableArray<int> relationIndexes)
        => [.. relationIndexes.Select(x => relations[x])];

    static bool ContainsSortedIndex(ImmutableArray<int> sortedIndexes, int candidateIndex)
        => !sortedIndexes.IsDefaultOrEmpty && sortedIndexes.BinarySearch(candidateIndex) >= 0;

    static int CompareRelations(
        ConceptRelation left,
        ConceptRelation right)
    {
        var source = StringComparer.Ordinal.Compare(left.SourceConceptId, right.SourceConceptId);
        if (source != 0)
            return source;

        var target = StringComparer.Ordinal.Compare(left.TargetConceptId, right.TargetConceptId);
        if (target != 0)
            return target;

        var relationType = StringComparer.Ordinal.Compare(left.RelationTypeId, right.RelationTypeId);
        if (relationType != 0)
            return relationType;

        return left.Weight.CompareTo(right.Weight);
    }

    static ImmutableDictionary<string, int> BuildCanonicalConceptIndexMap(
        ImmutableArray<string> canonicalConceptIds,
        ImmutableDictionary<string, string> canonicalConceptIdByConceptId)
    {
        var indexByCanonicalConceptId = canonicalConceptIds
            .Select((conceptId, index) => (conceptId, index))
            .ToImmutableDictionary(static x => x.conceptId, static x => x.index, StringComparer.Ordinal);

        var builder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var (conceptId, canonicalConceptId) in canonicalConceptIdByConceptId)
            builder[conceptId] = indexByCanonicalConceptId[canonicalConceptId];

        return builder.ToImmutable();
    }

    static ImmutableDictionary<string, int> BuildRelationTypeIndexMap(ImmutableArray<string> relationTypeIds) => relationTypeIds
        .Select((relationTypeId, index) => (relationTypeId, index))
        .ToImmutableDictionary(static x => x.relationTypeId, static x => x.index, StringComparer.Ordinal);

    static ImmutableArray<ImmutableArray<int>> BuildRelationAdjacencyByConceptIndex(
        int conceptCount,
        ImmutableArray<ConceptRelation> relations,
        ImmutableArray<int> relationSourceConceptIndexByRelationIndex,
        ImmutableArray<int> relationTargetConceptIndexByRelationIndex,
        bool outgoing
        )
    {
        var adjacency = Enumerable.Range(0, conceptCount)
            .Select(_ => new List<int>())
            .ToArray();

        for (var relationIndex = 0; relationIndex < relations.Length; relationIndex++)
        {
            var conceptIndex = outgoing
                ? relationSourceConceptIndexByRelationIndex[relationIndex]
                : relationTargetConceptIndexByRelationIndex[relationIndex];
            adjacency[conceptIndex].Add(relationIndex);
        }

        return [..
            adjacency.Select(bucket =>
            {
                bucket.Sort((left, right) => CompareRelations(relations[left], relations[right]));
                return bucket.ToImmutableArray();
            })];
    }

    static ImmutableArray<ImmutableArray<int>> BuildRelationIndexesByTypeIndex(
        int relationTypeCount,
        ImmutableArray<ConceptRelation> relations,
        ImmutableArray<int> relationTypeIndexByRelationIndex
        )
    {
        var buckets = Enumerable.Range(0, relationTypeCount)
            .Select(_ => new List<int>())
            .ToArray();

        for (var relationIndex = 0; relationIndex < relations.Length; relationIndex++)
            buckets[relationTypeIndexByRelationIndex[relationIndex]].Add(relationIndex);

        return [..
            buckets.Select(bucket =>
            {
                bucket.Sort((left, right) => CompareRelations(relations[left], relations[right]));
                return bucket.ToImmutableArray();
            })];
    }

    static ImmutableArray<ImmutableArray<int>> CompileConceptSetIndex(
        ImmutableArray<string> canonicalConceptIds,
        ImmutableDictionary<string, int> canonicalConceptIndexByConceptId,
        ImmutableDictionary<string, ImmutableHashSet<string>> setByConceptId
        )
    {
        var compiled = new ImmutableArray<int>[canonicalConceptIds.Length];
        for (var conceptIndex = 0; conceptIndex < canonicalConceptIds.Length; conceptIndex++)
        {
            var conceptId = canonicalConceptIds[conceptIndex];
            compiled[conceptIndex] = setByConceptId.TryGetValue(conceptId, out var concepts)
                ? [..
                    concepts
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .Select(x => canonicalConceptIndexByConceptId[x])]
                : [];
        }

        return [.. compiled];
    }

    static ImmutableArray<ImmutableArray<ushort>> BuildTaxonomyHopDistanceMatrix(
        ImmutableArray<string> canonicalConceptIds,
        ImmutableDictionary<string, int> canonicalConceptIndexByConceptId,
        ImmutableArray<ConceptRelation> canonicalSubConceptRelations
        )
    {
        var adjacency = Enumerable.Range(0, canonicalConceptIds.Length)
            .Select(_ => new List<int>())
            .ToArray();

        foreach (var relation in canonicalSubConceptRelations)
        {
            var childConceptIndex = canonicalConceptIndexByConceptId[relation.SourceConceptId];
            var parentConceptIndex = canonicalConceptIndexByConceptId[relation.TargetConceptId];
            adjacency[childConceptIndex].Add(parentConceptIndex);
            adjacency[parentConceptIndex].Add(childConceptIndex);
        }

        var distances = new ushort[canonicalConceptIds.Length][];
        for (var startConceptIndex = 0; startConceptIndex < canonicalConceptIds.Length; startConceptIndex++)
        {
            var distanceRow = Enumerable.Repeat(ushort.MaxValue, canonicalConceptIds.Length).ToArray();
            Queue<int> queue = new();
            distanceRow[startConceptIndex] = 0;
            queue.Enqueue(startConceptIndex);

            while (queue.Count > 0)
            {
                var conceptIndex = queue.Dequeue();
                var nextDistance = (ushort)(distanceRow[conceptIndex] + 1);
                foreach (var adjacentConceptIndex in adjacency[conceptIndex])
                {
                    if (distanceRow[adjacentConceptIndex] <= nextDistance)
                        continue;

                    distanceRow[adjacentConceptIndex] = nextDistance;
                    queue.Enqueue(adjacentConceptIndex);
                }
            }

            distances[startConceptIndex] = distanceRow;
        }

        return [.. distances.Select(static row => ImmutableArray.Create(row))];
    }

    static ImmutableArray<RelationLawFlags> CompileRelationLawFlags(
        ImmutableArray<string> relationTypeIds,
        ImmutableDictionary<string, RelationLawFlags> relationLawFlagsByTypeId
        )
    {
        var compiled = new RelationLawFlags[relationTypeIds.Length];
        for (var relationTypeIndex = 0; relationTypeIndex < relationTypeIds.Length; relationTypeIndex++)
            compiled[relationTypeIndex] = relationLawFlagsByTypeId.GetValueOrDefault(relationTypeIds[relationTypeIndex], RelationLawFlags.None);
        return [.. compiled];
    }

    static ImmutableArray<ImmutableArray<int>> CompileRelationTypeSetIndex(
        ImmutableArray<string> relationTypeIds,
        ImmutableDictionary<string, int> relationTypeIndexByRelationTypeId,
        ImmutableDictionary<string, ImmutableHashSet<string>> setByRelationTypeId
        )
    {
        var compiled = new ImmutableArray<int>[relationTypeIds.Length];
        for (var relationTypeIndex = 0; relationTypeIndex < relationTypeIds.Length; relationTypeIndex++)
        {
            var relationTypeId = relationTypeIds[relationTypeIndex];
            compiled[relationTypeIndex] = setByRelationTypeId.TryGetValue(relationTypeId, out var relationTypeIdsInSet)
                ? [..
                    relationTypeIdsInSet
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .Select(x => relationTypeIndexByRelationTypeId[x])]
                : [];
        }

        return [.. compiled];
    }

    static ImmutableArray<ImmutableArray<int>> CompileRelationTypeToConceptSetIndex(
        ImmutableArray<string> relationTypeIds,
        ImmutableDictionary<string, int> canonicalConceptIndexByConceptId,
        ImmutableDictionary<string, ImmutableHashSet<string>> conceptSetByRelationTypeId)
    {
        var compiled = new ImmutableArray<int>[relationTypeIds.Length];
        for (var relationTypeIndex = 0; relationTypeIndex < relationTypeIds.Length; relationTypeIndex++)
        {
            var relationTypeId = relationTypeIds[relationTypeIndex];
            compiled[relationTypeIndex] = conceptSetByRelationTypeId.TryGetValue(relationTypeId, out var conceptIds)
                ? [..
                    conceptIds
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .Select(x => canonicalConceptIndexByConceptId[x])]
                : [];
        }

        return [.. compiled];
    }

    static ImmutableArray<ImmutableArray<CardinalityRuleEntry>> CompileCardinalityRulesByTypeIndex(
        ImmutableArray<string> relationTypeIds,
        ImmutableDictionary<string, int> relationTypeIndexByRelationTypeId,
        ImmutableDictionary<string, int> canonicalConceptIndexByConceptId,
        ImmutableArray<RelationCardinalityRule> rules
        )
    {
        var compiled = Enumerable.Range(0, relationTypeIds.Length)
            .Select(_ => new List<CardinalityRuleEntry>())
            .ToArray();

        foreach (var rule in rules)
        {
            var relationTypeIndex = relationTypeIndexByRelationTypeId[rule.RelationTypeId];
            compiled[relationTypeIndex].Add(new(
                DomainConceptIndex: canonicalConceptIndexByConceptId[rule.DomainConceptId],
                Rule: rule));
        }

        return [..
            compiled.Select(x => x.ToImmutableArray())];
    }

    static ImmutableDictionary<string, ImmutableDictionary<string, ImmutableArray<int>>> CompileScopedMeaningConceptIndexes(
        ImmutableDictionary<string, ImmutableDictionary<string, ImmutableArray<string>>> scopedMeaningsByScope,
        ImmutableDictionary<string, int> canonicalConceptIndexByConceptId
        )
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, ImmutableArray<int>>>(StringComparer.Ordinal);
        foreach (var (scope, meanings) in scopedMeaningsByScope)
        {
            var scopedBuilder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (symbol, conceptIds) in meanings)
            {
                scopedBuilder[symbol] = [..
                    conceptIds
                        .Select(x => canonicalConceptIndexByConceptId[x])];
            }

            builder[scope] = scopedBuilder.ToImmutable();
        }

        return builder.ToImmutable();
    }

    static ImmutableDictionary<string, int> CompileScopedDefaultMeaningConceptIndexes(
        ImmutableDictionary<string, string> scopedDefaultMeaningByScope,
        ImmutableDictionary<string, int> canonicalConceptIndexByConceptId
        )
    {
        var builder = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var (scope, conceptId) in scopedDefaultMeaningByScope)
            builder[scope] = canonicalConceptIndexByConceptId[conceptId];
        return builder.ToImmutable();
    }

    static ImmutableDictionary<string, ImmutableArray<string>> CompileAllowedSymbols(
        ImmutableDictionary<string, ImmutableHashSet<string>> allowedSymbolsByScope)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (var (scope, allowedSymbols) in allowedSymbolsByScope)
            builder[scope] = [.. allowedSymbols.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
        return builder.ToImmutable();
    }

    static ImmutableArray<string> CollectConceptIds(Ontology ontology)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var conceptId in ontology.Concepts.Keys)
            ids.Add(conceptId);
        foreach (var relation in ontology.Relations)
        {
            ids.Add(relation.SourceConceptId);
            ids.Add(relation.TargetConceptId);
        }
        foreach (var rule in ontology.Rules)
        {
            switch (rule)
            {
                case RelationDomainRule domain:
                    ids.Add(domain.DomainConceptId);
                    break;
                case RelationRangeRule range:
                    ids.Add(range.RangeConceptId);
                    break;
                case RelationCardinalityRule cardinality:
                    ids.Add(cardinality.DomainConceptId);
                    break;
                case ScopedSymbolMeaningRule meaning:
                    ids.Add(meaning.ConceptId);
                    break;
                case ScopedDefaultMeaningRule defaultMeaning:
                    ids.Add(defaultMeaning.ConceptId);
                    break;
            }
        }

        return [.. ids.OrderBy(x => x, StringComparer.Ordinal)];
    }

    static ImmutableDictionary<string, RelationLawFlags> BuildRelationLawFlags(Ontology ontology)
    {
        var flags = ImmutableDictionary.CreateBuilder<string, RelationLawFlags>(StringComparer.Ordinal);
        foreach (var relationTypeId in ontology.RelationTypes.Keys)
            flags[relationTypeId] = RelationLawFlags.None;
        foreach (var rule in ontology.Rules.OfType<RelationLawRule>())
            flags[rule.RelationTypeId] = rule.Flags;
        return flags.ToImmutable();
    }

    static ImmutableDictionary<string, ImmutableHashSet<string>> BuildRelationTypeAncestors(Ontology ontology)
    {
        var parentsByChild = ontology.RelationTypes.Keys.ToDictionary(
            x => x,
            _ => new List<string>(),
            StringComparer.Ordinal
            );

        foreach (var rule in ontology.Rules.OfType<SubRelationRule>())
        {
            if (!parentsByChild.TryGetValue(rule.ChildRelationTypeId, out var parents))
            {
                parents = [];
                parentsByChild[rule.ChildRelationTypeId] = parents;
            }
            parents.Add(rule.ParentRelationTypeId);
        }

        var memo = new Dictionary<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        foreach (var relationTypeId in parentsByChild.Keys)
            _ = Resolve(relationTypeId);

        return memo.ToImmutableDictionary(StringComparer.Ordinal);

        ImmutableHashSet<string> Resolve(string relationTypeId)
        {
            if (memo.TryGetValue(relationTypeId, out var cached))
                return cached;

            var ancestors = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var parent in parentsByChild.GetValueOrDefault(relationTypeId, []).OrderBy(x => x, StringComparer.Ordinal))
            {
                ancestors.Add(parent);
                ancestors.UnionWith(Resolve(parent));
            }

            var result = ancestors.ToImmutable();
            memo[relationTypeId] = result;
            return result;
        }
    }

    static ImmutableArray<ConceptRelation> GetRelations(
        ImmutableArray<ConceptRelation> relations,
        ImmutableDictionary<string, ImmutableHashSet<string>> relationTypeAncestorsByTypeId,
        string relationTypeId
        ) =>
        FilterRelations(relations, relationTypeAncestorsByTypeId, relationTypeId, excludeSelf: false);

    static ImmutableDictionary<string, string> BuildCanonicalConceptMap(ImmutableArray<string> conceptIds, ImmutableArray<ConceptRelation> equivalenceRelations)
    {
        var parent = conceptIds.ToDictionary(x => x, x => x, StringComparer.Ordinal);

        foreach (var relation in equivalenceRelations)
            Union(relation.SourceConceptId, relation.TargetConceptId);

        var byRoot = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var conceptId in conceptIds)
        {
            var root = Find(conceptId);
            if (!byRoot.TryGetValue(root, out var members))
            {
                members = [];
                byRoot[root] = members;
            }
            members.Add(conceptId);
        }

        var canonical = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (_, members) in byRoot)
        {
            members.Sort(StringComparer.Ordinal);
            var canonicalId = members[0];
            foreach (var member in members)
                canonical[member] = canonicalId;
        }

        return canonical.ToImmutable();

        string Find(string conceptId)
        {
            if (!parent.TryGetValue(conceptId, out var root))
            {
                parent[conceptId] = conceptId;
                return conceptId;
            }

            while (!string.Equals(root, parent[root], StringComparison.Ordinal))
            {
                parent[root] = parent[parent[root]];
                root = parent[root];
            }

            parent[conceptId] = root;
            return root;
        }

        void Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (string.Equals(leftRoot, rightRoot, StringComparison.Ordinal))
                return;

            if (StringComparer.Ordinal.Compare(leftRoot, rightRoot) <= 0)
                parent[rightRoot] = leftRoot;
            else
                parent[leftRoot] = rightRoot;
        }
    }

    static ImmutableArray<ConceptRelation> CanonicalizeRelations(
        ImmutableArray<ConceptRelation> relations,
        ImmutableDictionary<string, string> canonicalConceptByConceptId,
        ImmutableDictionary<string, RelationLawFlags> relationLawFlagsByTypeId
        )
    {
        if (relations.IsDefaultOrEmpty)
            return [];

        return [..
            relations
                .Select(x =>
                {
                    var source = canonicalConceptByConceptId[x.SourceConceptId];
                    var target = canonicalConceptByConceptId[x.TargetConceptId];
                    var flags = relationLawFlagsByTypeId.GetValueOrDefault(x.RelationTypeId, RelationLawFlags.None);
                    if (flags.HasFlag(RelationLawFlags.Symmetric) && StringComparer.Ordinal.Compare(source, target) > 0)
                        (source, target) = (target, source);

                    return new ConceptRelation(
                        sourceConceptId: source,
                        targetConceptId: target,
                        relationTypeId: x.RelationTypeId,
                        weight: x.Weight,
                        properties: x.Properties);
                })
                .Distinct()
                .OrderBy(x => x.SourceConceptId, StringComparer.Ordinal)
                .ThenBy(x => x.TargetConceptId, StringComparer.Ordinal)
                .ThenBy(x => x.RelationTypeId, StringComparer.Ordinal)
                .ThenBy(x => x.Weight)];
    }

    static ImmutableArray<ConceptRelation> FilterRelations(
        ImmutableArray<ConceptRelation> relations,
        ImmutableDictionary<string, ImmutableHashSet<string>> relationTypeAncestorsByTypeId,
        string relationTypeId,
        bool excludeSelf
        )
    {
        if (relations.IsDefaultOrEmpty)
            return [];

        return [..
            relations
                .Where(x =>
                    (string.Equals(x.RelationTypeId, relationTypeId, StringComparison.Ordinal)
                     || relationTypeAncestorsByTypeId.GetValueOrDefault(x.RelationTypeId, ImmutableHashSet<string>.Empty).Contains(relationTypeId))
                    && (!excludeSelf || !string.Equals(x.SourceConceptId, x.TargetConceptId, StringComparison.Ordinal)))];
    }

    static void EnsureAcyclic(ImmutableArray<string> canonicalConceptIds, ImmutableArray<ConceptRelation> subConceptRelations)
    {
        var incomingCount = canonicalConceptIds.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        var childrenByParent = canonicalConceptIds.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var relation in subConceptRelations)
        {
            incomingCount[relation.SourceConceptId] = incomingCount[relation.SourceConceptId] + 1;
            childrenByParent[relation.TargetConceptId].Add(relation.SourceConceptId);
        }

        Queue<string> queue = new(
            incomingCount
                .Where(x => x.Value == 0)
                .Select(x => x.Key)
                .OrderBy(x => x, StringComparer.Ordinal)
            );

        var visited = 0;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            visited++;
            foreach (var child in childrenByParent[node].OrderBy(x => x, StringComparer.Ordinal))
            {
                incomingCount[child]--;
                if (incomingCount[child] == 0)
                    queue.Enqueue(child);
            }
        }

        if (visited != canonicalConceptIds.Length)
            throw new InvalidOperationException("Ontology contains a cycle in the subconcept hierarchy.");
    }

    static ImmutableDictionary<string, ImmutableHashSet<string>> BuildAncestors(ImmutableArray<string> canonicalConceptIds, ImmutableArray<ConceptRelation> subConceptRelations)
    {
        var parentsByChild = canonicalConceptIds.ToDictionary(
            x => x,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var relation in subConceptRelations)
            parentsByChild[relation.SourceConceptId].Add(relation.TargetConceptId);

        var memo = new Dictionary<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
        foreach (var conceptId in canonicalConceptIds)
            _ = Resolve(conceptId);

        return memo.ToImmutableDictionary(StringComparer.Ordinal);

        ImmutableHashSet<string> Resolve(string conceptId)
        {
            if (memo.TryGetValue(conceptId, out var cached))
                return cached;

            var ancestors = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var parent in parentsByChild[conceptId].OrderBy(x => x, StringComparer.Ordinal))
            {
                ancestors.Add(parent);
                ancestors.UnionWith(Resolve(parent));
            }

            var result = ancestors.ToImmutable();
            memo[conceptId] = result;
            return result;
        }
    }

    static ImmutableDictionary<string, ImmutableHashSet<string>> BuildDescendants(
        ImmutableArray<string> canonicalConceptIds,
        ImmutableDictionary<string, ImmutableHashSet<string>> ancestorsByConceptId)
    {
        var descendants = canonicalConceptIds.ToDictionary(
            x => x,
            _ => ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var (conceptId, ancestors) in ancestorsByConceptId)
        {
            foreach (var ancestor in ancestors)
                descendants[ancestor].Add(conceptId);
        }

        return descendants.ToImmutableDictionary(
            static x => x.Key,
            static x => x.Value.ToImmutable(),
            StringComparer.Ordinal);
    }

    static ImmutableDictionary<string, ImmutableHashSet<string>> BuildDisjointSets(
        ImmutableArray<string> canonicalConceptIds,
        ImmutableArray<ConceptRelation> disjointRelations)
    {
        var disjoint = canonicalConceptIds.ToDictionary(
            x => x,
            _ => ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var relation in disjointRelations)
        {
            disjoint[relation.SourceConceptId].Add(relation.TargetConceptId);
            disjoint[relation.TargetConceptId].Add(relation.SourceConceptId);
        }

        return disjoint.ToImmutableDictionary(
            static x => x.Key,
            static x => x.Value.ToImmutable(),
            StringComparer.Ordinal
            );
    }

    static ImmutableDictionary<string, ImmutableHashSet<string>> BuildRelationConceptRuleIndex<TRule>(
        IEnumerable<TRule> rules,
        ImmutableDictionary<string, string> canonicalConceptByConceptId,
        Func<TRule, string> relationTypeSelector,
        Func<TRule, string> conceptSelector
        ) where TRule : OntologyRule
    {
        var index = new Dictionary<string, ImmutableHashSet<string>.Builder>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            var relationTypeId = relationTypeSelector(rule).Trim();
            var conceptId = canonicalConceptByConceptId[conceptSelector(rule)];
            if (!index.TryGetValue(relationTypeId, out var concepts))
            {
                concepts = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                index[relationTypeId] = concepts;
            }
            concepts.Add(conceptId);
        }

        return index.ToImmutableDictionary(
            static x => x.Key,
            static x => x.Value.ToImmutable(),
            StringComparer.Ordinal
            );
    }

    static ImmutableArray<RelationCardinalityRule> NormalizeCardinalityRules(
        IEnumerable<RelationCardinalityRule> rules,
        ImmutableDictionary<string, string> canonicalConceptByConceptId)
    {
        return [..
            rules
                .Select(x => new RelationCardinalityRule(
                    relationTypeId: x.RelationTypeId,
                    domainConceptId: canonicalConceptByConceptId[x.DomainConceptId],
                    min: x.Min,
                    max: x.Max))
                .Distinct()
                .OrderBy(x => x.RelationTypeId, StringComparer.Ordinal)
                .ThenBy(x => x.DomainConceptId, StringComparer.Ordinal)
                .ThenBy(x => x.Min)
                .ThenBy(x => x.Max)];
    }

    static ImmutableDictionary<string, ImmutableDictionary<string, ImmutableArray<string>>> BuildScopedMeanings(
        IEnumerable<ScopedSymbolMeaningRule> rules,
        ImmutableDictionary<string, string> canonicalConceptByConceptId
        )
    {
        var byScope = new Dictionary<string, Dictionary<string, SortedSet<string>>>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!byScope.TryGetValue(rule.Scope, out var meanings))
            {
                meanings = new(StringComparer.OrdinalIgnoreCase);
                byScope[rule.Scope] = meanings;
            }

            if (!meanings.TryGetValue(rule.Symbol, out var conceptIds))
            {
                conceptIds = new(StringComparer.Ordinal);
                meanings[rule.Symbol] = conceptIds;
            }

            conceptIds.Add(canonicalConceptByConceptId[rule.ConceptId]);
        }

        return byScope.ToImmutableDictionary(
            static x => x.Key,
            static x => x.Value.ToImmutableDictionary(
                static y => y.Key,
                static y => y.Value.ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
    }

    static ImmutableDictionary<string, string> BuildScopedDefaultMeanings(
        IEnumerable<ScopedDefaultMeaningRule> rules,
        ImmutableDictionary<string, string> canonicalConceptByConceptId
        )
    {
        var byScope = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var rule in rules.OrderBy(x => x.Scope, StringComparer.Ordinal))
            byScope[rule.Scope] = canonicalConceptByConceptId[rule.ConceptId];
        return [..byScope];
    }

    static ImmutableDictionary<string, ImmutableHashSet<string>> BuildAllowedSymbols(IEnumerable<ScopedAllowedSymbolsRule> rules)
    {
        var byScope = new Dictionary<string, ImmutableHashSet<string>.Builder>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (!byScope.TryGetValue(rule.Scope, out var symbols))
            {
                symbols = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
                byScope[rule.Scope] = symbols;
            }

            symbols.UnionWith(rule.AllowedSymbols);
        }

        return byScope.ToImmutableDictionary(
            static x => x.Key,
            static x => x.Value.ToImmutable(),
            StringComparer.Ordinal
            );
    }

    static ImmutableDictionary<string, ImmutableArray<string>> BuildScopesByLastToken(
        IEnumerable<string> meaningScopes,
        IEnumerable<string> defaultScopes,
        IEnumerable<string> allowedScopes)
    {
        var byLastToken = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in meaningScopes.Concat(defaultScopes).Concat(allowedScopes))
        {
            if (string.IsNullOrWhiteSpace(scope))
                continue;

            var normalizedScope = scope.Trim();
            var lastToken = LastPathToken(normalizedScope);
            if (lastToken.Length == 0)
                continue;

            if (!byLastToken.TryGetValue(lastToken, out var scopes))
            {
                scopes = new SortedSet<string>(StringComparer.Ordinal);
                byLastToken[lastToken] = scopes;
            }

            scopes.Add(normalizedScope);
        }

        return byLastToken.ToImmutableDictionary(
            static x => x.Key,
            static x => x.Value.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    static string LastPathToken(string scope)
    {
        var trimmed = scope.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        var slash = trimmed.LastIndexOf('/');
        var dot = trimmed.LastIndexOf('.');
        var separator = Math.Max(slash, dot);
        if (separator >= 0 && separator + 1 < trimmed.Length)
            return trimmed[(separator + 1)..];
        return trimmed;
    }
}
