using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Authoring;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Processes.Authoring;

internal sealed record AuthoredProcessSource(string Reference, string Description);

internal sealed class ProcessAuthoringContext
{
    readonly ProcessAuthoringMetadata metadata;
    readonly IClrTypeRefMapper typeRefMapper;
    readonly Dictionary<Type, ValueContract> contracts = [];
    readonly Dictionary<object, AuthoredProcessSource> sources = new(ReferenceEqualityComparer.Instance);

    public ProcessAuthoringContext(
        ProcessAuthoringMetadata metadata,
        IClrTypeRefMapper typeRefMapper,
        Type inputType,
        Type resultType,
        ValueContract? inputContract = null,
        ValueContract? resultContract = null)
    {
        this.metadata = Guard.RequireNotNull(metadata);
        this.typeRefMapper = Guard.RequireNotNull(typeRefMapper);
        InputContract = inputContract ?? Contract(Guard.RequireNotNull(inputType));
        ResultContract = resultContract ?? Contract(Guard.RequireNotNull(resultType));
    }

    public ProcessAuthoringMetadata Metadata => metadata;

    public ValueContract InputContract { get; }

    public ValueContract ResultContract { get; }

    public ValueContract Contract<TValue>() => Contract(typeof(TValue));

    public ProcessValue<TValue> Value<TValue>(
        Expr expression,
        ValueContract contract,
        AuthoredProcessSource source)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(contract);
        var value = new ProcessValue<TValue>(this, expression, contract);
        RegisterIfAbsent(expression, source);
        return value;
    }

    public ProcessValue<TValue> Constant<TValue>(TValue value, AuthoredProcessSource source) =>
        Value<TValue>(Expr.Const(ObservationValue.FromObject(value)), Contract<TValue>(), source);

    public PortableValue Pattern<TValue>(TValue value, ValueContract contract, AuthoredProcessSource source)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var observed = ObservationValue.FromObject(value);
        var pattern = observed.Kind == ObservationValueKind.Null
            ? PortableValue.Null(contract)
            : PortableValue.Concrete(contract, observed);
        RegisterIfAbsent(pattern, source);
        return pattern;
    }

    public ProcessValue<TField> Field<TSource, TField>(
        ProcessValue<TSource> sourceValue,
        FieldPath path,
        AuthoredProcessSource source)
    {
        RequireValue(sourceValue);
        Expr expression = sourceValue.Expression switch
        {
            BindingExpr binding => Expr.Field(binding.Binding, path),
            FieldExpr { Binding: not null } field => new FieldExpr(
                new([.. field.Path.Segments, .. path.Segments]),
                field.Binding),
            _ => throw new InvalidOperationException(
                "A typed Process field selector requires a value rooted in one canonical value binding.")
        };
        return Value<TField>(expression, ResolvePathContract(sourceValue.Contract, path), source);
    }

    public void RequireValue<TValue>(ProcessValue<TValue> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Context, this))
            throw new InvalidOperationException("A Process value belongs to another authoring session.");
    }

    public void RequireBinding<TValue>(ProcessBinding<TValue> binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!ReferenceEquals(binding.Context, this))
            throw new InvalidOperationException($"Process binding '{binding.Binding.Value}' belongs to another authoring session.");
    }

    public void RequireObligation(ProcessRequestObligation obligation)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        if (!ReferenceEquals(obligation.Context, this))
        {
            throw new InvalidOperationException(
                $"Request obligation '{obligation.Binding.Value}' belongs to another authoring session.");
        }
    }

    public AuthoredProcessSource Source(
        string sourceFile,
        int sourceLine,
        string sourceMember,
        string description)
    {
        var root = metadata.Provenance.Source.Reference;
        var member = string.IsNullOrWhiteSpace(sourceMember) ? "unknown" : sourceMember;
        var reference = sourceLine > 0
            ? $"{root}#{member}:L{sourceLine}"
            : $"{root}#{member}";
        var file = string.IsNullOrWhiteSpace(sourceFile) ? null : Path.GetFileName(sourceFile);
        var detail = file is null || sourceLine <= 0
            ? description
            : $"{description} ({file}:{sourceLine})";
        return new(reference, detail);
    }

    public void Register(object construct, AuthoredProcessSource source)
    {
        ArgumentNullException.ThrowIfNull(construct);
        ArgumentNullException.ThrowIfNull(source);
        if (!sources.TryAdd(construct, source))
            throw new InvalidOperationException("A canonical Process construct was registered twice by one authoring session.");
    }

    public void RegisterIfAbsent(object construct, AuthoredProcessSource source)
    {
        ArgumentNullException.ThrowIfNull(construct);
        ArgumentNullException.ThrowIfNull(source);
        sources.TryAdd(construct, source);
    }

    public ExecutionSourceMap BuildSourceMap(
        CanonicalProcessDefinition definition,
        AuthoredProcessSource rootSource)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rootSource);
        List<ExecutionSourceProvenance> entries = [];
        Add(entries, rootSource, ["input"]);
        Add(entries, rootSource, ["result"]);
        Add(entries, rootSource, ["entry"]);
        Add(entries, rootSource, ["recoveryPolicy"]);
        for (var index = 0; index < definition.Nodes.Length; index++)
            AddNode(entries, definition.Nodes[index], ["nodes", Index(index)], rootSource);
        return new([.. entries]);
    }

    ValueContract Contract(Type type)
    {
        if (contracts.TryGetValue(type, out var contract))
            return contract;

        var nullable = Nullable.GetUnderlyingType(type) is not null;
        contract = new(
            typeRefMapper.Map(type, nullability: null),
            nullability: nullable ? FieldNullability.Nullable : FieldNullability.NonNullable);
        contracts.Add(type, contract);
        return contract;
    }

    static ValueContract ResolvePathContract(ValueContract root, FieldPath path)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A Process field selector requires at least one semantic path segment.", nameof(path));

        var current = root;
        foreach (var segment in path.Segments)
        {
            var effectiveType = current.GetEffectiveType();
            ValueContract child = segment.Kind switch
            {
                SegmentKind.Field when effectiveType is ObjectTypeRef objectType =>
                    ContractForField(objectType, segment, path),
                SegmentKind.Element when effectiveType is ArrayTypeRef array => new(array.ElementType),
                _ => throw new InvalidOperationException(
                    $"Process field path '{path}' cannot be resolved through contract '{effectiveType}'.")
            };
            current = ComposePathContract(current, child);
        }

        return current;

        static ValueContract ContractForField(
            ObjectTypeRef objectType,
            FieldPathSegment segment,
            FieldPath completePath)
        {
            var field = objectType.Fields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, segment.Segment, StringComparison.Ordinal));
            if (field is null)
            {
                throw new InvalidOperationException(
                    $"Process field path '{completePath}' is absent from its typed source contract.");
            }

            return new(
                field.Type,
                cardinality: field.Cardinality,
                presence: field.Presence,
                nullability: field.Nullability);
        }
    }

    static ValueContract ComposePathContract(ValueContract parent, ValueContract child) => new(
        child.Type,
        child.Shape,
        child.Cardinality,
        parent.Presence == FieldPresence.Optional || child.Presence == FieldPresence.Optional
            ? FieldPresence.Optional
            : FieldPresence.Required,
        parent.Nullability == FieldNullability.Nullable || child.Nullability == FieldNullability.Nullable
            ? FieldNullability.Nullable
            : FieldNullability.NonNullable);

    void AddNode(
        List<ExecutionSourceProvenance> entries,
        ProcessNode node,
        ImmutableArray<string> path,
        AuthoredProcessSource rootSource)
    {
        var nodeSource = SourceFor(node, rootSource);
        Add(entries, nodeSource, path);
        switch (node)
        {
            case InvokeTransitionProcessNode invocation:
                AddConstruct(entries, invocation.Subject, path.Add("subject"), nodeSource);
                AddConstruct(entries, invocation.Input, path.Add("input"), nodeSource);
                AddContinuation(entries, invocation.Continuation, path.Add("continuation"), nodeSource);
                break;
            case EvaluateRelationProcessNode relation:
                AddConstruct(entries, relation.Input, path.Add("input"), nodeSource);
                AddContinuation(entries, relation.Continuation, path.Add("continuation"), nodeSource);
                break;
            case RequestProcessNode request:
                AddConstruct(entries, request.Payload, path.Add("payload"), nodeSource);
                AddOutcomes(entries, request.Outcomes, path.Add("outcomes"), nodeSource);
                break;
            case EmitEventProcessNode emission:
                AddConstruct(entries, emission.Payload, path.Add("payload"), nodeSource);
                AddEdge(entries, emission.Next, path.Add("next"), nodeSource);
                break;
            case SendSignalProcessNode signal:
                AddConstruct(entries, signal.Target, path.Add("target"), nodeSource);
                AddConstruct(entries, signal.Payload, path.Add("payload"), nodeSource);
                AddEdge(entries, signal.Next, path.Add("next"), nodeSource);
                break;
            case ChoiceProcessNode choice:
                for (var index = 0; index < choice.Cases.Length; index++)
                {
                    var choiceCase = choice.Cases[index];
                    var casePath = path.Add("cases").Add(Index(index));
                    var caseSource = AddConstruct(entries, choiceCase, casePath, nodeSource);
                    AddConstruct(entries, choiceCase.Predicate, casePath.Add("predicate"), caseSource);
                    AddEdge(entries, choiceCase.Next, casePath.Add("next"), caseSource);
                }
                if (choice.Fallback is not null)
                    AddFallback(entries, choice.Fallback, path.Add("fallback"), nodeSource);
                break;
            case MatchProcessNode match:
                AddConstruct(entries, match.Value, path.Add("value"), nodeSource);
                for (var index = 0; index < match.Cases.Length; index++)
                {
                    var matchCase = match.Cases[index];
                    var casePath = path.Add("cases").Add(Index(index));
                    var caseSource = AddConstruct(entries, matchCase, casePath, nodeSource);
                    AddConstruct(entries, matchCase.Pattern, casePath.Add("pattern"), caseSource);
                    AddEdge(entries, matchCase.Next, casePath.Add("next"), caseSource);
                }
                if (match.Fallback is not null)
                    AddFallback(entries, match.Fallback, path.Add("fallback"), nodeSource);
                break;
            case ForkProcessNode fork:
                for (var index = 0; index < fork.Branches.Length; index++)
                {
                    var branch = fork.Branches[index];
                    var branchPath = path.Add("branches").Add(Index(index));
                    var branchSource = AddConstruct(entries, branch, branchPath, nodeSource);
                    AddEdge(entries, branch.Start, branchPath.Add("start"), branchSource);
                }
                break;
            case JoinProcessNode join:
                AddConstruct(entries, join.Policy, path.Add("policy"), nodeSource);
                AddEdge(entries, join.Next, path.Add("next"), nodeSource);
                break;
            case AwaitMatchProcessNode awaitMatch:
                for (var index = 0; index < awaitMatch.Clauses.Length; index++)
                    AddAwaitClause(entries, awaitMatch.Clauses[index], path.Add("clauses").Add(Index(index)), nodeSource);
                break;
            case TimerProcessNode timer:
                AddConstruct(entries, timer.DueAt, path.Add("dueAt"), nodeSource);
                AddEdge(entries, timer.Next, path.Add("next"), nodeSource);
                break;
            case ReplyProcessNode reply:
                AddConstruct(entries, reply.Payload, path.Add("payload"), nodeSource);
                AddEdge(entries, reply.Next, path.Add("next"), nodeSource);
                break;
            case DurableCutProcessNode cut:
                AddEdge(entries, cut.Resume, path.Add("resume"), nodeSource);
                break;
            case InvokeProcessProcessNode child:
                AddConstruct(entries, child.OutcomeMapping, path.Add("outcomeMapping"), nodeSource);
                AddConstruct(entries, child.Input, path.Add("input"), nodeSource);
                AddOutcomes(entries, child.Outcomes, path.Add("outcomes"), nodeSource);
                break;
            case ForEachPartitionProcessNode partition:
                AddConstruct(entries, partition.Partitions, path.Add("partitions"), nodeSource);
                AddOutput(entries, partition.Partition, path.Add("partition"), nodeSource);
                AddConstruct(entries, partition.ProgressIdentity, path.Add("progressIdentity"), nodeSource);
                AddConstruct(entries, partition.OutcomeMapping, path.Add("outcomeMapping"), nodeSource);
                AddConstruct(entries, partition.ChildInput, path.Add("childInput"), nodeSource);
                AddConstruct(entries, partition.Limits, path.Add("limits"), nodeSource);
                if (partition.CapacityIdentity is not null)
                {
                    AddConstruct(entries, partition.CapacityIdentity, path.Add("capacityIdentity"), nodeSource);
                }
                for (var index = 0; index < partition.CapacityDomains.Length; index++)
                {
                    AddConstruct(
                        entries,
                        partition.CapacityDomains[index],
                        path.Add("capacityDomains").Add(Index(index)),
                        nodeSource);
                }
                AddEdge(entries, partition.Completed, path.Add("completed"), nodeSource);
                AddEdge(entries, partition.Failed, path.Add("failed"), nodeSource);
                break;
            case RepeatAcrossActivationProcessNode recurrence:
                AddConstruct(entries, recurrence.ContinueWhen, path.Add("continueWhen"), nodeSource);
                AddConstruct(entries, recurrence.Progress, path.Add("progress"), nodeSource);
                AddConstruct(entries, recurrence.Policy, path.Add("policy"), nodeSource);
                AddEdge(entries, recurrence.Repeat, path.Add("repeat"), nodeSource);
                AddEdge(entries, recurrence.Completed, path.Add("completed"), nodeSource);
                AddEdge(entries, recurrence.Exhausted, path.Add("exhausted"), nodeSource);
                AddEdge(entries, recurrence.Stalled, path.Add("stalled"), nodeSource);
                break;
            case ReturnProcessNode returned:
                AddConstruct(entries, returned.Result, path.Add("result"), nodeSource);
                break;
            case FailProcessNode failed:
                AddConstruct(entries, failed.Result, path.Add("result"), nodeSource);
                break;
            default:
                throw new NotSupportedException(
                    $"Canonical Process source mapping does not support node '{node.GetType().FullName}'.");
        }
    }

    void AddOutcomes(
        List<ExecutionSourceProvenance> entries,
        ImmutableArray<ProcessRequestOutcomeBranch> outcomes,
        ImmutableArray<string> path,
        AuthoredProcessSource fallback)
    {
        for (var index = 0; index < outcomes.Length; index++)
        {
            var outcome = outcomes[index];
            var outcomePath = path.Add(Index(index));
            var source = AddConstruct(entries, outcome, outcomePath, fallback);
            AddContinuation(entries, outcome.Continuation, outcomePath.Add("continuation"), source);
        }
    }

    void AddAwaitClause(
        List<ExecutionSourceProvenance> entries,
        ProcessAwaitClause clause,
        ImmutableArray<string> path,
        AuthoredProcessSource fallback)
    {
        var source = AddConstruct(entries, clause, path, fallback);
        switch (clause)
        {
            case ProcessAwaitInteractionClause interaction:
                AddOutput(entries, interaction.Input, path.Add("input"), source);
                if (interaction.RequestObligation is not null)
                    AddConstruct(entries, interaction.RequestObligation, path.Add("requestObligation"), source);
                if (interaction.Guard is not null)
                    AddConstruct(entries, interaction.Guard, path.Add("guard"), source);
                break;
            case ProcessAwaitTimerClause timer:
                AddConstruct(entries, timer.DueAt, path.Add("dueAt"), source);
                break;
            default:
                throw new NotSupportedException(
                    $"Canonical Process source mapping does not support AwaitMatch clause '{clause.GetType().FullName}'.");
        }
        AddContinuation(entries, clause.Continuation, path.Add("continuation"), source);
    }

    void AddFallback(
        List<ExecutionSourceProvenance> entries,
        ProcessFallback fallback,
        ImmutableArray<string> path,
        AuthoredProcessSource parentSource)
    {
        var source = AddConstruct(entries, fallback, path, parentSource);
        AddEdge(entries, fallback.Next, path.Add("next"), source);
    }

    void AddContinuation(
        List<ExecutionSourceProvenance> entries,
        ProcessContinuation continuation,
        ImmutableArray<string> path,
        AuthoredProcessSource fallback)
    {
        var source = AddConstruct(entries, continuation, path, fallback);
        AddEdge(entries, continuation.Edge, path.Add("edge"), source);
        if (continuation.Output is not null)
            AddOutput(entries, continuation.Output, path.Add("output"), source);
    }

    void AddEdge(
        List<ExecutionSourceProvenance> entries,
        ProcessEdge edge,
        ImmutableArray<string> path,
        AuthoredProcessSource fallback) =>
        AddConstruct(entries, edge, path, fallback);

    void AddOutput(
        List<ExecutionSourceProvenance> entries,
        ProcessOutputBinding output,
        ImmutableArray<string> path,
        AuthoredProcessSource fallback) =>
        AddConstruct(entries, output, path, fallback);

    AuthoredProcessSource AddConstruct(
        List<ExecutionSourceProvenance> entries,
        object construct,
        ImmutableArray<string> path,
        AuthoredProcessSource fallback)
    {
        var source = SourceFor(construct, fallback);
        Add(entries, source, path);
        return source;
    }

    AuthoredProcessSource SourceFor(object construct, AuthoredProcessSource fallback) =>
        sources.TryGetValue(construct, out var source) ? source : fallback;

    static void Add(
        List<ExecutionSourceProvenance> entries,
        AuthoredProcessSource source,
        ImmutableArray<string> path) =>
        entries.Add(new(source.Reference, new(path), source.Description));

    static string Index(int index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
