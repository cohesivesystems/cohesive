using System.Collections.Immutable;
using System.Numerics;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Formal context for FCA with stable object, attribute, and incidence sets.
/// </summary>
public sealed class FormalContext<TObject, TAttribute>
    where TObject : notnull
    where TAttribute : notnull
{
    readonly ImmutableDictionary<TObject, ImmutableHashSet<TAttribute>> objectAttributes;

    /// <summary>Initializes a new instance of the formal context type.</summary>
    public FormalContext(
        IEnumerable<TObject> objects,
        IEnumerable<TAttribute> attributes,
        IReadOnlyDictionary<TObject, IReadOnlyCollection<TAttribute>> incidence
        )
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(incidence);

        Objects = NormalizeDistinct(objects);
        Attributes = NormalizeDistinct(attributes);
        objectAttributes = NormalizeIncidence(Objects, Attributes, incidence);
    }

    /// <summary>
    /// Objects in the formal context.
    /// </summary>
    public ImmutableArray<TObject> Objects { get; }

    /// <summary>
    /// Attributes in the formal context.
    /// </summary>
    public ImmutableArray<TAttribute> Attributes { get; }

    /// <summary>
    /// Returns the attributes incident on one object.
    /// </summary>
    public ImmutableHashSet<TAttribute> AttributesOf(TObject obj)
        => objectAttributes.GetValueOrDefault(obj, ImmutableHashSet<TAttribute>.Empty);

    /// <summary>
    /// Indicates whether one object has one attribute.
    /// </summary>
    public bool HasIncidence(TObject obj, TAttribute attribute)
        => objectAttributes.TryGetValue(obj, out var attributes) && attributes.Contains(attribute);

    /// <summary>
    /// Returns the object extent for one attribute set.
    /// </summary>
    public ImmutableHashSet<TObject> ExtentOfAttributeSet(IEnumerable<TAttribute> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var required = attributes.ToImmutableHashSet();
        var extent = ImmutableHashSet.CreateBuilder<TObject>();

        foreach (var obj in Objects)
        {
            if (required.IsSubsetOf(objectAttributes[obj]))
                extent.Add(obj);
        }

        return extent.ToImmutable();
    }

    /// <summary>
    /// Returns the shared intent for one object set.
    /// </summary>
    public ImmutableHashSet<TAttribute> IntentOfObjectSet(IEnumerable<TObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);

        var selected = NormalizeDistinct(objects);
        if (selected.IsDefaultOrEmpty || selected.Length == 0)
            return [.. Attributes];

        var intent = objectAttributes.GetValueOrDefault(selected[0], ImmutableHashSet<TAttribute>.Empty).ToBuilder();
        for (var i = 1; i < selected.Length; i++)
        {
            if (!objectAttributes.TryGetValue(selected[i], out var attributes))
                return [];

            intent.IntersectWith(attributes);
        }

        return intent.ToImmutable();
    }

    /// <summary>
    /// Returns the closure of one attribute set.
    /// </summary>
    public ImmutableHashSet<TAttribute> ClosureOfAttributeSet(IEnumerable<TAttribute> attributes)
        => IntentOfObjectSet(ExtentOfAttributeSet(attributes));

    /// <summary>
    /// Returns the closure of one object set.
    /// </summary>
    public ImmutableHashSet<TObject> ClosureOfObjectSet(IEnumerable<TObject> objects)
        => ExtentOfAttributeSet(IntentOfObjectSet(objects));

    static ImmutableArray<T> NormalizeDistinct<T>(IEnumerable<T> values) where T : notnull
    {
        HashSet<T> seen = [];
        List<T> normalized = [];
        foreach (var value in values)
        {
            if (!seen.Add(value))
                continue;
            normalized.Add(value);
        }
        return [.. normalized];
    }

    static ImmutableDictionary<TObject, ImmutableHashSet<TAttribute>> NormalizeIncidence(
        ImmutableArray<TObject> objects,
        ImmutableArray<TAttribute> attributes,
        IReadOnlyDictionary<TObject, IReadOnlyCollection<TAttribute>> incidence
        )
    {
        var knownAttributes = attributes.ToImmutableHashSet();
        var normalized = ImmutableDictionary.CreateBuilder<TObject, ImmutableHashSet<TAttribute>>();

        foreach (var obj in objects)
        {
            if (!incidence.TryGetValue(obj, out var rawAttributes) || rawAttributes is null)
            {
                normalized[obj] = ImmutableHashSet<TAttribute>.Empty;
                continue;
            }

            normalized[obj] = [..
                rawAttributes
                    .Where(knownAttributes.Contains)];
        }

        return normalized.ToImmutable();
    }
}

