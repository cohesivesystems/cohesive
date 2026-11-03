using System.Collections.Immutable;
using Cohesive.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Attaches explicit query serving choices to source storage without changing repository ownership.</summary>
public static class ServiceReadSelectionComposition
{
    /// <summary>Selects one reader instead of the source composition's native query interpretation.</summary>
    /// <typeparam name="TInput">Query parameter type.</typeparam><typeparam name="TResult">Complete query result.</typeparam>
    /// <param name="source">Canonical source placement and native repository preparation.</param>
    /// <param name="query">Canonical query to select, including its shape and relationship inputs.</param>
    /// <param name="binding">Serving resource and minimum consistency guarantee, also used by explanation.</param>
    /// <param name="prepare">Admits native resources once per bound composition and returns a bounded-reader factory.
    /// Clients remain host-owned. Called only when this query is selected by an endpoint; failures propagate without fallback.</param>
    /// <returns>Immutable storage composition; call PrepareQueries before BindAsync.</returns>
    /// <exception cref="ArgumentException">The query is already explicitly selected.</exception>
    public static IServiceStorageComposition SelectQuery<TInput, TResult>(this IServiceStorageComposition source,
        RelationQuery<TInput, TResult> query, ServiceQueryReadBinding binding,
        Func<IServiceProvider, CancellationToken, ValueTask<Func<int, long, IRelationQueryReader<TInput, TResult>>>> prepare)
    {
        return Select(source, query, binding, prepare, flag: null);
    }

