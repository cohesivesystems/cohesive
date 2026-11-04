using Cohesive.Model.Serialization;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Transitions.Compilation;

/// <summary>
/// One configuration assignment projected from an authoritative Cohesive.Machines edge.
/// </summary>
/// <remarks>
/// This is linker evidence, not a second lifecycle definition. Its owning
/// <see cref="TransitionMachineEdgeLink.Machine"/> reference pins the exact Machine content from which it was
/// derived.
/// </remarks>
public sealed record TransitionMachineConfigurationAssignment
{
    /// <summary>Creates one Machine-derived configuration assignment.</summary>
    /// <param name="path">Aggregate-relative configuration path established by the edge.</param>
    /// <param name="value">Exact portable target value assigned at <paramref name="path"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is default or empty, or <paramref name="value"/> cannot establish a target
    /// configuration or violates its declared contract.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public TransitionMachineConfigurationAssignment(FieldPath path, PortableValue value)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A Machine configuration assignment requires a non-empty path.", nameof(path));
        if (path.Segments.Any(static segment => segment.Kind != SegmentKind.Field))
        {
            throw new ArgumentException(
                "Machine configuration assignments support only concrete field paths.",
                nameof(path));
        }

        Path = path;
        Value = Guard.RequireNotNull(value);
        if (value.State is PortableValueState.Missing
            or PortableValueState.Unknown
            or PortableValueState.Failed)
        {
            throw new ArgumentException(
                $"A Machine configuration assignment cannot establish value state '{value.State}'.",
                nameof(value));
        }
        if (value.State == PortableValueState.Absent
            && value.Contract.Presence == FieldPresence.Required
            || value.State == PortableValueState.Null
                && value.Contract.Nullability == FieldNullability.NonNullable
            || value.State == PortableValueState.Concrete
                && !value.Contract.IsSatisfiedByConstant(value.Value!.Value))
        {
            throw new ArgumentException(
                "A Machine configuration assignment value does not satisfy its declared contract.",
                nameof(value));
        }
    }

    /// <summary>Aggregate-relative configuration path established by the edge.</summary>
    public FieldPath Path { get; }

    /// <summary>Exact portable target value assigned at <see cref="Path"/>.</summary>
    public PortableValue Value { get; }
}