/// <summary>
/// Formal concept with one extent and one intent.
/// </summary>
public sealed record FormalConcept<TObject, TAttribute>(
    ImmutableHashSet<TObject> Extent,
    ImmutableHashSet<TAttribute> Intent
    )
    where TObject : notnull
    where TAttribute : notnull
{
    /// <summary>
    /// Objects sharing the intent.
    /// </summary>
    public ImmutableHashSet<TObject> Extent { get; } = Extent ?? throw new ArgumentNullException(nameof(Extent));

    /// <summary>
    /// Attributes shared by the extent.
    /// </summary>
    public ImmutableHashSet<TAttribute> Intent { get; } = Intent ?? throw new ArgumentNullException(nameof(Intent));
}

/// <summary>
/// One node in the concept lattice Hasse diagram.
/// </summary>
public sealed record ConceptLatticeNode<TObject, TAttribute>
    where TObject : notnull
    where TAttribute : notnull
{
    /// <summary>Initializes a new instance of the concept lattice node type.</summary>
    public ConceptLatticeNode(
        int id,
        FormalConcept<TObject, TAttribute> concept,
        ImmutableArray<int> parents,
        ImmutableArray<int> children
        )
    {
        Id = id;
        Concept = concept ?? throw new ArgumentNullException(nameof(concept));
        Parents = parents.IsDefault ? [] : parents;
        Children = children.IsDefault ? [] : children;
    }

    /// <summary>Gets the id.</summary>
    public int Id { get; }

    /// <summary>Gets the concept.</summary>
    public FormalConcept<TObject, TAttribute> Concept { get; }

    /// <summary>Gets the parents.</summary>
    public ImmutableArray<int> Parents { get; }

    /// <summary>Gets the children.</summary>
    public ImmutableArray<int> Children { get; }
}

/// <summary>
/// FCA concept lattice built from one formal context.
/// </summary>
public sealed class ConceptLattice<TObject, TAttribute>(
    FormalContext<TObject, TAttribute> context,
    ImmutableArray<ConceptLatticeNode<TObject, TAttribute>> nodes
    )
    where TObject : notnull
    where TAttribute : notnull
{
    /// <summary>Gets the context.</summary>
    public FormalContext<TObject, TAttribute> Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Gets the nodes.</summary>
    public ImmutableArray<ConceptLatticeNode<TObject, TAttribute>> Nodes { get; } = nodes.IsDefault ? [] : nodes;
}

