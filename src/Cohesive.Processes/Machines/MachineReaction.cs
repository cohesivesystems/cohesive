using Cohesive.Authoring;
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

/// <summary>Decoded reaction semantics. Author through the fluent builder; persist the canonical reaction Document.</summary>
/// <param name="Source">Bound source entity shape, including its exact machine authority.</param>
/// <param name="Movement">Selected edge in that machine.</param>
/// <param name="Target">Exact target Transition declaration.</param>
/// <param name="Process">Canonical request projection and invocation; the sole action authority.</param>
/// <param name="Identity">Closed identity declaration; an episode always carries its required field selector.</param>
/// <param name="Prerequisite">Exact reaction whose successful target receipt releases the Process wait, or null.</param>
public sealed record MachineReactionPayload(Shape Source, ExecutionNodeId Movement,
    ExecutionDefinitionDocument Target, ExecutionDefinitionDocument Process,
    MachineReactionIdentity Identity, ExecutionDefinitionDocument? Prerequisite = null);

/// <summary>A committed-movement trigger and a pure request policy lowered to an ordinary Process.</summary>
/// <remarks>Definitions carry no transport, callback or runtime state. A successful Process result does
/// not by itself prove target admission: inspect the target receipt's decision kind.</remarks>
public sealed class MachineReaction
{
    /// <summary>Semantic kind for the bounded single-target reaction profile.</summary>
    public static ExecutionDefinitionKind Kind { get; } = new("machine.reaction/v1");
    /// <summary>Stable target invocation node within the generated Process.</summary>
    public static ExecutionNodeId InvocationNode { get; } = new("reaction/request");
    /// <summary>Durable prerequisite wait node in coordinated reaction Processes.</summary>
    public static ExecutionNodeId PrerequisiteNode { get; } = new("reaction/prerequisite");
    internal static TimeSpan PrerequisiteRetentionHorizon { get; } = TimeSpan.FromDays(30);
    internal static ExecutionNodeId PrerequisiteClause { get; } = new("reaction/prerequisite/succeeded");
    internal static ExecutionNodeId ReturnNode { get; } = new("reaction/result");