/// <summary>
/// Immutable, fingerprint-attributed edge semantics projected by a Cohesive.Machines linker.
/// </summary>
/// <remarks>
/// Transition IR persists only the exact Machine and edge references. This linked slice contributes source and
/// target configuration predicates, optional ambiguity evidence, and edge-owned state assignments to static sparse analysis and reference
/// interpretation. It contains no callback, resolver, service handle, or independently authored lifecycle graph.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionMachineEdgeLink
{
    /// <summary>Creates a linked Machine edge slice.</summary>
    /// <param name="machine">Exact authoritative Machine definition revision and fingerprint.</param>
    /// <param name="edge">Stable edge identity within <paramref name="machine"/>.</param>
    /// <param name="sourceConfiguration">Pure predicate recognizing legal source configurations.</param>
    /// <param name="targetConfiguration">Pure predicate recognizing the established target configuration.</param>
    /// <param name="assignments">Edge-owned assignments; empty for predicate-established configurations.</param>
    /// <param name="admissionGuards">Ordered sibling guards, shared at construction; empty for legacy unguarded links.</param>
    /// <param name="selectedGuardIndex">Index of this edge's guard, or -1 when no guards are supplied.</param>
    /// <param name="diagnostics">Proof limitations applying to this edge and its siblings.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="machine"/>, <paramref name="sourceConfiguration"/>, or
    /// <paramref name="targetConfiguration"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="edge"/> is default; <paramref name="assignments"/> is default, contains null, or contains
    /// overlapping paths; or the guard array/index is invalid.
    /// </exception>
    [JsonConstructor]
    public TransitionMachineEdgeLink(
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        Expr sourceConfiguration,
        Expr targetConfiguration,
        ImmutableArray<TransitionMachineConfigurationAssignment> assignments,
        ImmutableArray<Expr> admissionGuards = default,
        int selectedGuardIndex = -1,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Machine = Guard.RequireNotNull(machine);
        if (string.IsNullOrWhiteSpace(edge.Value))
            throw new ArgumentException("A linked Machine edge requires a stable identity.", nameof(edge));
        if (assignments.IsDefault || assignments.Any(static assignment => assignment is null))
        {
            throw new ArgumentException(
                "A linked Machine edge requires an initialized array of non-null configuration assignments.",
                nameof(assignments));
        }

        var normalized = assignments
            .OrderBy(
                static assignment => assignment.Path,
                TransitionStructuralOrdering.FieldPaths)
            .ToImmutableArray();
        for (var rightIndex = 0; rightIndex < normalized.Length; rightIndex++)
        {
            for (var leftIndex = 0; leftIndex < rightIndex; leftIndex++)
            {
                if (normalized[leftIndex].Path.Overlaps(normalized[rightIndex].Path))
                {
                    throw new ArgumentException(
                        $"Machine configuration paths '{normalized[leftIndex].Path}' and "
                        + $"'{normalized[rightIndex].Path}' overlap.",
                        nameof(assignments));
                }
            }
        }

        Edge = edge;
        SourceConfiguration = Guard.RequireNotNull(sourceConfiguration);
        TargetConfiguration = Guard.RequireNotNull(targetConfiguration);
        Assignments = normalized;
        AdmissionGuards = admissionGuards.IsDefault ? [] : admissionGuards;
        if (AdmissionGuards.Any(guard => guard is null)
            || (AdmissionGuards.IsEmpty ? selectedGuardIndex != -1 : selectedGuardIndex < 0 || selectedGuardIndex >= AdmissionGuards.Length))
            throw new ArgumentException("Supply non-null guards and a valid selected guard index.", nameof(admissionGuards));
        SelectedGuardIndex = selectedGuardIndex;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Link diagnostics scoped to this edge's selection group.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Exact authoritative Machine definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Machine { get; }

    /// <summary>Stable edge identity within <see cref="Machine"/>.</summary>
    public ExecutionNodeId Edge { get; }

    /// <summary>Pure predicate recognizing legal source configurations.</summary>
    public Expr SourceConfiguration { get; }

    /// <summary>Ordered portable sibling guards; every invocation evaluates these at most once.</summary>
    public ImmutableArray<Expr> AdmissionGuards { get; }

    /// <summary>Index of the requested edge in AdmissionGuards, or -1 for an unguarded link.</summary>
    public int SelectedGuardIndex { get; }

    /// <summary>Derived compiler expression for the requested edge's source admission.</summary>
    /// <returns>The derived source-state and selected-guard predicate.</returns>
    public Expr CreateSourceAdmission() => AdmissionGuards.IsEmpty ? SourceConfiguration
        : Expr.And(SourceConfiguration, AdmissionGuards[SelectedGuardIndex]);

    /// <summary>Derived compiler-only ambiguity expression; runtime uses the guard array directly.</summary>
    /// <returns>A derived ambiguity predicate, or null for a singleton group.</returns>
    public Expr? CreateAmbiguousConfiguration()
    {
        if (AdmissionGuards.Length < 2) return null;
        Expr Count(int start, int length) => length == 1
            ? Expr.If(AdmissionGuards[start], Expr.Const(1), Expr.Const(0))
            : Expr.Add(Count(start, length / 2), Count(start + length / 2, length - length / 2));
        return Expr.And(SourceConfiguration, Expr.Gt(Count(0, AdmissionGuards.Length), Expr.Const(1)));
    }

    /// <summary>Pure predicate recognizing the established target configuration.</summary>
    public Expr TargetConfiguration { get; }

    /// <summary>Edge-owned target assignments in deterministic field-path order.</summary>
    public ImmutableArray<TransitionMachineConfigurationAssignment> Assignments { get; }
}

/// <summary>
/// Immutable exact-reference catalog of Cohesive.Machines edge slices available during Transition linking.
/// </summary>
public sealed class TransitionMachineLinkCatalog
{
    readonly Dictionary<MachineEdgeKey, TransitionMachineEdgeLink> edges;

    /// <summary>An empty Machine link catalog.</summary>
    public static TransitionMachineLinkCatalog Empty { get; } = new([]);

    /// <summary>Creates an exact Machine edge-link catalog.</summary>
    /// <param name="edges">Machine-derived edge slices.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="edges"/> contains null or duplicates an exact Machine and edge identity.
    /// </exception>
    public TransitionMachineLinkCatalog(ImmutableArray<TransitionMachineEdgeLink> edges)
    {
        if (edges.IsDefault)
            edges = [];
        if (edges.Any(static edge => edge is null))
            throw new ArgumentException("A Machine link catalog cannot contain null edges.", nameof(edges));

        var normalized = edges
            .OrderBy(static edge => edge, TransitionStructuralOrdering.MachineEdges)
            .ToImmutableArray();
        this.edges = new(normalized.Length);
        foreach (var edge in normalized)
        {
            if (!this.edges.TryAdd(new(edge.Machine, edge.Edge), edge))
            {
                throw new ArgumentException(
                    $"Machine edge '{edge.Edge.Value}' is linked more than once for exact definition "
                    + $"'{edge.Machine.DefinitionId.Value}'.",
                    nameof(edges));
            }
        }

        Edges = normalized;
        Diagnostics = [.. normalized.SelectMany(edge => edge.Diagnostics).Distinct()];
    }

    /// <summary>Link-time proof limitations and validation evidence.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Linked edge slices in deterministic exact-reference and edge order.</summary>
    public ImmutableArray<TransitionMachineEdgeLink> Edges { get; }

    /// <summary>Attempts to resolve one exact Machine edge.</summary>
    /// <param name="machine">Exact Machine definition reference.</param>
    /// <param name="edge">Stable edge identity within <paramref name="machine"/>.</param>
    /// <param name="link">Resolved immutable edge slice when present.</param>
    /// <returns><see langword="true"/> when the exact edge is linked; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="machine"/> is <see langword="null"/>.</exception>
    public bool TryGet(
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        out TransitionMachineEdgeLink link)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return edges.TryGetValue(new(machine, edge), out link!);
    }

    readonly record struct MachineEdgeKey(
        ExecutionDefinitionReference Machine,
        ExecutionNodeId Edge);
}

