using System.Collections.Immutable;
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
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(prepare);
        var key = ServiceQueryIdentity.For(query.CompilationRequest);
        if (source is ISelection previous && previous.Contains(key))
            throw new ArgumentException("The canonical query already has a serving selection.", nameof(query));
        return new Selection<TInput, TResult>(source, query, binding, prepare, key);
    }

    interface ISelection { bool Contains(RelationQueryPlanComponentFingerprint key); }

    sealed class Selection<TInput, TResult>(IServiceStorageComposition source, RelationQuery<TInput, TResult> query,
        ServiceQueryReadBinding binding,
        Func<IServiceProvider, CancellationToken, ValueTask<Func<int, long, IRelationQueryReader<TInput, TResult>>>> prepare,
        RelationQueryPlanComponentFingerprint key, ImmutableArray<ServiceQuerySelection> admitted = default)
        : IServiceStorageComposition, ISelection
    {
        public bool Contains(RelationQueryPlanComponentFingerprint candidate) => candidate == key
            || source is ISelection nested && nested.Contains(candidate);
        public ImmutableArray<ValueContract> Observations => source.Observations;
        public ImmutableArray<QualifiedShapeId> Shapes => source.Shapes;
        public ImmutableArray<object> Bindings => source.Bindings;
        public ServiceQueryReadBinding? DescribeRead(ServiceQuerySelection selection) =>
            ServiceQueryIdentity.For(selection.Request) == key ? binding : source.DescribeRead(selection);

        public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> selections)
        {
            var selected = ImmutableArray.CreateBuilder<ServiceQuerySelection>();
            var native = ImmutableArray.CreateBuilder<ServiceQuerySelection>();
            foreach (var selection in selections)
            {
                if (ServiceQueryIdentity.For(selection.Request) != key) { native.Add(selection); continue; }
                ServiceQueryConsistencyValidation.Require(binding.Consistency, selection.Consistency);
                selected.Add(selection);
            }
            var preparedSource = source.PrepareQueries(native.ToImmutable());
            return selected.Count == 0 ? preparedSource
                : new Selection<TInput, TResult>(preparedSource, query, binding, prepare, key, selected.ToImmutable());
        }

        public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(services);
            if (admitted.IsDefault) throw new InvalidOperationException("Admit query selections before binding storage.");
            cancellationToken.ThrowIfCancellationRequested();
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
            return new ServicePersistenceBuilder(persistence).Query(query, (rows, bytes) =>
                readers.TryGetValue((rows, bytes), out var reader) ? reader
                    : throw new InvalidOperationException("Query bounds differ from storage admission.")).Build();
        }
    }
}