/// <summary>
/// Brute-force FCA lattice builder intended for compact attribute sets.
/// </summary>
public static class ConceptLatticeBuilder
{
    /// <summary>Builds a concept lattice from a formal context.</summary>
    public static ConceptLattice<TObject, TAttribute> Build<TObject, TAttribute>(FormalContext<TObject, TAttribute> context)
        where TObject : notnull
        where TAttribute : notnull
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Attributes.Length >= 31)
        {
            throw new InvalidOperationException(
                "The brute-force lattice builder is intended for compact attribute sets (< 31 attributes). " +
                "Use a more scalable closure algorithm for larger contexts."
                );
        }

        var objectIndex = context.Objects
            .Select((value, index) => (value, index))
            .ToDictionary(x => x.value, x => x.index);
        
        var attributeIndex = context.Attributes
            .Select((value, index) => (value, index))
            .ToDictionary(x => x.value, x => x.index);
        
        Dictionary<string, FormalConcept<TObject, TAttribute>> unique = new(StringComparer.Ordinal);

        foreach (var attributeSubset in EnumerateAttributePowerSet(context.Attributes))
        {
            var closedIntent = context.ClosureOfAttributeSet(attributeSubset);
            var extent = context.ExtentOfAttributeSet(closedIntent);
            var concept = new FormalConcept<TObject, TAttribute>(extent, closedIntent);
            unique[CanonicalKey(concept, objectIndex, attributeIndex)] = concept;
        }

        var concepts = unique.Values.ToList();
        var parentsByNodeId = concepts.Select((_, index) => index).ToDictionary(x => x, _ => new List<int>());
        var childrenByNodeId = concepts.Select((_, index) => index).ToDictionary(x => x, _ => new List<int>());

        for (var i = 0; i < concepts.Count; i++)
        {
            for (var j = 0; j < concepts.Count; j++)
            {
                if (i == j || !IsStrictSubset(concepts[i].Extent, concepts[j].Extent))
                    continue;

                var covered = false;
                for (var k = 0; k < concepts.Count; k++)
                {
                    if (k == i || k == j)
                        continue;

                    if (IsStrictSubset(concepts[i].Extent, concepts[k].Extent)
                        && IsStrictSubset(concepts[k].Extent, concepts[j].Extent))
                    {
                        covered = true;
                        break;
                    }
                }

                if (covered)
                    continue;

                parentsByNodeId[i].Add(j);
                childrenByNodeId[j].Add(i);
            }
        }

        return new(
            context,
            [..
                concepts.Select((concept, index) => new ConceptLatticeNode<TObject, TAttribute>(
                    index,
                    concept,
                    [.. parentsByNodeId[index].OrderBy(x => x)],
                    [.. childrenByNodeId[index].OrderBy(x => x)]))]);
    }

    static IEnumerable<ImmutableHashSet<TAttribute>> EnumerateAttributePowerSet<TAttribute>(ImmutableArray<TAttribute> attributes)
        where TAttribute : notnull
    {
        var maxMask = 1 << attributes.Length;
        for (var mask = 0; mask < maxMask; mask++)
        {
            var subset = ImmutableHashSet.CreateBuilder<TAttribute>();
            for (var bit = 0; bit < attributes.Length; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                    subset.Add(attributes[bit]);
            }
            yield return subset.ToImmutable();
        }
    }

    static bool IsStrictSubset<T>(ImmutableHashSet<T> left, ImmutableHashSet<T> right) where T : notnull
        => left.IsSubsetOf(right) && !left.SetEquals(right);

    static string CanonicalKey<TObject, TAttribute>(
        FormalConcept<TObject, TAttribute> concept,
        IReadOnlyDictionary<TObject, int> objectIndex,
        IReadOnlyDictionary<TAttribute, int> attributeIndex
        )
        where TObject : notnull
        where TAttribute : notnull
    {
        var extent = string.Join("|", concept.Extent.Select(x => objectIndex[x]).OrderBy(x => x));
        var intent = string.Join("|", concept.Intent.Select(x => attributeIndex[x]).OrderBy(x => x));
        return $"E:{extent}::I:{intent}";
    }
}

