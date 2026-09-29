using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>Authors a single-entity service mutation as canonical Process data.</summary>
/// <remarks>This is a finite authoring projection, not an execution engine. Multi-entity coordination, branches,
/// recovery and compensation should use an explicitly authored Process. Query hosts own scoped acquisition;
/// the Transition owns domain invariants and freshness checks at its commit boundary.</remarks>
public static class ServiceMutation
{
    /// <summary>Starts composition with a typed fact-acquisition query.</summary>
    public static ServiceMutation<TInput, TFacts> HydrateWith<TInput, TFacts>(HostedQuery<TInput, TFacts> query)
        where TInput : notnull where TFacts : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsValid) throw new ServiceBindingValidationException(query.Validation);
        return new(query.InputContract, query.ResultContract,
            [new(query.Reference, query.InputContract, query.ResultContract, null)], false);
    }

    /// <summary>Starts composition with a Transition whose input already contains its required facts.</summary>
    public static ServiceMutation<TInput, TOutcome> Apply<TEntity, TInput, TOutcome>(
        Transition<TEntity, TInput, TOutcome> transition, Expression<Func<TInput, object?>> subject)
        where TEntity : notnull where TInput : notnull where TOutcome : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        return new ServiceMutation<TInput, TInput>(transition.Definition.Input, transition.Definition.Input, [], false)
            .Apply(transition, subject);
    }
}

// Authoring-only inputs. Canonical Process nodes, contracts and exact references remain the persisted authority.
internal sealed record ServiceMutationStep(ExecutionDefinitionReference Definition, ValueContract Input,
    ValueContract Output, FieldPath? Subject);

/// <summary>Typed finite composition of fact acquisition, one Transition, and optional result enrichment.</summary>
/// <typeparam name="TInput">Public operation input.</typeparam>
/// <typeparam name="TCurrent">Inferred output of the most recently declared step.</typeparam>
public sealed class ServiceMutation<TInput, TCurrent> where TInput : notnull where TCurrent : notnull
{
    readonly ValueContract input;
    readonly ValueContract current;
    readonly ImmutableArray<ServiceMutationStep> steps;
    readonly bool applied;

    internal ServiceMutation(ValueContract input, ValueContract current, ImmutableArray<ServiceMutationStep> steps, bool applied)
    {
        this.input = input;
        this.current = current;
        this.steps = steps;
        this.applied = applied;
    }

    /// <summary>Applies one entity Transition to acquired facts and infers its declared outcome contract.</summary>
    /// <remarks>The subject selector is lowered immediately to a portable member path and never executed.</remarks>
    /// <exception cref="InvalidOperationException">A mutation was already declared; use an explicit Process for multiple writes.</exception>
    /// <exception cref="ArgumentException">The selector or exact input contract is incompatible.</exception>
    public ServiceMutation<TInput, TOutcome> Apply<TEntity, TOutcome>(Transition<TEntity, TCurrent, TOutcome> transition,
        Expression<Func<TCurrent, object?>> subject) where TEntity : notnull where TOutcome : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(subject);
        if (applied) throw new InvalidOperationException("Multiple entity mutations require an explicitly authored Process.");
        if (!transition.IsValid) throw new ServiceBindingValidationException(transition.Validation);
        var definition = transition.Definition;
        RequireContract(definition.Input);
        return new(input, definition.Outcome,
            steps.Add(new(transition.Reference, definition.Input, definition.Outcome, FieldPath.Capture(subject))), true);
    }

    /// <summary>Projects the Transition outcome through an exact typed relation query without reloading its source.</summary>
    /// <remarks>An enrichment failure does not undo a prior commit. Query hosts must enforce related-read authorization.</remarks>
    public ServiceMutation<TInput, TResult> EnrichWith<TResult>(HostedQuery<TCurrent, TResult> query) where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!applied) throw new InvalidOperationException("Declare the entity Transition before response enrichment.");
        if (!query.IsValid) throw new ServiceBindingValidationException(query.Validation);
        RequireContract(query.InputContract);
        return new(input, query.ResultContract, steps.Add(new(query.Reference, query.InputContract, query.ResultContract, null)), true);
    }

    /// <summary>Lowers the composition to the existing typed Process authoring and validation pipeline.</summary>
    /// <remarks>The generated public output is the last declared step's output. No commit-node selector is needed.
    /// Exact links and shape evidence must still be supplied when compiling the returned Process for execution.</remarks>
    public Process<TInput, TCurrent> Build(ProcessAuthoringMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!applied) throw new InvalidOperationException("A service mutation requires an entity Transition.");
        if (metadata.EntryId is { } entry && entry != Node(0))
            throw new ArgumentException("The entry of a service mutation is derived from its first step.", nameof(metadata));
        var derivedMetadata = new ProcessAuthoringMetadata(metadata.DefinitionId, metadata.RevisionId, Node(0),
            metadata.RecoveryPolicy, metadata.Provenance, metadata.DisplayName, metadata.Description);
        return ProcessAuthoring.Create<TInput, TCurrent>(derivedMetadata, input, current, builder =>
        {
            var binding = ProcessBindingIds.Input;
            for (var index = 0; index < steps.Length; index++)
            {
                var step = steps[index];
                var id = Node(index);
                var value = builder.CanonicalValue<object>(Expr.BoundValue(binding), step.Input);
                var output = builder.Output<object>(new($"value/{index}"), step.Output);
                var continuation = builder.Continuation(builder.Edge(new($"edge/{index}"), Node(index + 1)), output);
                if (step.Subject is { } subject)
                    builder.InvokeTransition(id, step.Definition,
                        builder.CanonicalValue<object>(Expr.Field(binding, subject), new(new ScalarTypeRef(ScalarTypeKind.String))), value, continuation);
                else
                    builder.EvaluateRelation(id, step.Definition, value, continuation);
                binding = output.Binding;
            }
            builder.Return(Node(steps.Length), builder.CanonicalValue<TCurrent>(Expr.BoundValue(binding), current));
        });
    }

    void RequireContract(ValueContract required)
    {
        if (current != required)
            throw new ArgumentException("The previous step's exact output contract must match the next step's input contract.");
    }

    static ExecutionNodeId Node(int index) => new($"step/{index}");
}
