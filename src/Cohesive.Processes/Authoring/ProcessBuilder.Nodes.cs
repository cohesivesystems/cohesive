using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Authoring;

public sealed partial class ProcessBuilder<TInput, TResult>
{
    /// <summary>Adds an invocation of one exact aggregate Transition.</summary>
    /// <typeparam name="TSubject">CLR type of the authoritative aggregate subject expression.</typeparam>
    /// <typeparam name="TTransitionInput">CLR type of the Transition input expression.</typeparam>
    /// <param name="id">Stable Process-node identity.</param>
    /// <param name="transition">Exact Transition definition revision and fingerprint.</param>
    /// <param name="subject">Typed aggregate-subject expression.</param>
    /// <param name="input">Typed Transition input expression.</param>
    /// <param name="continuation">Continuation selected after the Transition outcome is available.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="transition"/>, <paramref name="subject"/>, <paramref name="input"/>, or
    /// <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A typed value belongs to another authoring session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> InvokeTransition<TSubject, TTransitionInput>(
        ExecutionNodeId id,
        ExecutionDefinitionReference transition,
        ProcessValue<TSubject> subject,
        ProcessValue<TTransitionInput> input,
        ProcessContinuation continuation,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(transition);
        context.RequireValue(subject);
        context.RequireValue(input);
        ArgumentNullException.ThrowIfNull(continuation);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Transition invocation '{id.Value}'");
        return Add(new InvokeTransitionProcessNode(id, transition, subject.Expression, input.Expression, continuation), source);
    }

    /// <summary>Adds evaluation of one exact canonical Relation or Query.</summary>
    /// <typeparam name="TQueryInput">CLR type of the query input expression.</typeparam>
    /// <param name="id">Stable Process-node identity.</param>
    /// <param name="relation">Exact Relation or Query definition revision and fingerprint.</param>
    /// <param name="input">Typed query input expression.</param>
    /// <param name="continuation">Continuation selected after the query result is available.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relation"/>, <paramref name="input"/>, or <paramref name="continuation"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="input"/> belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> EvaluateRelation<TQueryInput>(
        ExecutionNodeId id,
        ExecutionDefinitionReference relation,
        ProcessValue<TQueryInput> input,
        ProcessContinuation continuation,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(relation);
        context.RequireValue(input);
        ArgumentNullException.ThrowIfNull(continuation);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Relation evaluation '{id.Value}'");
        return Add(new EvaluateRelationProcessNode(id, relation, input.Expression, continuation), source);
    }

    /// <summary>Adds one durable typed Request obligation with explicit terminal continuations.</summary>
    /// <typeparam name="TPayload">CLR type of the Request payload.</typeparam>
    /// <param name="id">Stable Process-node and logical-emission identity basis.</param>
    /// <param name="contract">Exact typed Request contract.</param>
    /// <param name="payload">Typed Request payload expression.</param>
    /// <param name="outcomes">Set-like terminal Request outcome branches.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/> or <paramref name="payload"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="payload"/> belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> Request<TPayload>(
        ExecutionNodeId id,
        RequestContractReference contract,
        ProcessValue<TPayload> payload,
        ImmutableArray<ProcessRequestOutcomeBranch> outcomes,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(contract);
        context.RequireValue(payload);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Request '{id.Value}'");
        return Add(new RequestProcessNode(id, contract, payload.Expression, outcomes), source);
    }

    /// <summary>Adds one typed domain-event emission.</summary>
    /// <typeparam name="TPayload">CLR type of the event payload.</typeparam>
    /// <param name="id">Stable Process-node and logical-emission identity basis.</param>
    /// <param name="contract">Exact typed domain-event contract.</param>
    /// <param name="payload">Typed event payload expression.</param>
    /// <param name="next">Edge selected after the emission intent is accepted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/>, <paramref name="payload"/>, or <paramref name="next"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="payload"/> belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> EmitEvent<TPayload>(
        ExecutionNodeId id,
        DomainEventContractReference contract,
        ProcessValue<TPayload> payload,
        ProcessEdge next,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(contract);
        context.RequireValue(payload);
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Domain-event emission '{id.Value}'");
        return Add(new EmitEventProcessNode(id, contract, payload.Expression, next), source);
    }

    /// <summary>Adds one typed Signal send to an explicitly computed semantic target.</summary>
    /// <typeparam name="TTarget">CLR type of the Signal target expression.</typeparam>
    /// <typeparam name="TPayload">CLR type of the Signal payload expression.</typeparam>
    /// <param name="id">Stable Process-node and logical-emission identity basis.</param>
    /// <param name="contract">Exact typed Signal contract.</param>
    /// <param name="target">Typed semantic target expression.</param>
    /// <param name="payload">Typed Signal payload expression.</param>
    /// <param name="next">Edge selected after the send intent is accepted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/>, <paramref name="target"/>, <paramref name="payload"/>, or
    /// <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A typed value belongs to another authoring session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> SendSignal<TTarget, TPayload>(
        ExecutionNodeId id,
        SignalContractReference contract,
        ProcessValue<TTarget> target,
        ProcessValue<TPayload> payload,
        ProcessEdge next,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(contract);
        context.RequireValue(target);
        context.RequireValue(payload);
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Signal send '{id.Value}'");
        return Add(new SendSignalProcessNode(id, contract, target.Expression, payload.Expression, next), source);
    }

    /// <summary>Adds an explicitly ordered portable predicate Choice.</summary>
    /// <param name="id">Stable Process-node identity.</param>
    /// <param name="selection">Explicit ordered case-selection semantics.</param>
    /// <param name="completeness">Declared branch coverage mode.</param>
    /// <param name="cases">Semantically ordered predicate cases.</param>
    /// <param name="fallback">Optional explicit fallback branch.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> duplicates an authored node.</exception>
    public ProcessBuilder<TInput, TResult> Choice(
        ExecutionNodeId id,
        CaseSelection selection,
        BranchCompleteness completeness,
        ImmutableArray<ProcessChoiceCase> cases,
        ProcessFallback? fallback = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Choice '{id.Value}'");
        return Add(new ChoiceProcessNode(id, selection, completeness, cases, fallback), source);
    }

    /// <summary>Adds an explicitly typed and ordered exact-pattern Match.</summary>
    /// <typeparam name="TValue">CLR type projected into the Match value contract.</typeparam>
    /// <param name="id">Stable Process-node identity.</param>
    /// <param name="selection">Explicit ordered case-selection semantics.</param>
    /// <param name="completeness">Declared branch coverage mode.</param>
    /// <param name="value">Typed value expression being matched.</param>
    /// <param name="cases">Semantically ordered exact-value cases.</param>
    /// <param name="fallback">Optional explicit fallback branch.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="value"/> belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> Match<TValue>(
        ExecutionNodeId id,
        CaseSelection selection,
        BranchCompleteness completeness,
        ProcessValue<TValue> value,
        ImmutableArray<ProcessMatchCase> cases,
        ProcessFallback? fallback = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(value);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Match '{id.Value}'");
        return Add(new MatchProcessNode(id, selection, completeness, value.Expression, value.Contract, cases, fallback), source);
    }

    /// <summary>Adds a finite normalized parallel Fork owned by one reciprocal Join.</summary>
    /// <param name="id">Stable Process-node identity.</param>
    /// <param name="branches">Set-like stable branch declarations.</param>
    /// <param name="join">Stable identity of the reciprocal Join.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> duplicates an authored node.</exception>
    public ProcessBuilder<TInput, TResult> Fork(
        ExecutionNodeId id,
        ImmutableArray<ProcessForkBranch> branches,
        ExecutionNodeId join,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Fork '{id.Value}'");
        return Add(new ForkProcessNode(id, branches, join), source);
    }

    /// <summary>Adds a finite normalized parallel Fork with explicit durable admission limits.</summary>
    /// <param name="id">Stable Process-node identity.</param>
    /// <param name="branches">Set-like stable branch declarations and optional capacity assignments.</param>
    /// <param name="join">Stable identity of the reciprocal Join.</param>
    /// <param name="limits">Hard finite branch, per-activation start, and parallelism limits.</param>
    /// <param name="capacityDomains">Optional named capacity-domain limits.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> duplicates an authored node.</exception>
    public ProcessBuilder<TInput, TResult> Fork(
        ExecutionNodeId id,
        ImmutableArray<ProcessForkBranch> branches,
        ExecutionNodeId join,
        ProcessWorkLimits limits,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(limits);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Fork '{id.Value}'");
        context.RegisterIfAbsent(limits, source);
        foreach (var domain in capacityDomains.IsDefault ? [] : capacityDomains)
        {
            if (domain is not null)
                context.RegisterIfAbsent(domain, source);
        }
        return Add(new ForkProcessNode(id, branches, join, limits, capacityDomains), source);
    }

    /// <summary>Adds a Join that converges tokens from one reciprocal Fork.</summary>
    /// <param name="id">Stable Process-node identity.</param>
    /// <param name="fork">Stable identity of the reciprocal Fork.</param>
    /// <param name="policy">Explicit completion, failure, cancellation, ordering, and tie-break policy.</param>
    /// <param name="next">Edge selected after the Join is satisfied.</param>
    /// <param name="result">Optional typed projection populated from the deterministically selected branches.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="policy"/> or <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> duplicates an authored node.</exception>
    public ProcessBuilder<TInput, TResult> Join(
        ExecutionNodeId id,
        ExecutionNodeId fork,
        ProcessJoinPolicy policy,
        ProcessEdge next,
        ProcessJoinResultProjection? result = null,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Join '{id.Value}'");
        context.RegisterIfAbsent(policy, source);
        return Add(new JoinProcessNode(id, fork, policy, next, result), source);
    }

    /// <summary>Adds a durable AwaitMatch over a closed set of typed interaction and timer clauses.</summary>
    /// <param name="id">Stable Process-node and durable-wait identity basis.</param>
    /// <param name="arbitration">Explicit exclusive winner-selection semantics.</param>
    /// <param name="clauses">Set-like typed interaction and timer clauses.</param>
    /// <param name="lateInput">Disposition for input arriving after a winner completed the wait.</param>
    /// <param name="staleInput">Disposition for input targeting incompatible continuation state.</param>
    /// <param name="duplicateInput">Disposition for repeated logical input.</param>
    /// <param name="missingTarget">Disposition when no compatible durable target can be resolved.</param>
    /// <param name="retentionHorizon">Minimum duration for which the wait remains addressable.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> duplicates an authored node.</exception>
    public ProcessBuilder<TInput, TResult> AwaitMatch(
        ExecutionNodeId id,
        ProcessAwaitArbitration arbitration,
        ImmutableArray<ProcessAwaitClause> clauses,
        ProcessAwaitInputDisposition lateInput,
        ProcessAwaitInputDisposition staleInput,
        ProcessAwaitInputDisposition duplicateInput,
        ProcessAwaitMissingTargetDisposition missingTarget,
        TimeSpan retentionHorizon,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"AwaitMatch '{id.Value}'");
        return Add(
            new AwaitMatchProcessNode(
                id,
                arbitration,
                clauses,
                lateInput,
                staleInput,
                duplicateInput,
                missingTarget,
                retentionHorizon),
            source);
    }

    /// <summary>Adds an absolute-time durable Timer.</summary>
    /// <param name="id">Stable Process-node and timer identity basis.</param>
    /// <param name="dueAt">Typed absolute due instant.</param>
    /// <param name="next">Edge selected after the timer is durably admitted as due.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dueAt"/> or <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="dueAt"/> belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> Timer(
        ExecutionNodeId id,
        ProcessValue<DateTimeOffset> dueAt,
        ProcessEdge next,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(dueAt);
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Timer '{id.Value}'");
        return Add(new TimerProcessNode(id, dueAt.Expression, next), source);
    }

    /// <summary>Adds a typed Reply that discharges one admitted Request obligation.</summary>
    /// <typeparam name="TPayload">CLR type of the Reply payload.</typeparam>
    /// <param name="id">Stable Process-node and logical-emission identity basis.</param>
    /// <param name="contract">Exact typed Reply contract.</param>
    /// <param name="request">Definitely visible Request obligation being discharged.</param>
    /// <param name="payload">Typed Reply payload expression.</param>
    /// <param name="next">Edge selected after the Reply intent is accepted.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="contract"/>, <paramref name="request"/>, <paramref name="payload"/>, or
    /// <paramref name="next"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A supplied authoring handle belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> Reply<TPayload>(
        ExecutionNodeId id,
        ReplyContractReference contract,
        ProcessRequestObligation request,
        ProcessValue<TPayload> payload,
        ProcessEdge next,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(contract);
        context.RequireObligation(request);
        context.RequireValue(payload);
        ArgumentNullException.ThrowIfNull(next);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Reply '{id.Value}'");
        return Add(new ReplyProcessNode(id, contract, request.Binding, payload.Expression, next), source);
    }

    /// <summary>Adds an explicit durable activation boundary.</summary>
    /// <param name="id">Stable durable-cut node identity.</param>
    /// <param name="resume">Edge at which a later activation resumes.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resume"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> duplicates an authored node.</exception>
    public ProcessBuilder<TInput, TResult> DurableCut(
        ExecutionNodeId id,
        ProcessEdge resume,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(resume);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Durable cut '{id.Value}'");
        return Add(new DurableCutProcessNode(id, resume), source);
    }

    /// <summary>Adds one exact child Process invocation through the canonical durable Request/Reply protocol.</summary>
    /// <typeparam name="TChildInput">CLR type of the child Process input.</typeparam>
    /// <param name="id">Stable Process-node and child-invocation identity basis.</param>
    /// <param name="process">Exact child Process definition revision and fingerprint.</param>
    /// <param name="contract">Exact Request contract used to start and join the child.</param>
    /// <param name="outcomeMapping">Total child-terminal-status to Request-outcome mapping.</param>
    /// <param name="input">Typed child Process input expression.</param>
    /// <param name="purpose">Explicit work, compensation, or reconciliation purpose.</param>
    /// <param name="cancellation">Explicit parent-to-child cancellation behavior.</param>
    /// <param name="outcomes">Set-like terminal Request outcome continuations.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="process"/>, <paramref name="contract"/>, <paramref name="outcomeMapping"/>, or
    /// <paramref name="input"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="input"/> belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> InvokeProcess<TChildInput>(
        ExecutionNodeId id,
        ExecutionDefinitionReference process,
        RequestContractReference contract,
        ProcessChildOutcomeMapping outcomeMapping,
        ProcessValue<TChildInput> input,
        ProcessChildPurpose purpose,
        ProcessChildCancellationPolicy cancellation,
        ImmutableArray<ProcessRequestOutcomeBranch> outcomes,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(outcomeMapping);
        context.RequireValue(input);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Child Process invocation '{id.Value}'");
        context.RegisterIfAbsent(outcomeMapping, source);
        return Add(
            new InvokeProcessProcessNode(
                id,
                process,
                contract,
                outcomeMapping,
                input.Expression,
                purpose,
                cancellation,
                outcomes),
            source);
    }

    /// <summary>Adds finite bounded partition work using one exact child Process per partition.</summary>
    /// <typeparam name="TPartitions">CLR finite collection type containing the partitions.</typeparam>
    /// <typeparam name="TPartition">CLR type of one partition value.</typeparam>
    /// <typeparam name="TChildInput">CLR type of each child Process input.</typeparam>
    /// <param name="id">Stable Process-node and bounded-work occurrence identity basis.</param>
    /// <param name="partitions">Typed finite partition collection expression.</param>
    /// <param name="partition">Typed lexical binding for one partition value.</param>
    /// <param name="progressIdentity">Typed stable string identity for the visible partition.</param>
    /// <param name="process">Exact child Process definition used for every partition.</param>
    /// <param name="contract">Exact Request contract used to start and join each child.</param>
    /// <param name="outcomeMapping">Total child-terminal-status to Request-outcome mapping.</param>
    /// <param name="childInput">Typed child input evaluated with <paramref name="partition"/> visible.</param>
    /// <param name="limits">Explicit finite item, activation-start, and parallelism limits.</param>
    /// <param name="failure">Explicit sibling-admission behavior after one child fails.</param>
    /// <param name="capacityIdentity">Optional typed capacity-domain identity for the visible partition.</param>
    /// <param name="capacityDomains">
    /// Canonical capacity-domain limits; empty exactly when <paramref name="capacityIdentity"/> is null.
    /// </param>
    /// <param name="cancellation">Explicit parent-to-child cancellation behavior.</param>
    /// <param name="completed">Edge selected after every partition child completes successfully.</param>
    /// <param name="failed">Edge selected when bounded child work reaches its failed outcome.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// A typed handle, definition or contract reference, outcome mapping, limit, or edge argument is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A typed handle belongs to another authoring session, <paramref name="partition"/> is the invocation-input
    /// binding, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> ForEachPartition<TPartitions, TPartition, TChildInput>(
        ExecutionNodeId id,
        ProcessValue<TPartitions> partitions,
        ProcessBinding<TPartition> partition,
        ProcessValue<string> progressIdentity,
        ExecutionDefinitionReference process,
        RequestContractReference contract,
        ProcessChildOutcomeMapping outcomeMapping,
        ProcessValue<TChildInput> childInput,
        ProcessWorkLimits limits,
        ProcessPartitionFailurePolicy failure,
        ProcessValue<string>? capacityIdentity,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains,
        ProcessChildCancellationPolicy cancellation,
        ProcessEdge completed,
        ProcessEdge failed,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
        where TPartitions : IEnumerable<TPartition>
    {
        context.RequireValue(partitions);
        context.RequireBinding(partition);
        context.RequireValue(progressIdentity);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(outcomeMapping);
        context.RequireValue(childInput);
        ArgumentNullException.ThrowIfNull(limits);
        if (capacityIdentity is not null)
            context.RequireValue(capacityIdentity);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(failed);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Bounded partition work '{id.Value}'");
        context.RegisterIfAbsent(outcomeMapping, source);
        context.RegisterIfAbsent(limits, source);
        foreach (var domain in capacityDomains.IsDefault ? [] : capacityDomains)
        {
            if (domain is not null)
                context.RegisterIfAbsent(domain, source);
        }
        return Add(
            new ForEachPartitionProcessNode(
                id,
                partitions.Expression,
                partition.RequireOutput(),
                progressIdentity.Expression,
                process,
                contract,
                outcomeMapping,
                childInput.Expression,
                limits,
                failure,
                capacityIdentity?.Expression,
                capacityDomains,
                cancellation,
                completed,
                failed),
            source);
    }

    /// <summary>Adds explicitly bounded recurrence across durable Process activations.</summary>
    /// <typeparam name="TProgress">CLR type projected into the recurrence progress contract.</typeparam>
    /// <param name="id">Stable recurrence node and occurrence identity basis.</param>
    /// <param name="continueWhen">Typed Boolean expression deciding whether another occurrence is required.</param>
    /// <param name="progress">Typed value used to prove progress across occurrences.</param>
    /// <param name="policy">Explicit occurrence and unchanged-progress limits.</param>
    /// <param name="repeat">Edge selected at a durable cut when another occurrence is admitted.</param>
    /// <param name="completed">Edge selected when recurrence is complete.</param>
    /// <param name="exhausted">Edge selected when the total occurrence limit is reached.</param>
    /// <param name="stalled">Edge selected when progress remains unchanged beyond its limit.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// A typed value, policy, or edge argument is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A typed value belongs to another authoring session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> RepeatAcrossActivation<TProgress>(
        ExecutionNodeId id,
        ProcessValue<bool> continueWhen,
        ProcessValue<TProgress> progress,
        ProcessRecurrencePolicy policy,
        ProcessEdge repeat,
        ProcessEdge completed,
        ProcessEdge exhausted,
        ProcessEdge stalled,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(continueWhen);
        context.RequireValue(progress);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(repeat);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(exhausted);
        ArgumentNullException.ThrowIfNull(stalled);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Durable recurrence '{id.Value}'");
        context.RegisterIfAbsent(policy, source);
        return Add(
            new RepeatAcrossActivationProcessNode(
                id,
                continueWhen.Expression,
                progress.Expression,
                progress.Contract,
                policy,
                repeat,
                completed,
                exhausted,
                stalled),
            source);
    }

    /// <summary>Adds a successful typed terminal Process result.</summary>
    /// <param name="id">Stable terminal node identity.</param>
    /// <param name="result">Typed Process result expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="result"/> belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> Return(
        ExecutionNodeId id,
        ProcessValue<TResult> result,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(result);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Successful Process result '{id.Value}'");
        return Add(new ReturnProcessNode(id, result.Expression), source);
    }

    /// <summary>Adds a failed typed terminal Process result.</summary>
    /// <param name="id">Stable terminal node identity.</param>
    /// <param name="result">Typed Process failure-result expression.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>This Process builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="result"/> belongs to another session, or <paramref name="id"/> duplicates an authored node.
    /// </exception>
    public ProcessBuilder<TInput, TResult> Fail(
        ExecutionNodeId id,
        ProcessValue<TResult> result,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
    {
        context.RequireValue(result);
        var source = context.Source(sourceFile, sourceLine, sourceMember, $"Failed Process result '{id.Value}'");
        return Add(new FailProcessNode(id, result.Expression), source);
    }
}