/// <summary>
/// Dense compiled lattice index for fast FCA access patterns.
/// </summary>
public sealed class ConceptLatticeIndex<TObject, TAttribute>
    where TObject : notnull
    where TAttribute : notnull
{
    internal ConceptLatticeIndex(
        ConceptLattice<TObject, TAttribute> sourceLattice,
        int[] nodeIds,
        TObject[] objects,
        TAttribute[] attributes,
        ImmutableDictionary<TObject, int> denseObjectByValue,
        ImmutableDictionary<TAttribute, int> denseAttributeByValue,
        int[][] parents,
        int[][] children,
        ulong[][] extentBits,
        ulong[][] intentBits,
        int[,] nodeDistance)
    {
        SourceLattice = sourceLattice;
        NodeIds = nodeIds;
        Objects = objects;
        Attributes = attributes;
        DenseObjectByValue = denseObjectByValue;
        DenseAttributeByValue = denseAttributeByValue;
        Parents = parents;
        Children = children;
        ExtentBits = extentBits;
        IntentBits = intentBits;
        NodeDistance = nodeDistance;
    }

    /// <summary>Gets the source lattice.</summary>
    public ConceptLattice<TObject, TAttribute> SourceLattice { get; }

    /// <summary>Gets the node count.</summary>
    public int NodeCount => NodeIds.Length;

    /// <summary>Gets the object count.</summary>
    public int ObjectCount => Objects.Length;

    /// <summary>Gets the attribute count.</summary>
    public int AttributeCount => Attributes.Length;

    /// <summary>Gets the node ids.</summary>
    public int[] NodeIds { get; }

    /// <summary>Gets the objects.</summary>
    public TObject[] Objects { get; }

    /// <summary>Gets the attributes.</summary>
    public TAttribute[] Attributes { get; }

    /// <summary>Gets the dense object by value.</summary>
    public ImmutableDictionary<TObject, int> DenseObjectByValue { get; }

    /// <summary>Gets the dense attribute by value.</summary>
    public ImmutableDictionary<TAttribute, int> DenseAttributeByValue { get; }

    /// <summary>Gets the parents.</summary>
    public int[][] Parents { get; }

    /// <summary>Gets the children.</summary>
    public int[][] Children { get; }

    /// <summary>Gets the extent bits.</summary>
    public ulong[][] ExtentBits { get; }

    /// <summary>Gets the intent bits.</summary>
    public ulong[][] IntentBits { get; }

    /// <summary>Gets the node distance.</summary>
    public int[,] NodeDistance { get; }
}

/// <summary>
/// Compiles one lattice into dense arrays and distance tables.
/// </summary>
public static class ConceptLatticeCompiler
{
    /// <summary>Compiles a concept lattice into an indexed representation.</summary>
    public static ConceptLatticeIndex<TObject, TAttribute> Compile<TObject, TAttribute>(ConceptLattice<TObject, TAttribute> lattice)
        where TObject : notnull
        where TAttribute : notnull
    {
        ArgumentNullException.ThrowIfNull(lattice);

        var objects = lattice.Context.Objects.ToArray();
        var attributes = lattice.Context.Attributes.ToArray();
        var denseObjectByValue = objects
            .Select((value, index) => (value, index))
            .ToImmutableDictionary(x => x.value, x => x.index);
        var denseAttributeByValue = attributes
            .Select((value, index) => (value, index))
            .ToImmutableDictionary(x => x.value, x => x.index);
        var nodeIds = lattice.Nodes.Select(x => x.Id).ToArray();
        var parents = lattice.Nodes.Select(x => x.Parents.ToArray()).ToArray();
        var children = lattice.Nodes.Select(x => x.Children.ToArray()).ToArray();
        var extentBits = new ulong[lattice.Nodes.Length][];
        var intentBits = new ulong[lattice.Nodes.Length][];

        for (var i = 0; i < lattice.Nodes.Length; i++)
        {
            extentBits[i] = DenseBitSet.CreateEmpty(objects.Length);
            intentBits[i] = DenseBitSet.CreateEmpty(attributes.Length);

            foreach (var obj in lattice.Nodes[i].Concept.Extent)
                DenseBitSet.Set(extentBits[i], denseObjectByValue[obj]);

            foreach (var attribute in lattice.Nodes[i].Concept.Intent)
                DenseBitSet.Set(intentBits[i], denseAttributeByValue[attribute]);
        }

        var nodeDistance = BuildNodeDistance(parents, children);

        return new(
            sourceLattice: lattice,
            nodeIds: nodeIds,
            objects: objects,
            attributes: attributes,
            denseObjectByValue: denseObjectByValue,
            denseAttributeByValue: denseAttributeByValue,
            parents: parents,
            children: children,
            extentBits: extentBits,
            intentBits: intentBits,
            nodeDistance: nodeDistance
            );
    }

