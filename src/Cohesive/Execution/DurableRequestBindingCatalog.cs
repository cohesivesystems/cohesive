using System.Collections.Immutable;

namespace Cohesive.Execution;

/// <summary>Immutable exact-reference catalog of durable Request execution bindings.</summary>
/// <remarks>
/// The catalog is deployment policy rather than interaction-contract authority. It rejects duplicate exact
/// bindings and conflicting fingerprints so admission can prove that each required Request has one concrete
/// interpretation before execution begins.
/// </remarks>
public sealed class DurableRequestBindingCatalog : IDurableRequestBindingResolver
{
    readonly ImmutableDictionary<RequestContractReference, DurableRequestBinding> bindings;

    /// <summary>Creates an immutable exact-reference binding catalog.</summary>
    /// <param name="bindings">Concrete durable Request bindings deployed together.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is null, repeats an exact Request reference, or conflicts with another fingerprint for the same
    /// Request identity and revision.
    /// </exception>
    public DurableRequestBindingCatalog(IEnumerable<DurableRequestBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var builder = ImmutableDictionary.CreateBuilder<RequestContractReference, DurableRequestBinding>();
        Dictionary<(ExecutionDefinitionId Definition, ExecutionRevisionId Revision), RequestContractReference>
            revisions = [];
        foreach (var binding in bindings)
        {
            if (binding is null)
            {
                throw new ArgumentException(
                    "A durable Request binding catalog cannot contain null entries.",
                    nameof(bindings));
            }

            var request = binding.Request;
            var revisionKey = (request.Definition.DefinitionId, request.Definition.RevisionId);
            if (revisions.TryGetValue(revisionKey, out var retained)
                && retained.Definition.Fingerprint != request.Definition.Fingerprint)
            {
                throw new ArgumentException(
                    $"Request contract '{request.Definition.DefinitionId.Value}' revision "
                    + $"'{request.Definition.RevisionId.Value}' is bound with conflicting fingerprints "
                    + $"'{retained.Definition.Fingerprint.Value}' and "
                    + $"'{request.Definition.Fingerprint.Value}'.",
                    nameof(bindings));
            }
            revisions[revisionKey] = request;
            if (!builder.TryAdd(request, binding))
            {
                throw new ArgumentException(
                    $"Exact Request contract '{Describe(request)}' is bound more than once.",
                    nameof(bindings));
            }
        }

        this.bindings = builder.ToImmutable();
    }

    /// <summary>Number of exact durable Request bindings in the catalog.</summary>
    public int Count => bindings.Count;

    /// <summary>Attempts to resolve one exact Request contract without constructing a runtime envelope.</summary>
    /// <param name="request">Exact canonical Request contract.</param>
    /// <param name="binding">Receives the matching concrete binding when present.</param>
    /// <returns><see langword="true"/> when exactly one binding was admitted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public bool TryResolve(RequestContractReference request, out DurableRequestBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(request);
        return bindings.TryGetValue(request, out binding);
    }

    /// <inheritdoc />
    public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(request);
        return TryResolve(request.Contract, out binding);
    }

    static string Describe(RequestContractReference request) =>
        $"{request.Definition.DefinitionId.Value}@{request.Definition.RevisionId.Value}#"
        + request.Definition.Fingerprint.Value;
}
