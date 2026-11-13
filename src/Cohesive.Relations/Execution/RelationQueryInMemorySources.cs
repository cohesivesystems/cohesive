using Cohesive.Authoring;
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
    readonly ImmutableDictionary<Type, QualifiedShapeId> sourceTypes;
    readonly int maximumRows;
    readonly RelationQueryPlacementBuilder placement;
    readonly Dictionary<RelationQueryInputId, ImmutableArray<(string Id, ObservationValue Value)>> sources = [];
    string? rootIdentityField;

    internal RelationQueryInMemorySources(CompiledRelationQueryPlan plan,
        ImmutableDictionary<Type, QualifiedShapeId> sourceTypes, int maximumRows)
    {
        this.plan = plan;
        this.sourceTypes = sourceTypes;
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
    public RelationQueryInMemorySources Root<T>(ClrAuthoringShape<T> shape, Expression<Func<T, string>> identity)
        where T : notnull
    {
        if (rootIdentityField is not null)
            throw new InvalidOperationException("The supplied root is already configured.");
        ArgumentNullException.ThrowIfNull(shape);
        var input = FindInput(shape.Id, root: true, nameof(shape));
        var field = IdentityField(shape, identity);
        ConfigurePlacement(placement.Place(input, DeclareSource(input), shape), field);
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
    public RelationQueryInMemorySources Bind<T>(ClrAuthoringShape<T> shape, IEnumerable<T> rows,
        Expression<Func<T, string>> identity) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(rows);
        var input = FindInput(shape.Id, root: false, nameof(shape));
        return Bind(input, rows, IdentityField(shape, identity), shape);
    }

    /// <summary>Snapshots a complete collection for the acquired input authored with its exact CLR row type.</summary>
    /// <typeparam name="T">CLR source row type registered during authoring.</typeparam>
    /// <param name="rows">Finite rows with unique, nonempty identities, enumerated once at configuration time.</param>
    /// <returns>This configuration builder.</returns>
    /// <exception cref="ArgumentNullException">The collection or a row is null.</exception>
    /// <exception cref="ArgumentException">The row type does not select exactly one acquired input,
    /// the rows violate the input contract, or the collection exceeds its row limit.</exception>
    /// <exception cref="InvalidOperationException">The input is already bound or has no unambiguous default string identity.</exception>
    /// <exception cref="NotSupportedException">A row cannot be represented as observation data.</exception>
    /// <remarks>Uses the shape's sole string identity field, or its string field named Id if no identity is declared.
    /// Use the explicit overload to choose another field.</remarks>
    public RelationQueryInMemorySources Bind<T>(IEnumerable<T> rows) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (!sourceTypes.TryGetValue(typeof(T), out var shape))
            throw new ArgumentException($"Row type '{typeof(T)}' is not a source type of this prepared relation.", nameof(rows));
        var input = FindInput(shape, root: false, nameof(rows));
        return Bind(input, rows, DefaultIdentityField(input));
    }

    RelationQueryInMemorySources Bind<T>(RelationQuerySourceInputContract input, IEnumerable<T> rows, string field,
        ClrAuthoringShape<T>? shape = null) where T : notnull
    {
        if (sources.ContainsKey(input.Input.Id))
            throw new InvalidOperationException($"Source '{input.Node}' is already configured.");
        var graphShape = GetGraphShape(input);
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
        var source = DeclareSource(input);
        ConfigurePlacement(shape is null ? placement.Place(input, source) : placement.Place(input, source, shape), field);
        sources.Add(input.Input.Id, snapshot.ToImmutable());
        return this;
    }

    RelationQuerySourceInputContract FindInput(QualifiedShapeId shape, bool root, string parameterName)
    {
        var matches = plan.InputContract.Sources.Where(input => input.Shape == shape
            && (input.Role == RelationQuerySourceInputRole.RelationRoot) == root).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException($"Shape '{shape}' must select exactly one {(root ? "root" : "acquired")} input.", parameterName);
        return matches[0];
    }

    GraphShapeId GetGraphShape(RelationQuerySourceInputContract input)
    {
        var graph = plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == input.Shape.GraphId).Graph;
        return new(graph, input.Shape.ShapeId);
    }

    string DefaultIdentityField(RelationQuerySourceInputContract input)
    {
        var graphShape = GetGraphShape(input);
        var shape = graphShape.Graph.GetShape(input.Shape);
        var declared = shape.Fields.Where(static field => field.Role == FieldRole.Identity).ToArray();
        var identity = declared.Length switch
        {
            0 when shape.TryGetField("Id", out var conventional) => conventional,
            1 => declared[0],
            _ => null
        };
        if (identity is { Cardinality: FieldCardinality.Single, Type: ScalarTypeRef { Kind: ScalarTypeKind.String } })
            return identity.Name.Value;
        var binding = input.Role == RelationQuerySourceInputRole.RelationRoot ? nameof(Root) : nameof(Bind);
        throw new InvalidOperationException($"Source '{input.Node}' has no unambiguous default string identity. Specify its identity using {binding}.");
    }

    static string IdentityField<T>(ClrAuthoringShape<T> shape, Expression<Func<T, string>> identity) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!FieldPath.Capture(identity, shape.ResolveMemberPath).TryGetDirectFieldName(out var field))
            throw new ArgumentException("An in-memory identity selector must name a direct property.", nameof(identity));
        return field;
    }

    RelationQueryPlacementSourceHandle DeclareSource(RelationQuerySourceInputContract input) =>
        placement.Source(input.Input.Id.Value, Profile,
            limits: new(maximumBatchSize: maximumRows, maximumBufferedRows: maximumRows,
                maximumFanOut: maximumRows, maximumConcurrency: 1));

    static void ConfigurePlacement(RelationQueryPlacementInputBuilder binding, string field) =>
        binding.FieldsBySemanticPath()
            .Identity(FieldPath.FromField(field), RelationQueryPlacementBuilder.FrameworkIdentitySourceSelector);

    internal IRelationQueryEvaluator Build(RelationQueryCompilationRequest request)
    {
        if (sources.Count != plan.InputContract.Sources.Length - 1)
            throw new InvalidOperationException("Bind each acquired source before creating the evaluator.");
        var field = rootIdentityField;
        if (field is null)
        {
            var root = plan.InputContract.Sources.Single(input => input.Role == RelationQuerySourceInputRole.RelationRoot);
            field = DefaultIdentityField(root);
            ConfigurePlacement(placement.Place(root, DeclareSource(root)), field);
        }
        var placed = placement.Build().RequireValue().Placement;
        var readers = placed.Bindings.Where(binding => sources.ContainsKey(binding.Input))
            .Select(binding =>
            {
                var source = placed.SourceInstances.Single(candidate => candidate.Id == binding.Source);
                var contract = plan.InputContract.Sources.Single(input => input.Input.Id == binding.Input);
                var fields = RelationQuerySourceReadFields.CreateSemantic(contract.Fields, binding);
                return new MemoryReader(new(source.Id, source.ExecutionDomain, source.TargetProfile,
                    RelationQueryLogicalPartitionIdentity.WholeSource), binding, fields, sources[binding.Input]);
            }).ToArray();
        var policy = new RelationQueryPhysicalPlanningPolicy(
            new($"{Convention}/max-{maximumRows.ToString(CultureInfo.InvariantCulture)}"), Convention,
            maximumBatchSize: maximumRows, maximumBufferedRows: maximumRows, maximumLocalRows: maximumRows,
            maximumFanOut: maximumRows, maximumReferenceKeysPerObservation: maximumRows, maximumConcurrency: 1);
        return new BoundEvaluator(request, field, new RelationQueryEvaluator(_ => placed, policy, readers));
    }

    internal sealed class BoundEvaluator(RelationQueryCompilationRequest request, string rootIdentityField,
        RelationQueryEvaluator evaluator) : IRelationQueryEvaluator
    {
        internal string RequireRootIdentityField(RelationQueryCompilationRequest compilation, string parameterName)
        {
            if (!ReferenceEquals(request, compilation))
                throw new ArgumentException("The in-memory evaluator belongs to a different prepared request.", parameterName);
            return rootIdentityField;
        }

        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(evaluation);
            cancellationToken.ThrowIfCancellationRequested();
            var identityField = RequireRootIdentityField(evaluation.Compilation, nameof(evaluation));
            if (evaluation.SuppliedRoots is { } roots)
            {
                foreach (var root in roots.Observations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!root.Fields.TryGetValue(identityField, out var key)
                        || key.Kind != ObservationValueKind.String
                        || !string.Equals(key.String, root.Identity, StringComparison.Ordinal))
                        throw new ArgumentException("Supplied root identities must match the declared identity field.", nameof(evaluation));
                }
            }
            return evaluator.EvaluateAsync(evaluation, cancellationToken);
        }
    }

    internal sealed class MemoryReader(RelationQuerySourceReaderDescriptor descriptor, RelationQuerySourcePlacementBinding binding,
        ImmutableArray<RelationQuerySourceReadField> fields, ImmutableArray<(string Id, ObservationValue Value)> rows)
        : IRelationQuerySourceReader
    {
        readonly RelationQuerySourceReadResult result = Project(binding, fields, rows);

        public RelationQuerySourceReaderDescriptor Descriptor => descriptor;

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Source != descriptor.Source || request.Shape != binding.Shape || request.PlacementBinding != binding.Id
                || request.Constraint is not RelationQueryBoundedEnumeration enumeration
                || !request.Fields.SequenceEqual(fields))
                throw new InvalidOperationException("An in-memory source requires its exact placement, field selection and bounded enumeration.");
            if (result.Observations.Length > enumeration.MaximumRows || result.Observations.Length > request.MaximumBufferedRows)
                return ValueTask.FromResult(new RelationQuerySourceReadResult(RelationQuerySourceReadState.Inconclusive));

            return ValueTask.FromResult(result);
        }

        static RelationQuerySourceReadResult Project(RelationQuerySourcePlacementBinding binding,
            ImmutableArray<RelationQuerySourceReadField> fields, ImmutableArray<(string Id, ObservationValue Value)> rows)
        {
            var observations = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(rows.Length);
            foreach (var row in rows)
            {
                var projected = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(fields.Length);
                foreach (var field in fields)
                    projected.Add(RelationQuerySourceReadFieldResult.Project(field, row.Value));
                observations.Add(new(row.Id, binding.Shape, projected.MoveToImmutable()));
            }
            return new(RelationQuerySourceReadState.Complete, observations.MoveToImmutable());
        }
    }
}