/// <summary>
/// Structural canonical ordering shared by Machine link construction and Transition compilation.
/// </summary>
internal static class TransitionStructuralOrdering
{
    internal static IComparer<FieldPath> FieldPaths { get; } =
        Comparer<FieldPath>.Create(CompareFieldPaths);

    internal static IComparer<TransitionObservationAccess> ObservationAccesses { get; } =
        Comparer<TransitionObservationAccess>.Create(CompareObservationAccesses);

    internal static IComparer<TransitionMachineEdgeLink> MachineEdges { get; } =
        Comparer<TransitionMachineEdgeLink>.Create(CompareEdges);

    static int CompareFieldPaths(FieldPath left, FieldPath right)
    {
        var sharedLength = Math.Min(left.Segments.Length, right.Segments.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            var leftSegment = left.Segments[index];
            var rightSegment = right.Segments[index];
            var comparison = leftSegment.Kind.CompareTo(rightSegment.Kind);
            if (comparison != 0)
                return comparison;

            comparison = StringComparer.Ordinal.Compare(leftSegment.Segment, rightSegment.Segment);
            if (comparison != 0)
                return comparison;
        }

        return left.Segments.Length.CompareTo(right.Segments.Length);
    }

    static int CompareObservationAccesses(
        TransitionObservationAccess? left,
        TransitionObservationAccess? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        if (left.IsWhole != right.IsWhole)
            return left.IsWhole ? -1 : 1;
        return left.IsWhole
            ? 0
            : CompareFieldPaths(left.Path!.Value, right.Path!.Value);
    }

    static int CompareEdges(TransitionMachineEdgeLink? left, TransitionMachineEdgeLink? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var comparison = StringComparer.Ordinal.Compare(
            left.Machine.DefinitionId.Value,
            right.Machine.DefinitionId.Value);
        if (comparison != 0)
            return comparison;

        comparison = StringComparer.Ordinal.Compare(
            left.Machine.RevisionId.Value,
            right.Machine.RevisionId.Value);
        if (comparison != 0)
            return comparison;

        comparison = StringComparer.Ordinal.Compare(
            left.Machine.Fingerprint.Algorithm,
            right.Machine.Fingerprint.Algorithm);
        if (comparison != 0)
            return comparison;

        comparison = StringComparer.Ordinal.Compare(
            left.Machine.Fingerprint.Canonicalization,
            right.Machine.Fingerprint.Canonicalization);
        if (comparison != 0)
            return comparison;

        comparison = StringComparer.Ordinal.Compare(
            left.Machine.Fingerprint.Value,
            right.Machine.Fingerprint.Value);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Edge.Value, right.Edge.Value);
    }
}