    internal MachineReaction(ExecutionDefinitionDocument document)
    {
        if (document.Kind != Kind || !document.Extensions.IsEmpty)
            throw new ArgumentException("Expected an unextended machine.reaction/v1 document.", nameof(document));
        Document = document;
        Payload = document.GetDefinition<MachineReactionPayloadWire>().ToModel();
        var payload = Payload;
        ArgumentNullException.ThrowIfNull(payload.Identity);
        if (payload.Identity is MachineReactionIdentity.Episode episode) ValidateEpisodeField(payload.Source, episode.Key);
        if (payload.Prerequisite is { } prerequisiteDocument)
        {
            var prerequisite = new MachineReaction(prerequisiteDocument);
            if (payload.Identity is MachineReactionIdentity.Occurrence
                || prerequisite.Payload.Identity != payload.Identity
                || prerequisite.Payload.Source != payload.Source
                || prerequisite.Document.Metadata.DefinitionId == document.Metadata.DefinitionId)
                throw new InvalidOperationException("Prerequisites require distinct policies with the same source shape and entity/episode identity selector.");
            PrerequisiteSignal = CreatePrerequisiteSignal(prerequisiteDocument);
        }
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
            || process.Nodes.Length != (payload.Prerequisite is null ? 2 : 3)
            || process.Entry != (payload.Prerequisite is null ? InvocationNode : PrerequisiteNode)
            || process.Nodes.OfType<InvokeTransitionProcessNode>().SingleOrDefault() is not { } invoke
            || invoke.Id != InvocationNode || invoke.Transition != payload.Target.Reference
            || invoke.Continuation.Edge.Target != ReturnNode || invoke.Receipt is not null
            || process.Result != target.Outcome
            || process.Nodes.OfType<ReturnProcessNode>().SingleOrDefault() is not { } returned
            || returned.Id != ReturnNode || returned.Result is not BindingExpr resultBinding
            || invoke.Continuation.Output?.Binding != resultBinding.Binding)
            throw new InvalidOperationException("Reaction Process must invoke its exact target from the source snapshot.");
        if (payload.Prerequisite is not null)
        {
            var wait = process.Nodes.OfType<AwaitMatchProcessNode>().SingleOrDefault();
            if (wait is null || wait.Id != PrerequisiteNode || wait.Clauses.Length != 1
                || wait.Clauses[0] is not ProcessAwaitInteractionClause clause
                || clause.Contract != new SignalContractReference(PrerequisiteSignal!.Reference)
                || clause.Id != PrerequisiteClause || clause.Guard is not null
                || clause.Continuation.Edge.Target != InvocationNode)
                throw new InvalidOperationException("Coordinated reaction Process must await its exact prerequisite before dispatch.");
        }
        var compilation = Compile();
        if (!compilation.IsSuccessful)
            throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics.Select(d => d.Message)));
    }

    /// <summary>Canonical, fingerprinted policy authority.</summary>
    public ExecutionDefinitionDocument Document { get; }
    /// <summary>Derived portable payload.</summary>
    public MachineReactionPayload Payload { get; }

    internal static ValueContract ValidateEpisodeField(Shape source, FieldPath path)
    {
        if (path.Segments.IsDefaultOrEmpty || path.Segments.Length != 1 || !path.Segments[0].TryGetFieldIdentity(out var name)
            || !source.TryGetField(name, out var field) || field.Presence != FieldPresence.Required
            || field.Nullability != FieldNullability.NonNullable || field.Cardinality != FieldCardinality.Single
            || field.Type is not ScalarTypeRef { Kind: ScalarTypeKind.String or ScalarTypeKind.Guid or ScalarTypeKind.Int32 or ScalarTypeKind.Int64 })
            throw new InvalidOperationException("Episode keys must select a direct required non-null string, Guid, Int32 or Int64 source field.");
        return ValueContract.FromField(field);
    }

    /// <summary>Derived exact success-signal contract for the prerequisite, or null for independent reactions.</summary>
    public ExecutionDefinitionDocument? PrerequisiteSignal { get; }

    internal static ExecutionDefinitionDocument CreatePrerequisiteSignal(ExecutionDefinitionDocument prerequisite) =>
        InteractionContractDocuments.Create(
            new("reaction/prerequisite/" + prerequisite.Metadata.Fingerprint.Value), new("1"),
            new SignalContractDefinition(new(new(new ScalarTypeRef(ScalarTypeKind.String)), new("reaction/prerequisite-receipt/v1"))),
            new(new("cohesive.machine-reaction", "1"), new("reaction/prerequisite"), DocumentOrigin.Generated));

    /// <summary>Compiles the request Process using the exact target declaration retained by the policy.</summary>
    /// <param name="interactions">Host interaction catalog; by default includes the derived prerequisite signal when coordinated. Explicit catalogs must include that contract.</param>
    /// <returns>Existing Process compilation result and structured diagnostics.</returns>
    /// <exception cref="InvalidOperationException">The retained target declaration cannot be linked.</exception>
    public ProcessCompilationResult Compile(InteractionContractCatalog? interactions = null)
    {
        var payload = Payload;
        var validation = ProcessDefinitionLink.TryCreateTransition(payload.Target, out var link);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
        if (interactions is null) InteractionContractCatalog.TryCreate(PrerequisiteSignal is null ? [] : [PrerequisiteSignal], out interactions);
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
    MachineReactionIdentity identity = new MachineReactionIdentity.Occurrence();
    MachineReaction? prerequisite;

    /// <summary>Declares one reaction occurrence per source entity and exact policy revision.</summary>
    /// <returns>This authoring source; conflicting second source receipts will fail admission.</returns>
    /// <exception cref="InvalidOperationException">Episode identity has already been selected.</exception>
    public MachineReactionSource<TSource> OncePerSourceEntity()
    {
        if (identity is MachineReactionIdentity.Episode) throw new InvalidOperationException("Episode identity is already selected.");
        identity = new MachineReactionIdentity.Entity(); return this;
    }

    /// <summary>Declares one occurrence per source entity and explicit typed episode field.</summary>
    /// <typeparam name="TKey">String, Guid, Int32 or Int64 field type.</typeparam>
    /// <param name="key">Direct required source property identifying the business episode; no computation or closure is retained.</param>
    /// <returns>This source with a portable field selector. Coordinated dependents inherit it through After.</returns>
    /// <exception cref="ArgumentNullException">Key is null.</exception>
    /// <exception cref="ArgumentException">The selector is not a property path.</exception>
    /// <exception cref="InvalidOperationException">Identity is already selected or the field contract is unsupported.</exception>
    public MachineReactionSource<TSource> OncePerEpisode<TKey>(Expression<Func<TSource, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (identity is not MachineReactionIdentity.Occurrence)
            throw new InvalidOperationException("Reaction identity is already selected.");
        var boxed = Expression.Lambda<Func<TSource, object?>>(Expression.Convert(key.Body, typeof(object)), key.Parameters);
        var path = FieldPath.Capture(boxed);
        _ = MachineReaction.ValidateEpisodeField(source.Shape, path);
        identity = new MachineReactionIdentity.Episode(path);
        return this;
    }

    /// <summary>Requires a successful target receipt from another reaction on the same source entity and, when selected, the same episode.</summary>
    /// <param name="reaction">Exact prerequisite reaction declaration.</param>
    /// <returns>This source with the prerequisite's entity/episode identity selector and a durable prerequisite wait.</returns>
    /// <exception cref="ArgumentNullException">The reaction is null.</exception>
    /// <exception cref="ArgumentException">The prerequisite uses occurrence identity, belongs to another source shape, or conflicts with an explicitly selected identity.</exception>
    /// <exception cref="InvalidOperationException">A prerequisite is already declared.</exception>
    public MachineReactionSource<TSource> After(MachineReaction reaction)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        if (prerequisite is not null) throw new InvalidOperationException("This profile supports one prerequisite per reaction.");
        if (reaction.Payload.Identity is MachineReactionIdentity.Occurrence || reaction.Payload.Source != source.Shape
            || identity is not MachineReactionIdentity.Occurrence && identity != reaction.Payload.Identity)
            throw new ArgumentException("The prerequisite must share the source shape and entity/episode identity selector.", nameof(reaction));
        prerequisite = reaction;
        identity = reaction.Payload.Identity;
        return this;
    }
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
    /// <exception cref="ExpressionAuthoringException">A projection is outside the portable expression language.</exception>
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
        var lowerer = new ExpressionLowerer(author.Clr.ResolveMemberPath);
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
            new(new(id + "/request"), new(revision), prerequisite is null ? MachineReaction.InvocationNode : MachineReaction.PrerequisiteNode, ProcessRecoveryPolicy.ContinueAttempt, provenance),
            ValueContract.FromShape(source.Shape), target.Definition.Outcome,
            p =>
            {
                if (prerequisite is not null)
                {
                    var evidence = p.Output<string>(new ValueBindingId("reaction.prerequisite.receipt"), new ValueContract(new ScalarTypeRef(ScalarTypeKind.String)));
                    var clause = p.AwaitInteractionClause(MachineReaction.PrerequisiteClause,
                        new SignalContractReference(MachineReaction.CreatePrerequisiteSignal(prerequisite.Document).Reference), evidence,
                        requestObligation: null, guard: null, priority: 0,
                        p.Continuation(p.Edge(new ProcessEdgeId("reaction/prerequisite-released"), MachineReaction.InvocationNode)));
                    p.AwaitMatch(MachineReaction.PrerequisiteNode, ProcessAwaitArbitration.ExclusivePriorityThenClauseId, [clause],
                        ProcessAwaitInputDisposition.Observe, ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.ReusePriorDisposition, ProcessAwaitMissingTargetDisposition.DeadLetter, MachineReaction.PrerequisiteRetentionHorizon);
                }
                var result = p.Output<TResult>(new ValueBindingId("reaction.outcome"), target.Definition.Outcome);
                p.InvokeTransition(MachineReaction.InvocationNode, target.Reference,
                    p.CanonicalValue<string>(subjectValue, new(new Cohesive.Model.Authoring.DefaultClrTypeRefMapper().Map(typeof(string), null))),
                    p.CanonicalValue<TInput>(inputValue, target.Definition.Input),
                    p.Continuation(p.Edge(new ProcessEdgeId("reaction/returned"), MachineReaction.ReturnNode), result));
                p.Return(MachineReaction.ReturnNode, result.Value);
            }, sourceFile, sourceLine);
        return new(ExecutionDefinitionDocument.Create(MachineReaction.Kind, new(id), new(revision),
            MachineReactionPayloadWire.FromModel(new(source.Shape, new(movement.Id), target.Document, process.Document, identity, prerequisite?.Document)), provenance));
    }
}
