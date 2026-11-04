using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Attaches explicit query serving choices to source storage without changing repository ownership.</summary>
public static class ServiceReadSelectionComposition
{
    /// <summary>Attaches a fixed or contextual live choice to an alternate reader declaration.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Complete result.</typeparam>
    /// <param name="source">Canonical live source.</param><param name="query">Canonical query realized by both candidates.</param>
    /// <param name="binding">Alternate resource and minimum guarantee; the native candidate must meet this guarantee.</param>
    /// <param name="prepare">Admits the alternate once, before source preparation; never called for Native selection.</param>
    /// <param name="selection">Fixed native/alternate or flag choice. Defaults to fixed alternate.</param>
    /// <returns>An immutable composition requiring offline query admission before binding.</returns>
    /// <exception cref="ServiceBindingValidationException">The query already has an explicit serving selection.</exception>
    public static IServiceStorageComposition SelectQuery<TInput, TResult>(this IServiceStorageComposition source,
        RelationQuery<TInput, TResult> query, ServiceQueryReadBinding binding,
        Func<IServiceProvider, CancellationToken, ValueTask<Func<int, long, IRelationQueryReader<TInput, TResult>>>> prepare,
        ServiceReadSelection<TInput>? selection = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(prepare);
        selection ??= ServiceReadSelection<TInput>.Alternate;
        var key = ServiceQueryIdentity.For(query.CompilationRequest);
        if (source is ISelection previous && previous.Contains(key))
            throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.QueryPlacementUnsupported,
                "The canonical query already has a serving selection.", "/bindings/queries/selection");
        if (selection.IsNative) return source;
        return new Selection<TInput, TResult>(source, query, binding, prepare, key, selection);
    }

    interface ISelection { bool Contains(RelationQueryPlanComponentFingerprint key); }

    sealed class Selection<TInput, TResult>(IServiceStorageComposition source, RelationQuery<TInput, TResult> query,
        ServiceQueryReadBinding binding,
        Func<IServiceProvider, CancellationToken, ValueTask<Func<int, long, IRelationQueryReader<TInput, TResult>>>> prepare,
        RelationQueryPlanComponentFingerprint key, ServiceReadSelection<TInput> choice, ImmutableArray<ServiceQuerySelection> admitted = default)
        : IServiceStorageComposition, ISelection
    {
        public bool Contains(RelationQueryPlanComponentFingerprint candidate) => candidate == key
            || source is ISelection nested && nested.Contains(candidate);
        public ImmutableArray<ValueContract> Observations => source.Observations;
        public ImmutableArray<QualifiedShapeId> Shapes => source.Shapes;
        public ImmutableArray<object> Bindings => source.Bindings;
        public ServiceQueryReadSelection DescribeRead(ServiceQuerySelection selection)
        {
            if (ServiceQueryIdentity.For(selection.Request) != key) return source.DescribeRead(selection);
            return choice.Flag is not { } flag ? binding : new ServiceQueryFeatureSelection(flag, NativeBinding(selection), binding);
        }

        ServiceQueryReadBinding NativeBinding(ServiceQuerySelection selection) =>
            source.DescribeRead(selection) as ServiceQueryReadBinding
                ?? throw ServiceBindingValidationException.Error(ServiceBindingDiagnosticCodes.QueryPlacementUnsupported,
                    "A flag choice requires a fixed native reader.", "/bindings/queries/selection");

        public IServiceStorageComposition PrepareQueries(ImmutableArray<ServiceQuerySelection> selections)
        {
            var selected = ImmutableArray.CreateBuilder<ServiceQuerySelection>();
            var native = ImmutableArray.CreateBuilder<ServiceQuerySelection>();
            foreach (var selection in selections)
            {
                if (ServiceQueryIdentity.For(selection.Request) != key) { native.Add(selection); continue; }
                ServiceQueryConsistencyValidation.Require(binding.Consistency, selection.Consistency);
                if (choice.Flag is not null)
                {
                    ServiceQueryConsistencyValidation.Require(NativeBinding(selection).Consistency, binding.Consistency);
                    native.Add(selection);
                }
                selected.Add(selection);
            }
            var preparedSource = source.PrepareQueries(native.ToImmutable());
            return selected.Count == 0 ? preparedSource
                : new Selection<TInput, TResult>(preparedSource, query, binding, prepare, key, choice, selected.ToImmutable());
        }

        public async ValueTask<IServicePersistence> BindAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(services);
            if (admitted.IsDefault) throw new InvalidOperationException("Admit query selections before binding storage.");
            cancellationToken.ThrowIfCancellationRequested();
            var decide = choice.Flag is null ? null : await choice.PrepareAsync(services, cancellationToken).ConfigureAwait(false);
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
            if (choice.Flag is not null)
                foreach (var (bounds, alternate) in readers)
                {
                    var live = persistence.Query(query, bounds.Item1, bounds.Item2, binding.Consistency);
                    if (ServiceQueryIdentity.For(live.Definition.CompilationRequest) != key)
                        throw new InvalidOperationException("Native reader must realize the admitted canonical query.");
                    ServiceQueryConsistencyValidation.Require(live, binding.Consistency);
                    readers[bounds] = new FlagReader<TInput, TResult>(query, binding.Consistency, decide!, live, alternate);
                }
            return new ServicePersistenceBuilder(persistence).Query(query, (rows, bytes) =>
                readers.TryGetValue((rows, bytes), out var reader) ? reader
                    : throw new InvalidOperationException("Query bounds differ from storage admission.")).Build();
        }
    }

    sealed class FlagReader<TInput, TResult>(RelationQuery<TInput, TResult> definition, RelationQueryConsistency consistency,
        Func<TInput, CancellationToken, ValueTask<bool>> decide, IRelationQueryReader<TInput, TResult> disabled,
        IRelationQueryReader<TInput, TResult> enabled) : IRelationQueryReader<TInput, TResult>
    {
        public RelationQuery<TInput, TResult> Definition => definition;
        public RelationQueryConsistency Consistency => consistency;
        public Task<TResult> ReadAsync(TInput input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = decide(input, cancellationToken);
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
