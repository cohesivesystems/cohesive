using System.Collections.Immutable;

namespace Cohesive.Execution;

/// <summary>Immutable exact-reference catalog of durable Request operation adapters.</summary>
/// <remarks>
/// Adapter capabilities are the deployment authority for supported Request contracts. The catalog rejects overlap
/// instead of relying on registration order, so one durable Request can never select two physical interpreters.
/// </remarks>
public sealed class DurableOperationAdapterCatalog :
    IDurableOperationAdapterResolver,
    IDurableOperationAdapterCapabilityResolver
{
    readonly ImmutableDictionary<RequestContractReference, IDurableOperationAdapter> adapters;

    /// <summary>Creates an immutable adapter catalog from declared exact capabilities.</summary>
    /// <param name="adapters">Physical durable-operation adapters deployed together.</param>
    /// <exception cref="ArgumentNullException"><paramref name="adapters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An adapter is null, declares no supported Request, overlaps another adapter, or conflicts with another
    /// fingerprint for the same Request identity and revision.
    /// </exception>
    public DurableOperationAdapterCatalog(IEnumerable<IDurableOperationAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var builder = ImmutableDictionary.CreateBuilder<RequestContractReference, IDurableOperationAdapter>();
        Dictionary<(ExecutionDefinitionId Definition, ExecutionRevisionId Revision), RequestContractReference>
            revisions = [];
        foreach (var adapter in adapters)
        {
            if (adapter is null)
                throw new ArgumentException("A durable-operation adapter catalog cannot contain null entries.", nameof(adapters));
            if (adapter.Capabilities.SupportedRequests.IsDefaultOrEmpty)
                throw new ArgumentException("Every durable-operation adapter must declare a supported Request.", nameof(adapters));
            foreach (var request in adapter.Capabilities.SupportedRequests)
            {
                var revisionKey = (request.Definition.DefinitionId, request.Definition.RevisionId);
                if (revisions.TryGetValue(revisionKey, out var retained)
                    && retained.Definition.Fingerprint != request.Definition.Fingerprint)
                {
                    throw new ArgumentException(
                        $"Request contract '{request.Definition.DefinitionId.Value}' revision "
                        + $"'{request.Definition.RevisionId.Value}' has conflicting adapter fingerprints.",
                        nameof(adapters));
                }
                revisions[revisionKey] = request;
                if (!builder.TryAdd(request, adapter))
                {
                    throw new ArgumentException(
                        $"Exact Request contract '{request.Definition.DefinitionId.Value}' is handled more than once.",
                        nameof(adapters));
                }
            }
        }
        this.adapters = builder.ToImmutable();
    }

    /// <summary>Number of exact Request contracts covered by the catalog.</summary>
    public int Count => adapters.Count;

    /// <inheritdoc />
    public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(request);
        return adapters.TryGetValue(request.Contract, out adapter);
    }

    /// <inheritdoc />
    public bool TryResolve(
        RequestContractReference request,
        out DurableOperationAdapterCapabilities? capabilities)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (adapters.TryGetValue(request, out var adapter))
        {
            capabilities = adapter.Capabilities;
            return true;
        }

        capabilities = null;
        return false;
    }
}
