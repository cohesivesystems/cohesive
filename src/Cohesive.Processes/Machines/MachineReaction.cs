using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using Cohesive.Transitions.Machines;

namespace Cohesive.Processes.Machines;

/// <summary>Portable reaction payload. Construct only through authoring lowering or persistence.</summary>
/// <param name="Source">Bound source entity shape, including its exact machine authority.</param>
/// <param name="Movement">Selected edge in that machine.</param>
/// <param name="Target">Exact target Transition declaration.</param>
/// <param name="Process">Canonical request projection and invocation; the sole action authority.</param>
public sealed record MachineReactionPayload(Shape Source, ExecutionNodeId Movement,
    ExecutionDefinitionDocument Target, ExecutionDefinitionDocument Process);

/// <summary>A committed-movement trigger and a pure request policy lowered to an ordinary Process.</summary>
/// <remarks>Definitions carry no transport, callback or runtime state. A successful Process result does
/// not by itself prove target admission: inspect the target receipt's decision kind.</remarks>
public sealed class MachineReaction
{
    /// <summary>Semantic kind for the bounded single-target reaction profile.</summary>
    public static ExecutionDefinitionKind Kind { get; } = new("machine.reaction/v1");
    /// <summary>Stable target invocation node within the generated Process.</summary>
    public static ExecutionNodeId InvocationNode { get; } = new("reaction/request");
    internal static ExecutionNodeId ReturnNode { get; } = new("reaction/result");

    internal MachineReaction(ExecutionDefinitionDocument document)
    {
        if (document.Kind != Kind || !document.Extensions.IsEmpty)
            throw new ArgumentException("Expected an unextended machine.reaction/v1 document.", nameof(document));
        Document = document;
        Payload = document.GetDefinition<MachineReactionPayload>();
        var payload = Payload;
        var binding = EntityMachineBinding.Read(payload.Source)
            ?? throw new InvalidOperationException("A reaction source must have an authoritative machine.");
        var machine = MachineDefinition.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(binding.Machine));
        if (!machine.Edges.Any(edge => edge.Id == payload.Movement.Value))
            throw new InvalidOperationException("Reaction movement is absent from the source machine.");
        var target = payload.Target.GetDefinition<Cohesive.Transitions.IR.TransitionDefinition>();
        if (target.MachineBinding is null)
            throw new InvalidOperationException("A reaction target must have an authoritative machine.");
        var targetCompilation = Cohesive.Transitions.Compilation.TransitionStaticCompiler.Compile(payload.Target);
        if (!targetCompilation.IsSuccessful)
            throw new InvalidOperationException(string.Join("; ", targetCompilation.Validation.Diagnostics.Select(d => d.Message)));
        var process = payload.Process.GetDefinition<ProcessDefinition>();
        if (process.Input != ValueContract.FromShape(payload.Source)
            || process.Nodes.Length != 2 || process.Entry != InvocationNode
            || process.Nodes.OfType<InvokeTransitionProcessNode>().SingleOrDefault() is not { } invoke
            || invoke.Id != InvocationNode || invoke.Transition != payload.Target.Reference
            || invoke.Continuation.Edge.Target != ReturnNode || invoke.Receipt is not null
            || process.Result != target.Outcome
            || process.Nodes.OfType<ReturnProcessNode>().SingleOrDefault() is not { } returned
            || returned.Id != ReturnNode || returned.Result is not BindingExpr resultBinding
            || invoke.Continuation.Output?.Binding != resultBinding.Binding)
            throw new InvalidOperationException("Reaction Process must invoke its exact target from the source snapshot.");
        var compilation = Compile();
        if (!compilation.IsSuccessful)
            throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics.Select(d => d.Message)));
    }

    /// <summary>Canonical, fingerprinted policy authority.</summary>
    public ExecutionDefinitionDocument Document { get; }
    /// <summary>Derived portable payload.</summary>
    public MachineReactionPayload Payload { get; }

    /// <summary>Compiles the request Process using the exact target declaration retained by the policy.</summary>
    /// <param name="interactions">Host interaction catalog; defaults to an empty catalog for this bounded profile.</param>
    /// <returns>Existing Process compilation result and structured diagnostics.</returns>
    /// <exception cref="InvalidOperationException">The retained target declaration cannot be linked.</exception>
    public ProcessCompilationResult Compile(InteractionContractCatalog? interactions = null)
    {
        var payload = Payload;
        var validation = ProcessDefinitionLink.TryCreateTransition(payload.Target, out var link);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
        if (interactions is null) InteractionContractCatalog.TryCreate([], out interactions);
        return ProcessStaticCompiler.Compile(payload.Process, new(definitions: [link!], interactionContracts: interactions));
    }

    /// <summary>Loads and validates an exact persisted reaction revision.</summary>
    /// <param name="json">Canonical execution document JSON.</param>
    /// <returns>A verified reaction, including linked Process validation.</returns>
    /// <exception cref="ArgumentException">The document kind is unsupported.</exception>
    /// <exception cref="InvalidOperationException">The policy or its linked definitions are invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">The persisted document cannot be decoded.</exception>
    public static MachineReaction Deserialize(string json) => new(ExecutionDefinitionJsonSerializer.Deserialize(json));

    /// <summary>Begins a typed reaction declaration for one source movement.</summary>
    /// <typeparam name="TSource">Source entity snapshot type.</typeparam>
    /// <param name="source">Authoritative bound source entity.</param>
    /// <param name="movement">Owner-scoped committed movement to observe.</param>
    /// <param name="id">Stable policy identity.</param>
    /// <param name="revision">Explicit policy revision.</param>
    /// <returns>A transient authoring source completed by Request.</returns>
    /// <exception cref="ArgumentException">The movement belongs to another exact machine.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static MachineReactionSource<TSource> On<TSource>(EntityDefinition source, MachineMovement movement,
        string id, string revision) where TSource : notnull => new(source, movement, id, revision);
}