    /// <summary>Selects between an admitted alternate and native reader once per invocation using a live flag.</summary>
    /// <typeparam name="TInput">Query input and feature targeting context.</typeparam>
    /// <typeparam name="TResult">Complete result projection.</typeparam>
    /// <param name="source">Canonical source placement and disabled reader.</param>
    /// <param name="query">Canonical query implemented by both readers.</param>
    /// <param name="flag">Flag key. True uses the alternate; false uses the native reader.</param>
    /// <param name="binding">Alternate backend and minimum guarantee; native must provide at least this guarantee.</param>
    /// <param name="prepare">Alternate preparation, run before source schema preparation, regardless of current flag value.</param>
    /// <returns>Composition requiring a host-owned IFeatureFlagEvaluator of TInput at binding.</returns>
    /// <exception cref="ArgumentException">The flag is empty or the query already has a serving selection.</exception>
    /// <remarks>Both readers are admitted before traffic. No reflection, re-preparation or provider lookup occurs per read.
    /// Provider and reader failures propagate; there is no automatic fallback or cross-request targeting cache.</remarks>
    public static IServiceStorageComposition SelectQueryByFeatureFlag<TInput, TResult>(this IServiceStorageComposition source,
        RelationQuery<TInput, TResult> query, string flag, ServiceQueryReadBinding binding,
        Func<IServiceProvider, CancellationToken, ValueTask<Func<int, long, IRelationQueryReader<TInput, TResult>>>> prepare)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);
        return Select(source, query, binding, prepare, flag);
    }

    static IServiceStorageComposition Select<TInput, TResult>(IServiceStorageComposition source,
        RelationQuery<TInput, TResult> query, ServiceQueryReadBinding binding,
        Func<IServiceProvider, CancellationToken, ValueTask<Func<int, long, IRelationQueryReader<TInput, TResult>>>> prepare,
        string? flag)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(prepare);
        var key = ServiceQueryIdentity.For(query.CompilationRequest);
        if (source is ISelection previous && previous.Contains(key))
            throw new ArgumentException("The canonical query already has a serving selection.", nameof(query));
        return new Selection<TInput, TResult>(source, query, binding, prepare, key, flag);
    }

    interface ISelection { bool Contains(RelationQueryPlanComponentFingerprint key); }

    sealed class Selection<TInput, TResult>(IServiceStorageComposition source, RelationQuery<TInput, TResult> query,
        ServiceQueryReadBinding binding,
        Func<IServiceProvider, CancellationToken, ValueTask<Func<int, long, IRelationQueryReader<TInput, TResult>>>> prepare,
        RelationQueryPlanComponentFingerprint key, string? flag, ImmutableArray<ServiceQuerySelection> admitted = default)
        : IServiceStorageComposition, ISelection
    {
        public bool Contains(RelationQueryPlanComponentFingerprint candidate) => candidate == key
            || source is ISelection nested && nested.Contains(candidate);
        public ImmutableArray<ValueContract> Observations => source.Observations;
        public ImmutableArray<QualifiedShapeId> Shapes => source.Shapes;
        public ImmutableArray<object> Bindings => source.Bindings;
        public ServiceQueryReadBinding DescribeRead(ServiceQuerySelection selection)
        {
            if (ServiceQueryIdentity.For(selection.Request) != key) return source.DescribeRead(selection);
            return flag is null ? binding : binding with
            { FeatureSelection = new(flag, source.DescribeRead(selection), binding) };
        }

        public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> selections)
        {
            var selected = ImmutableArray.CreateBuilder<ServiceQuerySelection>();
            var native = ImmutableArray.CreateBuilder<ServiceQuerySelection>();
            foreach (var selection in selections)
            {
                if (ServiceQueryIdentity.For(selection.Request) != key) { native.Add(selection); continue; }
                ServiceQueryConsistencyValidation.Require(binding.Consistency, selection.Consistency);
                if (flag is not null)
                {
                    ServiceQueryConsistencyValidation.Require(source.DescribeRead(selection).Consistency, binding.Consistency);
                    native.Add(selection);
                }
                selected.Add(selection);
            }
            var preparedSource = source.PrepareQueries(native.ToImmutable());
            return selected.Count == 0 ? preparedSource
                : new Selection<TInput, TResult>(preparedSource, query, binding, prepare, key, flag, selected.ToImmutable());
        }

        public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(services);
            if (admitted.IsDefault) throw new InvalidOperationException("Admit query selections before binding storage.");
            cancellationToken.ThrowIfCancellationRequested();
            var evaluator = flag is null ? null : services.GetRequiredService<IFeatureFlagEvaluator<TInput>>();
            var factory = await prepare(services, cancellationToken).ConfigureAwait(false);
            Dictionary<(int, long), IRelationQueryReader<TInput, TResult>> readers = [];
            foreach (var selection in admitted)
            {
                var bounds = (selection.MaximumRows, selection.MaximumBytes);
                if (!readers.TryGetValue(bounds, out var reader))
                {
                    reader = factory(bounds.Item1, bounds.Item2);
                    if (reader is null || ServiceQueryIdentity.For(reader.Definition.CompilationRequest) != key)
                        throw new InvalidOperationException("Selected reader must realize the admitted canonical query.");
                    ServiceQueryConsistencyValidation.Require(reader, binding.Consistency);
                    readers.Add(bounds, reader);
                }
                ServiceQueryConsistencyValidation.Require(reader, selection.Consistency);
            }
            // Serving admission must succeed before source preparation can run schema initialization.
            var persistence = await source.BindAsync(services, cancellationToken).ConfigureAwait(false);
            if (flag is not null)
                foreach (var (bounds, alternate) in readers)
                {
                    var live = persistence.Query(query, bounds.Item1, bounds.Item2, binding.Consistency);
                    if (ServiceQueryIdentity.For(live.Definition.CompilationRequest) != key)
                        throw new InvalidOperationException("Native reader must realize the admitted canonical query.");
                    ServiceQueryConsistencyValidation.Require(live, binding.Consistency);
                    readers[bounds] = new FlagReader<TInput, TResult>(query, binding.Consistency, flag, evaluator!, live, alternate);
                }
            return new ServicePersistenceBuilder(persistence).Query(query, (rows, bytes) =>
                readers.TryGetValue((rows, bytes), out var reader) ? reader
                    : throw new InvalidOperationException("Query bounds differ from storage admission.")).Build();
        }
    }

    sealed class FlagReader<TInput, TResult>(RelationQuery<TInput, TResult> definition, RelationQueryConsistency consistency,
        string flag, IFeatureFlagEvaluator<TInput> evaluator, IRelationQueryReader<TInput, TResult> disabled,
        IRelationQueryReader<TInput, TResult> enabled) : IRelationQueryReader<TInput, TResult>
    {
        public RelationQuery<TInput, TResult> Definition => definition;
        public RelationQueryConsistency Consistency => consistency;
        public Task<TResult> ReadAsync(TInput input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = evaluator.IsEnabledAsync(flag, input, cancellationToken);
            return decision.IsCompletedSuccessfully
                ? Read(decision.Result, input, cancellationToken) : AwaitDecision(decision, input, cancellationToken);
        }
        Task<TResult> Read(bool selected, TInput input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return (selected ? enabled : disabled).ReadAsync(input, ct);
        }
        async Task<TResult> AwaitDecision(ValueTask<bool> decision, TInput input, CancellationToken ct) =>
            await Read(await decision.ConfigureAwait(false), input, ct).ConfigureAwait(false);
    }
}
