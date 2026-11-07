using System.Collections.Immutable;
using System.Globalization;
using System.Linq.Expressions;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Execution;

/// <summary>Configures supplied roots and complete in-memory source sets for one prepared relation.</summary>
/// <remarks>Use during evaluator creation. Each binding selects one input by role and exact CLR shape.</remarks>
public sealed class RelationQueryInMemorySources
{
    const string Convention = "cohesive.relations/in-memory-sources/v1";
    static readonly RelationQueryTargetCapabilityProfile Profile = new(
        new("cohesive.relations/in-memory-source"), new(Convention),
        [RelationQueryDocument.CurrentSchemaVersion],
        [RelationQueryCompilationProvenance.CurrentCompilerProfile],
        [
            new(new("enumerate"), new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration)),
            new(new("project"), new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.FieldProjection)),
            new(new("identity"), new PrimitiveRelationQueryCapability(RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead))
        ]);

    readonly CompiledRelationQueryPlan plan;
    readonly int maximumRows;
    readonly RelationQueryPlacementBuilder placement;
    readonly Dictionary<RelationQueryInputId, ImmutableArray<(string Id, ObservationValue Value)>> sources = [];
    string? rootIdentityField;

    internal RelationQueryInMemorySources(CompiledRelationQueryPlan plan, int maximumRows)
    {
        this.plan = plan;
        this.maximumRows = maximumRows;
        placement = RelationQueryPlacement.For(plan);
    }

    /// <summary>Declares the identity field of the relation roots supplied on each invocation.</summary>
    /// <typeparam name="T">CLR root type.</typeparam>
    /// <param name="shape">Exact CLR shape used to author the root.</param>
    /// <param name="identity">Direct string property identifying each root.</param>
    /// <returns>This configuration builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The shape or selector does not identify the compiled root contract.</exception>
    /// <exception cref="InvalidOperationException">The root is already declared or CLR metadata cannot resolve the selector.</exception>
    public RelationQueryInMemorySources Root<T>(RelationQueryClrShape<T> shape, Expression<Func<T, string>> identity)
        where T : notnull
    {
        if (rootIdentityField is not null)
            throw new InvalidOperationException("The supplied root is already configured.");
        var input = FindInput(shape, root: true);
        var field = IdentityField(shape, identity);
        Place(input, shape, field);
        rootIdentityField = field;
        return this;
    }

    /// <summary>Snapshots a complete acquired source set and validates its shape and identity keys.</summary>
    /// <typeparam name="T">CLR source row type.</typeparam>
    /// <param name="shape">Exact CLR shape selecting a single acquired input.</param>
    /// <param name="rows">Finite rows, enumerated once at configuration time.</param>
    /// <param name="identity">Direct string property containing unique, nonempty source identities.</param>
    /// <returns>This configuration builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument or row is null.</exception>
    /// <exception cref="ArgumentException">The input is missing or ambiguous, rows violate their shape or identity contract,
    /// or the collection exceeds its configured row limit.</exception>
    /// <exception cref="InvalidOperationException">The input is already bound or CLR metadata cannot resolve the selector.</exception>
    /// <exception cref="NotSupportedException">A row cannot be represented as observation data.</exception>
    public RelationQueryInMemorySources Bind<T>(RelationQueryClrShape<T> shape, IEnumerable<T> rows,
        Expression<Func<T, string>> identity) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(rows);
        var input = FindInput(shape, root: false);
        if (sources.ContainsKey(input.Input.Id))
            throw new InvalidOperationException($"Source '{input.Node}' is already configured.");
        var field = IdentityField(shape, identity);
        var graph = plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == input.Shape.GraphId).Graph;
        var graphShape = new GraphShapeId(graph, input.Shape.ShapeId);
        var snapshot = ImmutableArray.CreateBuilder<(string Id, ObservationValue Value)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (snapshot.Count == maximumRows)
                throw new ArgumentException($"Source '{input.Node}' exceeds its row limit of {maximumRows}.", nameof(rows));
            ArgumentNullException.ThrowIfNull(row);
            var value = Observation.Create(graphShape, ObservationValue.FromObject(row)).Value;
            if (!value.TryGetProperty(field, out var key) || key.Kind != ObservationValueKind.String
                || string.IsNullOrWhiteSpace(key.String) || !keys.Add(key.String))
                throw new ArgumentException($"Source '{input.Node}' requires unique, nonempty string identities.", nameof(rows));
            snapshot.Add((key.String, value));
        }
        Place(input, shape, field);
        sources.Add(input.Input.Id, snapshot.ToImmutable());
        return this;
    }

    RelationQuerySourceInputContract FindInput<T>(RelationQueryClrShape<T> shape, bool root) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(shape);
        var matches = plan.InputContract.Sources.Where(input => input.Shape == shape.Id
            && (input.Role == RelationQuerySourceInputRole.RelationRoot) == root).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException($"Shape '{shape.Id}' must select exactly one {(root ? "root" : "acquired")} input.", nameof(shape));
        return matches[0];
    }

    static string IdentityField<T>(RelationQueryClrShape<T> shape, Expression<Func<T, string>> identity) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!FieldPath.Capture(identity, shape.ResolveMemberPath).TryGetDirectFieldName(out var field))
            throw new ArgumentException("An in-memory identity selector must name a direct property.", nameof(identity));
        return field;
    }

    void Place<T>(RelationQuerySourceInputContract input, RelationQueryClrShape<T> shape, string field) where T : notnull
    {
        var source = placement.Source(input.Input.Id.Value, Profile,
            limits: new(maximumBatchSize: maximumRows, maximumBufferedRows: maximumRows,
                maximumFanOut: maximumRows, maximumConcurrency: 1));
        placement.Place(input, source, shape).FieldsBySemanticPath()
            .Identity(FieldPath.FromField(field), RelationQueryPlacementBuilder.FrameworkIdentitySourceSelector);
    }

    internal IRelationQueryEvaluator Build(RelationQueryCompilationRequest request)
    {
        if (rootIdentityField is not { } field || sources.Count != plan.InputContract.Sources.Length - 1)
            throw new InvalidOperationException("Configure the supplied root and each acquired source before creating the evaluator.");
        var placed = placement.Build().RequireValue().Placement;
        var readers = placed.Bindings.Where(binding => sources.ContainsKey(binding.Input))
            .Select(binding =>
            {
                var source = placed.SourceInstances.Single(candidate => candidate.Id == binding.Source);
                return new MemoryReader(new(source.Id, source.ExecutionDomain, source.TargetProfile,
                    RelationQueryLogicalPartitionIdentity.WholeSource), binding, sources[binding.Input]);
            }).ToArray();
        var policy = new RelationQueryPhysicalPlanningPolicy(
            new($"{Convention}/max-{maximumRows.ToString(CultureInfo.InvariantCulture)}"), Convention,
            maximumBatchSize: maximumRows, maximumBufferedRows: maximumRows, maximumLocalRows: maximumRows,
            maximumFanOut: maximumRows, maximumReferenceKeysPerObservation: maximumRows, maximumConcurrency: 1);
        return new BoundEvaluator(request, field, new RelationQueryEvaluator(_ => placed, policy, readers));
    }

    sealed class BoundEvaluator(RelationQueryCompilationRequest request, string rootIdentityField,
        RelationQueryEvaluator evaluator) : IRelationQueryEvaluator
    {
        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(evaluation);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(request, evaluation.Compilation))
                throw new ArgumentException("The in-memory evaluator belongs to a different prepared request.", nameof(evaluation));
            if (evaluation.SuppliedRoots is { } roots)
            {
                foreach (var root in roots.Observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!root.Fields.TryGetValue(rootIdentityField, out var key)
                        || key.Kind != ObservationValueKind.String
                        || !string.Equals(key.String, root.Identity, StringComparison.Ordinal))
                        throw new ArgumentException("Supplied root identities must match the declared identity field.", nameof(evaluation));
                }
            }
            return evaluator.EvaluateAsync(evaluation, cancellationToken);
        }
    }

    sealed class MemoryReader(RelationQuerySourceReaderDescriptor descriptor, RelationQuerySourcePlacementBinding binding,
        ImmutableArray<(string Id, ObservationValue Value)> rows) : IRelationQuerySourceReader
    {
        public RelationQuerySourceReaderDescriptor Descriptor => descriptor;

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Source != descriptor.Source || request.Shape != binding.Shape || request.PlacementBinding != binding.Id
                || request.Constraint is not RelationQueryBoundedEnumeration enumeration)
                throw new InvalidOperationException("An in-memory source requires its exact placement and bounded enumeration.");
            if (rows.Length > enumeration.MaximumRows || rows.Length > request.MaximumBufferedRows)
                return ValueTask.FromResult(new RelationQuerySourceReadResult(RelationQuerySourceReadState.Inconclusive));

            var observations = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(rows.Length);
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(request.Fields.Length);
                foreach (var field in request.Fields)
                    fields.Add(RelationQuerySourceReadFieldResult.Project(field, row.Value));
                observations.Add(new(row.Id, binding.Shape, fields.MoveToImmutable()));
            }
            return ValueTask.FromResult(new RelationQuerySourceReadResult(RelationQuerySourceReadState.Complete,
                observations.MoveToImmutable()));
        }
    }
}