/// <summary>Transient typed policy source; no expressions or callbacks survive lowering.</summary>
/// <typeparam name="TSource">Source entity snapshot type.</typeparam>
public sealed class MachineReactionSource<TSource> where TSource : notnull
{
    readonly EntityDefinition source;
    readonly MachineMovement movement;
    readonly string id;
    readonly string revision;
    internal MachineReactionSource(EntityDefinition source, MachineMovement movement, string id, string revision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        if (source.Machine?.Machine.Reference != movement.Resolve().Machine.Document.Reference)
            throw new ArgumentException("Movement must belong to the source entity's exact machine.", nameof(movement));
        this.source = source; this.movement = movement; this.id = id; this.revision = revision;
    }

    /// <summary>Projects an entity ID and command from committed source facts into a target invocation.</summary>
    /// <typeparam name="TTarget">Target entity type.</typeparam>
    /// <typeparam name="TInput">Target command type.</typeparam>
    /// <typeparam name="TResult">Target outcome type.</typeparam>
    /// <param name="target">Exact bound target Transition; its guards remain authoritative.</param>
    /// <param name="subject">Pure target entity-ID projection.</param>
    /// <param name="input">Pure command projection.</param>
    /// <param name="sourceFile">Authoring source file.</param>
    /// <param name="sourceLine">Authoring source line.</param>
    /// <returns>A validated portable reaction and its derived Process.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">Contracts, binding or generated Process are invalid.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">A projection is outside the portable expression language.</exception>
    public MachineReaction Request<TTarget, TInput, TResult>(Transition<TTarget, TInput, TResult> target,
        Expression<Func<TSource, string>> subject, Expression<Func<TSource, TInput>> input,
        [CallerFilePath] string sourceFile = "", [CallerLineNumber] int sourceLine = 0)
        where TTarget : notnull where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(input);
        var author = new RelationQueryExpressionAuthoring();
        _ = author.Clr.Shape<TSource>();
        if (target.Definition.Input.Type is ObjectTypeRef) _ = author.Clr.Shape<TInput>();
        var lowerer = new RelationQueryExpressionLowerer(author.Clr.ResolveMemberPath);
        var subjectValue = lowerer.LowerBoundValue(subject, [ProcessBindingIds.Input], id + "/subject").RequireValue().Value;
        var inputValue = lowerer.LowerBoundValue(input, [ProcessBindingIds.Input], id + "/input").RequireValue().Value;
        // The target contract owns the top-level constructed command type; Process validation checks
        // every field against that declaration instead of retaining the lowerer's open object type.
        if (inputValue is CallExpr { Function: ExprFunctionNames.Object } construction)
            inputValue = construction with { ReturnType = target.Definition.Input.Type
                ?? throw new InvalidOperationException("Reaction command construction requires a structural target contract.") };
        var provenance = new ExecutionProvenance(new("cohesive.machine-reaction", "1"),
            new(id), DocumentOrigin.User);
        var process = ProcessAuthoring.Create<TSource, TResult>(
            new(new(id + "/request"), new(revision), MachineReaction.InvocationNode, ProcessRecoveryPolicy.ContinueAttempt, provenance),
            ValueContract.FromShape(source.Shape), target.Definition.Outcome,
            p =>
            {
                var result = p.Output<TResult>(new ValueBindingId("reaction.outcome"), target.Definition.Outcome);
                p.InvokeTransition(MachineReaction.InvocationNode, target.Reference,
                    p.CanonicalValue<string>(subjectValue, new(new Cohesive.Model.Authoring.DefaultClrTypeRefMapper().Map(typeof(string), null))),
                    p.CanonicalValue<TInput>(inputValue, target.Definition.Input),
                    p.Continuation(p.Edge(new ProcessEdgeId("reaction/returned"), MachineReaction.ReturnNode), result));
                p.Return(MachineReaction.ReturnNode, result.Value);
            }, sourceFile, sourceLine);
        return new(ExecutionDefinitionDocument.Create(MachineReaction.Kind, new(id), new(revision),
            new MachineReactionPayload(source.Shape, new(movement.Id), target.Document, process.Document), provenance));
    }
}