    static int[,] BuildNodeDistance(int[][] parents, int[][] children)
    {
        var distance = new int[parents.Length, parents.Length];
        for (var i = 0; i < parents.Length; i++)
        {
            for (var j = 0; j < parents.Length; j++)
                distance[i, j] = int.MaxValue;
        }

        var undirectedAdjacency = new List<int>[parents.Length];
        for (var i = 0; i < parents.Length; i++)
        {
            undirectedAdjacency[i] = [];
            undirectedAdjacency[i].AddRange(parents[i]);
            undirectedAdjacency[i].AddRange(children[i]);
        }

        for (var i = 0; i < parents.Length; i++)
        {
            Queue<(int Node, int Depth)> queue = [];
            queue.Enqueue((i, 0));

            while (queue.Count > 0)
            {
                var (node, depth) = queue.Dequeue();
                if (distance[i, node] <= depth)
                    continue;

                distance[i, node] = depth;
                foreach (var next in undirectedAdjacency[node])
                    queue.Enqueue((next, depth + 1));
            }
        }

        return distance;
    }
}

/// <summary>
/// Small helper for lattice similarity and containment queries.
/// </summary>
public sealed class ConceptLatticeReasoner<TObject, TAttribute>(ConceptLatticeIndex<TObject, TAttribute> index)
    where TObject : notnull
    where TAttribute : notnull
{
    readonly ConceptLatticeIndex<TObject, TAttribute> index = index ?? throw new ArgumentNullException(nameof(index));

    /// <summary>Determines whether a lattice node contains an object.</summary>
    public bool NodeContainsObject(int nodeId, int denseObjectId)
        => DenseBitSet.Get(index.ExtentBits[nodeId], denseObjectId);

    /// <summary>Determines whether a lattice node contains an attribute.</summary>
    public bool NodeContainsAttribute(int nodeId, int denseAttributeId)
        => DenseBitSet.Get(index.IntentBits[nodeId], denseAttributeId);

    /// <summary>Counts attributes shared by two node intents.</summary>
    public int SharedIntentCount(int leftNodeId, int rightNodeId)
        => DenseBitSet.IntersectionCount(index.IntentBits[leftNodeId], index.IntentBits[rightNodeId]);

    /// <summary>Counts objects shared by two node extents.</summary>
    public int SharedExtentCount(int leftNodeId, int rightNodeId)
        => DenseBitSet.IntersectionCount(index.ExtentBits[leftNodeId], index.ExtentBits[rightNodeId]);

    /// <summary>Computes Jaccard similarity between two node intents.</summary>
    public double IntentJaccard(int leftNodeId, int rightNodeId)
        => DenseBitSet.Jaccard(index.IntentBits[leftNodeId], index.IntentBits[rightNodeId]);

    /// <summary>Computes Jaccard similarity between two node extents.</summary>
    public double ExtentJaccard(int leftNodeId, int rightNodeId)
        => DenseBitSet.Jaccard(index.ExtentBits[leftNodeId], index.ExtentBits[rightNodeId]);

    /// <summary>Gets the shortest-path distance between two lattice nodes.</summary>
    public int ShortestPathDistance(int leftNodeId, int rightNodeId)
        => index.NodeDistance[leftNodeId, rightNodeId];
}

static class DenseBitSet
{
    public static ulong[] CreateEmpty(int bitCount)
    {
        var words = (bitCount + 63) >> 6;
        return new ulong[words];
    }

    public static void Set(ulong[] bits, int index)
        => bits[index >> 6] |= 1UL << (index & 63);

    public static bool Get(ulong[] bits, int index)
        => (bits[index >> 6] & (1UL << (index & 63))) != 0;

    public static int IntersectionCount(ulong[] left, ulong[] right)
    {
        var count = 0;
        for (var i = 0; i < left.Length; i++)
            count += BitOperations.PopCount(left[i] & right[i]);
        return count;
    }

    public static int UnionCount(ulong[] left, ulong[] right)
    {
        var count = 0;
        for (var i = 0; i < left.Length; i++)
            count += BitOperations.PopCount(left[i] | right[i]);
        return count;
    }

    public static double Jaccard(ulong[] left, ulong[] right)
    {
        var union = UnionCount(left, right);
        if (union == 0)
            return 1d;

        return IntersectionCount(left, right) / (double)union;
    }
}
