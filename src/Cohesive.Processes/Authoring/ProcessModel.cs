using Cohesive.Prelude;
using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Authoring;

/// <summary>Declares the semantic identity of one generated process on a partial class.</summary>
/// <param name="id">Stable process identity, independent of the CLR type name.</param>
/// <param name="revision">Explicit semantic revision.</param>
/// <param name="recovery">Canonical recovery policy; execution lifetime is selected separately.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProcessAttribute(string id, string revision,
    ProcessRecoveryPolicy recovery = ProcessRecoveryPolicy.ContinueAttempt) : Attribute
{
    /// <summary>Stable semantic identity.</summary>
    public string Id { get; } = id;
    /// <summary>Exact semantic revision.</summary>
    public string Revision { get; } = revision;
    /// <summary>Declared interruption recovery behavior.</summary>
    public ProcessRecoveryPolicy Recovery { get; } = recovery;
}

/// <summary>Instance-owned syntax-only process with one generated, cached canonical definition.</summary>
/// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process result.</typeparam>
/// <remarks>Complete construction before accessing Definition. Successful preparation and failures are cached
/// per instance. Only immutable declaration dependencies may be retained; never invocation-scoped services.</remarks>
public abstract class ProcessModel<TInput, TResult> : IAuthoringModel, IExecutionDefinitionModel
{
    readonly AuthoringState<Process<TInput, TResult>> definition;

    /// <summary>Creates lazy definition storage without calling derived authoring during construction.</summary>
    protected ProcessModel() => definition = new(CreateDefinition);

    /// <summary>Canonical process definition, generated once on first access.</summary>
    public Process<TInput, TResult> Definition => definition.Value;
    void IAuthoringModel.Complete() => _ = Definition;
    ExecutionDefinitionDocument IExecutionDefinitionModel.Document => Definition.Document;

    /// <summary>Generated source provenance, also usable by explicitly authored child contracts.</summary>
    public ExecutionProvenance Provenance => Metadata.Provenance;
    /// <summary>Generated identity, revision and recovery policy.</summary>
    protected abstract ProcessAuthoringMetadata Metadata { get; }
    /// <summary>Generated lowering through the shared process builder.</summary>
    /// <returns>The canonical process and its preparation evidence.</returns>
    protected abstract Process<TInput, TResult> CreateDefinition();
    /// <summary>Syntax-only workflow entry point; runtime invocation uses the prepared process.</summary>
    /// <param name="process">Generator-recognized workflow authoring context.</param>
    /// <param name="input">Typed invocation input represented in IR.</param>
    /// <returns>A syntax-only result; this method must never be invoked at runtime.</returns>
    protected abstract ProcessTask<TResult> Execute(ProcessContext process, TInput input);
}
