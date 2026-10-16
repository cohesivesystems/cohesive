using Cohesive.Execution;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Processes.Authoring;

/// <summary>Typed host attachment retained by generated authoring; canonical Process references remain authoritative.</summary>
/// <remarks>Attachments are setup-only, never serialized into IR or invoked to discover runtime dependencies.</remarks>
public abstract class ProcessDependency
{
    private protected ProcessDependency() { }
    /// <summary>Exact canonical definition selected by this attachment.</summary>
    public abstract ExecutionDefinitionReference Reference { get; }
    private protected abstract object Authority { get; }
    /// <summary>Tests whether two attachments retain the very same declaration object and binding recipe.</summary>
    /// <param name="other">Another generated attachment.</param>
    /// <returns>True only for reference-identical declaration authorities, not merely equal semantic references.</returns>
    public bool HasSameAuthority(ProcessDependency? other) => other is not null && ReferenceEquals(Authority, other.Authority);
    /// <summary>Contributes the typed attachment to a host-specific receiver.</summary>
    /// <param name="receiver">Setup receiver, never retained.</param>
    /// <exception cref="ArgumentNullException">Receiver is null.</exception>
    public abstract void Contribute(IProcessDependencyReceiver receiver);

    /// <summary>Retains a typed transition without compiling it or resolving storage.</summary>
    /// <typeparam name="TEntity">Entity state.</typeparam><typeparam name="TInput">Input.</typeparam><typeparam name="TResult">Outcome.</typeparam>
    /// <param name="transition">Exact transition authority.</param>
    /// <returns>A setup attachment for the existing transition.</returns>
    /// <exception cref="ArgumentNullException">Transition is null.</exception>
    public static ProcessDependency Transition<TEntity, TInput, TResult>(Transition<TEntity, TInput, TResult> transition) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        return new TransitionDependency<TEntity, TInput, TResult>(transition);
    }

    /// <summary>Retains a declared entity point-read recipe.</summary>
    /// <typeparam name="TEntity">Entity state.</typeparam><typeparam name="TInput">Input.</typeparam><typeparam name="TResult">Result.</typeparam>
    /// <param name="read">Exact query and native entity-read attachment.</param>
    /// <returns>A setup attachment for the point read.</returns>
    /// <exception cref="ArgumentNullException">Read is null.</exception>
    public static ProcessDependency PointRead<TEntity, TInput, TResult>(EntityPointRead<TEntity, TInput, TResult> read)
        where TEntity : class where TInput : notnull where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(read);
        return new PointReadDependency<TEntity, TInput, TResult>(read);
    }

    sealed class TransitionDependency<TEntity, TInput, TResult>(Transition<TEntity, TInput, TResult> transition) : ProcessDependency where TEntity : notnull
    {
        private protected override object Authority => transition;
        public override ExecutionDefinitionReference Reference => transition.Reference;
        public override void Contribute(IProcessDependencyReceiver receiver) { ArgumentNullException.ThrowIfNull(receiver); receiver.Transition(transition); }
    }
    sealed class PointReadDependency<TEntity, TInput, TResult>(EntityPointRead<TEntity, TInput, TResult> read) : ProcessDependency
        where TEntity : class where TInput : notnull where TResult : notnull
    {
        private protected override object Authority => read;
        public override ExecutionDefinitionReference Reference => read.Reference;
        public override void Contribute(IProcessDependencyReceiver receiver) { ArgumentNullException.ThrowIfNull(receiver); receiver.PointRead(read); }
    }
}

/// <summary>Adapter seam for generated typed dependencies; it owns no canonical semantics or runtime state.</summary>
public interface IProcessDependencyReceiver
{
    /// <summary>Registers a transition selected by the process.</summary>
    /// <typeparam name="TEntity">Entity state.</typeparam><typeparam name="TInput">Input.</typeparam><typeparam name="TResult">Outcome.</typeparam>
    /// <param name="transition">Canonical transition.</param>
    void Transition<TEntity, TInput, TResult>(Transition<TEntity, TInput, TResult> transition) where TEntity : notnull;
    /// <summary>Registers an explicit entity point-read attachment selected by the process.</summary>
    /// <typeparam name="TEntity">Entity state.</typeparam><typeparam name="TInput">Input.</typeparam><typeparam name="TResult">Result.</typeparam>
    /// <param name="read">Canonical query and native recipe.</param>
    void PointRead<TEntity, TInput, TResult>(EntityPointRead<TEntity, TInput, TResult> read)
        where TEntity : class where TInput : notnull where TResult : notnull;
}

/// <summary>An observational hosted query attached to one entity read by key in the repository's trusted partition.</summary>
/// <typeparam name="TEntity">Entity state.</typeparam><typeparam name="TInput">Invocation input.</typeparam><typeparam name="TResult">Result projection.</typeparam>
/// <remarks>Key/result delegates are explicit host attachments, not portable query semantics. They must be thread-safe;
/// their implementation contract must be revised when behavior changes. No repository, connection, or request is retained.</remarks>
public sealed class EntityPointRead<TEntity, TInput, TResult> where TEntity : class where TInput : notnull where TResult : notnull
{
    /// <summary>Attaches a native single-entity read recipe to an existing independently declared query contract.</summary>
    /// <param name="query">Exact observational query authority.</param><param name="entity">Exact entity authority.</param>
    /// <param name="key">Invocation-time identity projection.</param><param name="result">Projection of the entity or its absence.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The query is not observational.</exception>
    public EntityPointRead(HostedQuery<TInput, TResult> query, DomainEntity<TEntity> entity, Func<TInput, string> key, Func<TEntity?, TResult> result)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(result);
        if (query.EvaluationSemantics != Cohesive.Relations.IR.HostedQueryEvaluationSemantics.Observation)
            throw new ArgumentException("An entity point read requires observational query semantics.", nameof(query));
        Query = query; Entity = entity; Key = key; Result = result;
    }
    /// <summary>Canonical hosted query.</summary>
    public HostedQuery<TInput, TResult> Query { get; }
    /// <summary>Exact canonical query reference.</summary>
    public ExecutionDefinitionReference Reference => Query.Reference;
    /// <summary>Canonical entity authority.</summary>
    public DomainEntity<TEntity> Entity { get; }
    /// <summary>Invocation-time identity projection.</summary>
    public Func<TInput, string> Key { get; }
    /// <summary>Invocation-time result projection.</summary>
    public Func<TEntity?, TResult> Result { get; }
}
